using Asp.Versioning;
using IndxServer.Models;
using IndxServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Registers behavioural events from the search front-end: select (a result was chosen) and
    /// convert (something valuable happened). Cheap and fire-and-forget by design — the events
    /// are queued and written in batches (<see cref="StatisticsWriter"/>), so the answer is 202.
    /// A Search-level key suffices: this is the key that already sits in the storefront.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class EventsController(TeamContextResolver resolver, StatisticsService statistics)
        : DatasetApiController(resolver)
    {
        /// <summary>
        /// The user chose a result: the document, its 1-based position, and optionally the
        /// queryId from the search response's Indx-Query-Id header and a subject.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/events/select")]
        public IActionResult Select(string teamName, string dataSetName, [FromBody] SelectEventRequest request)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (request == null)
                return ApiProblems.InvalidArgument("Select event body is required");
            if (request.Position < 1)
                return ApiProblems.InvalidArgument("Position is 1-based and must be at least 1");
            if (statistics.Enabled)
                statistics.Writer!.RecordSelect(new SelectEventRow(
                    ctx.OwnerKey, dataSetName, Blank(request.QueryId), Number(ctx, dataSetName, request.Key, request.DocumentKey),
                    request.Position, Blank(request.Subject),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Counted: request.Count ?? true));
            return Accepted();
        }

        /// <summary>
        /// Something the customer considers valuable happened for a document: an order, an
        /// add-to-cart — the type is theirs. QueryId is optional; without it the conversion
        /// still counts on the document.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/events/convert")]
        public IActionResult Convert(string teamName, string dataSetName, [FromBody] ConvertEventRequest request)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (request == null)
                return ApiProblems.InvalidArgument("Convert event body is required");
            if (string.IsNullOrWhiteSpace(request.Type))
                return ApiProblems.InvalidArgument("Type is required — the customer's own name for the event, e.g. 'order'");
            if (statistics.Enabled)
                statistics.Writer!.RecordConvert(new ConvertEventRow(
                    ctx.OwnerKey, dataSetName, Blank(request.QueryId), Number(ctx, dataSetName, request.Key, request.DocumentKey),
                    request.Type.Trim(), request.Value, Blank(request.Currency), request.Quantity,
                    Blank(request.Subject), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Counted: request.Count ?? true));
            return Accepted();
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        /// <summary>The number the statistics keep a document by: its key as text when the event
        /// gives one (<see cref="DocumentsController.KeyNumber"/>), else its documentKey.</summary>
        private static long Number(TeamContext ctx, string dataSetName, string? key, long documentKey) =>
            string.IsNullOrEmpty(key) ? documentKey
                : DocumentsController.KeyNumber(IndxServer.Engine.IndxServerInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey), key);
    }
}
