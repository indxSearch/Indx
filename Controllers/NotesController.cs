using IndxServer.Engine;
using IndxServer.Services;
using Indx.Storage;
using Indx.Utilities;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace IndxServer.Controllers
{
    /// <summary>A note as written: the content and what is said about it. The scope, name and
    /// language are the route's and the query's; who wrote it is the server's to say.</summary>
    public sealed record NoteWrite(string Content, string? ContentType = null, string? Description = null, string? Visibility = null,
        string? SourceRef = null, DateTime? SourceUpdatedUtc = null);

    /// <summary>One note of a sync's batch: <see cref="NoteWrite"/> with where in the dataset it goes.</summary>
    public sealed record NoteBatchItem(string Name, string Content, long RecordKey = 0, string? Language = null, string? ContentType = null,
        string? Description = null, string? Visibility = null, string? SourceRef = null, DateTime? SourceUpdatedUtc = null);

    /// <summary>A note as read; <see cref="Content"/> is null in a listing.</summary>
    public sealed record NoteView(string Name, string Language, string? DataSetName, long RecordKey, string? UserId, string? Content,
        int Length, string ContentType, string Description, string Visibility, string WrittenBy, string WrittenVia,
        string? SourceRef, DateTime? SourceUpdatedUtc, long Version, DateTime CreatedUtc, DateTime UpdatedUtc)
    {
        internal static NoteView Of(Note n, bool withContent, int length) => new(n.Name, n.Language,
            n.DataSetName.Length > 0 ? n.DataSetName : null, n.RecordKey, n.UserId.Length > 0 ? n.UserId : null,
            withContent ? n.Content : null, length, n.ContentType, n.Description, n.Visibility, n.WrittenBy, n.WrittenVia,
            n.SourceRef, n.SourceUpdatedUtc, n.Version, n.CreatedUtc, n.UpdatedUtc);
    }

    /// <summary>The outcome of one note in a batch.</summary>
    public sealed record NoteBatchOutcome(string Name, long RecordKey, string Language, string Outcome, string? Detail);

    /// <summary>
    /// Notes an agent reads beside the search (Notes/Jens/metadata-for-agents-2026-11-06.md): named
    /// texts of the team, a dataset, one record of it, or a console user, never searched. A book's
    /// table of contents is a record's note. Reading takes a Search key, which sees the public notes
    /// only; writing takes a Full key or an editor. A user's notes are that user's own (or an admin's
    /// to read and delete). Who wrote a note is set from the caller, never from the body.
    /// </summary>
    public class NotesController(TeamContextResolver resolver, NoteStore notes) : DatasetApiController(resolver)
    {
        private const string TeamNotes = "teams/{teamName}/notes";
        private const string DatasetNotes = DataSetRoute + "/notes";
        private const string RecordNotes = DataSetRoute + "/documents/{documentKey:long}/notes";

        // ── Read ─────────────────────────────────────────────────────────────

        /// <summary>The team's notes (or, with <paramref name="user"/>, that user's in the team), by
        /// name and language, without their content.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(TeamNotes)]
        public ActionResult<NoteView[]> ListTeamNotes(string teamName, [FromQuery] string? user = null) =>
            List(teamName, null, 0, user);

        /// <summary>The dataset's notes (or, with <paramref name="user"/>, that user's on it), without content.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DatasetNotes)]
        public ActionResult<NoteView[]> ListDatasetNotes(string teamName, string dataSetName, [FromQuery] string? user = null) =>
            List(teamName, dataSetName, 0, user);

        /// <summary>A record's notes, without content: its table of contents, its reviews.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(RecordNotes)]
        public ActionResult<NoteView[]> ListRecordNotes(string teamName, string dataSetName, long documentKey) =>
            List(teamName, dataSetName, documentKey, null);

        /// <summary>One team note with its content.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(TeamNotes + "/{name}")]
        public ActionResult<NoteView> GetTeamNote(string teamName, string name, [FromQuery] string? user = null, [FromQuery] string? language = null) =>
            GetOne(teamName, null, 0, user, name, language);

        /// <summary>One dataset note with its content.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DatasetNotes + "/{name}")]
        public ActionResult<NoteView> GetDatasetNote(string teamName, string dataSetName, string name, [FromQuery] string? user = null, [FromQuery] string? language = null) =>
            GetOne(teamName, dataSetName, 0, user, name, language);

        /// <summary>One record note with its content.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(RecordNotes + "/{name}")]
        public ActionResult<NoteView> GetRecordNote(string teamName, string dataSetName, long documentKey, string name, [FromQuery] string? language = null) =>
            GetOne(teamName, dataSetName, documentKey, null, name, language);

        // ── Write ────────────────────────────────────────────────────────────

        /// <summary>Writes a team note, created or replaced. <c>If-Match</c> with the version read
        /// refuses the write (409) when another changed the note since.</summary>
        [HttpPut(TeamNotes + "/{name}")]
        public ActionResult<NoteView> PutTeamNote(string teamName, string name, [FromBody] NoteWrite body, [FromQuery] string? user = null, [FromQuery] string? language = null) =>
            Put(teamName, null, 0, user, name, language, body);

        /// <summary>Writes a dataset note, created or replaced (<c>If-Match</c> as for a team note).</summary>
        [HttpPut(DatasetNotes + "/{name}")]
        public ActionResult<NoteView> PutDatasetNote(string teamName, string dataSetName, string name, [FromBody] NoteWrite body, [FromQuery] string? user = null, [FromQuery] string? language = null) =>
            Put(teamName, dataSetName, 0, user, name, language, body);

        /// <summary>Writes a record note, created or replaced (<c>If-Match</c> as for a team note). The
        /// record need not be loaded yet: a sync may write the notes before the catalogue; a note whose
        /// record is not there is left out when read.</summary>
        [HttpPut(RecordNotes + "/{name}")]
        public ActionResult<NoteView> PutRecordNote(string teamName, string dataSetName, long documentKey, string name, [FromBody] NoteWrite body, [FromQuery] string? language = null) =>
            Put(teamName, dataSetName, documentKey, null, name, language, body);

        /// <summary>
        /// A sync's batch for the dataset: each note written as a single PUT writes it, and with
        /// <c>prune=true</c> the notes this caller wrote on the dataset before and did not send now are
        /// deleted, so a backend keeps its share in step without touching anyone else's. 200 with the
        /// outcome of each note in order; refused ones are said, the others are written.
        /// </summary>
        [HttpPut(DatasetNotes)]
        public ActionResult<NoteBatchOutcome[]> PutDatasetNotes(string teamName, string dataSetName, [FromBody] NoteBatchItem[] body, [FromQuery] bool prune = false)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (body == null) return ApiProblems.InvalidArgument("The body must be a JSON array of notes.");
            if (!DatasetExists(dataSetName, ctx)) return ApiProblems.DatasetNotFound(dataSetName);
            var (by, via) = Writer();
            var items = body.Select(i => new Note(ctx.OwnerKey, dataSetName, i.RecordKey, "", i.Name ?? "", i.Language ?? "", i.Content ?? "",
                i.ContentType ?? "text/plain", i.Description ?? "", i.Visibility ?? NoteValues.Public, by, via, i.SourceRef, i.SourceUpdatedUtc,
                0, default, default)).ToList();
            var outcomes = notes.PutBatch(ctx.OwnerKey, dataSetName, via, items, prune);
            return outcomes.Select(o => new NoteBatchOutcome(o.Note.Name, o.Note.RecordKey, o.Note.Language,
                o.Outcome.ToString().ToLowerInvariant(), o.Objection)).ToArray();
        }

        /// <summary>Deletes a team note (204; 404 when there was none).</summary>
        [HttpDelete(TeamNotes + "/{name}")]
        public ActionResult DeleteTeamNote(string teamName, string name, [FromQuery] string? user = null, [FromQuery] string? language = null) =>
            Delete(teamName, null, 0, user, name, language);

        /// <summary>Deletes a dataset note.</summary>
        [HttpDelete(DatasetNotes + "/{name}")]
        public ActionResult DeleteDatasetNote(string teamName, string dataSetName, string name, [FromQuery] string? user = null, [FromQuery] string? language = null) =>
            Delete(teamName, dataSetName, 0, user, name, language);

        /// <summary>Deletes a record note.</summary>
        [HttpDelete(RecordNotes + "/{name}")]
        public ActionResult DeleteRecordNote(string teamName, string dataSetName, long documentKey, string name, [FromQuery] string? language = null) =>
            Delete(teamName, dataSetName, documentKey, null, name, language);

        /// <summary>Deletes every note a user has in the team: the user's own, or an admin's to do.</summary>
        [HttpDelete("teams/{teamName}/users/{userId}/notes")]
        public ActionResult DeleteUserNotes(string teamName, string userId)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (UserScopeObjection(ctx, userId) is { } refused) return refused;
            notes.DeleteUser(ctx.OwnerKey, userId);
            return NoContent();
        }

        // ── The common parts ─────────────────────────────────────────────────

        private ActionResult<NoteView[]> List(string teamName, string? dataSetName, long recordKey, string? user)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Scope(ctx, dataSetName, recordKey, user, out var refused) is not { } scope) return refused!;
            if (RecordGone(ctx, scope)) return Array.Empty<NoteView>();
            return notes.List(scope, withContent: true, publicOnly: PublicOnly())
                .Select(n => NoteView.Of(n, withContent: false, n.Content.Length)).ToArray();
        }

        private ActionResult<NoteView> GetOne(string teamName, string? dataSetName, long recordKey, string? user, string name, string? language)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Scope(ctx, dataSetName, recordKey, user, out var refused) is not { } scope) return refused!;
            var note = RecordGone(ctx, scope) ? null : notes.Get(scope, name, language ?? "");
            if (note == null || (PublicOnly() && note.Visibility != NoteValues.Public))
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "noteNotFound", "Note not found",
                    $"No note '{name}'{(string.IsNullOrEmpty(language) ? "" : $" in '{language}'")} here.");
            Response.Headers.ETag = $"\"{note.Version}\"";
            return NoteView.Of(note, withContent: true, note.Content.Length);
        }

        private ActionResult<NoteView> Put(string teamName, string? dataSetName, long recordKey, string? user, string name, string? language, NoteWrite body)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (body == null) return ApiProblems.InvalidArgument("The body must be a note: at least its content.");
            if (Scope(ctx, dataSetName, recordKey, user, out var refused) is not { } scope) return refused!;
            long? ifVersion = null;
            if (Request.Headers.IfMatch.ToString() is { Length: > 0 } match)
            {
                if (!long.TryParse(match.Trim('"', ' ', 'W', '/'), out var version))
                    return ApiProblems.InvalidArgument($"If-Match is the note's version as its ETag gave it, \"{match}\" is not one.");
                ifVersion = version;
            }
            var (by, via) = Writer();
            var note = new Note(ctx.OwnerKey, scope.DataSetName, scope.RecordKey, scope.UserId, name, language ?? "", body.Content ?? "",
                body.ContentType ?? "text/plain", body.Description ?? "", body.Visibility ?? NoteValues.Public, by, via,
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
            var view = NoteView.Of(stored, withContent: false, stored.Content.Length);
            return outcome == NoteStore.WriteOutcome.Created ? StatusCode(StatusCodes.Status201Created, view) : view;
        }

        private ActionResult Delete(string teamName, string? dataSetName, long recordKey, string? user, string name, string? language)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (Scope(ctx, dataSetName, recordKey, user, out var refused) is not { } scope) return refused!;
            return notes.Delete(scope, name, language ?? "")
                ? NoContent()
                : ApiProblems.Problem(StatusCodes.Status404NotFound, "noteNotFound", "Note not found", $"No note '{name}' here.");
        }

        /// <summary>The scope a request names, or the refusal: the dataset must exist, and a user's
        /// notes are that user's own.</summary>
        private NoteScope? Scope(TeamContext ctx, string? dataSetName, long recordKey, string? user, out ActionResult? refused)
        {
            refused = null;
            if (dataSetName != null && !DatasetExists(dataSetName, ctx))
            {
                refused = ApiProblems.DatasetNotFound(dataSetName);
                return null;
            }
            if (!string.IsNullOrEmpty(user) && UserScopeObjection(ctx, user) is { } objection)
            {
                refused = objection;
                return null;
            }
            return new NoteScope(ctx.OwnerKey, dataSetName ?? "", recordKey, user ?? "");
        }

        /// <summary>A user's notes are reached by that user signed in, or by an admin of the team; never
        /// by an API key, which is no person.</summary>
        private ActionResult? UserScopeObjection(TeamContext ctx, string userId)
        {
            if (ApiKeyScope.For(HttpContext) != null)
                return ApiProblems.Problem(StatusCodes.Status403Forbidden, "insufficientRole", "Not a user's",
                    "A user's notes are reached by the user signed in, not by an API key.");
            var caller = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (caller != userId && !TeamRoles.CanAdmin(ctx.Role))
                return ApiProblems.Problem(StatusCodes.Status403Forbidden, "insufficientRole", "Not your notes",
                    "A user's notes are that user's own, or the team admin's to manage.");
            return null;
        }

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

        /// <summary>A record note whose record is not in the dataset (deleted by a route that did not
        /// reach the notes, a webhook's delete) is not shown.</summary>
        private static bool RecordGone(TeamContext ctx, NoteScope scope) =>
            scope.RecordKey != 0
            && IndxServerInternalApi.Manager.ResolveEngine(scope.DataSetName, ctx.OwnerKey) is { } engine
            && string.IsNullOrEmpty(engine.GetJsonDataOfKey(scope.RecordKey));
    }
}
