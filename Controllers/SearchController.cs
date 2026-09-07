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
    [ApiVersion("2.0-beta")]
    [Route("api")]
    [ApiController]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [EnableCors("NewPolicy")]
    // Every error is an RFC 9457 ProblemDetails (application/problem+json) carrying a
    // machine-readable "code" extension — declared here so the OpenAPI spec is honest for every
    // endpoint: 400 (invalidArgument / invalidDatasetName / loadFailed / operationFailed),
    // 403 (insufficientRole), 404 (datasetNotFound / documentNotFound / teamNotFound), and
    // 409 (invalidState with currentState/allowedStates/retryable + Retry-After header when
    // retryable, or shadowBusy). 401 comes body-less from the JWT middleware.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public class SearchController(TeamContextResolver resolver, TeamService teams, BoostRuleStore boostStore, IEditionService edition) : Controller
    {
        private const string DataSetRoute = "teams/{teamName}/datasets/{dataSetName}";

        #region Public Methods
        /// <summary>
        /// As Analyze but handles a stream as input text.
        /// </summary>
        [HttpPost(DataSetRoute + "/analyze")]
        public async Task<ActionResult<SystemStatus>> AnalyzeStreamAsync(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            HttpContext.Request.EnableBuffering();
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngineForInit(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var state = matcher.Status;
            if (state.InvalidDataSetName)
                return ApiProblems.InvalidDatasetName(dataSetName);
            var pm = new ProcessMonitor();
            matcher.Init(HttpContext.Request.Body, pm);
            pm.WaitForCompletion();
            if (!pm.Succeeded)
                return ApiProblems.LoadFailed("Analyze failed - the request body is not parseable JSON.");
            if (matcher.DocumentFields == null)
                return ApiProblems.OperationFailed("Analyze did not produce a field set.");
            if (matcher.Persistence == null)
                return ApiProblems.OperationFailed("The dataset has no storage attached, so the analyzed fields could not be saved.");
            matcher.Persistence.SaveDocumentFields(matcher.DocumentFields.GetSerialized());
            return Ok(state);
        }

        /// <summary>
        /// Analyze the fields of a string containing json. Since json may be invalid, which will cause
        /// a 400 error, it is sent as plain text.
        /// </summary>
        [RequestSizeLimit(2_000_000_000)]
        [HttpPost(DataSetRoute + "/analyze/text")]
        public ActionResult<SystemStatus> AnalyzeString(string teamName, string dataSetName, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (string.IsNullOrEmpty(jsonData))
                return ApiProblems.InvalidArgument("null or empty jsonData argument");
            var state = IndxCloudInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (state == null || state.InvalidDataSetName)
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngineForInit(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var df = DocumentFields.Analyze(jsonData, out string error2);
            if (!string.IsNullOrEmpty(error2) || df == null)
                return ApiProblems.InvalidArgument(error2);
            matcher.SetDocumentFieldsInternal(df);
            if (matcher.Persistence == null)
                return ApiProblems.OperationFailed("The dataset has no storage attached, so the analyzed fields could not be saved.");
            matcher.Persistence.SaveDocumentFields(df.GetSerialized());
            return state;
        }

        /// <summary>
        /// CombineFilters will combine two filters using AND or OR operation.
        /// </summary>
        [HttpPost(DataSetRoute + "/filters/combine")]
        public ActionResult<FilterProxy> CombineFilters(string teamName, string dataSetName, [FromBody] CombinedFilterProxy combineFilters)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
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
        [HttpPost(DataSetRoute + "/boosts/from-filter")]
        public ActionResult<BoostProxy> CreateBoost(string teamName, string dataSetName, [FromBody] BoostProxy boost)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateBoost", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = matcher.GetFilterFromKey(boost.FilterProxy.HashString);
            if (filter == null)
                return ApiProblems.InvalidArgument("Unknown filter key. Create the filter first, then reference it by the returned key.");
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
                return ApiProblems.InvalidDatasetName(dataSetName);
            return boostStore.Load(ctx.OwnerKey, dataSetName).ToArray();
        }

        /// <summary>
        /// Replaces the dataset's boost rules (whole list). Each rule needs a name, at least one
        /// condition, and every condition must reference a Filterable field and be either a value
        /// match or a numeric range (not both).
        /// </summary>
        [HttpPut(DataSetRoute + "/boosts")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetBoostRules(string teamName, string dataSetName, [FromBody] BoostRule[] rules)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            if (rules == null)
                return ApiProblems.InvalidArgument("rules body is required");

            // Best-effort field validation when the engine is loaded; never hard-fail on a
            // hibernated dataset (apply-time skips unbuildable conditions anyway).
            var fields = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey)?.DocumentFields;
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Name))
                    return ApiProblems.InvalidArgument("each rule needs a name");
                if (rule.Conditions == null || rule.Conditions.Count == 0)
                    return ApiProblems.InvalidArgument($"rule '{rule.Name}' needs at least one condition");
                foreach (var c in rule.Conditions)
                {
                    var hasValue = !string.IsNullOrEmpty(c.Value);
                    var hasRange = c.Min.HasValue || c.Max.HasValue;
                    if (hasValue == hasRange)
                        return ApiProblems.InvalidArgument($"condition on '{c.Field}' must be either a value or a range");
                    if (fields != null && fields.GetField(c.Field) is not { Filterable: true })
                        return ApiProblems.InvalidArgument($"field '{c.Field}' is not filterable");
                }
            }

            // Boost-rule scheduling is a gated feature (Free Managed can't schedule). Strip any
            // schedule window when it's disabled — defensive (the UI hides the control) and
            // self-healing on a plan downgrade.
            if (!edition.IsEnabled(EditionFeature.BoostRuleScheduling))
                foreach (var rule in rules) { rule.ActiveFrom = null; rule.ActiveUntil = null; }

            boostStore.Save(ctx.OwnerKey, dataSetName, rules);
            return NoContent();
        }

        /// <summary>Clears all boost rules for the dataset.</summary>
        [HttpDelete(DataSetRoute + "/boosts")]
        public IActionResult DeleteBoostRules(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            boostStore.Delete(ctx.OwnerKey, dataSetName);
            return NoContent();
        }

        /// <summary>
        /// Creates the data set (201). Idempotent: an existing data set is left exactly as it is and
        /// answers 200.
        /// <para>
        /// There is no longer a <c>configuration</c> query parameter — there was one configuration to
        /// choose from, so choosing was theatre. Clients that still send <c>?configuration=400</c>
        /// (every published <c>@indxsearch/intrface</c> does) are unaffected: a query value that binds
        /// to nothing is ignored, which <c>UnknownQueryParameterTests</c> pins. Note the one
        /// behaviour change that comes with it — a garbage value used to be a validation 400 from
        /// enum binding and is now simply ignored, which is safe only because the value no longer
        /// reaches anything.
        /// </para>
        /// </summary>
        [HttpPut(DataSetRoute)]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult CreateOrOpen(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            using var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, dataSetName, ctx.OwnerKey);
            if (persistence.DataSetExists())
                return Ok();
            // Still writes 400. The column is kept for the serialized configuration it will hold, and
            // until then ResolveConfiguration reads 400 as ConfigurationParameters.Default — so this
            // and that have to keep agreeing. ConfigurationResolutionTests asserts they do.
            persistence.CreateOrOpenDataSet(IndxCloudInternalApi.DefaultConfigurationNumber);
            return StatusCode(StatusCodes.Status201Created);
        }

        /// <summary>
        /// CreateRangeFilter will create a RangeFilter which may be passed to any search.
        /// </summary>
        [HttpPost(DataSetRoute + "/filters/range")]
        public ActionResult<FilterProxy> CreateRangeFilter(string teamName, string dataSetName, [FromBody] RangeFilterProxy rangeFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateRangeFilter", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = matcher.CreateRangeFilter(rangeFilter.FieldName, rangeFilter.LowerLimit, rangeFilter.UpperLimit, out var filterError);
            if (filter == null)
                return ApiProblems.InvalidArgument(filterError ?? "invalid filter arguments");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// CreateValueFilter will create a ValueFilter which may be passed to any search.
        /// </summary>
        [HttpPost(DataSetRoute + "/filters/value")]
        public ActionResult<FilterProxy> CreateValueFilter(string teamName, string dataSetName, [FromBody] ValueFilterProxy valueFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateValueFilter", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = matcher.CreateValueFilter(valueFilter.FieldName, valueFilter.Value, out var filterError);
            if (filter == null)
                return ApiProblems.InvalidArgument(filterError ?? "invalid filter arguments");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// DeleteDataSet, will delete the entire dataSet including all contained Documents.
        /// Requires team Admin.
        /// </summary>
        [HttpDelete(DataSetRoute)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult DeleteDataSet(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, admin: true);
            if (ctx == null) return error!;
            if (!IndxCloudInternalApi.Manager.DeleteDataSet(dataSetName, ctx.OwnerKey))
                return ApiProblems.DatasetNotFound(dataSetName);
            return NoContent();
        }

        /// <summary>
        /// Deletes a document from the dataset by its key.
        /// </summary>
        [HttpDelete(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteJsonRecord(string teamName, string dataSetName, long documentKey)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "DeleteJsonRecord", SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.DeleteJsonRecord(documentKey);
            if (!result)
                return ApiProblems.DocumentNotFound(documentKey);
            return NoContent();
        }

        /// <summary>
        /// Deletes documents from the dataset by their keys.
        /// </summary>
        [HttpDelete(DataSetRoute + "/documents")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteJsonRecords(string teamName, string dataSetName, [FromBody] long[] documentKeys)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "DeleteJsonRecords", engine =>
            {
                // All-or-nothing: validate every key before deleting anything, so one bad
                // key can't leave the batch half-deleted.
                var missing = documentKeys.Where(k => string.IsNullOrEmpty(engine.GetJsonDataOfKey(k))).ToArray();
                if (missing.Length > 0)
                    return ApiProblems.DocumentsNotFound(missing);
                foreach (var documentKey in documentKeys)
                {
                    var result = engine.DeleteJsonRecord(documentKey);
                    if (!result)
                        return ApiProblems.DocumentNotFound(documentKey);
                }
                return NoContent();
            }, SystemState.Ready);
        }

        /// <summary>
        /// GetAllFields will return the fields found during analyze.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields")]
        public ActionResult<string[]> GetAllFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, true, false, false, false, false, false);
        }

        /// <summary>
        /// GetFacetableFields will return the array of facetable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields/facetable")]
        public ActionResult<string[]> GetFacetableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, false, true, false);
        }

        /// <summary>
        /// GetFilterableFields will return the array of filterable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields/filterable")]
        public ActionResult<string[]> GetFilterableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, true, false, false);
        }

        /// <summary>
        /// Returns the raw json records as string[] for the keys.
        /// </summary>
        [HttpPost(DataSetRoute + "/documents/lookup")]
        public ActionResult<string[]> GetJson(string teamName, string dataSetName, [FromBody] long[] keys)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            ICloudSearchEngine? engine = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null)
                return ApiProblems.DatasetNotFound(dataSetName);
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
        [HttpGet(DataSetRoute + "/documents/count")]
        public ActionResult<CountResponse> GetNumberOfJsonRecordsInDb(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var engine = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return Ok(new CountResponse(engine.Persistence?.NumberOfJsonRecords() ?? 0));
        }

        /// <summary>
        /// GetSearchableFields will return the array of searchable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields/searchable")]
        public ActionResult<string[]> GetSearchableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, true, false, false, false, false);
        }

        /// <summary>
        /// GetSortableFields will return the array of sortable field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields/sortable")]
        public ActionResult<string[]> GetSortableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxCloudInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, true, false, false, false);
        }

        /// <summary>
        /// GetStatus will return the status of the dataSetName in the search engine.
        /// </summary>
        [HttpGet(DataSetRoute + "/status")]
        public ActionResult<CloudSystemStatus> GetStatus(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var status = IndxCloudInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (status == null)
                return ApiProblems.DatasetNotFound(dataSetName);

            return new CloudSystemStatus(status)
            {
                ShadowBuildInProgress = IndxCloudInternalApi.Manager.IsShadowBuildInProgress(dataSetName, ctx.OwnerKey),
                ShadowBuildStartedUtc = IndxCloudInternalApi.Manager.ShadowBuildStartedUtc(dataSetName, ctx.OwnerKey),
            };
        }

        /// <summary>
        /// GetWordIndexingFields will return the array of word-indexing field names.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields/word-indexing")]
        public ActionResult<string[]> GetWordIndexingFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
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
        [HttpPost(DataSetRoute + "/index")]
        [ProducesResponseType(typeof(SystemStatus), StatusCodes.Status202Accepted)]
        public IActionResult IndexDataSet(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;

            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);

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
                    return ApiProblems.ShadowBusy(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    // Same mapping as SetFieldConfiguration's shadow path — a failed shadow
                    // build is a clean 400, not an unhandled 500.
                    return ApiProblems.OperationFailed(ex.Message);
                }
            }
            else if (!IndxCloudInternalApi.Manager.DoIndex(dataSetName, ctx.OwnerKey))
            {
                return ApiProblems.OperationFailed("Indexing could not be started.");
            }

            var status = IndxCloudInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (status == null)
                return ApiProblems.OperationFailed("Indexing did not report a status.");
            // The work continues in the background — 202 with the current status;
            // poll GET status until Ready.
            return Accepted(status);
        }

        /// <summary>
        /// Inserts one single Json record. The route key must match the document's key field
        /// (the engine keys documents from the body, so a disagreeing route would otherwise
        /// silently insert under a different key than the URL claims).
        /// </summary>
        [HttpPost(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        public ActionResult InsertJsonRecord(string teamName, string dataSetName, long documentKey, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "InsertJsonRecord", SystemState.Created, SystemState.Loaded, SystemState.Ready) is { } stateError)
                return stateError;
            if (TryReadBodyKey(matcher, jsonData, out long bodyKey) && bodyKey != documentKey)
                return ApiProblems.InvalidArgument(
                    $"The route addresses document {documentKey} but the body's key field says {bodyKey}. Nothing was inserted.");
            var result = matcher.InsertJsonRecord(jsonData, out string error2);
            if (!result)
                return ApiProblems.InvalidArgument(error2);
            return StatusCode(StatusCodes.Status201Created);
        }

        /// <summary>
        /// Inserts new JSON records into the dataset.
        /// </summary>
        [HttpPost(DataSetRoute + "/documents")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        public ActionResult InsertJsonRecords(string teamName, string dataSetName, [FromBody] string[] jsonRecords)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "InsertJsonRecords", engine =>
            {
                var result = engine.InsertJsonRecords(jsonRecords, null, out string error2);
                if (!result)
                    return ApiProblems.InvalidArgument(error2);
                return StatusCode(StatusCodes.Status201Created);
            }, SystemState.Created, SystemState.Loaded, SystemState.Ready);
        }

        /// <summary>
        /// Loads the jsonData into search engine from the database.
        /// </summary>
        [HttpPost(DataSetRoute + "/load/from-database")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> LoadFromDatabaseAsync(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;

            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var pm = new ProcessMonitor();
            var success = IndxCloudInternalApi.Manager.LoadFromDatabase(dataSetName, ctx.OwnerKey, pm);
            if (!success)
                return ApiProblems.LoadFailed("LoadFromDatabase failed.");
            await pm.WaitForCompletionAsync();
            if (!pm.Succeeded)
                return ApiProblems.LoadFailed(pm.ErrorMessage);
            return NoContent();
        }

        /// <summary>
        /// Loads the jsonData into search engine as a stream.
        /// </summary>
        [HttpPost(DataSetRoute + "/load")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult LoadStreamAsync(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            HttpContext.Request.EnableBuffering();
            if (HttpContext.Request.ContentLength == null || HttpContext.Request.ContentLength == 0)
                return ApiProblems.InvalidArgument("Empty request body, stream missing");
            var bodyStream = HttpContext.Request.Body;
            bodyStream.Position = 0;

            // In-place load: streams straight into the engine (peak memory ~1× — no second copy). A
            // failed load is non-destructive on disk (the lib's clear+append is atomic) and leaves the
            // dataset recoverable (Error → WakeUp/reload). For zero-downtime updates, use replace.
            var pm = new ProcessMonitor();
            if (IndxCloudInternalApi.Manager.Load(dataSetName, ctx.OwnerKey, bodyStream, pm))
                pm.WaitForCompletion();
            else
                return ApiProblems.LoadFailed("LoadStream failed.");
            if (!pm.Succeeded)
                return ApiProblems.LoadFailed(IndxCloudInternalApi.Manager.DescribeLoadFailure(dataSetName, ctx.OwnerKey, pm.ErrorMessage));

            return NoContent();
        }

        /// <summary>
        /// Atomically replaces the entire dataset's documents with the streamed JSON. Unlike
        /// delete+recreate this preserves the dataset's identity, field configuration, boost rules
        /// and description, serves the old data with zero downtime until the new index is ready, and
        /// leaves the old dataset untouched if the new JSON fails to build. Works whether the dataset
        /// is Ready, hibernated or idle-evicted (the old documents are never reloaded). Returns a
        /// summary of how the new schema differed from the previous field configuration.
        /// </summary>
        [RequestSizeLimit(2_000_000_000)]
        [HttpPost(DataSetRoute + "/replace")]
        public ActionResult<ReplaceSchemaChange> Replace(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            HttpContext.Request.EnableBuffering();
            if (HttpContext.Request.ContentLength is null or 0)
                return ApiProblems.InvalidArgument("Empty request body, stream missing");
            var bodyStream = HttpContext.Request.Body;
            bodyStream.Position = 0;
            try
            {
                var summary = IndxCloudInternalApi.Manager.RunReplaceFromJson(dataSetName, ctx.OwnerKey, bodyStream);
                return Ok(summary);
            }
            catch (ShadowBusyException ex)
            {
                return ApiProblems.ShadowBusy(ex.Message);
            }
            catch (DataSetNotFoundException)
            {
                return ApiProblems.DatasetNotFound(dataSetName);
            }
            catch (Exception ex)
            {
                // Build failed (bad JSON / index error) — old dataset is unchanged.
                return ApiProblems.LoadFailed(ex.Message);
            }
        }

        /// <summary>
        /// Loads the jsonData into search engine as a string.
        /// </summary>
        [RequestSizeLimit(2_000_000_000)]
        [HttpPost(DataSetRoute + "/load/text")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult LoadString(string teamName, string dataSetName, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
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
                return ApiProblems.LoadFailed("LoadString failed.");
            if (!pm.Succeeded)
                return ApiProblems.LoadFailed(IndxCloudInternalApi.Manager.DescribeLoadFailure(dataSetName, ctx.OwnerKey, pm.ErrorMessage));
            return NoContent();
        }

        /// <summary>
        /// Search will validate the search query and return the search result.
        /// </summary>
        [HttpPost(DataSetRoute + "/search")]
        public ActionResult<Indx.Api.Result> Search(string teamName, string dataSetName, [FromBody] CloudQuery query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query == null)
                return ApiProblems.InvalidArgument("Search query body is required");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
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
        [HttpPut(DataSetRoute + "/fields/configuration")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetFieldConfiguration(string teamName, string dataSetName, [FromBody] FieldProxy[] fields)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var df = matcher.DocumentFields;
            if (df == null)
                return ApiProblems.InvalidArgument("The dataset has not been analyzed yet, so there are no fields to configure.");

            // If any proposed change requires rebuilding the index AND the engine is serving
            // searches, route via the shadow-swap path so live searches are not blocked. The
            // override is applied between Init and Load on the shadow so MakeSearchEngines builds
            // _indexableFields against the new Searchable set.
            bool needsReindex = df.RequiresReindex(fields);
            if (needsReindex && matcher.Status.SystemState == SystemState.Ready)
            {
                foreach (var cfg in fields)
                    if (df.GetField(cfg.FieldName) == null)
                        return ApiProblems.InvalidArgument(
                            $"Field '{cfg.FieldName}' does not exist in this dataset.");

                try
                {
                    IndxCloudInternalApi.Manager.RunFieldConfigurationOnShadow(dataSetName, ctx.OwnerKey, fields);
                    return NoContent();
                }
                catch (ShadowBusyException ex)
                {
                    return ApiProblems.ShadowBusy(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return ApiProblems.OperationFailed(ex.Message);
                }
            }

            // Inline: only query-time flags changed, or engine is not yet Ready.
            var failed = matcher.SetFieldConfiguration(fields);
            if (failed != null)
                return ApiProblems.InvalidArgument($"Field '{failed}' does not exist in this dataset.");
            return NoContent();
        }

        /// <summary>Sets the Searchable property and weight on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/searchable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSearchableFields(string teamName, string dataSetName, [FromBody] (string Name, float Weight)[] fields)
            => SetFieldFlag(teamName, dataSetName, fields.Select(f => f.Name), (f, t) => { f.Searchable = true; f.Weight = fields.First(x => x.Name == t).Weight; });

        /// <summary>Sets the Filterable property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/filterable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetFilterableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Filterable = true);

        /// <summary>Sets the Facetable property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/facetable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetFacetableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Facetable = true);

        /// <summary>Sets the Sortable property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/sortable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSortableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Sortable = true);

        /// <summary>Sets the WordIndexing property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/word-indexing")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetWordIndexingFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.WordIndexing = true);

        /// <summary>
        /// GetFieldConfiguration returns the full configuration of every field in the dataset.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields/configuration")]
        public ActionResult<FieldProxy[]> GetFieldConfiguration(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return matcher.GetFieldConfiguration();
        }

        /// <summary>
        /// Returns the dataset's synonym list, or a <c>null</c> body when it has none — always 200.
        /// The list widens every search on this dataset: a query word that matches an entry gets that
        /// entry's terms appended to the query text before scoring.
        /// <para>One list per dataset — there is no name to supply, and no other dataset is affected.</para>
        /// </summary>
        [HttpGet(DataSetRoute + "/synonyms")]
        [ProducesResponseType(typeof(SynonymList), StatusCodes.Status200OK)]
        public ActionResult<SynonymList?> GetSynonymList(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);

            // JsonResult, not Ok(): Ok(null) is turned into 204 with an empty body by
            // HttpNoContentOutputFormatter, and an empty body makes the idiomatic client call
            // (GetFromJsonAsync<SynonymList>) throw instead of yielding null. This writes a literal
            // JSON null with 200, so "no list" and "here is the list" are one code path for callers.
            return new JsonResult(IndxCloudInternalApi.Manager.GetSynonyms(dataSetName, ctx.OwnerKey));
        }

        /// <summary>
        /// Sets the dataset's synonym list, replacing any list it already had. Send a body of
        /// <c>null</c> to remove the list — the dataset then searches without synonyms again.
        /// <para>Takes effect on the very next search: synonyms are applied to the query text, so
        /// nothing is re-indexed and the dataset does not have to be in any particular state. The
        /// list is stored with the dataset, so it survives a restart and follows the dataset if it
        /// is transferred to another team.</para>
        /// <para>Note that widening a query lowers Coverage scores in proportion to how much text is
        /// added — see the remarks on <c>Indx.Api.SynonymList</c>.</para>
        /// </summary>
        [HttpPut(DataSetRoute + "/synonyms")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSynonymList(string teamName, string dataSetName, [FromBody] SynonymList? list)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);

            if (!IndxCloudInternalApi.Manager.SetSynonyms(dataSetName, ctx.OwnerKey, list))
                return ApiProblems.OperationFailed(
                    "The synonym list could not be stored — the dataset has no storage attached.");
            return NoContent();
        }

        /// <summary>
        /// Returns the dataset's declared key field — the JSON field whose value identifies each
        /// document (the primary key). Empty string means none is declared (the engine auto-generates
        /// keys). Required, when set, to be a numeric field.
        /// </summary>
        [HttpGet(DataSetRoute + "/fields/key")]
        public ActionResult<string> GetKeyField(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxCloudInternalApi.Manager.GetDeclaredKeyField(dataSetName, ctx.OwnerKey);
        }

        /// <summary>
        /// Declares the dataset's key field (the JSON field whose value is each document's primary key).
        /// Pass an empty string to clear it (auto-generated keys). The field must be numeric. The choice
        /// is preserved across reloads and applied on the next Load/replace; documents already loaded
        /// keep their existing keys until the data is reloaded.
        /// </summary>
        [HttpPut(DataSetRoute + "/fields/key")]
        public IActionResult SetKeyField(string teamName, string dataSetName, [FromBody] string fieldName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);

            var failure = IndxCloudInternalApi.Manager.SetKeyField(
                dataSetName, ctx.OwnerKey, fieldName ?? "", out var needsReloadToReKey);
            if (failure != null)
                return ApiProblems.InvalidArgument(failure);
            return Ok(new { keyField = fieldName ?? "", needsReloadToReKey });
        }

        /// <summary>
        /// Updates existing JSON records in the dataset.
        /// </summary>
        [HttpPut(DataSetRoute + "/documents")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult UpdateJsonRecords(string teamName, string dataSetName, [FromBody] string[] jsonRecords)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "UpdateJsonRecords", engine =>
            {
                // The engine's batch update validates every record's key before mutating
                // anything, so a bad record rejects the whole batch instead of leaving it
                // half-applied (the old per-record loop aborted mid-way). Records whose key
                // matches no live document are skipped, by the engine's batch contract.
                var result = engine.UpdateJsonRecords(jsonRecords, null, out string error2);
                if (!result)
                    return ApiProblems.InvalidArgument(error2);
                return NoContent();
            }, SystemState.Ready);
        }

        /// <summary>
        /// Updates one single Document. The route key must address an existing document and
        /// match the document's key field (the engine keys documents from the body, so a
        /// disagreeing route would otherwise silently update a different document than the
        /// URL claims).
        /// </summary>
        [HttpPut(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult UpdateJsonRecord(string teamName, string dataSetName, long documentKey, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "UpdateJsonRecord", SystemState.Ready) is { } stateError)
                return stateError;
            if (string.IsNullOrEmpty(matcher.GetJsonDataOfKey(documentKey)))
                return ApiProblems.DocumentNotFound(documentKey);
            if (TryReadBodyKey(matcher, jsonData, out long bodyKey) && bodyKey != documentKey)
                return ApiProblems.InvalidArgument(
                    $"The route addresses document {documentKey} but the body's key field says {bodyKey}. Nothing was updated.");
            var result = matcher.UpdateJsonRecord(jsonData, out string error2);
            if (!result)
            {
                // The engine skips records whose key matches no live document; for a single
                // update that means the addressed document is gone (deleted tombstone).
                if (error2.EndsWith("no valid records to update"))
                    return ApiProblems.DocumentNotFound(documentKey);
                return ApiProblems.InvalidArgument(error2);
            }
            return NoContent();
        }

        /// <summary>
        /// Updates a single field on a document identified by its key.
        /// </summary>
        [HttpPatch(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult UpdateField(string teamName, string dataSetName, long documentKey, [FromBody] UpdateFieldProxy update)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "UpdateField", SystemState.Loaded, SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.UpdateField(documentKey, update.FieldName, UnwrapJsonElement(update.Value)!, out string error2);
            if (!result)
                return ApiProblems.InvalidArgument(error2);
            return NoContent();
        }

        /// <summary>
        /// Deletes all documents matching the given filter.
        /// </summary>
        [HttpPost(DataSetRoute + "/documents/delete-by-filter")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteRecordsInFilter(string teamName, string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "DeleteRecordsInFilter", engine =>
            {
                var filter = engine.GetFilterFromKey(filterProxy.HashString);
                if (filter == null)
                    return ApiProblems.InvalidArgument("Unknown filter key. Create the filter first, then reference it by the returned key.");
                engine.LoadFilters(new[] { filter });
                engine.DeleteRecordsInFilter(filter);
                return NoContent();
            }, SystemState.Ready);
        }

        /// <summary>
        /// Updates a field on all documents matching the given filter. Returns the number of updated documents.
        /// </summary>
        [HttpPost(DataSetRoute + "/documents/update-by-filter")]
        public ActionResult<CountResponse> UpdateFieldInFilter(string teamName, string dataSetName, [FromBody] FilterFieldUpdateProxy payload)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "UpdateFieldInFilter", engine =>
            {
                var filter = engine.GetFilterFromKey(payload.Filter.HashString);
                if (filter == null)
                    return ApiProblems.InvalidArgument("Unknown filter key. Create the filter first, then reference it by the returned key.");
                var count = engine.UpdateFieldInFilter(filter, payload.FieldName, UnwrapJsonElement(payload.Value)!, out string error2);
                if (count == 0 && !string.IsNullOrEmpty(error2))
                    return ApiProblems.InvalidArgument(error2);
                return Ok(new CountResponse(count));
            }, SystemState.Ready);
        }

        /// <summary>
        /// Deletes a single filter from the filter cache.
        /// </summary>
        [HttpPost(DataSetRoute + "/filters/delete")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteFilter(string teamName, string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var filter = matcher.GetFilterFromKey(filterProxy.HashString);
            if (filter == null)
                return ApiProblems.InvalidArgument("Unknown filter key. Create the filter first, then reference it by the returned key.");
            var result = matcher.DeleteFilter(filter);
            if (!result)
                return ApiProblems.InvalidArgument("The filter is not registered on this dataset (it may already have been deleted).");
            return NoContent();
        }

        /// <summary>
        /// Deletes all filters from the filter cache.
        /// </summary>
        [HttpDelete(DataSetRoute + "/filters")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteAllFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            matcher.DeleteAllFilters();
            return NoContent();
        }

        /// <summary>
        /// Pre-loads all registered filters in the background.
        /// </summary>
        [HttpPost(DataSetRoute + "/filters/load")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult LoadAllFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "LoadAllFilters", SystemState.Loaded, SystemState.Indexing, SystemState.Ready) is { } stateError)
                return stateError;
            matcher.LoadAllFilters();
            return NoContent();
        }

        /// <summary>
        /// Returns the number of filters currently registered in the filter cache.
        /// </summary>
        [HttpGet(DataSetRoute + "/filters/count")]
        public ActionResult<CountResponse> GetNumberOfFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            ICloudSearchEngine? matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return Ok(new CountResponse(matcher.NumberOfFilters));
        }

        /// <summary>
        /// Hibernates the dataset, freeing in-memory structures while retaining persisted data.
        /// </summary>
        [HttpPost(DataSetRoute + "/hibernate")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult Hibernate(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "Hibernate", SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.Hibernate(out string errorMessage);
            if (!result)
                return ApiProblems.InvalidArgument(errorMessage);
            return NoContent();
        }

        /// <summary>
        /// Wakes up a hibernated dataset, restoring it from the persisted state.
        /// </summary>
        [HttpPost(DataSetRoute + "/wakeup")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult WakeUp(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "WakeUp", SystemState.Hibernated) is { } stateError)
                return stateError;
            var result = matcher.WakeUp();
            if (!result)
                return ApiProblems.OperationFailed("WakeUp failed - the dataset could not be restored from storage.");
            return NoContent();
        }

        /// <summary>
        /// Marks the specified fields as embeddable so that their vector values are indexed
        /// during the next Load. Must be called after AnalyzeStream and before LoadStream.
        /// </summary>
        [HttpPut(DataSetRoute + "/fields/embeddable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetEmbeddableFields(string teamName, string dataSetName, [FromBody] string[] fields)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            // No lifecycle-state guard: SetEmbeddableFields is idempotent and may legitimately be
            // re-sent on an already-Ready dataset (it operates on DocumentFields, which the manager
            // null-checks). Adding a Created-only guard would wrongly reject that idempotent re-send.
            if (!IndxCloudInternalApi.Manager.SetEmbeddableFields(fields, dataSetName, ctx.OwnerKey))
                return ApiProblems.InvalidArgument("SetEmbeddableFields failed — dataset not found or unknown field name");
            return NoContent();
        }

        /// <summary>
        /// Searches a single embedding field using approximate nearest-neighbour search.
        /// </summary>
        [HttpPost(DataSetRoute + "/search/vector")]
        public ActionResult<Indx.CloudApi.EmbeddingResultEntry[]> VectorSearch(
            string teamName, string dataSetName, [FromBody] Indx.CloudApi.VectorQueryProxy query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query?.Vector == null || query.Vector.Length == 0)
                return ApiProblems.InvalidArgument("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.FieldName))
                return ApiProblems.InvalidArgument("FieldName is required");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "VectorSearch", SystemState.Ready) is { } stateError)
                return stateError;
            return IndxCloudInternalApi.Manager.VectorSearch(query, dataSetName, ctx.OwnerKey);
        }

        /// <summary>
        /// Combines text search with embedding nearest-neighbour search and blends scores.
        /// </summary>
        [HttpPost(DataSetRoute + "/search/hybrid")]
        public ActionResult<Indx.CloudApi.EmbeddingResultEntry[]> HybridSearch(
            string teamName, string dataSetName, [FromBody] Indx.CloudApi.HybridQueryProxy query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query?.Vector == null || query.Vector.Length == 0)
                return ApiProblems.InvalidArgument("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.EmbeddingField))
                return ApiProblems.InvalidArgument("EmbeddingField is required");
            var matcher = IndxCloudInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "HybridSearch", SystemState.Ready) is { } stateError)
                return stateError;
            return IndxCloudInternalApi.Manager.HybridSearch(query, dataSetName, ctx.OwnerKey);
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Resolves the team from the route and the JWT user id, enforcing membership and the
        /// requested permission level. On failure sets <paramref name="error"/> to the response
        /// to return and returns null: 401 if unauthenticated, 404 teamNotFound when the team
        /// does not exist OR the caller is not a member (identical on purpose — team names must
        /// not be enumerable), 403 insufficientRole only when the caller IS a member but the
        /// role is too low.
        /// </summary>
        private TeamContext? ResolveTeam(string teamName, out ActionResult? error, bool write = false, bool admin = false)
        {
            error = null;
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) { error = Unauthorized(); return null; }

            var ctx = resolver.Resolve(teamName, userId);
            if (ctx == null) { error = ApiProblems.TeamNotFound(teamName); return null; }
            if (admin && !TeamRoles.CanAdmin(ctx.Role)) { error = ApiProblems.InsufficientRole("Admin"); return null; }
            if (write && !TeamRoles.CanWrite(ctx.Role)) { error = ApiProblems.InsufficientRole("Editor"); return null; }
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
                return ApiProblems.DatasetNotFound(dataSetName);
            if (allowed.Length > 0 && RequireState(engine, operationName, allowed) is { } stateError)
                return stateError;
            try
            {
                return IndxCloudInternalApi.Manager.RunHeavyOnShadowIfReady(dataSetName, teamId, mutation);
            }
            catch (KeyNotFoundException)
            {
                return ApiProblems.DatasetNotFound(dataSetName);
            }
            catch (ShadowBusyException ex)
            {
                return ApiProblems.ShadowBusy(ex.Message);
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
            problem.Extensions["code"] = "invalidState";
            problem.Extensions["operation"] = operation;
            problem.Extensions["currentState"] = state.ToString();
            problem.Extensions["allowedStates"] = allowed.Select(s => s.ToString()).ToArray();
            problem.Extensions["retryable"] = retryable;
            if (state == SystemState.Error && !string.IsNullOrEmpty(status.ErrorMessage))
                problem.Extensions["errorMessage"] = status.ErrorMessage;
            if (retryable)
                Response.Headers["Retry-After"] = "2";
            // application/problem+json, matching every other error in the API
            // (Conflict(object) would serve it as plain application/json).
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status409Conflict,
                ContentTypes = { "application/problem+json" }
            };
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
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxCloudInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var df = matcher.DocumentFields;
            if (df == null)
                return ApiProblems.InvalidArgument("The dataset has not been analyzed yet, so there are no fields to configure.");
            foreach (var name in fieldNames)
            {
                var f = df.GetField(name);
                if (f == null)
                    return ApiProblems.InvalidArgument($"Field '{name}' does not exist in this dataset.");
                apply(f, name);
            }
            return NoContent();
        }

        /// <summary>
        /// Reads the dataset's key-field value out of a single JSON document body, so the
        /// single-record routes can honor their {documentKey} instead of silently ignoring it.
        /// Returns false when the body carries no readable key — the engine then reports its
        /// own, more specific error.
        /// </summary>
        private static bool TryReadBodyKey(ICloudSearchEngine engine, string jsonData, out long key)
        {
            key = 0;
            var keyField = engine.DocumentFields?.NameOfDocumentKeyField;
            if (string.IsNullOrEmpty(keyField) || string.IsNullOrEmpty(jsonData))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty(keyField, out var el))
                    return false;
                return el.ValueKind switch
                {
                    JsonValueKind.Number => el.TryGetInt64(out key),
                    JsonValueKind.String => long.TryParse(el.GetString(), out key),
                    _ => false
                };
            }
            catch (JsonException)
            {
                return false;
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

#pragma warning disable 1591
    /// <summary>Dataset entry returned by GetMyDataSets — the owning team and the caller's role.</summary>
    public record DataSetListDto(string Name, string TeamName, string Role);
#pragma warning restore 1591
}
