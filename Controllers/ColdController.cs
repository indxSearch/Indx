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

    /// <summary>One item of a sync's batch: <see cref="ColdWrite"/> with the record it is of, by its
    /// Indx key (<see cref="RecordKey"/>) or by the sender's own id (<see cref="Id"/>): the id a webhook
    /// gave the record, or the record's key as text (12 Nov 2026, Notes/cold-storage.md).</summary>
    public sealed record ColdBatchItem(string Name, string Content, long? RecordKey = null, string? Id = null, string? Language = null,
        string? ContentType = null, string? Description = null, string? Visibility = null, string? SourceRef = null,
        DateTime? SourceUpdatedUtc = null);

    /// <summary>An item as read; <see cref="Content"/> is null in a listing.</summary>
    public sealed record ColdView(string Name, string Language, long RecordKey, string? Content,
        int Length, string ContentType, string Description, string Visibility, string WrittenBy, string WrittenVia,
        string? SourceRef, DateTime? SourceUpdatedUtc, long Version, DateTime CreatedUtc, DateTime UpdatedUtc)
    {
        internal static ColdView Of(ColdItem n, bool withContent) => new(n.Name, n.Language, n.RecordKey,
            withContent ? n.Content : null, n.Content.Length, n.ContentType, n.Description, n.Visibility, n.WrittenBy, n.WrittenVia,
            n.SourceRef, n.SourceUpdatedUtc, n.Version, n.CreatedUtc, n.UpdatedUtc);
    }

    /// <summary>The outcome of one item in a batch; <see cref="Id"/> as the item gave it, and its key
    /// once found (0 when no record has that id).</summary>
    public sealed record ColdBatchOutcome(long RecordKey, string Name, string Language, string Outcome, string? Detail, string? Id = null);

    /// <summary>
    /// Cold storage: items kept beside a record, never searched and not in memory, read when a
    /// question needs them: a book's table of contents, a review, a whole article
    /// (Notes/cold-storage.md). Reading takes a Search key, which sees the public items only; who sees
    /// more is <see cref="ColdValues.AccessFor"/>. Writing takes a Full key or an editor. An item is
    /// private unless written otherwise. Who wrote an item is set from the caller, never from the body.
    /// </summary>
    public class ColdController(TeamContextResolver resolver, ColdStore cold, IndxServer.Services.Webhooks.WebhookStore webhooks) : DatasetApiController(resolver)
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
            return cold.List(scope, withContent: true, Reader(ctx))
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
            if (item == null || !ColdValues.Reads(Reader(ctx), item.Visibility))
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
            var keys = KeysOf(ctx, dataSetName, body);
            var items = new List<ColdItem>();
            var answers = new ColdBatchOutcome?[body.Length];
            var slots = new List<int>();
            for (int i = 0; i < body.Length; i++)
            {
                var b = body[i];
                if (keys[i] is not { } key)
                {
                    answers[i] = new ColdBatchOutcome(0, b?.Name ?? "", b?.Language ?? "", "refused",
                        b is { RecordKey: null, Id: null } || b == null
                            ? "Name the record by recordKey (its Indx key) or id (your own id for it)."
                            : $"No record has the id '{b.Id}': one sent by this dataset's webhook, or the record's key as a number.",
                        b?.Id);
                    continue;
                }
                items.Add(new ColdItem(ctx.OwnerKey, dataSetName, key, b.Name ?? "", b.Language ?? "", b.Content ?? "",
                    b.ContentType ?? "text/plain", b.Description ?? "", b.Visibility ?? ColdValues.Private, by, via, b.SourceRef, b.SourceUpdatedUtc,
                    0, default, default));
                slots.Add(i);
            }
            var outcomes = cold.PutBatch(ctx.OwnerKey, dataSetName, via, items, prune);
            for (int j = 0; j < outcomes.Count; j++)
            {
                var o = outcomes[j];
                answers[slots[j]] = new ColdBatchOutcome(o.Item.RecordKey, o.Item.Name, o.Item.Language,
                    o.Outcome.ToString().ToLowerInvariant(), o.Objection, body[slots[j]].Id);
            }
            return answers.Select(a => a!).ToArray();
        }

        /// <summary>
        /// Each item's record key: its <c>recordKey</c>, else its <c>id</c> as the dataset's webhook gave
        /// the record (the key registry, one lookup for the batch), else the id itself when it is a
        /// whole number, the dataset's own key. Null where none is found.
        /// </summary>
        private long?[] KeysOf(TeamContext ctx, string dataSetName, ColdBatchItem?[] body)
        {
            var ids = body.Where(b => b is { RecordKey: null, Id: { Length: > 0 } }).Select(b => b!.Id!).Distinct().ToList();
            var registered = ids.Count > 0 ? webhooks.Keys(ctx.OwnerKey, dataSetName, ids) : new Dictionary<string, long>();
            return body.Select(b => b switch
            {
                null => (long?)null,
                { RecordKey: { } key } => key,
                { Id: { Length: > 0 } id } when registered.TryGetValue(id, out var key) => key,
                { Id: { Length: > 0 } id } when long.TryParse(id, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var key) => key,
                _ => null,
            }).ToArray();
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

        /// <summary>What the caller may read (<see cref="ColdValues.AccessFor"/>): over HTTP a Search key
        /// reads the public items.</summary>
        private ColdAccess Reader(TeamContext ctx) => ColdValues.AccessFor(ApiKeyScope.For(HttpContext), ctx.Role) ?? ColdAccess.Public;

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
