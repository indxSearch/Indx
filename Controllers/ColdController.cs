using IndxServer.Engine;
using IndxServer.Services;
using Indx.Storage;
using Indx.Utilities;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace IndxServer.Controllers
{
    /// <summary>An item as written: the content and what is said about it. The record, name and
    /// language are the route's and the query's; who wrote it is the server's to say.</summary>
    public sealed record ColdWrite(string Content, string? ContentType = null, string? Description = null, string? Visibility = null,
        string? SourceRef = null, DateTime? SourceUpdatedUtc = null);

    /// <summary>One item of a sync's batch: <see cref="ColdWrite"/> with the record it is of.</summary>
    public sealed record ColdBatchItem(long RecordKey, string Name, string Content, string? Language = null, string? ContentType = null,
        string? Description = null, string? Visibility = null, string? SourceRef = null, DateTime? SourceUpdatedUtc = null);

    /// <summary>An item as read; <see cref="Content"/> is null in a listing.</summary>
    public sealed record ColdView(string Name, string Language, long RecordKey, string? Content,
        int Length, string ContentType, string Description, string Visibility, string WrittenBy, string WrittenVia,
        string? SourceRef, DateTime? SourceUpdatedUtc, long Version, DateTime CreatedUtc, DateTime UpdatedUtc)
    {
        internal static ColdView Of(ColdItem n, bool withContent) => new(n.Name, n.Language, n.RecordKey,
            withContent ? n.Content : null, n.Content.Length, n.ContentType, n.Description, n.Visibility, n.WrittenBy, n.WrittenVia,
            n.SourceRef, n.SourceUpdatedUtc, n.Version, n.CreatedUtc, n.UpdatedUtc);
    }

    /// <summary>The outcome of one item in a batch.</summary>
    public sealed record ColdBatchOutcome(long RecordKey, string Name, string Language, string Outcome, string? Detail);

    /// <summary>
    /// Cold storage: items kept beside a record, never searched and not in memory, read when a
    /// question needs them: a book's table of contents, a review, a whole article
    /// (Notes/cold-storage.md). Reading takes a Search key, which sees the public items only; a Read
    /// or Full key and the console see every one. Writing takes a Full key or an editor. An item is
    /// private unless written otherwise. Who wrote an item is set from the caller, never from the body.
    /// </summary>
    public class ColdController(TeamContextResolver resolver, ColdStore cold) : DatasetApiController(resolver)
    {
        private const string DatasetCold = DataSetRoute + "/cold";
        private const string RecordCold = DataSetRoute + "/documents/{documentKey:long}/cold";

        /// <summary>A record's cold storage, without content: its table of contents, its reviews.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(RecordCold)]
        public ActionResult<ColdView[]> ListRecordCold(string teamName, string dataSetName, long documentKey)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            var scope = new ColdScope(ctx.OwnerKey, dataSetName, documentKey);
            if (RecordGone(ctx, scope)) return Array.Empty<ColdView>();
            return cold.List(scope, withContent: true, Reader())
                .Select(n => ColdView.Of(n, withContent: false)).ToArray();
        }

        /// <summary>One item with its content.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(RecordCold + "/{name}")]
        public ActionResult<ColdView> GetRecordColdItem(string teamName, string dataSetName, long documentKey, string name, [FromQuery] string? language = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            var scope = new ColdScope(ctx.OwnerKey, dataSetName, documentKey);
            var item = RecordGone(ctx, scope) ? null : cold.Get(scope, name, language ?? "");
            if (item == null || !ColdValues.Reads(Reader(), item.Visibility))
                return NotFoundItem(name, language);
            Response.Headers.ETag = $"\"{item.Version}\"";
            return ColdView.Of(item, withContent: true);
        }

        /// <summary>Writes an item, created or replaced whole. <c>If-Match</c> with the version read
        /// refuses the write (409) when another changed the item since. The record need not be loaded
        /// yet: a sync may write the items before the catalogue; an item whose record is not there is
        /// left out when read.</summary>
        [HttpPut(RecordCold + "/{name}")]
        public ActionResult<ColdView> PutRecordColdItem(string teamName, string dataSetName, long documentKey, string name, [FromBody] ColdWrite body, [FromQuery] string? language = null)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (body == null) return ApiProblems.InvalidArgument("The body must be an item: at least its content.");
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            long? ifVersion = null;
            if (Request.Headers.IfMatch.ToString() is { Length: > 0 } match)
            {
                if (!long.TryParse(match.Trim('"', ' ', 'W', '/'), out var version))
                    return ApiProblems.InvalidArgument($"If-Match is the item's version as its ETag gave it, \"{match}\" is not one.");
                ifVersion = version;
            }
            var (by, via) = Writer();
            var item = new ColdItem(ctx.OwnerKey, dataSetName, documentKey, name, language ?? "", body.Content ?? "",
                body.ContentType ?? "text/plain", body.Description ?? "", body.Visibility ?? ColdValues.Private, by, via,
                body.SourceRef, body.SourceUpdatedUtc, 0, default, default);
            var outcome = cold.Put(item, ifVersion, out var stored, out var objection);
            switch (outcome)
            {
                case ColdStore.WriteOutcome.VersionMismatch:
                case ColdStore.WriteOutcome.OlderThanStored:
                    return ApiProblems.Problem(StatusCodes.Status409Conflict, "coldConflict", "The item has changed", objection!);
                case ColdStore.WriteOutcome.Refused:
                    return ApiProblems.InvalidArgument(objection!);
            }
            Response.Headers.ETag = $"\"{stored!.Version}\"";
            var view = ColdView.Of(stored, withContent: false);
            return outcome == ColdStore.WriteOutcome.Created ? StatusCode(StatusCodes.Status201Created, view) : view;
        }

        /// <summary>
        /// A sync's batch for the dataset's records: each item written as a single PUT writes it, and
        /// with <c>prune=true</c> the items this caller wrote on the dataset before and did not send now
        /// are deleted, so a backend keeps its share in step without touching anyone else's. 200 with
        /// the outcome of each item in order; refused ones are said, the others are written.
        /// </summary>
        [HttpPut(DatasetCold)]
        public ActionResult<ColdBatchOutcome[]> PutRecordCold(string teamName, string dataSetName, [FromBody] ColdBatchItem[] body, [FromQuery] bool prune = false)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (body == null) return ApiProblems.InvalidArgument("The body must be a JSON array of items.");
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            var (by, via) = Writer();
            var items = body.Select(i => new ColdItem(ctx.OwnerKey, dataSetName, i.RecordKey, i.Name ?? "", i.Language ?? "", i.Content ?? "",
                i.ContentType ?? "text/plain", i.Description ?? "", i.Visibility ?? ColdValues.Private, by, via, i.SourceRef, i.SourceUpdatedUtc,
                0, default, default)).ToList();
            var outcomes = cold.PutBatch(ctx.OwnerKey, dataSetName, via, items, prune);
            return outcomes.Select(o => new ColdBatchOutcome(o.Item.RecordKey, o.Item.Name, o.Item.Language,
                o.Outcome.ToString().ToLowerInvariant(), o.Objection)).ToArray();
        }

        /// <summary>Deletes an item (204; 404 when there was none).</summary>
        [HttpDelete(RecordCold + "/{name}")]
        public ActionResult DeleteRecordColdItem(string teamName, string dataSetName, long documentKey, string name, [FromQuery] string? language = null)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            return cold.Delete(new ColdScope(ctx.OwnerKey, dataSetName, documentKey), name, language ?? "")
                ? NoContent()
                : NotFoundItem(name, language);
        }

        private static ActionResult NotFoundItem(string name, string? language) =>
            ApiProblems.Problem(StatusCodes.Status404NotFound, "coldNotFound", "Item not found",
                $"No item '{name}'{(string.IsNullOrEmpty(language) ? "" : $" in '{language}'")} on this record.");

        /// <summary>A Search key reads the public items only: it is shipped in a browser. A Read or Full
        /// key and a signed-in user read every one.</summary>
        private ColdReader Reader() => ApiKeyScope.For(HttpContext) is { } scope && !scope.AllowsLevel(ApiKeyLevel.Read)
            ? ColdReader.Browser : ColdReader.Owner;

        /// <summary>Who writes, as stored: a scoped key is a system, by its name for a team key and its
        /// user and id for a user's key; a signed-in person without a key is the console.</summary>
        private (string By, string Via) Writer()
        {
            var name = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name ?? "";
            if (ApiKeyScope.For(HttpContext) is { } scope)
            {
                var jti = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value ?? "";
                return (ColdValues.System, scope.IsTeamKey ? $"team key {name}" : $"key {jti} of {name}");
            }
            return (ColdValues.Console, name);
        }

        private static bool DatasetExists(string dataSetName, TeamContext ctx) =>
            FileNameValidity.IsValid(dataSetName)
            && new Persistence(IndxServerInternalApi.SearchDbConnectionString, dataSetName, ctx.OwnerKey).DataSetExists();

        /// <summary>An item whose record is not in the dataset (deleted by a route that did not reach the
        /// cold storage, a webhook's delete) is not shown.</summary>
        private static bool RecordGone(TeamContext ctx, ColdScope scope) =>
            IndxServerInternalApi.Manager.ResolveEngine(scope.DataSetName, ctx.OwnerKey) is { } engine
            && string.IsNullOrEmpty(engine.GetJsonDataOfKey(scope.RecordKey));
    }
}
