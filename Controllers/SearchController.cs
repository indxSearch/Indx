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
    /// Search: text, vector and hybrid.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class SearchController(TeamContextResolver resolver, StatisticsService statistics)
        : DatasetApiController(resolver)
    {
        /// <summary>
        /// Search will validate the search query and return the search result.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/search")]
        public ActionResult<Indx.Api.Result> Search(string teamName, string dataSetName, [FromBody] QueryProxy query)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (query == null)
                return ApiProblems.InvalidArgument("Search query body is required");
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "Search", SystemState.Ready) is { } stateError)
                return stateError;
            try
            {
                Indx.Api.Result res = IndxServerInternalApi.Manager.Search(query, dataSetName, ctx.OwnerKey);
                // The search event is logged server-side - that is what makes the zero-hit list
                // exist at all - and the minted queryId is what select/convert events reference.
                // Cost on this path is a struct and a queue append (measured in
                // Notes/statistics-design.md); the optional ?subject= gives per-subject history.
                if (statistics.Enabled)
                {
                    var queryId = Guid.NewGuid().ToString("N");
                    Response.Headers["Indx-Query-Id"] = queryId;
                    var subject = Request.Query["subject"].FirstOrDefault();
                    statistics.Writer!.RecordSearch(new SearchEventRow(
                        queryId, ctx.OwnerKey, dataSetName, query.Text ?? string.Empty, null,
                        res.Records?.Length ?? 0, string.IsNullOrWhiteSpace(subject) ? null : subject,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }
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
