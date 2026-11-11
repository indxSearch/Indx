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
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Search: text, vector and hybrid.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class SearchController(TeamContextResolver resolver, StatisticsService statistics,
        QueryParameterStore queryParameters, IConfiguration configuration,
        Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> mvcJson)
        : DatasetApiController(resolver)
    {
        /// <summary>A search's own steps as a <c>Server-Timing</c> header (milliseconds): the team
        /// lookup, the dataset lookup with its state check, the engine's search, and what follows it
        /// (the query-parameter header, the statistics). What a browser's network panel and
        /// <c>curl -D -</c> show, so the time a search costs can be split from the outside: on
        /// cloud.indx.co a search took about twice the library's own time (22 Oct 2026), and this is
        /// how to see where. Off with <c>Indx:ServerTiming=false</c>.</summary>
        private void ServerTiming(long start, long team, long dataset, long search)
        {
            if (!configuration.GetValue("Indx:ServerTiming", true)) return;
            var end = Stopwatch.GetTimestamp();
            static string Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            Response.Headers["Server-Timing"] =
                $"team;dur={Ms(start, team)}, dataset;dur={Ms(team, dataset)}, search;dur={Ms(dataset, search)}, after;dur={Ms(search, end)}, total;dur={Ms(start, end)}";
        }

        /// <summary>
        /// Search will validate the search query and return the search result. With
        /// <c>includeDocuments</c> the records' JSON comes with it, in <c>documents</c>, in the order
        /// of <c>records</c> (null for a record gone since), so a search is one call, not two.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search")]
        public ActionResult<Indx.Api.Result> Search(string teamName, string dataSetName, [FromBody] QueryProxy query)
        {
            var tStart = Stopwatch.GetTimestamp();
            var ctx = ResolveTeam(teamName, out var error);
            var tTeam = Stopwatch.GetTimestamp();
            if (ctx == null) return error!;
            if (query == null)
                return ApiProblems.InvalidArgument("Search query body is required");
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "Search", SystemState.Ready) is { } stateError)
                return stateError;
            var tDataset = Stopwatch.GetTimestamp();
            var outcome = RunSearch(ctx, dataSetName, matcher, query);
            if (outcome.Problem != null) return outcome.Problem;
            if (outcome.Refocused is not RefocusOutcome.None)
                Response.Headers[Refocusing.ResponseHeader] = outcome.Refocused.ToString().ToLowerInvariant();
            // Which values the dataset's query parameters decided, because the request left
            // them out: "why does my search find so few" is answered by one response. Absent
            // when the request (or the engine default) decided everything.
            if (outcome.FromDataset.Count > 0)
                Response.Headers[QueryParameterResolution.ResponseHeader] = string.Join(", ", outcome.FromDataset);
            if (outcome.QueryId != null)
                Response.Headers["Indx-Query-Id"] = outcome.QueryId;
            ServerTiming(tStart, tTeam, tDataset, outcome.SearchedAt);
            var cold = ColdOf(ctx, dataSetName, outcome.Result!);
            if (query.IncludeDocuments)
                return Ok(WithCold(WithDocuments(outcome.Result!, matcher), cold));
            // The result as it is, unless a record holds cold storage to say so.
            return cold.Count == 0 ? outcome.Result! : Ok(WithCold(ToNode(outcome.Result!), cold));
        }

        /// <summary>The most searches one <see cref="MultiSearch"/> call takes.</summary>
        public const int MaxSearchesPerCall = 20;

        /// <summary>
        /// Several searches of the dataset in one call, answered in the order sent: what an
        /// InstantSearch page asks for at once, its list and the counts of each refined OR facet
        /// without that facet's own filter (11 Nov 2026, backlog 90 step 4). Each runs as
        /// <see cref="Search"/> runs it, the statistics and the query string's parameters included;
        /// a search that fails is an <c>error</c> in its place, with the status and problem a single
        /// search would have answered, and the others are answered. Each answer carries its
        /// <c>queryId</c> where a single search's header would. At most
        /// <see cref="MaxSearchesPerCall"/> searches.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search/multi")]
        public ActionResult MultiSearch(string teamName, string dataSetName, [FromBody] QueryProxy[] queries)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (queries == null || queries.Length == 0)
                return ApiProblems.InvalidArgument("The body must be a JSON array of one or more searches.");
            if (queries.Length > MaxSearchesPerCall)
                return ApiProblems.InvalidArgument($"{queries.Length} searches; one call takes at most {MaxSearchesPerCall}.");
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "Search", SystemState.Ready) is { } stateError)
                return stateError;
            var answers = new System.Text.Json.Nodes.JsonArray();
            foreach (var query in queries)
            {
                if (query == null)
                {
                    answers.Add(ErrorOf(ApiProblems.InvalidArgument("A search in the list is null.")));
                    continue;
                }
                var outcome = RunSearch(ctx, dataSetName, matcher, query);
                if (outcome.Problem != null)
                {
                    answers.Add(ErrorOf(outcome.Problem));
                    continue;
                }
                var answer = WithCold(query.IncludeDocuments ? WithDocuments(outcome.Result!, matcher) : ToNode(outcome.Result!),
                    ColdOf(ctx, dataSetName, outcome.Result!));
                if (outcome.QueryId != null) answer["queryId"] = outcome.QueryId;
                answers.Add(answer);
            }
            return Ok(answers);
        }

        /// <summary>What one search came to: its result, or the problem a single search answers
        /// with; the refocus, the parameters the dataset decided and the query id, which a single
        /// search sends as headers.</summary>
        private sealed record SearchOutcome(Indx.Api.Result? Result, ObjectResult? Problem, RefocusOutcome Refocused,
            IReadOnlyList<string> FromDataset, string? QueryId, long SearchedAt);

        /// <summary>One search as the search route runs it, for <see cref="Search"/> and
        /// <see cref="MultiSearch"/> alike: one way in, so the two cannot drift.</summary>
        private SearchOutcome RunSearch(TeamContext ctx, string dataSetName, IServerSearchEngine matcher, QueryProxy query)
        {
            // When the search was asked for, not when it finished: a short text can take longer than
            // the keystroke after it, and the keystroke rule reads the session's searches in order.
            long askedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                // ?refocus=true: a search no record holds every word of comes back in BM25F's order
                // (Services.Refocusing); intrface asks while the judge reads a question or a description.
                var refocus = string.Equals(Request.Query["refocus"].FirstOrDefault()?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                Indx.Api.Result res = IndxServerInternalApi.Manager.Search(query, dataSetName, ctx.OwnerKey, out var resolvedFilterKey, refocus);
                var refocused = Refocusing.OutcomeOf(res);
                var tSearch = Stopwatch.GetTimestamp();
                var fromDataset = QueryParameterResolution.Resolve(query,
                    queryParameters.Load(ctx.OwnerKey, dataSetName)).FromDataset;
                string? minted = null;
                // The search event is logged server-side - that is what makes the zero-hit list
                // exist at all - and the minted queryId is what select/convert events reference.
                // Cost on this path is a struct and a queue append (measured in
                // Notes/statistics-design.md); the optional ?subject= gives per-subject history.
                if (statistics.Enabled)
                {
                    var queryId = Guid.NewGuid().ToString("N");
                    minted = queryId;
                    var subject = Request.Query["subject"].FirstOrDefault();
                    // ?session= is any per-page-load random id. It identifies no one; it lets the
                    // rollup see that "o", "os", "osl", "oslo" were one visitor typing one search.
                    var session = Request.Query["session"].FirstOrDefault();
                    // ?typed= is what the visitor typed when the page searched something else for
                    // it (the Ask phrases taken off a question): the keystroke rule follows the
                    // typing, which the searched text jumps out of as the phrases start to match.
                    var typed = Request.Query["typed"].FirstOrDefault();
                    // ?source=<name> names the surface the search came from: a search box, an app,
                    // an agent. ?count=false says it is not a visitor's search - an agent looking at
                    // what a query returns, a monitor, a test - so it is stored but left out of
                    // every aggregate, as the console's preview is, and whoever reads the statistics
                    // does not read their own probes back. Two facts, two parameters: until Oct 2026
                    // one ?probe= did both, and a surface that should count could not be named.
                    // A caller can only hide its own searches.
                    var source = StatisticsService.CleanSource(Request.Query["source"].FirstOrDefault());
                    // ?locale=<tag> is the language the page was in, as the storefront reports it
                    // (intrface sends the document's lang, else the browser's). Without it, the
                    // browser's own preference from Accept-Language, which a storefront calling from
                    // the visitor's browser sends for free; a server calling on the visitor's behalf
                    // sends its own or none, and then ?locale= is the way. Which languages people
                    // search in, not where they are: nothing here reads an address.
                    var locale = StatisticsService.CleanLocale(Request.Query["locale"].FirstOrDefault())
                                 ?? StatisticsService.CleanLocale(StatisticsService.FirstAcceptLanguage(Request.Headers.AcceptLanguage));
                    bool counted = !string.Equals(Request.Query["count"].FirstOrDefault()?.Trim(), "false",
                        StringComparison.OrdinalIgnoreCase);
                    // The header is minted either way, so a client can always read one; a click
                    // that references an unrecorded search is accepted as an orphan and still
                    // counts on the document.
                    var filterKey = statistics.FilterKeyToRecord(resolvedFilterKey, ctx.OwnerKey, dataSetName);
                    if (StatisticsService.ShouldRecord(query, filterKey, res))
                        statistics.Writer!.RecordSearch(new SearchEventRow(
                            queryId, ctx.OwnerKey, dataSetName, query.Text ?? string.Empty,
                            // The resolved filter's serialized key: it resolved, or the search
                            // above would have thrown.
                            filterKey,
                            res.Records?.Length ?? 0, string.IsNullOrWhiteSpace(subject) ? null : subject,
                            askedAt,
                            Session: string.IsNullOrWhiteSpace(session) ? null : session,
                            Source: source, Counted: counted,
                            Covered: StatisticsService.CoverageConfirmed(query, res),
                            Typed: string.IsNullOrWhiteSpace(typed) ? null : typed.Length > 1000 ? typed[..1000] : typed,
                            Locale: locale));
                }
                return new SearchOutcome(res, null, refocused, fromDataset, minted, tSearch);
            }
            catch (UnknownFilterException ex)
            {
                return Failed(ApiProblems.UnknownFilter(ex.Message));
            }
            catch (InvalidFilterExpressionException ex)
            {
                return Failed(ApiProblems.InvalidFilterExpression(ex.Expression, ex.Errors));
            }
            // PrepareSorting throws ArgumentException("invalid type") for a SortBy field whose type
            // is not one it can compare. Reaching it takes a field that is Sortable and untyped, which
            // the Field setters now refuse and Deserialize strips, and Search nulls a SortBy that is
            // not Sortable before sorting runs - so this is a guard, not a live path. It is here
            // because the cost of being wrong is asymmetric: a caller mistake logged as our incident
            // costs us an alert and tells them nothing. Same mapping fields/configuration already uses.
            catch (ArgumentException ex)
            {
                return Failed(ApiProblems.InvalidArgument(ex.Message));
            }

            static SearchOutcome Failed(ObjectResult problem) => new(null, problem, RefocusOutcome.None, [], null, 0);
        }

        /// <summary>The result as the response writes it, with the records' JSON in
        /// <c>documents</c>, in the order of <c>records</c>: each an object, null for a record gone
        /// since the search.</summary>
        private System.Text.Json.Nodes.JsonObject WithDocuments(Indx.Api.Result result, IServerSearchEngine engine)
        {
            var node = ToNode(result);
            var documents = new System.Text.Json.Nodes.JsonArray();
            foreach (var json in engine.GetJsonDataOfKeys(result.Records ?? []))
                documents.Add(string.IsNullOrEmpty(json) ? null : System.Text.Json.Nodes.JsonNode.Parse(json));
            node["documents"] = documents;
            return node;
        }

        /// <summary>Which of the result's records hold cold storage the caller may read, by key, from
        /// the store's memory (Notes/cold-storage.md): a Search key sees the public items only.</summary>
        private Dictionary<long, string[]> ColdOf(TeamContext ctx, string dataSetName, Indx.Api.Result result)
        {
            if (IndxServerInternalApi.Manager.Cold is not { } store || result.Records is not { Length: > 0 } records) return [];
            var reader = ApiKeyScope.For(HttpContext) is { } scope && !scope.AllowsLevel(ApiKeyLevel.Read)
                ? ColdReader.Browser : ColdReader.Owner;
            return store.NamesOf(ctx.OwnerKey, dataSetName, records.Select(r => r.DocumentKey), reader);
        }

        /// <summary>The answer with <c>cold</c>, the names each record holds by its key, where any does.</summary>
        private static System.Text.Json.Nodes.JsonObject WithCold(System.Text.Json.Nodes.JsonObject node, Dictionary<long, string[]> cold)
        {
            if (cold.Count == 0) return node;
            var map = new System.Text.Json.Nodes.JsonObject();
            foreach (var (key, names) in cold)
                map[key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonArray(names.Select(n => (System.Text.Json.Nodes.JsonNode?)n).ToArray());
            node["cold"] = map;
            return node;
        }

        /// <summary>The result as the response writes it (MVC's own JSON options), as a node.</summary>
        private System.Text.Json.Nodes.JsonObject ToNode(Indx.Api.Result result) =>
            System.Text.Json.JsonSerializer.SerializeToNode(result, result.GetType(), mvcJson.Value.JsonSerializerOptions)!.AsObject();

        /// <summary>A failed search in a multi-search's list: the status and the problem a single
        /// search would have answered with.</summary>
        private System.Text.Json.Nodes.JsonObject ErrorOf(ObjectResult problem) => new()
        {
            ["error"] = System.Text.Json.JsonSerializer.SerializeToNode(problem.Value, mvcJson.Value.JsonSerializerOptions),
            ["status"] = problem.StatusCode,
        };

        /// <summary>
        /// Searches a single embedding field using approximate nearest-neighbour search.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search/vector")]
        public ActionResult<Indx.Http.EmbeddingResultEntry[]> VectorSearch(
            string teamName, string dataSetName, [FromBody] Indx.Http.VectorQueryProxy query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query?.Vector == null || query.Vector.Length == 0)
                return ApiProblems.InvalidArgument("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.FieldName))
                return ApiProblems.InvalidArgument("FieldName is required");
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "VectorSearch", SystemState.Ready) is { } stateError)
                return stateError;
            try
            {
                return IndxServerInternalApi.Manager.VectorSearch(query, dataSetName, ctx.OwnerKey);
            }
            catch (UnknownFilterException ex)
            {
                return ApiProblems.UnknownFilter(ex.Message);
            }
            catch (InvalidFilterExpressionException ex)
            {
                return ApiProblems.InvalidFilterExpression(ex.Expression, ex.Errors);
            }
            catch (UnknownEmbeddingFieldException ex)
            {
                return ApiProblems.InvalidArgument(ex.Message);
            }
            catch (ArgumentException ex)
            {
                // Wrong vector length for this field's index — the message names both lengths.
                return ApiProblems.InvalidArgument(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // The field is embeddable but its index was never built: no document carried a
                // vector for it. A caller mistake, not a server fault.
                return ApiProblems.InvalidArgument(ex.Message);
            }
        }

        /// <summary>
        /// Combines text search with embedding nearest-neighbour search and blends scores.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search/hybrid")]
        public ActionResult<Indx.Http.EmbeddingResultEntry[]> HybridSearch(
            string teamName, string dataSetName, [FromBody] Indx.Http.HybridQueryProxy query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query?.Vector == null || query.Vector.Length == 0)
                return ApiProblems.InvalidArgument("Vector must be a non-empty float array");
            if (string.IsNullOrEmpty(query.EmbeddingField))
                return ApiProblems.InvalidArgument("EmbeddingField is required");
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "HybridSearch", SystemState.Ready) is { } stateError)
                return stateError;
            try
            {
                return IndxServerInternalApi.Manager.HybridSearch(query, dataSetName, ctx.OwnerKey);
            }
            catch (UnknownFilterException ex)
            {
                return ApiProblems.UnknownFilter(ex.Message);
            }
            catch (InvalidFilterExpressionException ex)
            {
                return ApiProblems.InvalidFilterExpression(ex.Expression, ex.Errors);
            }
            catch (UnknownEmbeddingFieldException ex)
            {
                return ApiProblems.InvalidArgument(ex.Message);
            }
            catch (ArgumentException ex)
            {
                // Wrong vector length for this field's index — the message names both lengths.
                return ApiProblems.InvalidArgument(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // The field is embeddable but its index was never built: no document carried a
                // vector for it. A caller mistake, not a server fault.
                return ApiProblems.InvalidArgument(ex.Message);
            }
        }
    }
}
