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
        QueryParameterStore queryParameters, IConfiguration configuration)
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
        /// Search will validate the search query and return the search result.
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
            // When the search was asked for, not when it finished: a short text can take longer than
            // the keystroke after it, and the keystroke rule reads the session's searches in order.
            long askedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                Indx.Api.Result res = IndxServerInternalApi.Manager.Search(query, dataSetName, ctx.OwnerKey);
                var tSearch = Stopwatch.GetTimestamp();
                // Which values the dataset's query parameters decided, because the request left
                // them out: "why does my search truncate" is answered by one response. Absent
                // when the request (or the engine default) decided everything.
                var fromDataset = QueryParameterResolution.Resolve(query,
                    queryParameters.Load(ctx.OwnerKey, dataSetName)).FromDataset;
                if (fromDataset.Count > 0)
                    Response.Headers[QueryParameterResolution.ResponseHeader] = string.Join(", ", fromDataset);
                // The search event is logged server-side - that is what makes the zero-hit list
                // exist at all - and the minted queryId is what select/convert events reference.
                // Cost on this path is a struct and a queue append (measured in
                // Notes/statistics-design.md); the optional ?subject= gives per-subject history.
                if (statistics.Enabled)
                {
                    var queryId = Guid.NewGuid().ToString("N");
                    Response.Headers["Indx-Query-Id"] = queryId;
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
                    bool counted = !string.Equals(Request.Query["count"].FirstOrDefault()?.Trim(), "false",
                        StringComparison.OrdinalIgnoreCase);
                    // The header is minted either way, so a client can always read one; a click
                    // that references an unrecorded search is accepted as an orphan and still
                    // counts on the document.
                    var filterKey = statistics.FilterKeyToRecord(query, ctx.OwnerKey, dataSetName);
                    if (StatisticsService.ShouldRecord(query, filterKey, res))
                        statistics.Writer!.RecordSearch(new SearchEventRow(
                            queryId, ctx.OwnerKey, dataSetName, query.Text ?? string.Empty,
                            // The token IS the filter's serialized key, and it resolved, or the
                            // search above would have thrown UnknownFilterException.
                            filterKey,
                            res.Records?.Length ?? 0, string.IsNullOrWhiteSpace(subject) ? null : subject,
                            askedAt,
                            Session: string.IsNullOrWhiteSpace(session) ? null : session,
                            Source: source, Counted: counted,
                            Covered: StatisticsService.CoverageConfirmed(query, res),
                            Typed: string.IsNullOrWhiteSpace(typed) ? null : typed.Length > 1000 ? typed[..1000] : typed));
                }
                ServerTiming(tStart, tTeam, tDataset, tSearch);
                return res;
            }
            catch (UnknownFilterException ex)
            {
                return ApiProblems.UnknownFilter(ex.Message);
            }
            // PrepareSorting throws ArgumentException("invalid type") for a SortBy field whose type
            // is not one it can compare. Reaching it takes a field that is Sortable and untyped, which
            // the Field setters now refuse and Deserialize strips, and Search nulls a SortBy that is
            // not Sortable before sorting runs - so this is a guard, not a live path. It is here
            // because the cost of being wrong is asymmetric: a caller mistake logged as our incident
            // costs us an alert and tells them nothing. Same mapping fields/configuration already uses.
            catch (ArgumentException ex)
            {
                return ApiProblems.InvalidArgument(ex.Message);
            }
        }

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
