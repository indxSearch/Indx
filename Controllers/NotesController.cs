using IndxServer.Engine;
using IndxServer.Services;
using Indx.Storage;
using Indx.Utilities;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace IndxServer.Controllers
{
    /// <summary>A note as written: the content and what is said about it. The record, name and
    /// language are the route's and the query's; who wrote it is the server's to say.</summary>
    public sealed record NoteWrite(string Content, string? ContentType = null, string? Description = null, string? Visibility = null,
        string? SourceRef = null, DateTime? SourceUpdatedUtc = null);

    /// <summary>One note of a sync's batch: <see cref="NoteWrite"/> with the record it is of.</summary>
    public sealed record NoteBatchItem(long RecordKey, string Name, string Content, string? Language = null, string? ContentType = null,
        string? Description = null, string? Visibility = null, string? SourceRef = null, DateTime? SourceUpdatedUtc = null);

    /// <summary>A note as read; <see cref="Content"/> is null in a listing.</summary>
    public sealed record NoteView(string Name, string Language, long RecordKey, string? Content,
        int Length, string ContentType, string Description, string Visibility, string WrittenBy, string WrittenVia,
        string? SourceRef, DateTime? SourceUpdatedUtc, long Version, DateTime CreatedUtc, DateTime UpdatedUtc)
    {
        internal static NoteView Of(Note n, bool withContent) => new(n.Name, n.Language, n.RecordKey,
            withContent ? n.Content : null, n.Content.Length, n.ContentType, n.Description, n.Visibility, n.WrittenBy, n.WrittenVia,
            n.SourceRef, n.SourceUpdatedUtc, n.Version, n.CreatedUtc, n.UpdatedUtc);
    }

    /// <summary>The outcome of one note in a batch.</summary>
    public sealed record NoteBatchOutcome(long RecordKey, string Name, string Language, string Outcome, string? Detail);

    /// <summary>
    /// Notes kept beside a record and read by an agent when a question needs them, never searched: a
    /// book's table of contents, a review, a whole article
    /// (Notes/Anders/notes-for-the-answer-agent-2026-11-11.md). Reading takes a Search key, which sees
    /// the public notes only; writing takes a Full key or an editor. A note is internal unless written
    /// public. Who wrote a note is set from the caller, never from the body.
    /// </summary>
    public class NotesController(TeamContextResolver resolver, NoteStore notes) : DatasetApiController(resolver)
    {
        private const string DatasetNotes = DataSetRoute + "/notes";
        private const string RecordNotes = DataSetRoute + "/documents/{documentKey:long}/notes";

        /// <summary>A record's notes, without content: its table of contents, its reviews.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(RecordNotes)]
        public ActionResult<NoteView[]> ListRecordNotes(string teamName, string dataSetName, long documentKey)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            var scope = new NoteScope(ctx.OwnerKey, dataSetName, documentKey);
            if (RecordGone(ctx, scope)) return Array.Empty<NoteView>();
            return notes.List(scope, withContent: true, publicOnly: PublicOnly())
                .Select(n => NoteView.Of(n, withContent: false)).ToArray();
        }

        /// <summary>One record note with its content.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(RecordNotes + "/{name}")]
        public ActionResult<NoteView> GetRecordNote(string teamName, string dataSetName, long documentKey, string name, [FromQuery] string? language = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            var scope = new NoteScope(ctx.OwnerKey, dataSetName, documentKey);
            var note = RecordGone(ctx, scope) ? null : notes.Get(scope, name, language ?? "");
            if (note == null || (PublicOnly() && note.Visibility != NoteValues.Public))
                return NotFoundNote(name, language);
            Response.Headers.ETag = $"\"{note.Version}\"";
            return NoteView.Of(note, withContent: true);
        }

        /// <summary>Writes a record note, created or replaced. <c>If-Match</c> with the version read
        /// refuses the write (409) when another changed the note since. The record need not be loaded
        /// yet: a sync may write the notes before the catalogue; a note whose record is not there is
        /// left out when read.</summary>
        [HttpPut(RecordNotes + "/{name}")]
        public ActionResult<NoteView> PutRecordNote(string teamName, string dataSetName, long documentKey, string name, [FromBody] NoteWrite body, [FromQuery] string? language = null)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (body == null) return ApiProblems.InvalidArgument("The body must be a note: at least its content.");
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            long? ifVersion = null;
            if (Request.Headers.IfMatch.ToString() is { Length: > 0 } match)
            {
                if (!long.TryParse(match.Trim('"', ' ', 'W', '/'), out var version))
                    return ApiProblems.InvalidArgument($"If-Match is the note's version as its ETag gave it, \"{match}\" is not one.");
                ifVersion = version;
            }
            var (by, via) = Writer();
            var note = new Note(ctx.OwnerKey, dataSetName, documentKey, name, language ?? "", body.Content ?? "",
                body.ContentType ?? "text/plain", body.Description ?? "", body.Visibility ?? NoteValues.Internal, by, via,
                body.SourceRef, body.SourceUpdatedUtc, 0, default, default);
            var outcome = notes.Put(note, ifVersion, out var stored, out var objection);
            switch (outcome)
            {
                case NoteStore.WriteOutcome.VersionMismatch:
                case NoteStore.WriteOutcome.OlderThanStored:
                    return ApiProblems.Problem(StatusCodes.Status409Conflict, "noteConflict", "The note has changed", objection!);
                case NoteStore.WriteOutcome.Refused:
                    return ApiProblems.InvalidArgument(objection!);
            }
            Response.Headers.ETag = $"\"{stored!.Version}\"";
            var view = NoteView.Of(stored, withContent: false);
            return outcome == NoteStore.WriteOutcome.Created ? StatusCode(StatusCodes.Status201Created, view) : view;
        }

        /// <summary>
        /// A sync's batch for the dataset's records: each note written as a single PUT writes it, and
        /// with <c>prune=true</c> the notes this caller wrote on the dataset before and did not send now
        /// are deleted, so a backend keeps its share in step without touching anyone else's. 200 with
        /// the outcome of each note in order; refused ones are said, the others are written.
        /// </summary>
        [HttpPut(DatasetNotes)]
        public ActionResult<NoteBatchOutcome[]> PutRecordNotes(string teamName, string dataSetName, [FromBody] NoteBatchItem[] body, [FromQuery] bool prune = false)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (body == null) return ApiProblems.InvalidArgument("The body must be a JSON array of notes.");
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            var (by, via) = Writer();
            var items = body.Select(i => new Note(ctx.OwnerKey, dataSetName, i.RecordKey, i.Name ?? "", i.Language ?? "", i.Content ?? "",
                i.ContentType ?? "text/plain", i.Description ?? "", i.Visibility ?? NoteValues.Internal, by, via, i.SourceRef, i.SourceUpdatedUtc,
                0, default, default)).ToList();
            var outcomes = notes.PutBatch(ctx.OwnerKey, dataSetName, via, items, prune);
            return outcomes.Select(o => new NoteBatchOutcome(o.Note.RecordKey, o.Note.Name, o.Note.Language,
                o.Outcome.ToString().ToLowerInvariant(), o.Objection)).ToArray();
        }

        /// <summary>Deletes a record note (204; 404 when there was none).</summary>
        [HttpDelete(RecordNotes + "/{name}")]
        public ActionResult DeleteRecordNote(string teamName, string dataSetName, long documentKey, string name, [FromQuery] string? language = null)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            return notes.Delete(new NoteScope(ctx.OwnerKey, dataSetName, documentKey), name, language ?? "")
                ? NoContent()
                : NotFoundNote(name, language);
        }

        private static ActionResult NotFoundNote(string name, string? language) =>
            ApiProblems.Problem(StatusCodes.Status404NotFound, "noteNotFound", "Note not found",
                $"No note '{name}'{(string.IsNullOrEmpty(language) ? "" : $" in '{language}'")} on this record.");

        /// <summary>A Search key reads the public notes only: it is shipped in a browser.</summary>
        private bool PublicOnly() => ApiKeyScope.For(HttpContext) is { } scope && !scope.AllowsLevel(ApiKeyLevel.Read);

        /// <summary>Who writes, as stored: a scoped key is a system, by its name for a team key and its
        /// user and id for a user's key; a signed-in person without a key is the console.</summary>
        private (string By, string Via) Writer()
        {
            var name = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name ?? "";
            if (ApiKeyScope.For(HttpContext) is { } scope)
            {
                var jti = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value ?? "";
                return (NoteValues.System, scope.IsTeamKey ? $"team key {name}" : $"key {jti} of {name}");
            }
            return (NoteValues.Console, name);
        }

        private static bool DatasetExists(string dataSetName, TeamContext ctx) =>
            FileNameValidity.IsValid(dataSetName)
            && new Persistence(IndxServerInternalApi.SearchDbConnectionString, dataSetName, ctx.OwnerKey).DataSetExists();

        /// <summary>A note whose record is not in the dataset (deleted by a route that did not reach the
        /// notes, a webhook's delete) is not shown.</summary>
        private static bool RecordGone(TeamContext ctx, NoteScope scope) =>
            IndxServerInternalApi.Manager.ResolveEngine(scope.DataSetName, ctx.OwnerKey) is { } engine
            && string.IsNullOrEmpty(engine.GetJsonDataOfKey(scope.RecordKey));
    }
}
