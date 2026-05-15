using Asp.Versioning;
using Indx.Api;
using Indx.CloudApi;
using Indx.Core;
using Indx.Storage;
using Indx.Utilities;
using IndxCloudApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace IndxCloudApi.Controllers
{
    /// <summary>
    /// Provides API endpoints for managing, analyzing, indexing, and searching JSON-based datasets. Supports operations
    /// such as dataset creation, deletion, field configuration, data loading, and executing search queries. All
    /// endpoints require authentication and operate on datasets associated with the authenticated user.
    /// </summary>
    [ApiVersion("1.0-alpha")]
    [Route("api")]
    [ApiController]
    public class SearchController : Controller
    {
        #region Public Methods
        /// <summary>
        /// As Analyze but handles a stream as input text.
        /// </summary>
        [HttpPost("AnalyzeStreamAsync/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public async Task<ActionResult<SystemStatus>> AnalyzeStreamAsync(string dataSetName)
        {
            HttpContext.Request.EnableBuffering();
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngineForInit(dataSetName, userId);
            if (matcher == null)
                return BadRequest("AnalyzeStreamAsync non existing dataset name or configuration");
            var state = matcher.Status;
            if (state.InvalidDataSetName)
                return BadRequest("invalid dataSetName");
            var pm = new ProcessMonitor();
            matcher.Init(HttpContext.Request.Body, pm);
            pm.WaitForCompletion();
            if (!pm.Succeeded)
                return BadRequest("Analyze failed, likely invalid json data");
            if (matcher.DocumentFields == null)
                return BadRequest("Analyze failed, DocumentFields==null");
            if (matcher.Persistence == null)
                return BadRequest("Analyze failed, Persistence==null");
            matcher.Persistence.SaveDocumentFields(matcher.DocumentFields.GetSerialized());
            return Ok(state);
        }

        /// <summary>
        /// Analyze the fields of a string containing json. Since json may be invalid, which will cause
        /// a 400 error, it is sent as plain text.
        /// </summary>
        [RequestSizeLimit(2_000_000_000)]
        [HttpPost("AnalyzeString/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<SystemStatus> AnalyzeString(string dataSetName, [FromBody] string jsonData)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (string.IsNullOrEmpty(jsonData))
                return BadRequest("null or empty jsonData argument");
            var state = IndxCloudInternalApi.Manager.GetState(dataSetName, userId);
            if (state == null || state.InvalidDataSetName)
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngineForInit(dataSetName, userId);
            if (matcher == null)
                return BadRequest("AnalyzeString non existing dataset name or configuration");
            var df = DocumentFields.Analyze(jsonData, out string error);
            if (!string.IsNullOrEmpty(error) || df == null)
                return BadRequest(error);
            matcher.SetDocumentFieldsInternal(df);
            if (matcher.Persistence == null)
                return BadRequest("Analyze failed, Persistence==null");
            matcher.Persistence.SaveDocumentFields(df.GetSerialized());
            return state;
        }

        /// <summary>
        /// CombineFilters will combine two filters using AND or OR operation.
        /// </summary>
        [HttpPut("CombineFilters/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<FilterProxy> CombineFilters(string dataSetName, [FromBody] CombinedFilterProxy combineFilters)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("CombineFilters, non existing dataset name");
            var fa = matcher.GetFilterFromKey(combineFilters.A.HashString);
            var fb = matcher.GetFilterFromKey(combineFilters.B.HashString);
            matcher.LoadFilters(new Filter[] { fa, fb });
            Filter result;
            if (combineFilters.UseAndOperation)
                result = fa & fb;
            else
                result = fa | fb;
            var filterProxy = new FilterProxy(result.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// CreateBoost will create a Boost setup which may be passed to any search.
        /// </summary>
        [HttpPut("CreateBoost/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<BoostProxy> CreateBoost(string dataSetName, [FromBody] BoostProxy boost)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("CreateBoost non existing dataset name");
            var filter = matcher.GetFilterFromKey(boost.FilterProxy.HashString);
            if (filter == null)
                return BadRequest("invalid filter arguments");
            matcher.CreateBoost(filter, boost.BoostStrength);
            return Ok(boost);
        }

        /// <summary>
        /// CreateOrOpen will create a data set. Uses default configuration.
        /// </summary>
        [HttpPut("CreateOrOpen/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult CreateOrOpen(string dataSetName)
        {
            return CreateOrOpen(dataSetName, 400);
        }

        /// <summary>
        /// CreateOrOpen will create a data set with specified configuration.
        /// </summary>
        [HttpPut("CreateOrOpen/{dataSetName}/{configuration}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult CreateOrOpen(string dataSetName, int configuration)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            if (!CoreSearchEngine.ConfigurationExists(configuration))
                return BadRequest("illegal configuration number");
            var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, dataSetName, userId);
            if (!persistence.DataSetExists())
                persistence.CreateOrOpenDataSet(configuration);
            return Ok();
        }

        /// <summary>
        /// CreateRangeFilter will create a RangeFilter which may be passed to any search.
        /// </summary>
        [HttpPut("CreateRangeFilter/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<FilterProxy> CreateRangeFilter(string dataSetName, [FromBody] RangeFilterProxy rangeFilter)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("CreateRangeFilter non existing dataset name");
            var filter = matcher.CreateRangeFilter(rangeFilter.FieldName, rangeFilter.LowerLimit, rangeFilter.UpperLimit);
            if (filter == null)
                return BadRequest("invalid filter arguments");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// CreateValueFilter will create a ValueFilter which may be passed to any search.
        /// </summary>
        [HttpPut("CreateValueFilter/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<FilterProxy> CreateValueFilter(string dataSetName, [FromBody] ValueFilterProxy valueFilter)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("CreateRangeFilter non existing dataset name");
            var filter = matcher.CreateValueFilter(valueFilter.FieldName, valueFilter.Value);
            if (filter == null)
                return BadRequest("invalid filter arguments, filter value must have tostring implementation");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// DeleteDataSet, will delete the entire dataSet including all contained Documents.
        /// </summary>
        [HttpDelete("DeleteDataSet/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult DeleteDataSet(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!IndxCloudInternalApi.Manager.DeleteDataSet(dataSetName, userId))
                return BadRequest("Attempt to delete non exixting dataset");
            return Ok();
        }

        /// <summary>
        /// Deletes a document from the dataset by its key.
        /// </summary>
        [HttpDelete("{dataSetName}/{documentKey}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult DeleteJsonRecord(string dataSetName, long documentKey)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("DeleteDocument non existing dataset name");
            var result = matcher.DeleteJsonRecord(documentKey);
            if (!result)
                return BadRequest("DeleteDocument document not found");
            return Ok();
        }

        /// <summary>
        /// Deletes documents from the dataset by their keys.
        /// </summary>
        [HttpDelete("{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult DeleteJsonRecords(string dataSetName, [FromBody] long[] documentKeys)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavyOnShadowIfReady(dataSetName, userId, "DeleteJsonRecords", engine =>
            {
                foreach (var documentKey in documentKeys)
                {
                    var result = engine.DeleteJsonRecord(documentKey);
                    if (!result)
                        return BadRequest($"DeleteJsonRecords document not found: {documentKey}");
                }
                return Ok();
            });
        }

        /// <summary>
        /// GetAllFields will return the fields found during analyze.
        /// </summary>
        [HttpGet("GetallFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetAllFields(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, userId, true, false, false, false, false, false);
        }

        /// <summary>
        /// GetFacetableFields will return the array of facetable field names.
        /// </summary>
        [HttpGet("GetFacetableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetFacetableFields(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, userId, false, false, false, false, true, false);
        }

        /// <summary>
        /// GetFilterableFields will return the array of filterable field names.
        /// </summary>
        [HttpGet("GetFilterableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetFilterableFields(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, userId, false, false, false, true, false, false);
        }

        /// <summary>
        /// Returns the raw json records as string[] for the keys.
        /// </summary>
        [HttpPost("GetJson/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetJson(string dataSetName, [FromBody] long[] keys)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            ICloudSearchEngine? engine = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (engine == null)
                return BadRequest("GetJson non existing dataset name");
            if (engine.Status.SystemState == SystemState.Created || engine.Status.SystemState == SystemState.Loading)
                return BadRequest("GetJson invalid status, no data loaded or loading in progress");
            var jsonStrings = new string[keys.Length];
            for (int i = 0; i < jsonStrings.Length; i++)
                jsonStrings[i] = engine.GetJsonDataOfKey(keys[i]);
            return jsonStrings;
        }

        /// <summary>
        /// Returns the number of JSON records in the database for the given dataset.
        /// </summary>
        [HttpGet("GetNumberOfJsonRecordsInDb/{dataSetname}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<int> GetNumberOfJsonRecordsInDb(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, dataSetName, userId);
            if (!persistence.DataSetExists())
                return BadRequest("invalid dataSetName");
            return persistence.NumberOfJsonRecords();
        }

        /// <summary>
        /// GetSearchableFields will return the array of searchable field names.
        /// </summary>
        [HttpGet("GetSearchableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetSearchableFields(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, userId, false, true, false, false, false, false);
        }

        /// <summary>
        /// GetSortableFields will return the array of sortable field names.
        /// </summary>
        [HttpGet("GetSortableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetSortableFields(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, userId, false, false, true, false, false, false);
        }

        /// <summary>
        /// GetStatus will return the status of the dataSetName in the search engine.
        /// </summary>
        [HttpGet("GetStatus/{dataSetname}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<SystemStatus> GetStatus(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            var status = IndxCloudInternalApi.Manager.GetState(dataSetName, userId);
            if (status == null)
                return BadRequest("GetStatus failed, status==null");

            // Augment with shadow-build progress so clients can poll while a bulk update
            // or reindex is running.
            status.ShadowBuildInProgress = IndxCloudInternalApi.Manager.IsShadowBuildInProgress(dataSetName, userId);
            status.ShadowBuildStartedUtc = IndxCloudInternalApi.Manager.ShadowBuildStartedUtc(dataSetName, userId);
            return status;
        }

        /// <summary>
        /// GetWordIndexingFields will return the array of word-indexing field names.
        /// </summary>
        [HttpGet("GetWordIndexingFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetWordIndexingFields(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, userId, false, false, false, false, false, true);
        }

        /// <summary>
        /// Return the datasets created by current user if any.
        /// </summary>
        [HttpGet("GetUserDatasets")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<string[]> GetUserDataSets()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, "dummy", userId);
            return persistence.GetUserDataSets(userId);
        }

        /// <summary>
        /// IndexDataSet will start indexing of the loaded documents.
        /// </summary>
        [HttpGet]
        [Route("IndexDataSet/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<SystemStatus> IndexDataSet(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return BadRequest("unauthorized");

            var matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("IndexDataSet non existing dataset name");

            if (matcher.Status.SystemState == SystemState.Ready)
            {
                // Re-index without blocking searches: build shadow (which loads + indexes
                // internally) and swap it in. The mutation lambda is a no-op.
                try
                {
                    IndxCloudInternalApi.Manager.RunMutationOnShadow<int>(dataSetName, userId, _ => 0);
                }
                catch (ShadowBusyException ex)
                {
                    return StatusCode(StatusCodes.Status409Conflict, ex.Message);
                }
            }
            else if (!IndxCloudInternalApi.Manager.DoIndex(dataSetName, userId))
            {
                // Pre-Ready: first-time indexing path (Loaded -> Indexing -> Ready).
                var status1 = IndxCloudInternalApi.Manager.GetState(dataSetName, userId);
                if (status1 == null)
                    return BadRequest("IndexDataSet failed, DoIndex returned false");
                if (status1.SystemState == SystemState.Created)
                {
                    status1.ErrorMessage = "IndexDataSet failed, due to invalidstate, check Load operation completion status";
                    return StatusCode(StatusCodes.Status409Conflict, status1);
                }
            }

            var status = IndxCloudInternalApi.Manager.GetState(dataSetName, userId);
            if (status == null)
                return BadRequest("IndexDataSet failed, status==null");
            return status;
        }

        /// <summary>
        /// Inserts one single Json record.
        /// </summary>
        [HttpPost("{dataSetName}/insert/{documentKey:long}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult InsertJsonRecord(string dataSetName, [FromBody] string jsonData)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("InsertJsonRecord non existing dataset name");
            var result = matcher.InsertJsonRecord(jsonData, out string error);
            if (!result)
                return BadRequest(error);
            return Ok();
        }

        /// <summary>
        /// Inserts new JSON records into the dataset.
        /// </summary>
        [HttpPost("{dataSetName}/insert")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult InsertJsonRecords(string dataSetName, [FromBody] string[] jsonRecords)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavyOnShadowIfReady(dataSetName, userId, "InsertJsonRecords", engine =>
            {
                engine.InsertJsonRecords(jsonRecords, null, out _);
                return Ok();
            });
        }

        /// <summary>
        /// Loads the jsonData into search engine from the database.
        /// </summary>
        [HttpGet("LoadFromDatabase/{dataSetName}")]
        [EnableCors("AllowAllHeaders")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<ActionResult> LoadFromDatabaseAsync(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var pm = new ProcessMonitor();
            var success = IndxCloudInternalApi.Manager.LoadFromDatabase(dataSetName, userId, pm);
            if (!success)
                return BadRequest("LoadFromDatabaseAsync failed, success==null");
            await pm.WaitForCompletionAsync();
            if (!pm.Succeeded)
                return BadRequest(pm.ErrorMessage);
            return Ok();
        }

        /// <summary>
        /// Loads the jsonData into search engine as a stream.
        /// </summary>
        [HttpPut("LoadStream/{dataSetName}")]
        [EnableCors("AllowAllHeaders")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<ActionResult> LoadStreamAsync(string dataSetName)
        {
            HttpContext.Request.EnableBuffering();
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (HttpContext.Request.ContentLength == null || HttpContext.Request.ContentLength == 0)
                return BadRequest("Empty request body, stream missing");
            var bodyStream = HttpContext.Request.Body;
            bodyStream.Position = 0;
            var pm = new ProcessMonitor();
            if (IndxCloudInternalApi.Manager.Load(dataSetName, userId, bodyStream, pm))
                pm.WaitForCompletion();
            else
                return BadRequest("LoadStreamAsync failed, Load returned false");
            if (!pm.Succeeded)
                return StatusCode(StatusCodes.Status422UnprocessableEntity, pm.ErrorMessage);

            return Ok();
        }

        /// <summary>
        /// Loads the jsonData into search engine as a string.
        /// </summary>
        [RequestSizeLimit(2_000_000_000)]
        [HttpPut("LoadString/{dataSetName}")]
        [EnableCors("AllowAllHeaders")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public IActionResult LoadString(string dataSetName, [FromBody] string jsonData)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            var memoryStream = new MemoryStream();
            using (var writer = new StreamWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(jsonData);
                writer.Flush();
            }
            memoryStream.Position = 0;
            var pm = new ProcessMonitor();
            if (IndxCloudInternalApi.Manager.Load(dataSetName, userId, memoryStream, pm))
                pm.WaitForCompletion();
            else
                return BadRequest("LoadString failed, Load returned false");
            if (!pm.Succeeded)
                return BadRequest(pm.ErrorMessage);
            return Ok();
        }

        /// <summary>
        /// Search will validate the search query and return the search result.
        /// </summary>
        [HttpPost("Search/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<Indx.Api.Result> Search(string dataSetName, [FromBody] CloudQuery query)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            Indx.Api.Result res = IndxCloudInternalApi.Manager.Search(query, dataSetName, userId);
            return res;
        }

        /// <summary>
        /// SetFacetableFields sets the Facetable property on the specified fields.
        /// </summary>
        [Obsolete("Use SetFieldConfiguration instead. Scheduled for removal in a future release.")]
        [HttpPut("SetFacetableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetFacetableFields(string dataSetName, [FromBody] string[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetFacetableFields invalid status");
            foreach (var item in fields)
            {
                var f = df.GetField(item);
                if (f == null)
                    return BadRequest("SearchController.SetFacetableFields non existing fieldname");
                f.Facetable = true;
            }
            return Ok();
        }

        /// <summary>
        /// SetFilterableFields sets the Filterable property on the specified fields.
        /// </summary>
        [Obsolete("Use SetFieldConfiguration instead. Scheduled for removal in a future release.")]
        [HttpPut("SetFilterableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetFilterableFields(string dataSetName, [FromBody] string[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetFilterableFields invalid status");
            foreach (var item in fields)
            {
                var f = df.GetField(item);
                if (f == null)
                    return BadRequest("SearchController.SetFilterableFields non existing fieldname");
                f.Filterable = true;
            }
            return Ok();
        }

        /// <summary>
        /// SetSearchableFields sets the Searchable property and weight on the specified fields.
        /// </summary>
        [Obsolete("Use SetFieldConfiguration instead. Scheduled for removal in a future release.")]
        [HttpPut("SetSearchableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetSearchableFields(string dataSetName, [FromBody] (string Name, float Weight)[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetSearchableFields invalid status");
            foreach (var item in fields)
            {
                var f = df.GetField(item.Name);
                if (f == null)
                    return BadRequest("SearchController.SetSearchableFields non existing fieldname");
                f.Searchable = true;
                f.Weight = item.Weight;
            }
            return Ok();
        }

        /// <summary>
        /// SetSortableFields sets the Sortable property on the specified fields.
        /// </summary>
        [Obsolete("Use SetFieldConfiguration instead. Scheduled for removal in a future release.")]
        [HttpPut("SetSortableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetSortableFields(string dataSetName, [FromBody] string[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetSortableFields invalid status");
            foreach (var item in fields)
            {
                var f = df.GetField(item);
                if (f == null)
                    return BadRequest("SearchController.SetSortableFields non existing fieldname");
                f.Sortable = true;
            }
            return Ok();
        }

        /// <summary>
        /// SetWordIndexingFields sets the WordIndexing property on the specified fields.
        /// </summary>
        [Obsolete("Use SetFieldConfiguration instead. Scheduled for removal in a future release.")]
        [HttpPut("SetWordIndexingFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetWordIndexingFields(string dataSetName, [FromBody] string[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetWordIndexingFields invalid status");
            foreach (var item in fields)
            {
                var f = df.GetField(item);
                if (f == null)
                    return BadRequest("SearchController.SetWordIndexingFields non existing fieldname");
                f.WordIndexing = true;
            }
            return Ok();
        }

        /// <summary>
        /// SetFieldConfiguration sets any combination of field properties (Searchable, Filterable,
        /// Facetable, Sortable, WordIndexing, Embeddable, PreloadFilters, Weight, BM25Fb, BM25Fk1)
        /// in one call. Nullable properties have replace semantics: null = leave untouched,
        /// any value (including false) = overwrite.
        /// On validation failure of any item, returns BadRequest immediately; earlier items in the
        /// array remain applied (best-effort, consistent with Set*Fields).
        /// </summary>
        [HttpPut("SetFieldConfiguration/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetFieldConfiguration(string dataSetName, [FromBody] FieldProxy[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetFieldConfiguration invalid status");

            // If any proposed change requires rebuilding the inverted/word/vector index
            // AND the engine is currently serving searches, route via the shadow-swap path
            // so live searches are not blocked while the new index is built. The override
            // is applied between Init and Load on the shadow so MakeSearchEngines builds
            // _indexableFields against the new Searchable set — flipping the flag after
            // Load has no effect because that collection is never refreshed.
            bool needsReindex = df.RequiresReindex(fields);
            if (needsReindex && matcher.Status.SystemState == SystemState.Ready)
            {
                // Validate up front against the active engine so we can return BadRequest
                // before kicking off the shadow build.
                foreach (var cfg in fields)
                    if (df.GetField(cfg.FieldName) == null)
                        return BadRequest(
                            $"SearchController.SetFieldConfiguration non existing fieldname: {cfg.FieldName}");

                try
                {
                    IndxCloudInternalApi.Manager.RunFieldConfigurationOnShadow(dataSetName, userId, fields);
                    return Ok();
                }
                catch (ShadowBusyException ex)
                {
                    return StatusCode(StatusCodes.Status409Conflict, ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(ex.Message);
                }
            }

            // Inline: only query-time flags changed, or engine is not yet Ready.
            var failed = matcher.SetFieldConfiguration(fields);
            if (failed != null)
                return BadRequest($"SearchController.SetFieldConfiguration non existing fieldname: {failed}");
            return Ok();
        }

        /// <summary>
        /// GetFieldConfiguration returns the full configuration of every field in the dataset,
        /// including all flags, weights and BM25F parameters. Returns an empty array if the
        /// dataset does not exist or has no fields yet.
        /// </summary>
        [HttpGet("GetFieldConfiguration/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<FieldProxy[]> GetFieldConfiguration(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return Array.Empty<FieldProxy>();
            return matcher.GetFieldConfiguration();
        }

        /// <summary>
        /// SetBM25FFields sets per-field BM25F parameters (b and k1), activating BM25F multi-field
        /// scoring when at least one searchable field has either value set.
        /// </summary>
        [Obsolete("Use SetFieldConfiguration instead. Scheduled for removal in a future release.")]
        [HttpPut("SetBM25FFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetBM25FFields(string dataSetName, [FromBody] BM25FFieldProxy[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetBM25FFields invalid status");
            foreach (var item in fields)
            {
                var f = df.GetField(item.FieldName);
                if (f == null)
                    return BadRequest($"SearchController.SetBM25FFields non existing fieldname: {item.FieldName}");
                f.BM25Fb = item.BM25Fb;
                f.BM25Fk1 = item.BM25Fk1;
            }
            return Ok();
        }

        /// <summary>
        /// Updates existing JSON records in the dataset.
        /// </summary>
        [HttpPut("{dataSetName}/update")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult UpdateJsonRecords(string dataSetName, [FromBody] string[] jsonRecords)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavyOnShadowIfReady(dataSetName, userId, "UpdateJsonRecords", engine =>
            {
                foreach (var jsonData in jsonRecords)
                {
                    var result = engine.UpdateJsonRecord(jsonData, out string error);
                    if (!result)
                        return BadRequest(error);
                }
                return Ok();
            });
        }

        /// <summary>
        /// Updates one single Document.
        /// </summary>
        [HttpPut("{dataSetName}/update/{documentKey:long}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult UpdateJsonRecord(string dataSetName, [FromBody] string jsonData)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("UpdateJsonRecord non existing dataset name");
            var result = matcher.UpdateJsonRecord(jsonData, out string error);
            if (!result)
                return BadRequest(error);
            return Ok();
        }
        /// <summary>
        /// Updates a single field on a document identified by its key.
        /// </summary>
        [HttpPut("{dataSetName}/field/{documentKey:long}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult UpdateField(string dataSetName, long documentKey, [FromBody] UpdateFieldProxy update)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("UpdateField non existing dataset name");
            var result = matcher.UpdateField(documentKey, update.FieldName, UnwrapJsonElement(update.Value)!, out string error);
            if (!result)
                return BadRequest(error);
            return Ok();
        }

        /// <summary>
        /// Deletes all documents matching the given filter.
        /// </summary>
        [HttpDelete("DeleteRecordsInFilter/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult DeleteRecordsInFilter(string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavyOnShadowIfReady(dataSetName, userId, "DeleteRecordsInFilter", engine =>
            {
                var filter = engine.GetFilterFromKey(filterProxy.HashString);
                if (filter == null)
                    return BadRequest("DeleteRecordsInFilter invalid filter key");
                engine.LoadFilters(new[] { filter });
                engine.DeleteRecordsInFilter(filter);
                return Ok();
            });
        }

        /// <summary>
        /// Updates a field on all documents matching the given filter. Returns the number of updated documents.
        /// </summary>
        [HttpPut("UpdateFieldInFilter/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<int> UpdateFieldInFilter(string dataSetName, [FromBody] FilterFieldUpdateProxy payload)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavyOnShadowIfReady(dataSetName, userId, "UpdateFieldInFilter", engine =>
            {
                var filter = engine.GetFilterFromKey(payload.Filter.HashString);
                if (filter == null)
                    return BadRequest("UpdateFieldInFilter invalid filter key");
                var count = engine.UpdateFieldInFilter(filter, payload.FieldName, UnwrapJsonElement(payload.Value)!, out string error);
                if (count == 0 && !string.IsNullOrEmpty(error))
                    return BadRequest(error);
                return Ok(count);
            });
        }

        /// <summary>
        /// Deletes a single filter from the filter cache.
        /// </summary>
        [HttpDelete("DeleteFilter/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult DeleteFilter(string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("DeleteFilter non existing dataset name");
            var filter = matcher.GetFilterFromKey(filterProxy.HashString);
            if (filter == null)
                return BadRequest("DeleteFilter invalid filter key");
            var result = matcher.DeleteFilter(filter);
            if (!result)
                return BadRequest("DeleteFilter failed, filter not found in cache");
            return Ok();
        }

        /// <summary>
        /// Deletes all filters from the filter cache.
        /// </summary>
        [HttpDelete("DeleteAllFilters/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult DeleteAllFilters(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("DeleteAllFilters non existing dataset name");
            matcher.DeleteAllFilters();
            return Ok();
        }

        /// <summary>
        /// Pre-loads all registered filters in the background.
        /// </summary>
        [HttpPost("LoadAllFilters/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult LoadAllFilters(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("LoadAllFilters non existing dataset name");
            matcher.LoadAllFilters();
            return Ok();
        }

        /// <summary>
        /// Returns the number of filters currently registered in the filter cache.
        /// </summary>
        [HttpGet("GetNumberOfFilters/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<int> GetNumberOfFilters(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("GetNumberOfFilters non existing dataset name");
            return Ok(matcher.NumberOfFilters);
        }

        /// <summary>
        /// Hibernates the dataset, freeing in-memory structures while retaining persisted data.
        /// </summary>
        [HttpPut("Hibernate/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult Hibernate(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("Hibernate non existing dataset name");
            var result = matcher.Hibernate(out string errorMessage);
            if (!result)
                return BadRequest(errorMessage);
            return Ok();
        }

        /// <summary>
        /// Wakes up a hibernated dataset, restoring it from the persisted state.
        /// </summary>
        [HttpPut("WakeUp/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult WakeUp(string dataSetName)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest("WakeUp non existing dataset name");
            var result = matcher.WakeUp();
            if (!result)
                return BadRequest("WakeUp failed");
            return Ok();
        }

        /// <summary>
        /// Marks the specified fields as embeddable so that their vector values are indexed
        /// during the next Load. Must be called after AnalyzeStream and before LoadStream.
        /// </summary>
        [Obsolete("Use SetFieldConfiguration instead. Scheduled for removal in a future release.")]
        [HttpPut("SetEmbeddableFields/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public IActionResult SetEmbeddableFields(string dataSetName, [FromBody] string[] fields)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            if (!IndxCloudInternalApi.Manager.SetEmbeddableFields(fields, dataSetName, userId))
                return BadRequest("SetEmbeddableFields failed — dataset not found or unknown field name");
            return Ok();
        }

        /// <summary>
        /// Searches a single embedding field using approximate nearest-neighbour search.
        /// The dataset must be in Ready state (fully loaded and indexed).
        /// </summary>
        [HttpPost("VectorSearch/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<Indx.CloudApi.EmbeddingResultEntry[]> VectorSearch(
            string dataSetName, [FromBody] Indx.CloudApi.VectorQueryProxy query)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (query?.Vector == null || query.Vector.Length == 0)
                return BadRequest("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.FieldName))
                return BadRequest("FieldName is required");
            return IndxCloudInternalApi.Manager.VectorSearch(query, dataSetName, userId);
        }

        /// <summary>
        /// Combines text search with embedding nearest-neighbour search and blends scores.
        /// combined score = alpha * embeddingScore + (1 - alpha) * normalisedTextScore.
        /// The dataset must be in Ready state (fully loaded and indexed).
        /// </summary>
        [HttpPost("HybridSearch/{dataSetName}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("AllowAllHeaders")]
        public ActionResult<Indx.CloudApi.EmbeddingResultEntry[]> HybridSearch(
            string dataSetName, [FromBody] Indx.CloudApi.HybridQueryProxy query)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();
            if (query?.Vector == null || query.Vector.Length == 0)
                return BadRequest("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.EmbeddingField))
                return BadRequest("EmbeddingField is required");
            return IndxCloudInternalApi.Manager.HybridSearch(query, dataSetName, userId);
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Resolves the engine for (dataSetName, userId). If the engine is in Ready state,
        /// runs <paramref name="mutation"/> on a shadow instance (so live searches are not
        /// blocked) and swaps the result in atomically. Otherwise runs the mutation inline
        /// on the live engine. Maps <see cref="ShadowBusyException"/> to 409 Conflict.
        ///
        /// The mutation lambda returns the ActionResult that becomes the response, which
        /// preserves the existing endpoints' early-exit semantics (e.g. BadRequest mid-loop).
        /// </summary>
        private ActionResult RunHeavyOnShadowIfReady(
            string dataSetName,
            string userId,
            string operationName,
            Func<ICloudSearchEngine, ActionResult> mutation)
        {
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                return BadRequest($"{operationName} non existing dataset name");

            if (matcher.Status.SystemState != SystemState.Ready)
                return mutation(matcher);

            try
            {
                return IndxCloudInternalApi.Manager.RunMutationOnShadow(dataSetName, userId, mutation);
            }
            catch (ShadowBusyException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, ex.Message);
            }
        }

        /// <summary>
        /// ASP.NET Core deserializes 'object' properties as JsonElement. Unwrap to the
        /// appropriate primitive so engine methods (UpdateField, UpdateFieldInFilter) can
        /// use type-checking via Field.GetJsonValueKind.
        /// </summary>
        private static object? UnwrapJsonElement(object? value)
        {
            if (value is not JsonElement el)
                return value;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Number when el.TryGetInt64(out long l) => l,
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.Array => el.EnumerateArray()
                    .Select(e => UnwrapJsonElement(e))
                    .ToArray(),
                _ => value
            };
        }

        #endregion Private Methods
    }
}