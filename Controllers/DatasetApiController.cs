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
    // endpoint: 400 (invalidArgument / invalidDatasetName / loadFailed / operationFailed /
    // unknownFilter),
    // 403 (insufficientRole), 404 (datasetNotFound / documentNotFound / teamNotFound), and
    // 409 (invalidState with currentState/allowedStates/retryable + Retry-After header when
    // retryable, or shadowBusy). 401 comes body-less from the JWT middleware.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public abstract class DatasetApiController(TeamContextResolver resolver) : Controller
    {
        protected readonly TeamContextResolver resolver = resolver;

        protected const string DataSetRoute = "teams/{teamName}/datasets/{dataSetName}";

        /// <summary>
        /// Resolves the team from the route and the JWT user id, enforcing membership and the
        /// requested permission level. On failure sets <paramref name="error"/> to the response
        /// to return and returns null: 401 if unauthenticated, 404 teamNotFound when the team
        /// does not exist OR the caller is not a member (identical on purpose — team names must
        /// not be enumerable), 403 insufficientRole only when the caller IS a member but the
        /// role is too low.
        /// </summary>
        protected TeamContext? ResolveTeam(string teamName, out ActionResult? error, bool write = false, bool admin = false)
        {
            error = null;
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) { error = Unauthorized(); return null; }

            // A scoped key's team is checked inside Resolve, on the team row it loads anyway.
            var keyScope = ApiKeyScope.For(HttpContext);
            var ctx = resolver.Resolve(teamName, userId, keyScope);
            if (ctx == null) { error = ApiProblems.TeamNotFound(teamName); return null; }
            // ApiKeyScopeFilter has already held a scoped key to its datasets and each action's
            // declared level. Checked again here for every write and admin operation, so a write
            // action marked with too low a KeyAccess level still cannot be reached by a Search or
            // Read key.
            if ((write || admin) && keyScope is { } scope && !scope.AllowsLevel(ApiKeyLevel.Full))
            { error = ApiProblems.InsufficientKeyScope(ApiKeyLevel.Full); return null; }
            if (admin && !TeamRoles.CanAdmin(ctx.Role)) { error = ApiProblems.InsufficientRole("Admin"); return null; }
            if (write && !TeamRoles.CanWrite(ctx.Role)) { error = ApiProblems.InsufficientRole("Editor"); return null; }
            return ctx;
        }

        /// <summary>
        /// Resolves a client-supplied filter token, or sets <paramref name="error"/> to the 400
        /// to return. Covers the three ways a token fails to become a filter: the proxy is
        /// absent, its hashString is blank, or the engine cannot rebuild it.
        /// <para>
        /// Every endpoint that takes a token goes through here so the answer is the same one -
        /// CombineFilters dereferenced its two operands without checking either, which made a
        /// stale or mistyped token a 500, and the endpoints that did check reported it as
        /// invalidArgument rather than the unknownFilter code that names the actual condition.
        /// </para>
        /// </summary>
        protected static Filter? ResolveFilter(IServerSearchEngine engine, FilterProxy? proxy, string operand, out ActionResult? error)
        {
            error = null;
            if (proxy == null || string.IsNullOrWhiteSpace(proxy.HashString))
            {
                error = ApiProblems.InvalidArgument($"'{operand}' is required and must carry a hashString.");
                return null;
            }
            var filter = engine.GetFilterFromKey(proxy.HashString);
            if (filter == null)
                error = ApiProblems.UnknownFilter(
                    $"'{operand}' could not be resolved. Create the filter first, then reference it by the returned hashString; " +
                    "a filter also stops resolving if its field was removed or is no longer Filterable.");
            return filter;
        }

        /// <summary>
        /// Runs a heavy mutation against the team's dataset, using the shadow-swap path when the
        /// engine is Ready so live searches are not blocked. Maps not-found to 400 and a concurrent
        /// shadow build to 409.
        /// </summary>
        protected ActionResult RunHeavy(
            string dataSetName,
            string teamId,
            string operationName,
            Func<IServerSearchEngine, ActionResult> mutation,
            params SystemState[] allowed)
        {
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, teamId);
            if (engine == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (allowed.Length > 0 && RequireState(engine, operationName, allowed) is { } stateError)
                return stateError;
            try
            {
                return IndxServerInternalApi.Manager.RunHeavyOnShadowIfReady(dataSetName, teamId, mutation);
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
        protected ActionResult? RequireState(IServerSearchEngine engine, string operation, params SystemState[] allowed)
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
        protected static string StateGuidance(SystemState state, SystemStatus status) => state switch
        {
            SystemState.Created => "The dataset is created but not loaded. Call Load (LoadStream/LoadString/LoadFromDatabase) and then IndexDataSet.",
            SystemState.Loading => "Loading is in progress — retry once the dataset reaches Ready.",
            SystemState.Loaded => "The dataset is loaded but not indexed. Call IndexDataSet.",
            SystemState.Indexing => "Indexing is in progress — retry once the dataset reaches Ready.",
            SystemState.Hibernated => "The dataset is hibernated. Call WakeUp.",
            SystemState.Error => $"The dataset is in an error state: {status.ErrorMessage}",
            _ => ""
        };
 

        /// <summary>
        /// ASP.NET Core deserializes 'object' properties as JsonElement. Unwrap to the
        /// appropriate primitive so engine methods (UpdateField, UpdateFieldInFilter) can
        /// use type-checking via Field.GetJsonValueKind.
        /// </summary>
        protected static object? UnwrapJsonElement(object? value)
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

    }
}
