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
    // In the API description since 21 Oct 2026, with its guide and reference in the docs; it was kept
    // out while experimental. Its routes still answer 404 until the instance and the dataset show it.
    public class SearchOrAskController(TeamContextResolver resolver, SearchOrAskStore store, SearchOrAskService service,
        StatisticsService statistics)
        : DatasetApiController(resolver)
    {
        private const string Route = DataSetRoute + "/search-or-ask";

        public sealed record IntentRequest(string? Text);
        public sealed record FiltersRequest(string? Text);

        /// <summary>The window's Search or ask statistics. Rates are null where nothing was offered
        /// or asked. Cost is in USD as OpenRouter reports it, judge and answers together.</summary>
        public sealed record SearchOrAskStatisticsResponse(
            long Judged, long Offers, long StrongOffers, long Asks, double? TakeRate,
            long Answered, long NothingFound, long Failed, long Stopped, long Unanswered, double? UnansweredRate,
            long SourcesOpened, double? SourcesOpenedPerAnswer, double Cost, long? MedianFirstTextMs,
            AskQuestionStat[] Questions, AskOfferStat[] Offered,
            long Calls = 0, long CallsRefused = 0, int DaysBudgetRanOut = 0);
        /// <summary>A question, and for a follow-up the conversation so far (the last three turns are used).</summary>
        public sealed record AskRequest(string? Question, AskTurn[]? History = null);

        /// <summary>
        /// What the dataset sets, what it runs with (the site description and fields after defaults),
        /// and whether this instance can run it at all (<c>available</c>: an API key is configured).
        /// A search key may read it: a search page needs to know whether to ask, and the thresholds.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(Route)]
        public ActionResult<SearchOrAskSettingsResponse> GetSettings(string teamName, string dataSetName)
        {
            if (!service.Shown) return NotFound();
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var settings = service.Settings(ctx.OwnerKey, dataSetName);
            // A search key sits in every visitor's browser: the site's knowledge is not for it,
            // though answers may state what it says. A Read or Full key, or a person, gets them, so a
            // read, change and write back does not wipe them. A copy: the store's is shared.
            if (ApiKeyScope.For(HttpContext) is { } scope && !scope.AllowsLevel(ApiKeyLevel.Read) && settings.Knowledge != null)
            {
                settings = System.Text.Json.JsonSerializer.Deserialize<SearchOrAskSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
                settings.Knowledge = null;
            }
            var engine = IndxServerInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            var fields = SearchOrAskService.Fields(settings, engine?.DocumentFields == null ? null : engine.GetFieldList());
            return new SearchOrAskSettingsResponse(settings,
                new SearchOrAskEffective(service.SiteDescription(ctx.OwnerKey, dataSetName, settings),
                    fields.TitleField, fields.SectionFields, fields.SnippetFields, settings.AskPhrases ?? SearchOrAskPhrases.Default),
                service.Available);
        }

        /// <summary>Replaces the dataset's settings. Applies from the next call; no reindex.</summary>
        [HttpPut(Route)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSettings(string teamName, string dataSetName, [FromBody] SearchOrAskSettings settings)
        {
            if (!service.Shown) return NotFound();
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            if (settings == null)
                return ApiProblems.InvalidArgument("A body with the settings is required.");
            if (settings.Validate() is { } invalid)
                return ApiProblems.InvalidArgument(invalid);
            // Whether the dataset has the feature is a team admin's (PUT shown), not an editor's.
            settings.Shown = store.Load(ctx.OwnerKey, dataSetName)?.Shown ?? false;
            store.Save(ctx.OwnerKey, dataSetName, settings);
            return NoContent();
        }

        public sealed record ShownRequest(bool Shown);

        /// <summary>Whether this dataset has Search or ask (its Options tab): a team admin's switch.
        /// Experimental, so off by default; on, the dataset's tab and its visitor routes appear, and
        /// <c>enabled</c> in the settings then takes it live.</summary>
        [HttpPut(Route + "/shown")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetShown(string teamName, string dataSetName, [FromBody] ShownRequest request)
        {
            if (!service.Shown) return NotFound();
            var ctx = ResolveTeam(teamName, out var error, admin: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var settings = store.Load(ctx.OwnerKey, dataSetName) ?? new SearchOrAskSettings();
            settings.Shown = request?.Shown ?? false;
            store.Save(ctx.OwnerKey, dataSetName, settings);
            return NoContent();
        }

        /// <summary>Removes the dataset's settings: the feature is off and every value back to its default.</summary>
        [HttpDelete(Route)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult DeleteSettings(string teamName, string dataSetName)
        {
            if (!service.Shown) return NotFound();
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
            if (!service.Shown) return NotFound();
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            // The dataset's own switch (its Options tab): as though the route were not there.
            if (!service.ShownFor(ctx.OwnerKey, dataSetName)) return NotFound();
            var text = request?.Text ?? "";
            if (text.Trim().Length == 0 || text.Length > SearchOrAskService.MaxText)
                return ApiProblems.InvalidArgument($"Send {{ text }} of 1 to {SearchOrAskService.MaxText} characters.");
            if (ReadyEngine(dataSetName, ctx.OwnerKey, out var engineError) is not { } engine) return engineError!;

            var outcome = await service.IntentAsync(ctx.OwnerKey, dataSetName, engine, text,
                AuthRateLimiting.ClientIp(HttpContext), HttpContext.RequestAborted, origin: Origin());
            return Refusal(outcome) ?? (ActionResult<IntentResult>)outcome.Result!;
        }

        /// <summary>
        /// A catalogue's description of what to list, as filters on the dataset's own fields: call it
        /// when intent's <c>criteria</c> reaches the strong threshold. Each filter is a value or a
        /// number range on a Filterable field, checked against the values the dataset holds; a text
        /// field also needs to be Facetable, which is where its values come from. <c>text</c> is what
        /// no filter covers, to search for.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(Route + "/filters")]
        public async Task<ActionResult<FiltersResult>> Filters(string teamName, string dataSetName, [FromBody] FiltersRequest request)
        {
            if (!service.Shown) return NotFound();
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            // The dataset's own switch (its Options tab): as though the route were not there.
            if (!service.ShownFor(ctx.OwnerKey, dataSetName)) return NotFound();
            var text = request?.Text ?? "";
            if (text.Trim().Length == 0 || text.Length > SearchOrAskService.MaxText)
                return ApiProblems.InvalidArgument($"Send {{ text }} of 1 to {SearchOrAskService.MaxText} characters.");
            if (ReadyEngine(dataSetName, ctx.OwnerKey, out var engineError) is not { } engine) return engineError!;
            var outcome = await service.FiltersAsync(ctx.OwnerKey, dataSetName, engine, text.Trim(),
                AuthRateLimiting.ClientIp(HttpContext), HttpContext.RequestAborted);
            return Refusal(new IntentOutcome(outcome.Status, RetryAfterSeconds: outcome.RetryAfterSeconds))
                   ?? (ActionResult<FiltersResult>)outcome.Result!;
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
            if (!service.Shown) return NotFound();
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            // The dataset's own switch (its Options tab): as though the route were not there.
            if (!service.ShownFor(ctx.OwnerKey, dataSetName)) return NotFound();
            var question = request?.Question?.Trim() ?? "";
            if (question.Length == 0 || question.Length > SearchOrAskService.MaxQuestion)
                return ApiProblems.InvalidArgument($"Send {{ question }} of 1 to {SearchOrAskService.MaxQuestion} characters.");
            if (ReadyEngine(dataSetName, ctx.OwnerKey, out var engineError) is not { } engine) return engineError!;
            if (Refusal(service.CanAsk(ctx.OwnerKey, dataSetName, AuthRateLimiting.ClientIp(HttpContext))) is { } refused)
                return refused;

            // Minted here so the page has it from the first byte: a select that sends it as its
            // queryId is a cited source opened (Notes/search-or-ask-server.md, statistics).
            var askId = Guid.NewGuid().ToString("N");
            Response.Headers["Indx-Ask-Id"] = askId;
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
                await service.AskAsync(ctx.OwnerKey, dataSetName, engine, question, Send, HttpContext.RequestAborted,
                    askId: askId, origin: Origin(), history: request!.History);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // The visitor went away; there is no one to tell.
            }
            return new EmptyResult();
        }

        /// <summary>The statistics' view of the caller, read as a search reads it: <c>?queryId=</c>
        /// (the page's search behind this, whose text the statistics show), <c>?session=</c>,
        /// <c>?source=</c>, <c>?subject=</c> and <c>?count=false</c>.</summary>
        private SearchOrAskOrigin Origin() => new(
            Request.Query["queryId"].FirstOrDefault(),
            Request.Query["session"].FirstOrDefault(),
            StatisticsService.CleanSource(Request.Query["source"].FirstOrDefault()),
            Request.Query["subject"].FirstOrDefault(),
            !string.Equals(Request.Query["count"].FirstOrDefault()?.Trim(), "false", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// What Search or ask did in the last <c>days</c> (today included, in the dataset's time
        /// zone): offers shown and taken, questions asked and how they ended, cited sources opened,
        /// and what it cost. <c>questions</c> lists the questions asked, the ones the content did not
        /// answer first; <c>offered</c> the texts an offer was shown beside (from the searches the
        /// page named), so a false offer can be found. Counted rows only, like every statistics read.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/search-or-ask")]
        public ActionResult<SearchOrAskStatisticsResponse> Statistics(string teamName, string dataSetName, int days = 30,
            string? source = null, int limit = 50)
        {
            if (!service.Shown) return NotFound();
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            // The dataset's own switch (its Options tab): as though the route were not there.
            if (!service.ShownFor(ctx.OwnerKey, dataSetName)) return NotFound();
            if (!statistics.Enabled || statistics.Store is not { } s)
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "statisticsDisabled", "Statistics disabled",
                    "Statistics are switched off on this instance (Statistics:Enabled=false).");
            long today = s.Today(ctx.OwnerKey, dataSetName);
            long fromDay = today - Math.Clamp(days, 1, 3650) + 1;
            var src = StatisticsService.CleanSource(source);
            limit = Math.Clamp(limit, 1, 500);
            var o = s.AskOverview(ctx.OwnerKey, dataSetName, fromDay, today, src);
            var budget = s.AskBudget(ctx.OwnerKey, dataSetName, fromDay, today);
            static double? Rate(double n, double d) => d == 0 ? null : n / d;
            return new SearchOrAskStatisticsResponse(o.Judged, o.Offers, o.StrongOffers, o.Asks, Rate(o.Asks, o.Offers),
                o.Answered, o.NothingFound, o.Failed, o.Stopped, o.Unanswered, Rate(o.Unanswered, o.Asks),
                o.SourcesOpened, Rate(o.SourcesOpened, o.Answered), o.Cost + budget.Cost, o.MedianFirstTextMs,
                s.TopQuestions(ctx.OwnerKey, dataSetName, fromDay, today, limit, src).ToArray(),
                s.TopOffers(ctx.OwnerKey, dataSetName, fromDay, today, limit, src).ToArray())
            {
                Calls = budget.Calls, CallsRefused = budget.Refused, DaysBudgetRanOut = budget.DaysRunOut,
            };
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
