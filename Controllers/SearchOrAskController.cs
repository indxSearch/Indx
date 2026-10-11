using System.Text.Json;
using Indx.Api;
using Indx.Utilities;
using IndxServer.Engine;
using IndxServer.Services;
using IndxServer.Services.SearchOrAsk;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Search or ask: the dataset's settings, the intent judgement on a typing pause, and an answer
    /// written from the dataset's records. Notes/search-or-ask-server.md.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class SearchOrAskController(TeamContextResolver resolver, SearchOrAskStore store, SearchOrAskService service)
        : DatasetApiController(resolver)
    {
        private const string Route = DataSetRoute + "/search-or-ask";

        public sealed record IntentRequest(string? Text);
        public sealed record AskRequest(string? Question);

        /// <summary>
        /// What the dataset sets, what it runs with (the site description and fields after defaults),
        /// and whether this instance can run it at all (<c>available</c>: an API key is configured).
        /// A search key may read it: a search page needs to know whether to ask, and the thresholds.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(Route)]
        public ActionResult<SearchOrAskSettingsResponse> GetSettings(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var settings = service.Settings(ctx.OwnerKey, dataSetName);
            var engine = IndxServerInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            var fields = SearchOrAskService.Fields(settings, engine?.DocumentFields == null ? null : engine.GetFieldList());
            return new SearchOrAskSettingsResponse(settings,
                new SearchOrAskEffective(service.SiteDescription(ctx.OwnerKey, dataSetName, settings),
                    fields.TitleField, fields.SectionFields, fields.SnippetFields),
                service.Available);
        }

        /// <summary>Replaces the dataset's settings. Applies from the next call; no reindex.</summary>
        [HttpPut(Route)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSettings(string teamName, string dataSetName, [FromBody] SearchOrAskSettings settings)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            if (settings == null)
                return ApiProblems.InvalidArgument("A body with the settings is required.");
            if (settings.Validate() is { } invalid)
                return ApiProblems.InvalidArgument(invalid);
            store.Save(ctx.OwnerKey, dataSetName, settings);
            return NoContent();
        }

        /// <summary>Removes the dataset's settings: the feature is off and every value back to its default.</summary>
        [HttpDelete(Route)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult DeleteSettings(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            store.Delete(ctx.OwnerKey, dataSetName);
            return NoContent();
        }

        /// <summary>
        /// Judges what a visitor has typed so far: <c>p</c>, the probability they are asking for
        /// something an answer would serve, <c>answerable</c>, whether the results could answer it,
        /// and the <c>offer</c> those come to on their own. Call it on a typing pause, beside the
        /// search the page already ran. A judge that is slow or failing answers <c>p</c> 0 with a
        /// <c>reason</c>; it never fails the call.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(Route + "/intent")]
        public async Task<ActionResult<IntentResult>> Intent(string teamName, string dataSetName, [FromBody] IntentRequest request)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var text = request?.Text ?? "";
            if (text.Trim().Length == 0 || text.Length > SearchOrAskService.MaxText)
                return ApiProblems.InvalidArgument($"Send {{ text }} of 1 to {SearchOrAskService.MaxText} characters.");
            if (ReadyEngine(dataSetName, ctx.OwnerKey, out var engineError) is not { } engine) return engineError!;

            var outcome = await service.IntentAsync(ctx.OwnerKey, dataSetName, engine, text,
                AuthRateLimiting.ClientIp(HttpContext), HttpContext.RequestAborted);
            return Refusal(outcome) ?? (ActionResult<IntentResult>)outcome.Result!;
        }

        /// <summary>
        /// Answers a question from the dataset's records, as server-sent events: <c>sources</c> (the
        /// records the answer may use, numbered), <c>text</c> (the answer as it is written, citing
        /// sources as [n]), then <c>done</c>; or <c>error</c> with a code. The answer uses those
        /// records and nothing else, and says so when they do not hold it.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(Route + "/ask")]
        [Produces("text/event-stream")]
        public async Task<IActionResult> Ask(string teamName, string dataSetName, [FromBody] AskRequest request)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var question = request?.Question?.Trim() ?? "";
            if (question.Length == 0 || question.Length > SearchOrAskService.MaxQuestion)
                return ApiProblems.InvalidArgument($"Send {{ question }} of 1 to {SearchOrAskService.MaxQuestion} characters.");
            if (ReadyEngine(dataSetName, ctx.OwnerKey, out var engineError) is not { } engine) return engineError!;
            if (Refusal(service.CanAsk(ctx.OwnerKey, dataSetName, AuthRateLimiting.ClientIp(HttpContext))) is { } refused)
                return refused;

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            async Task Send(string name, object data)
            {
                await Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }
            try
            {
                await service.AskAsync(ctx.OwnerKey, dataSetName, engine, question, Send, HttpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // The visitor went away; there is no one to tell.
            }
            return new EmptyResult();
        }

        private IServerSearchEngine? ReadyEngine(string dataSetName, string teamId, out ActionResult? error)
        {
            error = null;
            if (!FileNameValidity.IsValid(dataSetName)) { error = ApiProblems.InvalidDatasetName(dataSetName); return null; }
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, teamId);
            if (engine == null) { error = ApiProblems.DatasetNotFound(dataSetName); return null; }
            if (RequireState(engine, "Search or ask", SystemState.Ready) is { } stateError) { error = stateError; return null; }
            return engine;
        }

        /// <summary>The refusals before any model is asked, as problems. 409 for "not on here" (an
        /// instance without a key, a dataset without the feature), 429 with Retry-After for the
        /// per-address limit and the daily budget.</summary>
        private ObjectResult? Refusal(IntentOutcome outcome)
        {
            switch (outcome.Status)
            {
                case IntentStatus.Unavailable:
                    return ApiProblems.Problem(StatusCodes.Status409Conflict, "searchOrAskUnavailable", "Search or ask is not available",
                        "This instance has no model key configured (Indx:SearchOrAsk:ApiKey).");
                case IntentStatus.Disabled:
                    return ApiProblems.Problem(StatusCodes.Status409Conflict, "searchOrAskDisabled", "Search or ask is off",
                        "Search or ask is not switched on for this dataset.");
                case IntentStatus.RateLimited:
                    Response.Headers.RetryAfter = outcome.RetryAfterSeconds.ToString();
                    var limited = ApiProblems.Problem(StatusCodes.Status429TooManyRequests, AuthRateLimiting.Code, "Too many requests",
                        $"Too many Search or ask calls from this address. Try again in {outcome.RetryAfterSeconds} seconds.");
                    ((ProblemDetails)limited.Value!).Extensions["retryAfterSeconds"] = outcome.RetryAfterSeconds;
                    return limited;
                case IntentStatus.BudgetSpent:
                    Response.Headers.RetryAfter = outcome.RetryAfterSeconds.ToString();
                    var spent = ApiProblems.Problem(StatusCodes.Status429TooManyRequests, "searchOrAskBudgetSpent", "Daily budget spent",
                        "This dataset has used its Search or ask calls for today (UTC).");
                    ((ProblemDetails)spent.Value!).Extensions["retryAfterSeconds"] = outcome.RetryAfterSeconds;
                    return spent;
                default:
                    return null;
            }
        }
    }
}
