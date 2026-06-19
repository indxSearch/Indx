using Asp.Versioning;
using Indx.Api;
using Indx.CloudApi;
using Indx.Core;
using Indx.Storage;
using Indx.Utilities;
using IndxCloudApi.Models;
using IndxCloudApi.Services;
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
    /// API endpoints for managing, analyzing, indexing, and searching JSON datasets. Datasets are
    /// owned by a <b>team</b>: every dataset endpoint is scoped under
    /// <c>api/teams/{teamName}/datasets/{dataSetName}</c>. The caller must be a member of the team;
    /// their team role (Admin/Editor/Viewer) decides what they may do. Membership is checked on
    /// every request, so removing a user from a team takes effect immediately.
    /// </summary>
    [ApiVersion("2.0-alpha")]
    [Route("api")]
    [ApiController]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableCors("NewPolicy")]
    // Calling a dataset operation in a state it cannot serve (e.g. Search before Ready) returns
    // 409 Conflict with a ProblemDetails body (currentState, allowedStates, retryable + Retry-After
    // header when retryable). Declared here so it appears in the OpenAPI spec for every endpoint.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public class SearchController(TeamContextResolver resolver, TeamService teams, BoostRuleStore boostStore, IEditionService edition) : Controller
    {
        private const string DataSetRoute = "teams/{teamName}/datasets/{dataSetName}";

        #region Public Methods
        /// <summary>
        /// As Analyze but handles a stream as input text.
        /// </summary>
        [HttpPost(DataSetRoute + "/AnalyzeStreamAsync")]
        public async Task<ActionResult<SystemStatus>> AnalyzeStreamAsync(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            HttpContext.Request.EnableBuffering();
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngineForInit(dataSetName, ctx.OwnerKey);
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
        [HttpPost(DataSetRoute + "/AnalyzeString")]
        public ActionResult<SystemStatus> AnalyzeString(string teamName, string dataSetName, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (string.IsNullOrEmpty(jsonData))
                return BadRequest("null or empty jsonData argument");
            var state = IndxCloudInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (state == null || state.InvalidDataSetName)
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngineForInit(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("AnalyzeString non existing dataset name or configuration");
            var df = DocumentFields.Analyze(jsonData, out string error2);
            if (!string.IsNullOrEmpty(error2) || df == null)
                return BadRequest(error2);
            matcher.SetDocumentFieldsInternal(df);
            if (matcher.Persistence == null)
                return BadRequest("Analyze failed, Persistence==null");
            matcher.Persistence.SaveDocumentFields(df.GetSerialized());
            return state;
        }

        /// <summary>
        /// CombineFilters will combine two filters using AND or OR operation.
        /// </summary>
        [HttpPut(DataSetRoute + "/CombineFilters")]
        public ActionResult<FilterProxy> CombineFilters(string teamName, string dataSetName, [FromBody] CombinedFilterProxy combineFilters)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("CombineFilters, non existing dataset name");
            if (RequireState(matcher, "CombineFilters", SystemState.Ready) is { } stateError)
                return stateError;
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
        [HttpPut(DataSetRoute + "/CreateBoost")]
        public ActionResult<BoostProxy> CreateBoost(string teamName, string dataSetName, [FromBody] BoostProxy boost)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("CreateBoost non existing dataset name");
            if (RequireState(matcher, "CreateBoost", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = matcher.GetFilterFromKey(boost.FilterProxy.HashString);
            if (filter == null)
                return BadRequest("invalid filter arguments");
            matcher.CreateBoost(filter, boost.BoostStrength);
            return Ok(boost);
        }

        /// <summary>
        /// Returns the dataset's persisted boost rules (server-side ranking rules applied when a
        /// search sets enableBoost). Config, not state-gated — available even when not Ready.
        /// </summary>
        [HttpGet(DataSetRoute + "/boosts")]
        public ActionResult<BoostRule[]> GetBoostRules(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return boostStore.Load(ctx.OwnerKey, dataSetName).ToArray();
        }

        /// <summary>
        /// Replaces the dataset's boost rules (whole list). Each rule needs a name, at least one
        /// condition, and every condition must reference a Filterable field and be either a value
        /// match or a numeric range (not both).
        /// </summary>
        [HttpPut(DataSetRoute + "/boosts")]
        public IActionResult SetBoostRules(string teamName, string dataSetName, [FromBody] BoostRule[] rules)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            if (rules == null)
                return BadRequest("rules body is required");

            // Best-effort field validation when the engine is loaded; never hard-fail on a
            // hibernated dataset (apply-time skips unbuildable conditions anyway).
            var fields = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey)?.DocumentFields;
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Name))
                    return BadRequest("each rule needs a name");
                if (rule.Conditions == null || rule.Conditions.Count == 0)
                    return BadRequest($"rule '{rule.Name}' needs at least one condition");
                foreach (var c in rule.Conditions)
                {
                    var hasValue = !string.IsNullOrEmpty(c.Value);
                    var hasRange = c.Min.HasValue || c.Max.HasValue;
                    if (hasValue == hasRange)
                        return BadRequest($"condition on '{c.Field}' must be either a value or a range");
                    if (fields != null && fields.GetField(c.Field) is not { Filterable: true })
                        return BadRequest($"field '{c.Field}' is not filterable");
                }
            }

            // Boost-rule scheduling is a gated feature (Free Managed can't schedule). Strip any
            // schedule window when it's disabled — defensive (the UI hides the control) and
            // self-healing on a plan downgrade.
            if (!edition.IsEnabled(EditionFeature.BoostRuleScheduling))
                foreach (var rule in rules) { rule.ActiveFrom = null; rule.ActiveUntil = null; }

            boostStore.Save(ctx.OwnerKey, dataSetName, rules);
            return Ok();
        }

        /// <summary>Clears all boost rules for the dataset.</summary>
        [HttpDelete(DataSetRoute + "/boosts")]
        public IActionResult DeleteBoostRules(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            boostStore.Delete(ctx.OwnerKey, dataSetName);
            return NoContent();
        }

        /// <summary>
        /// CreateOrOpen will create a data set. Uses default configuration.
        /// </summary>
        [HttpPut(DataSetRoute + "/CreateOrOpen")]
        public IActionResult CreateOrOpen(string teamName, string dataSetName)
        {
            return CreateOrOpen(teamName, dataSetName, ConfigurationProfile.Production);
        }

        /// <summary>
        /// CreateOrOpen will create a data set with the specified configuration profile.
        /// </summary>
        [HttpPut(DataSetRoute + "/CreateOrOpen/{configuration}")]
        public IActionResult CreateOrOpen(string teamName, string dataSetName, ConfigurationProfile configuration)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, dataSetName, ctx.OwnerKey);
            if (!persistence.DataSetExists())
                persistence.CreateOrOpenDataSet((int)configuration);
            return Ok();
        }

        /// <summary>
        /// CreateRangeFilter will create a RangeFilter which may be passed to any search.
        /// </summary>
        [HttpPut(DataSetRoute + "/CreateRangeFilter")]
        public ActionResult<FilterProxy> CreateRangeFilter(string teamName, string dataSetName, [FromBody] RangeFilterProxy rangeFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("CreateRangeFilter non existing dataset name");
            if (RequireState(matcher, "CreateRangeFilter", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = matcher.CreateRangeFilter(rangeFilter.FieldName, rangeFilter.LowerLimit, rangeFilter.UpperLimit);
            if (filter == null)
                return BadRequest("invalid filter arguments");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// CreateValueFilter will create a ValueFilter which may be passed to any search.
        /// </summary>
        [HttpPut(DataSetRoute + "/CreateValueFilter")]
        public ActionResult<FilterProxy> CreateValueFilter(string teamName, string dataSetName, [FromBody] ValueFilterProxy valueFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("CreateRangeFilter non existing dataset name");
            if (RequireState(matcher, "CreateValueFilter", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = matcher.CreateValueFilter(valueFilter.FieldName, valueFilter.Value);
            if (filter == null)
                return BadRequest("invalid filter arguments, filter value must have tostring implementation");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// DeleteDataSet, will delete the entire dataSet including all contained Documents.
        /// Requires team Admin.
        /// </summary>
        [HttpDelete(DataSetRoute)]
        public IActionResult DeleteDataSet(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, admin: true);
            if (ctx == null) return error!;
            if (!IndxCloudInternalApi.Manager.DeleteDataSet(dataSetName, ctx.OwnerKey))
                return BadRequest("Attempt to delete non exixting dataset");
            return Ok();
        }

        /// <summary>
        /// Deletes a document from the dataset by its key.
        /// </summary>
        [HttpDelete(DataSetRoute + "/documents/{documentKey:long}")]
        public ActionResult DeleteJsonRecord(string teamName, string dataSetName, long documentKey)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("DeleteDocument non existing dataset name");
            if (RequireState(matcher, "DeleteJsonRecord", SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.DeleteJsonRecord(documentKey);
            if (!result)
                return BadRequest("DeleteDocument document not found");
            return Ok();
        }

        /// <summary>
        /// Deletes documents from the dataset by their keys.
        /// </summary>
        [HttpDelete(DataSetRoute + "/documents")]
        public ActionResult DeleteJsonRecords(string teamName, string dataSetName, [FromBody] long[] documentKeys)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavy(dataSetName, ctx.OwnerKey, "DeleteJsonRecords", engine =>
            {
                foreach (var documentKey in documentKeys)
                {
                    var result = engine.DeleteJsonRecord(documentKey);
                    if (!result)
                        return BadRequest($"DeleteJsonRecords document not found: {documentKey}");
                }
                return Ok();
            }, SystemState.Ready);
        }

        /// <summary>
        /// GetAllFields will return the fields found during analyze.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetallFields")]
        public ActionResult<string[]> GetAllFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, true, false, false, false, false, false);
        }

        /// <summary>
        /// GetFacetableFields will return the array of facetable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetFacetableFields")]
        public ActionResult<string[]> GetFacetableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, false, true, false);
        }

        /// <summary>
        /// GetFilterableFields will return the array of filterable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetFilterableFields")]
        public ActionResult<string[]> GetFilterableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, true, false, false);
        }

        /// <summary>
        /// Returns the raw json records as string[] for the keys.
        /// </summary>
        [HttpPost(DataSetRoute + "/GetJson")]
        public ActionResult<string[]> GetJson(string teamName, string dataSetName, [FromBody] long[] keys)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            ICloudSearchEngine? engine = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null)
                return BadRequest("GetJson non existing dataset name");
            if (RequireState(engine, "GetJson", SystemState.Loaded, SystemState.Indexing, SystemState.Ready) is { } stateError)
                return stateError;
            var jsonStrings = new string[keys.Length];
            for (int i = 0; i < jsonStrings.Length; i++)
                jsonStrings[i] = engine.GetJsonDataOfKey(keys[i]);
            return jsonStrings;
        }

        /// <summary>
        /// Returns the number of JSON records in the database for the given dataset.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetNumberOfJsonRecordsInDb")]
        public ActionResult<int> GetNumberOfJsonRecordsInDb(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var engine = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null)
                return BadRequest("invalid dataSetName");
            return engine.Persistence?.NumberOfJsonRecords() ?? 0;
        }

        /// <summary>
        /// GetSearchableFields will return the array of searchable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetSearchableFields")]
        public ActionResult<string[]> GetSearchableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, true, false, false, false, false);
        }

        /// <summary>
        /// GetSortableFields will return the array of sortable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetSortableFields")]
        public ActionResult<string[]> GetSortableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, true, false, false, false);
        }

        /// <summary>
        /// GetStatus will return the status of the dataSetName in the search engine.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetStatus")]
        public ActionResult<CloudSystemStatus> GetStatus(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var status = IndxCloudInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (status == null)
                return BadRequest($"GetStatus failed: dataset '{dataSetName}' not found in team '{teamName}'");

            return new CloudSystemStatus(status)
            {
                ShadowBuildInProgress = IndxCloudInternalApi.Manager.IsShadowBuildInProgress(dataSetName, ctx.OwnerKey),
                ShadowBuildStartedUtc = IndxCloudInternalApi.Manager.ShadowBuildStartedUtc(dataSetName, ctx.OwnerKey),
            };
        }

        /// <summary>
        /// GetWordIndexingFields will return the array of word-indexing field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetWordIndexingFields")]
        public ActionResult<string[]> GetWordIndexingFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, false, false, true);
        }

        /// <summary>
        /// Returns all datasets the current user can reach via team membership, across every team
        /// they belong to. Each entry carries the owning team name and the caller's role on it.
        /// </summary>
        [HttpGet("me/datasets")]
        public async Task<ActionResult<DataSetListDto[]>> GetMyDataSets()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var result = new List<DataSetListDto>();
            foreach (var (team, role) in await teams.GetTeamsForUserAsync(userId))
            {
                foreach (var name in IndxCloudInternalApi.Manager.GetTeamDataSets(team.Id.ToString()))
                    result.Add(new DataSetListDto(name, team.Name, role));
            }
            return result.ToArray();
        }

        /// <summary>
        /// Lists the datasets owned by a single team.
        /// </summary>
        [HttpGet("teams/{teamName}/datasets")]
        public ActionResult<string[]> GetTeamDataSets(string teamName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetTeamDataSets(ctx.OwnerKey).ToArray();
        }

        /// <summary>
        /// IndexDataSet will start indexing of the loaded documents.
        /// </summary>
        [HttpGet(DataSetRoute + "/IndexDataSet")]
        public ActionResult<SystemStatus> IndexDataSet(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;

            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("IndexDataSet non existing dataset name");

            // First-time indexing needs Loaded; a Ready dataset is re-indexed via shadow-swap.
            // Created / Loading / Indexing / Hibernated / Error are not indexable here → 409.
            if (RequireState(matcher, "IndexDataSet", SystemState.Loaded, SystemState.Ready) is { } stateError)
                return stateError;

            if (matcher.Status.SystemState == SystemState.Ready)
            {
                // Re-index without blocking searches: build shadow (which loads + indexes
                // internally) and swap it in. Empty FieldProxy[] means no field-config changes.
                try
                {
                    IndxCloudInternalApi.Manager.RunFieldConfigurationOnShadow(
                        dataSetName, ctx.OwnerKey, Array.Empty<FieldProxy>());
                }
                catch (ShadowBusyException ex)
                {
                    return StatusCode(StatusCodes.Status409Conflict, ex.Message);
                }
            }
            else if (!IndxCloudInternalApi.Manager.DoIndex(dataSetName, ctx.OwnerKey))
            {
                return BadRequest("IndexDataSet failed, DoIndex returned false");
            }

            var status = IndxCloudInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (status == null)
                return BadRequest("IndexDataSet failed, status==null");
            return status;
        }

        /// <summary>
        /// Inserts one single Json record.
        /// </summary>
        [HttpPost(DataSetRoute + "/insert/{documentKey:long}")]
        public ActionResult InsertJsonRecord(string teamName, string dataSetName, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("InsertJsonRecord non existing dataset name");
            if (RequireState(matcher, "InsertJsonRecord", SystemState.Created, SystemState.Loaded, SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.InsertJsonRecord(jsonData, out string error2);
            if (!result)
                return BadRequest(error2);
            return Ok();
        }

        /// <summary>
        /// Inserts new JSON records into the dataset.
        /// </summary>
        [HttpPost(DataSetRoute + "/insert")]
        public ActionResult InsertJsonRecords(string teamName, string dataSetName, [FromBody] string[] jsonRecords)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavy(dataSetName, ctx.OwnerKey, "InsertJsonRecords", engine =>
            {
                var result = engine.InsertJsonRecords(jsonRecords, null, out string error2);
                if (!result)
                    return BadRequest(error2);
                return Ok();
            }, SystemState.Created, SystemState.Loaded, SystemState.Ready);
        }

        /// <summary>
        /// Loads the jsonData into search engine from the database.
        /// </summary>
        [HttpGet(DataSetRoute + "/LoadFromDatabase")]
        public async Task<ActionResult> LoadFromDatabaseAsync(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;

            var pm = new ProcessMonitor();
            var success = IndxCloudInternalApi.Manager.LoadFromDatabase(dataSetName, ctx.OwnerKey, pm);
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
        [HttpPut(DataSetRoute + "/LoadStream")]
        public ActionResult LoadStreamAsync(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            HttpContext.Request.EnableBuffering();
            if (HttpContext.Request.ContentLength == null || HttpContext.Request.ContentLength == 0)
                return BadRequest("Empty request body, stream missing");
            var bodyStream = HttpContext.Request.Body;
            bodyStream.Position = 0;
            var pm = new ProcessMonitor();
            if (IndxCloudInternalApi.Manager.Load(dataSetName, ctx.OwnerKey, bodyStream, pm))
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
        [HttpPut(DataSetRoute + "/LoadString")]
        public IActionResult LoadString(string teamName, string dataSetName, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            var memoryStream = new MemoryStream();
            using (var writer = new StreamWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(jsonData);
                writer.Flush();
            }
            memoryStream.Position = 0;
            var pm = new ProcessMonitor();
            if (IndxCloudInternalApi.Manager.Load(dataSetName, ctx.OwnerKey, memoryStream, pm))
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
        [HttpPost(DataSetRoute + "/Search")]
        public ActionResult<Indx.Api.Result> Search(string teamName, string dataSetName, [FromBody] CloudQuery query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query == null)
                return BadRequest("Search query body is required");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("Search non existing dataset name");
            if (RequireState(matcher, "Search", SystemState.Ready) is { } stateError)
                return stateError;
            Indx.Api.Result res = IndxCloudInternalApi.Manager.Search(query, dataSetName, ctx.OwnerKey);
            return res;
        }

        /// <summary>
        /// SetFieldConfiguration sets any combination of field properties (Searchable, Filterable,
        /// Facetable, Sortable, WordIndexing, Embeddable, PreloadFilters, Weight, BM25b, BM25k1)
        /// in one call. Nullable properties have replace semantics: null = leave untouched,
        /// any value (including false) = overwrite.
        /// </summary>
        [HttpPut(DataSetRoute + "/SetFieldConfiguration")]
        public IActionResult SetFieldConfiguration(string teamName, string dataSetName, [FromBody] FieldProxy[] fields)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("SearchController.SetFieldConfiguration invalid status");

            // If any proposed change requires rebuilding the index AND the engine is serving
            // searches, route via the shadow-swap path so live searches are not blocked. The
            // override is applied between Init and Load on the shadow so MakeSearchEngines builds
            // _indexableFields against the new Searchable set.
            bool needsReindex = df.RequiresReindex(fields);
            if (needsReindex && matcher.Status.SystemState == SystemState.Ready)
            {
                foreach (var cfg in fields)
                    if (df.GetField(cfg.FieldName) == null)
                        return BadRequest(
                            $"SearchController.SetFieldConfiguration non existing fieldname: {cfg.FieldName}");

                try
                {
                    IndxCloudInternalApi.Manager.RunFieldConfigurationOnShadow(dataSetName, ctx.OwnerKey, fields);
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

        /// <summary>Sets the Searchable property and weight on the specified fields (legacy helper).</summary>
        [HttpPut(DataSetRoute + "/SetSearchableFields")]
        public IActionResult SetSearchableFields(string teamName, string dataSetName, [FromBody] (string Name, float Weight)[] fields)
            => SetFieldFlag(teamName, dataSetName, fields.Select(f => f.Name), (f, t) => { f.Searchable = true; f.Weight = fields.First(x => x.Name == t).Weight; });

        /// <summary>Sets the Filterable property on the specified fields (legacy helper).</summary>
        [HttpPut(DataSetRoute + "/SetFilterableFields")]
        public IActionResult SetFilterableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Filterable = true);

        /// <summary>Sets the Facetable property on the specified fields (legacy helper).</summary>
        [HttpPut(DataSetRoute + "/SetFacetableFields")]
        public IActionResult SetFacetableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Facetable = true);

        /// <summary>Sets the Sortable property on the specified fields (legacy helper).</summary>
        [HttpPut(DataSetRoute + "/SetSortableFields")]
        public IActionResult SetSortableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Sortable = true);

        /// <summary>Sets the WordIndexing property on the specified fields (legacy helper).</summary>
        [HttpPut(DataSetRoute + "/SetWordIndexingFields")]
        public IActionResult SetWordIndexingFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.WordIndexing = true);

        /// <summary>
        /// GetFieldConfiguration returns the full configuration of every field in the dataset.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetFieldConfiguration")]
        public ActionResult<FieldProxy[]> GetFieldConfiguration(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return Array.Empty<FieldProxy>();
            return matcher.GetFieldConfiguration();
        }

        /// <summary>
        /// Updates existing JSON records in the dataset.
        /// </summary>
        [HttpPut(DataSetRoute + "/update")]
        public ActionResult UpdateJsonRecords(string teamName, string dataSetName, [FromBody] string[] jsonRecords)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavy(dataSetName, ctx.OwnerKey, "UpdateJsonRecords", engine =>
            {
                foreach (var jsonData in jsonRecords)
                {
                    var result = engine.UpdateJsonRecord(jsonData, out string error2);
                    if (!result)
                        return BadRequest(error2);
                }
                return Ok();
            }, SystemState.Ready);
        }

        /// <summary>
        /// Updates one single Document.
        /// </summary>
        [HttpPut(DataSetRoute + "/update/{documentKey:long}")]
        public ActionResult UpdateJsonRecord(string teamName, string dataSetName, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("UpdateJsonRecord non existing dataset name");
            if (RequireState(matcher, "UpdateJsonRecord", SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.UpdateJsonRecord(jsonData, out string error2);
            if (!result)
                return BadRequest(error2);
            return Ok();
        }

        /// <summary>
        /// Updates a single field on a document identified by its key.
        /// </summary>
        [HttpPut(DataSetRoute + "/field/{documentKey:long}")]
        public ActionResult UpdateField(string teamName, string dataSetName, long documentKey, [FromBody] UpdateFieldProxy update)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("UpdateField non existing dataset name");
            if (RequireState(matcher, "UpdateField", SystemState.Loaded, SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.UpdateField(documentKey, update.FieldName, UnwrapJsonElement(update.Value)!, out string error2);
            if (!result)
                return BadRequest(error2);
            return Ok();
        }

        /// <summary>
        /// Deletes all documents matching the given filter.
        /// </summary>
        [HttpDelete(DataSetRoute + "/DeleteRecordsInFilter")]
        public ActionResult DeleteRecordsInFilter(string teamName, string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavy(dataSetName, ctx.OwnerKey, "DeleteRecordsInFilter", engine =>
            {
                var filter = engine.GetFilterFromKey(filterProxy.HashString);
                if (filter == null)
                    return BadRequest("DeleteRecordsInFilter invalid filter key");
                engine.LoadFilters(new[] { filter });
                engine.DeleteRecordsInFilter(filter);
                return Ok();
            }, SystemState.Ready);
        }

        /// <summary>
        /// Updates a field on all documents matching the given filter. Returns the number of updated documents.
        /// </summary>
        [HttpPut(DataSetRoute + "/UpdateFieldInFilter")]
        public ActionResult<int> UpdateFieldInFilter(string teamName, string dataSetName, [FromBody] FilterFieldUpdateProxy payload)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            return RunHeavy(dataSetName, ctx.OwnerKey, "UpdateFieldInFilter", engine =>
            {
                var filter = engine.GetFilterFromKey(payload.Filter.HashString);
                if (filter == null)
                    return BadRequest("UpdateFieldInFilter invalid filter key");
                var count = engine.UpdateFieldInFilter(filter, payload.FieldName, UnwrapJsonElement(payload.Value)!, out string error2);
                if (count == 0 && !string.IsNullOrEmpty(error2))
                    return BadRequest(error2);
                return Ok(count);
            }, SystemState.Ready);
        }

        /// <summary>
        /// Deletes a single filter from the filter cache.
        /// </summary>
        [HttpDelete(DataSetRoute + "/DeleteFilter")]
        public ActionResult DeleteFilter(string teamName, string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
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
        [HttpDelete(DataSetRoute + "/DeleteAllFilters")]
        public ActionResult DeleteAllFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("DeleteAllFilters non existing dataset name");
            matcher.DeleteAllFilters();
            return Ok();
        }

        /// <summary>
        /// Pre-loads all registered filters in the background.
        /// </summary>
        [HttpPost(DataSetRoute + "/LoadAllFilters")]
        public ActionResult LoadAllFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("LoadAllFilters non existing dataset name");
            if (RequireState(matcher, "LoadAllFilters", SystemState.Loaded, SystemState.Indexing, SystemState.Ready) is { } stateError)
                return stateError;
            matcher.LoadAllFilters();
            return Ok();
        }

        /// <summary>
        /// Returns the number of filters currently registered in the filter cache.
        /// </summary>
        [HttpGet(DataSetRoute + "/GetNumberOfFilters")]
        public ActionResult<int> GetNumberOfFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("GetNumberOfFilters non existing dataset name");
            return Ok(matcher.NumberOfFilters);
        }

        /// <summary>
        /// Hibernates the dataset, freeing in-memory structures while retaining persisted data.
        /// </summary>
        [HttpPut(DataSetRoute + "/Hibernate")]
        public ActionResult Hibernate(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("Hibernate non existing dataset name");
            if (RequireState(matcher, "Hibernate", SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.Hibernate(out string errorMessage);
            if (!result)
                return BadRequest(errorMessage);
            return Ok();
        }

        /// <summary>
        /// Wakes up a hibernated dataset, restoring it from the persisted state.
        /// </summary>
        [HttpPut(DataSetRoute + "/WakeUp")]
        public ActionResult WakeUp(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("WakeUp non existing dataset name");
            if (RequireState(matcher, "WakeUp", SystemState.Hibernated) is { } stateError)
                return stateError;
            var result = matcher.WakeUp();
            if (!result)
                return BadRequest("WakeUp failed");
            return Ok();
        }

        /// <summary>
        /// Marks the specified fields as embeddable so that their vector values are indexed
        /// during the next Load. Must be called after AnalyzeStream and before LoadStream.
        /// </summary>
        [HttpPut(DataSetRoute + "/SetEmbeddableFields")]
        public IActionResult SetEmbeddableFields(string teamName, string dataSetName, [FromBody] string[] fields)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            // No lifecycle-state guard: SetEmbeddableFields is idempotent and may legitimately be
            // re-sent on an already-Ready dataset (it operates on DocumentFields, which the manager
            // null-checks). Adding a Created-only guard would wrongly reject that idempotent re-send.
            if (!IndxCloudInternalApi.Manager.SetEmbeddableFields(fields, dataSetName, ctx.OwnerKey))
                return BadRequest("SetEmbeddableFields failed — dataset not found or unknown field name");
            return Ok();
        }

        /// <summary>
        /// Searches a single embedding field using approximate nearest-neighbour search.
        /// </summary>
        [HttpPost(DataSetRoute + "/VectorSearch")]
        public ActionResult<Indx.CloudApi.EmbeddingResultEntry[]> VectorSearch(
            string teamName, string dataSetName, [FromBody] Indx.CloudApi.VectorQueryProxy query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query?.Vector == null || query.Vector.Length == 0)
                return BadRequest("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.FieldName))
                return BadRequest("FieldName is required");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("VectorSearch non existing dataset name");
            if (RequireState(matcher, "VectorSearch", SystemState.Ready) is { } stateError)
                return stateError;
            return IndxCloudInternalApi.Manager.VectorSearch(query, dataSetName, ctx.OwnerKey);
        }

        /// <summary>
        /// Combines text search with embedding nearest-neighbour search and blends scores.
        /// </summary>
        [HttpPost(DataSetRoute + "/HybridSearch")]
        public ActionResult<Indx.CloudApi.EmbeddingResultEntry[]> HybridSearch(
            string teamName, string dataSetName, [FromBody] Indx.CloudApi.HybridQueryProxy query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query?.Vector == null || query.Vector.Length == 0)
                return BadRequest("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.EmbeddingField))
                return BadRequest("EmbeddingField is required");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("HybridSearch non existing dataset name");
            if (RequireState(matcher, "HybridSearch", SystemState.Ready) is { } stateError)
                return stateError;
            return IndxCloudInternalApi.Manager.HybridSearch(query, dataSetName, ctx.OwnerKey);
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Resolves the team from the route and the JWT user id, enforcing membership and the
        /// requested permission level. On failure sets <paramref name="error"/> to the response
        /// to return (401 if unauthenticated, 403 if not a member or insufficient role) and
        /// returns null.
        /// </summary>
        private TeamContext? ResolveTeam(string teamName, out ActionResult? error, bool write = false, bool admin = false)
        {
            error = null;
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) { error = Unauthorized(); return null; }

            var ctx = resolver.Resolve(teamName, userId);
            if (ctx == null) { error = StatusCode(StatusCodes.Status403Forbidden, "Not a member of this team, or team does not exist"); return null; }
            if (admin && !TeamRoles.CanAdmin(ctx.Role)) { error = StatusCode(StatusCodes.Status403Forbidden, "Team Admin role required"); return null; }
            if (write && !TeamRoles.CanWrite(ctx.Role)) { error = StatusCode(StatusCodes.Status403Forbidden, "Team Editor role required"); return null; }
            return ctx;
        }

        /// <summary>
        /// Runs a heavy mutation against the team's dataset, using the shadow-swap path when the
        /// engine is Ready so live searches are not blocked. Maps not-found to 400 and a concurrent
        /// shadow build to 409.
        /// </summary>
        private ActionResult RunHeavy(
            string dataSetName,
            string teamId,
            string operationName,
            Func<ICloudSearchEngine, ActionResult> mutation,
            params SystemState[] allowed)
        {
            var engine = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, teamId);
            if (engine == null)
                return BadRequest($"{operationName} non existing dataset name");
            if (allowed.Length > 0 && RequireState(engine, operationName, allowed) is { } stateError)
                return stateError;
            try
            {
                return IndxCloudInternalApi.Manager.RunHeavyOnShadowIfReady(dataSetName, teamId, mutation);
            }
            catch (KeyNotFoundException)
            {
                return BadRequest($"{operationName} non existing dataset name");
            }
            catch (ShadowBusyException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, ex.Message);
            }
        }

        /// <summary>
        /// Lifecycle-state guard. Returns <c>null</c> when <paramref name="engine"/> is in one of the
        /// <paramref name="allowed"/> states, otherwise a 409 ProblemDetails describing the current
        /// state and how to proceed. Call AFTER the null-resolve check and BEFORE using the engine, so
        /// non-existing datasets stay 400 and only a genuine wrong lifecycle state becomes 409.
        /// </summary>
        private ActionResult? RequireState(ICloudSearchEngine engine, string operation, params SystemState[] allowed)
        {
            var status = engine.Status;
            if (allowed.Contains(status.SystemState))
                return null;

            var state = status.SystemState;
            bool retryable = state is SystemState.Loading or SystemState.Indexing;
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Dataset not in a valid state for this operation",
                Detail = $"{operation} requires the dataset to be {string.Join("/", allowed)}; " +
                         $"it is currently {state}. {StateGuidance(state, status)}"
            };
            problem.Extensions["operation"] = operation;
            problem.Extensions["currentState"] = state.ToString();
            problem.Extensions["allowedStates"] = allowed.Select(s => s.ToString()).ToArray();
            problem.Extensions["retryable"] = retryable;
            if (state == SystemState.Error && !string.IsNullOrEmpty(status.ErrorMessage))
                problem.Extensions["errorMessage"] = status.ErrorMessage;
            if (retryable)
                Response.Headers["Retry-After"] = "2";
            return Conflict(problem);
        }

        /// <summary>Per-state, operation-independent guidance shown in the 409 body.</summary>
        private static string StateGuidance(SystemState state, SystemStatus status) => state switch
        {
            SystemState.Created => "The dataset is created but not loaded. Call Load (LoadStream/LoadString/LoadFromDatabase) and then IndexDataSet.",
            SystemState.Loading => "Loading is in progress — retry once the dataset reaches Ready.",
            SystemState.Loaded => "The dataset is loaded but not indexed. Call IndexDataSet.",
            SystemState.Indexing => "Indexing is in progress — retry once the dataset reaches Ready.",
            SystemState.Hibernated => "The dataset is hibernated. Call WakeUp.",
            SystemState.Error => $"The dataset is in an error state: {status.ErrorMessage}",
            _ => ""
        };

        /// <summary>Shared body for the legacy Set*Fields helpers — resolve, validate, mutate each field.</summary>
        private IActionResult SetFieldFlag(string teamName, string dataSetName, IEnumerable<string> fieldNames, Action<Indx.Api.Field, string> apply)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return BadRequest("invalid dataSetName");
            var matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return BadRequest("non existing dataSetName");
            var df = matcher.DocumentFields;
            if (df == null)
                return BadRequest("invalid status");
            foreach (var name in fieldNames)
            {
                var f = df.GetField(name);
                if (f == null)
                    return BadRequest($"non existing fieldname: {name}");
                apply(f, name);
            }
            return Ok();
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

#pragma warning disable 1591
    /// <summary>Dataset entry returned by GetMyDataSets — the owning team and the caller's role.</summary>
    public record DataSetListDto(string Name, string TeamName, string Role);
#pragma warning restore 1591
}
