using Asp.Versioning;
using IndxServer.Engine;
using Indx.Api;
using Indx.Http;
using Indx.Core;
using Indx.Storage;
using Indx.Utilities;
using IndxServer.Models;
using IndxServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Dataset lifecycle: create/open, analyze, load, replace, index, status, hibernate and wake, delete - and the dataset listings.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class DatasetsController(TeamContextResolver resolver, TeamService teams) : DatasetApiController(resolver)
    {
        /// <summary>
        /// As Analyze but handles a stream as input text.
        /// </summary>
        [HttpPost(DataSetRoute + "/analyze")]
        public async Task<ActionResult<SystemStatus>> AnalyzeStreamAsync(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            HttpContext.Request.EnableBuffering();
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.FindSearchEngineForInit(dataSetName, ctx.OwnerKey);
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
            var state = IndxServerInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (state == null || state.InvalidDataSetName)
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.FindSearchEngineForInit(dataSetName, ctx.OwnerKey);
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
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPut(DataSetRoute)]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult CreateOrOpen(string teamName, string dataSetName)
        {
            // Opening a dataset that exists changes nothing, so it needs only read access; creating
            // one needs write. Every published @indxsearch/intrface calls this PUT when a search box
            // starts, so requiring write here meant every intrface front-end had to ship an Editor
            // or Admin credential to the browser — and no Search or Read key could run one.
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            using var persistence = new Persistence(IndxServerInternalApi.SearchDbConnectionString, dataSetName, ctx.OwnerKey);
            if (persistence.DataSetExists())
                return Ok();
            if (ResolveTeam(teamName, out error, write: true) == null) return error!;
            // Still writes 400. The column is kept for the serialized configuration it will hold, and
            // until then ResolveConfiguration reads 400 as ConfigurationParameters.Default — so this
            // and that have to keep agreeing. ConfigurationResolutionTests asserts they do.
            persistence.CreateOrOpenDataSet(IndxServerInternalApi.DefaultConfigurationNumber);
            return StatusCode(StatusCodes.Status201Created);
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
            if (!IndxServerInternalApi.Manager.DeleteDataSet(dataSetName, ctx.OwnerKey))
                return ApiProblems.DatasetNotFound(dataSetName);
            return NoContent();
        }

        /// <summary>
        /// GetStatus will return the status of the dataSetName in the search engine.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/status")]
        public ActionResult<ServerSystemStatus> GetStatus(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var status = IndxServerInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (status == null)
                return ApiProblems.DatasetNotFound(dataSetName);

            return new ServerSystemStatus(status)
            {
                ShadowBuildInProgress = IndxServerInternalApi.Manager.IsShadowBuildInProgress(dataSetName, ctx.OwnerKey),
                ShadowBuildStartedUtc = IndxServerInternalApi.Manager.ShadowBuildStartedUtc(dataSetName, ctx.OwnerKey),
            };
        }

        /// <summary>
        /// Returns all datasets the current user can reach via team membership, across every team
        /// they belong to. Each entry carries the owning team name and the caller's role on it.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read, FiltersToKeyScope = true)]
        [HttpGet("me/datasets")]
        public async Task<ActionResult<DataSetListDto[]>> GetMyDataSets()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var scope = ApiKeyScope.For(HttpContext);
            if (string.IsNullOrEmpty(userId) && scope?.IsTeamKey != true)
                return Unauthorized();

            // A scoped key sees only its own team, and only the datasets it names.
            var result = new List<DataSetListDto>();
            foreach (var (team, role) in await teams.GetTeamsForCallerAsync(userId, scope))
            {
                if (scope != null && !scope.AllowsTeam(team.Id)) continue;
                foreach (var name in IndxServerInternalApi.Manager.GetTeamDataSets(team.Id.ToString()))
                    if (scope == null || scope.AllowsDataset(name))
                        result.Add(new DataSetListDto(name, team.Name, role));
            }
            return result.ToArray();
        }

        /// <summary>
        /// Lists the datasets owned by a single team.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet("teams/{teamName}/datasets")]
        public ActionResult<string[]> GetTeamDataSets(string teamName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var scope = ApiKeyScope.For(HttpContext);
            return IndxServerInternalApi.Manager.GetTeamDataSets(ctx.OwnerKey)
                .Where(name => scope == null || scope.AllowsDataset(name))
                .ToArray();
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

            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
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
                    IndxServerInternalApi.Manager.RunFieldConfigurationOnShadow(
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
            else if (!IndxServerInternalApi.Manager.DoIndex(dataSetName, ctx.OwnerKey))
            {
                return ApiProblems.OperationFailed("Indexing could not be started.");
            }

            var status = IndxServerInternalApi.Manager.GetState(dataSetName, ctx.OwnerKey);
            if (status == null)
                return ApiProblems.OperationFailed("Indexing did not report a status.");
            // The work continues in the background — 202 with the current status;
            // poll GET status until Ready.
            return Accepted(status);
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

            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var pm = new ProcessMonitor();
            var success = IndxServerInternalApi.Manager.LoadFromDatabase(dataSetName, ctx.OwnerKey, pm);
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
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
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
            if (IndxServerInternalApi.Manager.Load(dataSetName, ctx.OwnerKey, bodyStream, pm))
                pm.WaitForCompletion();
            else
                return ApiProblems.LoadFailed("LoadStream failed.");
            if (!pm.Succeeded)
                return ApiProblems.LoadFailed(IndxServerInternalApi.Manager.DescribeLoadFailure(dataSetName, ctx.OwnerKey, pm.ErrorMessage));

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
                var summary = IndxServerInternalApi.Manager.RunReplaceFromJson(dataSetName, ctx.OwnerKey, bodyStream);
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
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var memoryStream = new MemoryStream();
            using (var writer = new StreamWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(jsonData);
                writer.Flush();
            }
            memoryStream.Position = 0;
            var pm = new ProcessMonitor();
            if (IndxServerInternalApi.Manager.Load(dataSetName, ctx.OwnerKey, memoryStream, pm))
                pm.WaitForCompletion();
            else
                return ApiProblems.LoadFailed("LoadString failed.");
            if (!pm.Succeeded)
                return ApiProblems.LoadFailed(IndxServerInternalApi.Manager.DescribeLoadFailure(dataSetName, ctx.OwnerKey, pm.ErrorMessage));
            return NoContent();
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
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
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
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "WakeUp", SystemState.Hibernated) is { } stateError)
                return stateError;
            var result = matcher.WakeUp();
            if (!result)
                return ApiProblems.OperationFailed("WakeUp failed - the dataset could not be restored from storage.");
            return NoContent();
        }
    }
    /// <summary>One row of the "my datasets" listing: a dataset with the team it belongs to and the caller's role.</summary>
    public record DataSetListDto(string Name, string TeamName, string Role);
}
