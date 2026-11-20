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
        Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> mvcJson, FacetValueIndex facetValues)
        : DatasetApiController(resolver)
    {
        /// <summary>A search's own steps as a <c>Server-Timing</c> header (milliseconds): the team
        /// lookup, the dataset lookup with its state check, the engine's search, and what follows it
        /// (the query-parameter header, the statistics). What a browser's network panel and
        /// <c>curl -D -</c> show, so the time a search costs can be split from the outside: on
        /// cloud.indx.co a search took about twice the library's own time (22 Oct 2026), and this is
        /// how to see where. Off with <c>Indx:ServerTiming=false</c>.</summary>
        private void ServerTiming(long start, long team, long dataset, long search)
            => ServerTiming(start, team, dataset, search - dataset, 1);

        /// <summary><see cref="ServerTiming(long, long, long, long)"/> for a call of several searches:
        /// <paramref name="searching"/> is their engine time together (Stopwatch ticks), named with how
        /// many there were, and <c>after</c> what is left of the call (the records' JSON, the cold
        /// storage, the statistics).</summary>
        private void ServerTiming(long start, long team, long dataset, long searching, int searches)
        {
            if (!configuration.GetValue("Indx:ServerTiming", true)) return;
            var end = Stopwatch.GetTimestamp();
            static string Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var search = dataset + searching;
            var desc = searches == 1 ? "" : $";desc=\"{searches} searches\"";
            Response.Headers["Server-Timing"] =
                $"team;dur={Ms(start, team)}, dataset;dur={Ms(team, dataset)}, search;dur={Ms(dataset, search)}{desc}, after;dur={Ms(search, end)}, total;dur={Ms(start, end)}";
        }

        /// <summary>
        /// Search will validate the search query and return the search result. With
        /// <c>includeDocuments</c> the records' JSON comes with it, in <c>documents</c>, in the order
        /// of <c>records</c> (null for a record gone since), so a search is one call, not two;
        /// <c>includeFields</c> keeps only the fields named.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search")]
        public ActionResult<Indx.Api.Result> Search(string teamName, string dataSetName, [FromBody] ServerQueryProxy query)
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
            var outcome = RunSearch(ctx, dataSetName, matcher, query, SearchStats.FromQueryString);
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
                return Ok(WithCold(WithDocuments(outcome.Result!, matcher, query.IncludeFields), cold));
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
        /// <para>A search may say for itself whether it counts in the statistics, and its source and
        /// session (<c>count</c>, <c>source</c>, <c>session</c> in its body), over the query string's:
        /// a page sends its visitor's search beside its helper searches and counts only the first
        /// (12 Nov 2026). <c>Server-Timing</c> gives the searches' time together.</para>
        /// <para>The engine searches them in one call, at once on the search slots it has free and one
        /// after the other when none is (ISearchEngine.Search(Query[]), 12 Nov 2026); <c>Server-Timing</c>'s
        /// search is the time until the last is answered.</para>
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search/multi")]
        public ActionResult MultiSearch(string teamName, string dataSetName, [FromBody] MultiSearchItem[] queries)
        {
            var tStart = Stopwatch.GetTimestamp();
            var ctx = ResolveTeam(teamName, out var error);
            var tTeam = Stopwatch.GetTimestamp();
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
            var tDataset = Stopwatch.GetTimestamp();
            var outcomes = RunSearches(ctx, dataSetName, [.. queries.Select(query => ((QueryProxy?)query,
                query == null ? SearchStats.NotRecorded : SearchStats.FromQueryString with
                {
                    Count = query.Count, Source = query.Source, Session = query.Session,
                }))], out long searching);
            var answers = new System.Text.Json.Nodes.JsonArray();
            for (int i = 0; i < queries.Length; i++)
                answers.Add(outcomes[i] switch
                {
                    null => ErrorOf(ApiProblems.InvalidArgument("A search in the list is null.")),
                    { Problem: { } problem } => ErrorOf(problem),
                    var outcome => Answer(ctx, dataSetName, matcher, queries[i], outcome),
                });
            ServerTiming(tStart, tTeam, tDataset, searching, queries.Length);
            return Ok(answers);
        }

        /// <summary>The most groups one <see cref="GroupedSearch"/> takes.</summary>
        public const int MaxGroupsPerCall = MaxSearchesPerCall;

        /// <summary>
        /// One text, answered in named sections: a search box's products, brands, articles and pages
        /// in one call, each section with its own filter (ANDed with the search's), rows and count
        /// (12 Nov 2026: one search per keystroke instead of several). A section may count
        /// by another filter than it lists (<c>countFilter</c>: list the ones in stock, count them
        /// all), and take its own candidates, ranking and match. The answer is
        /// <c>{ "groups": { name: result }, "queryId" }</c>, each result as a single search's, a
        /// section that fails an error in its place.
        /// <para>One search in the statistics: the section marked <c>primary</c>, else the first,
        /// whose <c>queryId</c> (also the <c>Indx-Query-Id</c> header) a click in any section
        /// references. Each section is its own search in the engine, all of them searched in one call,
        /// at once on the free search slots (ISearchEngine.Search(Query[]), 12 Nov 2026); were the
        /// library to match once for them all,
        /// the request and answer would stay as they are.</para>
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search/groups")]
        public ActionResult GroupedSearch(string teamName, string dataSetName, [FromBody] GroupedSearchRequest request)
        {
            var tStart = Stopwatch.GetTimestamp();
            var ctx = ResolveTeam(teamName, out var error);
            var tTeam = Stopwatch.GetTimestamp();
            if (ctx == null) return error!;
            if (request?.Groups is not { Length: > 0 } groups)
                return ApiProblems.InvalidArgument("The search needs one or more groups.");
            if (groups.Length > MaxGroupsPerCall)
                return ApiProblems.InvalidArgument($"{groups.Length} groups; one search takes at most {MaxGroupsPerCall}.");
            if (groups.Any(g => g == null || string.IsNullOrWhiteSpace(g.Name)))
                return ApiProblems.InvalidArgument("Every group needs a name.");
            if (groups.GroupBy(g => g.Name, StringComparer.Ordinal).FirstOrDefault(n => n.Count() > 1) is { } twice)
                return ApiProblems.InvalidArgument($"Two groups are named '{twice.Key}'; a name answers one group.");
            if (groups.Count(g => g.Primary) > 1)
                return ApiProblems.InvalidArgument("Only one group can be primary: the one that is the visitor's search.");
            if (groups.FirstOrDefault(g => g.Rows < 0) is { } negative)
                return ApiProblems.InvalidArgument($"Group '{negative.Name}' asks for {negative.Rows} rows.");
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "Search", SystemState.Ready) is { } stateError)
                return stateError;
            var tDataset = Stopwatch.GetTimestamp();

            var primary = groups.FirstOrDefault(g => g.Primary) ?? groups[0];
            string? queryId = null;
            // Each group's search, then each count by its own filter: the same search, one row,
            // nothing recorded. All searched by the engine in one call.
            var listed = groups.Select(group => GroupQuery(request, group, group.Filter, group.Rows)).ToArray();
            var counting = groups.Select(group =>
            {
                if (group.CountFilter == null) return null;
                var count = GroupQuery(request, group, group.CountFilter, 1);
                count.IncludeDocuments = false;
                // Facets cost and are thrown away here, but an empty text is answered through
                // them (Notes/Jens/empty-search-without-facetable-field-2026-11-08.md): off, a
                // browse would count nothing.
                if (!string.IsNullOrWhiteSpace(count.Text)) count.EnableFacets = false;
                return count;
            }).ToArray();
            var outcomes = RunSearches(ctx, dataSetName,
                [.. listed.Select((query, i) => ((QueryProxy?)query, groups[i] == primary ? SearchStats.FromQueryString : SearchStats.NotRecorded)),
                 .. counting.Select(query => ((QueryProxy?)query, SearchStats.NotRecorded))], out long searching);
            int searches = listed.Length + counting.Count(c => c != null);
            var answers = new System.Text.Json.Nodes.JsonObject();
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                var outcome = outcomes[i]!;
                if (outcome.Problem != null)
                {
                    answers[group.Name] = ErrorOf(outcome.Problem);
                    continue;
                }
                if (group == primary) queryId = outcome.QueryId;
                var answer = Answer(ctx, dataSetName, matcher, listed[i], outcome with { QueryId = null });
                if (outcomes[groups.Length + i] is { } counted)
                {
                    if (counted.Problem != null)
                    {
                        answers[group.Name] = ErrorOf(counted.Problem);
                        continue;
                    }
                    answer["count"] = counted.Result!.Count;
                }
                answers[group.Name] = answer;
            }
            if (queryId != null)
                Response.Headers["Indx-Query-Id"] = queryId;
            ServerTiming(tStart, tTeam, tDataset, searching, searches);
            var body = new System.Text.Json.Nodes.JsonObject { ["groups"] = answers };
            if (queryId != null) body["queryId"] = queryId;
            return Ok(body);
        }

        /// <summary>The most values one <see cref="FacetValueSearch"/> answers: InstantSearch's
        /// <c>maxFacetHits</c> goes to 100.</summary>
        public const int MaxFacetValueRows = 100;

        /// <summary>
        /// Searches the values of a facetable field, for a long facet's own search field ("Search
        /// actors…", InstantSearch's searchable refinement list): the values that match what was typed,
        /// with Indx's matching (typos, word starts, whole words anywhere in a value), each with how
        /// many records hold it, the most first (FacetValueIndex, 14 Nov 2026). With <c>search</c>
        /// the counts are of that search's records, as its facet would count them, and a value it
        /// holds none of is left out; a field ticked in its <c>facetSelections</c> is counted without
        /// its own ticks, as InstantSearch asks. Answers <c>{ values: [{ value, count }], exhaustive }</c>;
        /// the counts are exact. Nothing goes into the statistics.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/facets/{field}/search")]
        public ActionResult FacetValueSearch(string teamName, string dataSetName, string field, [FromBody] FacetValueSearchRequest request)
        {
            var tStart = Stopwatch.GetTimestamp();
            var ctx = ResolveTeam(teamName, out var error);
            var tTeam = Stopwatch.GetTimestamp();
            if (ctx == null) return error!;
            if (request == null) return ApiProblems.InvalidArgument("The body is required.");
            if (request.Rows is < 1 or > MaxFacetValueRows)
                return ApiProblems.InvalidArgument($"rows is from 1 to {MaxFacetValueRows}.");
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null) return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "Search", SystemState.Ready) is { } stateError) return stateError;
            if (matcher.DocumentFields?.GetField(field) is not { Facetable: true })
                return ApiProblems.InvalidArgument($"'{field}' is not a facetable field of this dataset.");
            if (request.Values is { Length: > FacetValueIndex.AmongLimit })
                return ApiProblems.InvalidArgument($"values holds at most {FacetValueIndex.AmongLimit}.");
            var tDataset = Stopwatch.GetTimestamp();

            // The counts: the facet over the visitor's search, or over every record. Over every record
            // it is the engine's own empty search, which the engine caches and clears on any change;
            // the counts made from its answer are kept with that answer, so a keystroke costs no
            // counting and a change in the data reaches the counts at once (14 Nov 2026).
            Dictionary<string, int> counts;
            long searchedAt;
            if (request.Search == null)
            {
                var facet = IndxServerInternalApi.Manager.Search(
                    new QueryProxy { Text = "", EnableFacets = true, FacetFields = [field], FacetValueLimit = 0, MaxNumberOfRecordsToReturn = 0 },
                    dataSetName, ctx.OwnerKey).Facets?.GetValueOrDefault(field) ?? [];
                counts = CountsOf(facet);
                searchedAt = Stopwatch.GetTimestamp();
            }
            else
            {
                var options = mvcJson.Value.JsonSerializerOptions;
                var counting = JsonSerializer.Deserialize<ServerQueryProxy>(JsonSerializer.Serialize(request.Search, options), options)!;
                counting.EnableFacets = true;
                counting.FacetFields = [field];
                counting.FacetValueLimit = 0;
                counting.MaxNumberOfRecordsToReturn = 0;
                counting.IncludeDocuments = false;
                var outcome = RunSearch(ctx, dataSetName, matcher, counting, SearchStats.NotRecorded);
                if (outcome.Problem != null) return outcome.Problem;
                counts = CountsOf(outcome.Result?.Facets?.GetValueOrDefault(field) ?? []);
                searchedAt = outcome.SearchedAt;
            }

            IEnumerable<KeyValuePair<string, int>> values;
            IReadOnlyList<string> found = [];
            bool fallback = false;
            var text = request.Text?.Trim() ?? "";
            // Every value of the field with its count over every record: the engine's cached empty
            // search, a new answer only when the data changed (FacetValueIndex keeps in step by it).
            KeyValuePair<string, int>[] All() => IndxServerInternalApi.Manager.Search(
                    new QueryProxy { Text = "", EnableFacets = true, FacetFields = [field], FacetValueLimit = 0, MaxNumberOfRecordsToReturn = 0 },
                    dataSetName, ctx.OwnerKey).Facets?.GetValueOrDefault(field) ?? [];
            if (text.Length == 0)
            {
                values = counts.OrderByDescending(v => v.Value);
                // The panel asks with no text when its field gets the focus: the micro-index is built
                // then, not on the first letter typed (0.7 s for 89 367 actors).
                _ = Task.Run(() => facetValues.Warm(ctx.OwnerKey, dataSetName, matcher, field, All));
            }
            else
            {
                // Among the values the caller names (a panel's own, whose counts it holds), or those a
                // visitor's search holds when they are few; else the whole field, and the values the
                // search does not hold dropped.
                var among = request.Values ?? (request.Search != null && counts.Count <= FacetValueIndex.AmongLimit ? counts.Keys.ToArray() : null);
                (found, fallback) = among != null
                    ? facetValues.FindAmong(ctx.OwnerKey, dataSetName, field, among, text)
                    : facetValues.Find(ctx.OwnerKey, dataSetName, matcher, field, text, All);
                values = found.Where(counts.ContainsKey).Select(v => new KeyValuePair<string, int>(v, counts[v]));
                // The match decides which values, the count their order: "jackson" gives Samuel L.
                // Jackson (111 films) before Jackson Yee (5), whose name merely starts with it. Not when
                // no value held every word: then the closest first, as the micro-index ordered them.
                if (!fallback) values = values.OrderByDescending(v => v.Value);
            }
            var answer = values.Take(request.Rows)
                .Select(v => new { value = v.Key, count = v.Value }).ToList();
            ServerTiming(tStart, tTeam, tDataset, searchedAt);
            return Ok(new { values = answer, fallback, exhaustive = true });
        }

        /// <summary>A facet's counts by value, kept with the facet array the engine answered: the same
        /// array (the engine's cached empty search) gives the same counts without making them again,
        /// and a new array, after any change to the data, makes new ones.</summary>
        private static Dictionary<string, int> CountsOf(KeyValuePair<string, int>[] facet) =>
            FacetCounts.GetValue(facet, f =>
            {
                var counts = new Dictionary<string, int>(f.Length, StringComparer.Ordinal);
                foreach (var (value, count) in f)
                    if (count > 0 && value is not ("null" or "")) counts[value] = count;
                return counts;
            });

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<KeyValuePair<string, int>[], Dictionary<string, int>> FacetCounts = new();

        /// <summary>A group's search: the request's, its filter ANDed with <paramref name="filter"/>,
        /// <paramref name="rows"/> rows, and the group's own candidates, ranking and match where it
        /// gives them. A copy, so one group's settings never reach the next.</summary>
        private ServerQueryProxy GroupQuery(GroupedSearchRequest request, SearchGroup group, string? filter, int rows)
        {
            var options = mvcJson.Value.JsonSerializerOptions;
            var query = JsonSerializer.Deserialize<ServerQueryProxy>(JsonSerializer.Serialize<ServerQueryProxy>(request, options), options)!;
            query.MaxNumberOfRecordsToReturn = rows;
            query.FilterExpression = (query.FilterExpression, filter) switch
            {
                ({ Length: > 0 } a, { Length: > 0 } b) => $"({a}) AND ({b})",
                (_, { Length: > 0 } b) => b,
                var (a, _) => a,
            };
            if (group.Candidates != null) query.Candidates = group.Candidates;
            if (group.Ranking != null) query.Ranking = group.Ranking;
            if (group.Match != null) query.Match = group.Match;
            return query;
        }

        /// <summary>One search's answer in a list: the result, its records' JSON when asked for, the
        /// cold storage they hold, and its <c>queryId</c>.</summary>
        private System.Text.Json.Nodes.JsonObject Answer(TeamContext ctx, string dataSetName, IServerSearchEngine matcher,
            ServerQueryProxy query, SearchOutcome outcome)
        {
            var answer = WithCold(query.IncludeDocuments ? WithDocuments(outcome.Result!, matcher, query.IncludeFields) : ToNode(outcome.Result!),
                ColdOf(ctx, dataSetName, outcome.Result!));
            if (outcome.QueryId != null) answer["queryId"] = outcome.QueryId;
            return answer;
        }

        /// <summary>What one search came to: its result, or the problem a single search answers
        /// with; the refocus, the parameters the dataset decided and the query id, which a single
        /// search sends as headers.</summary>
        private sealed record SearchOutcome(Indx.Api.Result? Result, ObjectResult? Problem, RefocusOutcome Refocused,
            IReadOnlyList<string> FromDataset, string? QueryId, long SearchedAt);

        /// <summary>How a search goes into the statistics: recorded or not at all, and whether it
        /// counts, its source and its session where the search says so itself (null: the query
        /// string's <c>?count=</c>, <c>?source=</c>, <c>?session=</c>).</summary>
        private sealed record SearchStats(bool Record, bool? Count = null, string? Source = null, string? Session = null)
        {
            public static readonly SearchStats FromQueryString = new(true);
            public static readonly SearchStats NotRecorded = new(false);
        }

        /// <summary>One search as the search route runs it, for <see cref="Search"/>,
        /// <see cref="MultiSearch"/> and <see cref="GroupedSearch"/> alike: one way in, so they cannot drift.</summary>
        private SearchOutcome RunSearch(TeamContext ctx, string dataSetName, IServerSearchEngine matcher, QueryProxy query, SearchStats stats)
        {
            // When the search was asked for, not when it finished: a short text can take longer than
            // the keystroke after it, and the keystroke rule reads the session's searches in order.
            long askedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Objection(query) is { } objection)
                return Failed(objection);
            IndxServerInternalApi.Searched searched;
            try
            {
                var res = IndxServerInternalApi.Manager.Search(query, dataSetName, ctx.OwnerKey, out var filterKey, RefocusAsked());
                searched = new(res, filterKey, null);
            }
            catch (Exception ex) when (ex is UnknownFilterException or InvalidFilterExpressionException or ArgumentException)
            {
                searched = new(null, null, ex);
            }
            return Finish(ctx, dataSetName, query, stats, askedAt, searched);
        }

        /// <summary>?refocus=true: a search no record holds every word of comes back in BM25F's
        /// order (Services.Refocusing); intrface asks while the judge reads a question or a
        /// description.</summary>
        private bool RefocusAsked() =>
            string.Equals(Request.Query["refocus"].FirstOrDefault()?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Why a search is refused before it runs, or null. The fields collapsed by come
        /// with the documents (12 Nov 2026): a record left out for its value must be seen to hold it.</summary>
        private static ObjectResult? Objection(QueryProxy query) =>
            query is ServerQueryProxy { IncludeFields: { Length: > 0 } include, CollapseBy: { Length: > 0 } collapse }
                && collapse.Except(include).ToList() is { Count: > 0 } missing
                ? ApiProblems.InvalidArgument($"includeFields must hold the fields of collapseBy; it lacks {string.Join(", ", missing)}.")
                : null;

        private static SearchOutcome Failed(ObjectResult problem) => new(null, problem, RefocusOutcome.None, [], null, 0);

        /// <summary>
        /// Several searches as the search route runs each, the engine searching them in one call
        /// (<see cref="IndxServerInternalApi.SearchMany"/>: at once on its free search slots): for
        /// <see cref="MultiSearch"/> and <see cref="GroupedSearch"/>. A null query is answered null,
        /// for the caller to say why. <paramref name="searching"/> is the time the engine took for
        /// them all.
        /// </summary>
        private SearchOutcome?[] RunSearches(TeamContext ctx, string dataSetName, (QueryProxy? Query, SearchStats Stats)[] searches, out long searching)
        {
            long askedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var outcomes = new SearchOutcome?[searches.Length];
            var proxies = new QueryProxy?[searches.Length];
            for (int i = 0; i < searches.Length; i++)
            {
                if (searches[i].Query is not { } query) continue;
                if (Objection(query) is { } objection) outcomes[i] = Failed(objection);
                else proxies[i] = query;
            }
            var before = Stopwatch.GetTimestamp();
            var searched = IndxServerInternalApi.Manager.SearchMany(proxies, dataSetName, ctx.OwnerKey, RefocusAsked());
            searching = Stopwatch.GetTimestamp() - before;
            for (int i = 0; i < searches.Length; i++)
                if (proxies[i] is { } query)
                    outcomes[i] = Finish(ctx, dataSetName, query, searches[i].Stats, askedAt, searched[i]);
            return outcomes;
        }

        /// <summary>What follows a search, for one or several: its problem when it was refused, the
        /// refocus, the parameters the dataset decided, and the statistics.</summary>
        private SearchOutcome Finish(TeamContext ctx, string dataSetName, QueryProxy query, SearchStats stats, long askedAt,
            IndxServerInternalApi.Searched searched)
        {
            switch (searched.Error)
            {
                case UnknownFilterException ex: return Failed(ApiProblems.UnknownFilter(ex.Message));
                case InvalidFilterExpressionException ex: return Failed(ApiProblems.InvalidFilterExpression(ex.Expression, ex.Errors));
                // PrepareSorting throws ArgumentException("invalid type") for a SortBy field whose type
                // is not one it can compare. Reaching it takes a field that is Sortable and untyped, which
                // the Field setters now refuse and Deserialize strips, and Search nulls a SortBy that is
                // not Sortable before sorting runs - so this is a guard, not a live path. It is here
                // because the cost of being wrong is asymmetric: a caller mistake logged as our incident
                // costs us an alert and tells them nothing. Same mapping fields/configuration already uses.
                case ArgumentException ex: return Failed(ApiProblems.InvalidArgument(ex.Message));
            }
            var res = searched.Result!;
            var resolvedFilterKey = searched.FilterKey;
            {
                var refocused = Refocusing.OutcomeOf(res);
                var tSearch = Stopwatch.GetTimestamp();
                var fromDataset = QueryParameterResolution.Resolve(query,
                    queryParameters.Load(ctx.OwnerKey, dataSetName)).FromDataset;
                string? minted = null;
                // The search event is logged server-side - that is what makes the zero-hit list
                // exist at all - and the minted queryId is what select/convert events reference.
                // Cost on this path is a struct and a queue append (measured in
                // Notes/statistics-design.md); the optional ?subject= gives per-subject history.
                if (statistics.Enabled && stats.Record)
                {
                    var queryId = Guid.NewGuid().ToString("N");
                    minted = queryId;
                    var subject = Request.Query["subject"].FirstOrDefault();
                    // ?session= is any per-page-load random id. It identifies no one; it lets the
                    // rollup see that "o", "os", "osl", "oslo" were one visitor typing one search.
                    var session = stats.Session ?? Request.Query["session"].FirstOrDefault();
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
                    var source = StatisticsService.CleanSource(stats.Source ?? Request.Query["source"].FirstOrDefault());
                    // ?locale=<tag> is the language the page was in, as the storefront reports it
                    // (intrface sends the document's lang, else the browser's). Without it, the
                    // browser's own preference from Accept-Language, which a storefront calling from
                    // the visitor's browser sends for free; a server calling on the visitor's behalf
                    // sends its own or none, and then ?locale= is the way. Which languages people
                    // search in, not where they are: nothing here reads an address.
                    var locale = StatisticsService.CleanLocale(Request.Query["locale"].FirstOrDefault())
                                 ?? StatisticsService.CleanLocale(StatisticsService.FirstAcceptLanguage(Request.Headers.AcceptLanguage));
                    bool counted = stats.Count ?? !string.Equals(Request.Query["count"].FirstOrDefault()?.Trim(), "false",
                        StringComparison.OrdinalIgnoreCase);
                    // The header is minted either way, so a client can always read one; a click
                    // that references an unrecorded search is accepted as an orphan and still
                    // counts on the document.
                    var filterKey = statistics.FilterKeyToRecord(resolvedFilterKey, ctx.OwnerKey, dataSetName);
                    // ?pickField=&pickValue=&pickText=&pickFallback=true: this search is the one a tick
                    // made, of a value found by typing into its facet's own search field (intrface's
                    // ValueFilterPanel). Only with the filter recorded: the pick is a step of it.
                    var pick = filterKey == null ? null : StatisticsService.CleanPick(
                        Request.Query["pickField"].FirstOrDefault(), Request.Query["pickValue"].FirstOrDefault(),
                        Request.Query["pickText"].FirstOrDefault(), Request.Query["pickFallback"].FirstOrDefault());
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
                            Locale: locale,
                            PickField: pick?.Field, PickValue: pick?.Value, PickText: pick?.Text, PickFallback: pick?.Fallback));
                }
                return new SearchOutcome(res, null, refocused, fromDataset, minted, tSearch);
            }
        }

        /// <summary>The result as the response writes it, with the records' JSON in
        /// <c>documents</c>, in the order of <c>records</c>: each an object, null for a record gone
        /// since the search. With <paramref name="fields"/>, each record with those top-level fields
        /// only: a display card and an id rather than the long text and the filter fields.</summary>
        private System.Text.Json.Nodes.JsonObject WithDocuments(Indx.Api.Result result, IServerSearchEngine engine, string[]? fields)
        {
            var node = ToNode(result);
            var kept = fields is { Length: > 0 } ? new HashSet<string>(fields, StringComparer.Ordinal) : null;
            var documents = new System.Text.Json.Nodes.JsonArray();
            foreach (var json in engine.GetJsonDataOfKeys(result.Records ?? []))
            {
                var document = string.IsNullOrEmpty(json) ? null : System.Text.Json.Nodes.JsonNode.Parse(json);
                if (kept != null && document is System.Text.Json.Nodes.JsonObject whole)
                {
                    var only = new System.Text.Json.Nodes.JsonObject();
                    foreach (var name in whole.Select(p => p.Key).Where(kept.Contains).ToList())
                    {
                        var value = whole[name];
                        whole.Remove(name);
                        only[name] = value;
                    }
                    document = only;
                }
                documents.Add(document);
            }
            node["documents"] = documents;
            return node;
        }

        /// <summary>Which of the result's records hold cold storage the caller may read, by key, from
        /// the store's memory, as <see cref="ColdValues.AccessFor"/> lets the caller.</summary>
        private Dictionary<long, string[]> ColdOf(TeamContext ctx, string dataSetName, Indx.Api.Result result)
        {
            if (IndxServerInternalApi.Manager.Cold is not { } store || result.Records is not { Length: > 0 } records) return [];
            if (ColdValues.AccessFor(ApiKeyScope.For(HttpContext), ctx.Role) is not { } access) return [];
            return store.NamesOf(ctx.OwnerKey, dataSetName, records.Select(r => r.DocumentKey), access);
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
