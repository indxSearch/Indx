using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// A named text an agent reads beside the search (Notes/Jens/metadata-for-agents-2026-11-06.md,
    /// "Revised 11 Nov 2026"): of the team, of a dataset, of one record, or of a console user, never
    /// searched. A book's table of contents is a record's note; the dataset's terms a dataset's.
    /// <see cref="RecordKey"/> 0 and an empty <see cref="DataSetName"/> or <see cref="UserId"/> stand
    /// for "none", so a level is the fields that are set.
    /// </summary>
    public sealed record Note(
        string TeamId,
        string DataSetName,
        long RecordKey,
        string UserId,
        string Name,
        string Language,
        string Content,
        string ContentType,
        string Description,
        string Visibility,
        string WrittenBy,
        string WrittenVia,
        string? SourceRef,
        DateTime? SourceUpdatedUtc,
        long Version,
        DateTime CreatedUtc,
        DateTime UpdatedUtc);

    /// <summary>Where a note lives: the team, and within it a dataset, a record of it, a console user,
    /// or none of them. The level is what is set.</summary>
    public sealed record NoteScope(string TeamId, string DataSetName = "", long RecordKey = 0, string UserId = "");

    /// <summary>The values of a note's closed fields, as stored.</summary>
    public static class NoteValues
    {
        public const string Public = "public";
        public const string Internal = "internal";
        public const string Console = "console";
        public const string System = "system";
        public const string Agent = "agent";
        public static readonly string[] ContentTypes = ["text/plain", "text/markdown", "application/json"];
    }

    /// <summary>
    /// The server's table of notes in indx.db, opened as the other stores open it (a connection of
    /// its own, <see cref="SqLiteManager"/> only to make sure the base schema is there), so the
    /// library's store of the records is not touched. A dataset's notes go with it on delete, rename
    /// and transfer, called where <see cref="IndxServer.Engine.IndxServerInternalApi"/> calls the
    /// other stores; a record's when the server deletes the record, and a read leaves out a record's
    /// notes whose record is gone, so a delete that did not reach here shows nothing.
    /// </summary>
    public class NoteStore(ILogger<NoteStore> logger)
    {
        private const string Table = "Notes";

        /// <summary>The longest a note's content may be, in characters: a user's is a fact or two, a
        /// record's or a dataset's a table of contents or a review.</summary>
        public const int MaxUserContent = 16_000;
        public const int MaxContent = 256_000;
        /// <summary>The most notes a scope may hold (a record, a dataset, a user), languages counted.</summary>
        public const int MaxPerScope = 50;
        public const int MaxName = 100;
        public const int MaxDescription = 500;

        private static string DbPath => IndxServer.Engine.IndxServerInternalApi.SearchDbConnectionString;
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Creates the table if the search DB exists. Idempotent; called at startup and before a write.</summary>
        public void EnsureTable()
        {
            if (!DbExists()) return;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $@"CREATE TABLE IF NOT EXISTS {Table} (
                           TeamId           TEXT    NOT NULL,
                           DataSetName      TEXT    NOT NULL DEFAULT '',
                           RecordKey        INTEGER NOT NULL DEFAULT 0,
                           UserId           TEXT    NOT NULL DEFAULT '',
                           Name             TEXT    NOT NULL,
                           Language         TEXT    NOT NULL DEFAULT '',
                           Content          TEXT    NOT NULL,
                           ContentType      TEXT    NOT NULL DEFAULT 'text/plain',
                           Description      TEXT    NOT NULL DEFAULT '',
                           Visibility       TEXT    NOT NULL DEFAULT 'public',
                           WrittenBy        TEXT    NOT NULL,
                           WrittenVia       TEXT    NOT NULL,
                           SourceRef        TEXT    NULL,
                           SourceUpdatedUtc TEXT    NULL,
                           Version          INTEGER NOT NULL DEFAULT 1,
                           CreatedUtc       TEXT    NOT NULL,
                           UpdatedUtc       TEXT    NOT NULL,
                           PRIMARY KEY (TeamId, DataSetName, RecordKey, UserId, Name, Language)
                       );
                       CREATE INDEX IF NOT EXISTS Notes_ByRecord ON {Table} (TeamId, DataSetName, RecordKey);
                       CREATE INDEX IF NOT EXISTS Notes_BySource ON {Table} (TeamId, WrittenVia);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure {Table} table", Table);
            }
        }

        /// <summary>Why a note may not be written as it is, or null: its name, sizes and closed values.</summary>
        public static string? Objection(Note note, int notesInScope)
        {
            if (string.IsNullOrWhiteSpace(note.Name) || note.Name.Length > MaxName)
                return $"name must be 1 to {MaxName} characters.";
            if (note.Name.Any(char.IsControl))
                return "name must not hold control characters.";
            int max = note.UserId.Length > 0 ? MaxUserContent : MaxContent;
            if (note.Content.Length > max)
                return $"content is {note.Content.Length} characters; {(note.UserId.Length > 0 ? "a user's note" : "a note")} holds at most {max}.";
            if (note.Description.Length > MaxDescription)
                return $"description is at most {MaxDescription} characters.";
            if (!NoteValues.ContentTypes.Contains(note.ContentType))
                return $"contentType must be one of {string.Join(", ", NoteValues.ContentTypes)}.";
            if (note.Visibility is not (NoteValues.Public or NoteValues.Internal))
                return "visibility must be public or internal.";
            if (note.Language.Length > 20)
                return "language is a language tag (nb, nn, en), at most 20 characters.";
            if (note.RecordKey != 0 && note.DataSetName.Length == 0)
                return "a record's note needs its dataset.";
            if (notesInScope >= MaxPerScope)
                return $"this {(note.RecordKey != 0 ? "record" : note.UserId.Length > 0 ? "user" : note.DataSetName.Length > 0 ? "dataset" : "team")} holds {MaxPerScope} notes already, the most it may.";
            return null;
        }

        /// <summary>The notes of a scope, by name and language; without content when
        /// <paramref name="withContent"/> is false (a listing). <paramref name="publicOnly"/> leaves out
        /// the internal ones. Empty when the database is not there yet.</summary>
        public List<Note> List(NoteScope scope, bool withContent, bool publicOnly, IReadOnlyCollection<string>? names = null)
        {
            var notes = new List<Note>();
            if (!DbExists()) return notes;
            EnsureTable();
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT TeamId, DataSetName, RecordKey, UserId, Name, Language,
                        {(withContent ? "Content" : "''")}, ContentType, Description, Visibility, WrittenBy, WrittenVia,
                        SourceRef, SourceUpdatedUtc, Version, CreatedUtc, UpdatedUtc, length(Content)
                    FROM {Table}
                    WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r AND UserId = $u
                      {(publicOnly ? "AND Visibility = 'public'" : "")}
                    ORDER BY Name, Language";
                Scope(cmd, scope);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var note = Read(reader);
                    if (names is { Count: > 0 } && !names.Contains(note.Name)) continue;
                    notes.Add(note);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list notes of {Team}/{Dataset}/{Record}", scope.TeamId, scope.DataSetName, scope.RecordKey);
            }
            return notes;
        }

        /// <summary>The note by name and language, or null.</summary>
        public Note? Get(NoteScope scope, string name, string language)
        {
            if (!DbExists()) return null;
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            return Get(conn, scope, name, language);
        }

        private static Note? Get(SqliteConnection conn, NoteScope scope, string name, string language, SqliteTransaction? tx = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"SELECT TeamId, DataSetName, RecordKey, UserId, Name, Language, Content, ContentType,
                    Description, Visibility, WrittenBy, WrittenVia, SourceRef, SourceUpdatedUtc, Version, CreatedUtc, UpdatedUtc, length(Content)
                FROM {Table}
                WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r AND UserId = $u AND Name = $n AND Language = $l";
            Scope(cmd, scope);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$l", language);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }

        /// <summary>What a write came to.</summary>
        public enum WriteOutcome { Created, Updated, VersionMismatch, OlderThanStored, Refused }

        /// <summary>
        /// Writes a note: created, or the stored one replaced. <paramref name="ifVersion"/>, when set,
        /// must be the stored version (a write against a note another changed since is refused); a
        /// note whose <see cref="Note.SourceUpdatedUtc"/> is older than the stored one's is refused, so
        /// a slow or repeated sync cannot put back what its source has since changed. An agent may not
        /// replace a note the console or a system wrote. <paramref name="objection"/> says why on
        /// anything but Created and Updated.
        /// </summary>
        public WriteOutcome Put(Note note, long? ifVersion, out Note? stored, out string? objection)
        {
            stored = null;
            new Indx.Storage.SqLiteManager(DbPath, createOrOpenDatabase: true); // the base schema
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            var scope = new NoteScope(note.TeamId, note.DataSetName, note.RecordKey, note.UserId);
            var old = Get(conn, scope, note.Name, note.Language, tx);
            if (ifVersion is long expected && (old?.Version ?? 0) != expected)
            {
                objection = old == null
                    ? $"the note '{note.Name}' does not exist; a write with a version expects one."
                    : $"the note '{note.Name}' is at version {old.Version}, not {expected}: it was changed since it was read.";
                stored = old;
                return WriteOutcome.VersionMismatch;
            }
            if (old != null && note.SourceUpdatedUtc is { } sent && old.SourceUpdatedUtc is { } had && sent < had)
            {
                objection = $"the note '{note.Name}' holds what its source changed at {had:o}; this write is of {sent:o}, older.";
                stored = old;
                return WriteOutcome.OlderThanStored;
            }
            if (old != null && note.WrittenBy == NoteValues.Agent && old.WrittenBy != NoteValues.Agent)
            {
                objection = $"the note '{note.Name}' was written by the {old.WrittenBy}; an agent does not replace it.";
                stored = old;
                return WriteOutcome.Refused;
            }
            objection = Objection(note, old == null ? Count(conn, scope, tx) : 0);
            if (objection != null)
                return WriteOutcome.Refused;

            var now = DateTime.UtcNow;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $@"INSERT INTO {Table} (TeamId, DataSetName, RecordKey, UserId, Name, Language, Content, ContentType,
                        Description, Visibility, WrittenBy, WrittenVia, SourceRef, SourceUpdatedUtc, Version, CreatedUtc, UpdatedUtc)
                    VALUES ($t, $d, $r, $u, $n, $l, $c, $ct, $desc, $vis, $by, $via, $sref, $supd, 1, $now, $now)
                    ON CONFLICT(TeamId, DataSetName, RecordKey, UserId, Name, Language) DO UPDATE SET
                        Content = $c, ContentType = $ct, Description = $desc, Visibility = $vis, WrittenBy = $by,
                        WrittenVia = $via, SourceRef = $sref, SourceUpdatedUtc = $supd, Version = Version + 1, UpdatedUtc = $now;";
                Scope(cmd, scope);
                cmd.Parameters.AddWithValue("$n", note.Name);
                cmd.Parameters.AddWithValue("$l", note.Language);
                cmd.Parameters.AddWithValue("$c", note.Content);
                cmd.Parameters.AddWithValue("$ct", note.ContentType);
                cmd.Parameters.AddWithValue("$desc", note.Description);
                cmd.Parameters.AddWithValue("$vis", note.Visibility);
                cmd.Parameters.AddWithValue("$by", note.WrittenBy);
                cmd.Parameters.AddWithValue("$via", note.WrittenVia);
                cmd.Parameters.AddWithValue("$sref", (object?)note.SourceRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$supd", note.SourceUpdatedUtc is { } s ? s.ToUniversalTime().ToString("o") : DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            stored = Get(conn, scope, note.Name, note.Language);
            return old == null ? WriteOutcome.Created : WriteOutcome.Updated;
        }

        /// <summary>Deletes a note; false when there was none.</summary>
        public bool Delete(NoteScope scope, string name, string language)
        {
            if (!DbExists()) return false;
            EnsureTable();
            return Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r AND UserId = $u AND Name = $n AND Language = $l",
                scope, ("$n", name), ("$l", language)) > 0;
        }

        /// <summary>
        /// A sync's batch for one dataset: every note written as <see cref="Put"/> writes it, and with
        /// <paramref name="prune"/> the dataset's notes this source (<paramref name="writtenVia"/>) wrote
        /// before and did not send now are deleted, so a backend keeps its share in step without
        /// touching anyone else's. The outcome per note, in order.
        /// </summary>
        public List<(Note Note, WriteOutcome Outcome, string? Objection)> PutBatch(string teamId, string dataSetName, string writtenVia,
            IReadOnlyList<Note> notes, bool prune)
        {
            var outcomes = new List<(Note, WriteOutcome, string?)>(notes.Count);
            foreach (var note in notes)
            {
                var outcome = Put(note, null, out _, out var objection);
                outcomes.Add((note, outcome, objection));
            }
            if (prune && DbExists())
            {
                var kept = new HashSet<(long, string, string, string)>(notes.Select(n => (n.RecordKey, n.UserId, n.Name, n.Language)));
                using var conn = Connection();
                conn.Open();
                var stale = new List<(long, string, string, string)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT RecordKey, UserId, Name, Language FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND WrittenVia = $via";
                    cmd.Parameters.AddWithValue("$t", teamId);
                    cmd.Parameters.AddWithValue("$d", dataSetName);
                    cmd.Parameters.AddWithValue("$via", writtenVia);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
                        if (!kept.Contains(row)) stale.Add(row);
                    }
                }
                foreach (var (record, user, name, language) in stale)
                    Delete(new NoteScope(teamId, dataSetName, record, user), name, language);
            }
            return outcomes;
        }

        /// <summary>A dataset's notes, its records' included, on the dataset's delete.</summary>
        public void DeleteDataset(string teamId, string dataSetName)
        {
            if (!DbExists() || dataSetName.Length == 0) return;
            EnsureTable();
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DataSetName = $d", new NoteScope(teamId, dataSetName));
        }

        /// <summary>A record's notes, on the record's delete.</summary>
        public void DeleteRecord(string teamId, string dataSetName, long recordKey)
        {
            if (!DbExists() || recordKey == 0) return;
            EnsureTable();
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r", new NoteScope(teamId, dataSetName, recordKey));
        }

        /// <summary>A user's notes in a team, on leaving it.</summary>
        public void DeleteUser(string teamId, string userId)
        {
            if (!DbExists() || userId.Length == 0) return;
            EnsureTable();
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND UserId = $u", new NoteScope(teamId, UserId: userId));
        }

        /// <summary>Re-keys a dataset's notes under its new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName) => Move(teamId, dataSetName, teamId, newName);

        /// <summary>Moves a dataset's notes to its new owning team. Its users' notes stay with the old
        /// team, whose members they are.</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName) => Move(fromTeamId, dataSetName, toTeamId, dataSetName);

        private void Move(string fromTeam, string fromName, string toTeam, string toName)
        {
            if (!DbExists() || fromName.Length == 0) return;
            EnsureTable();
            Execute($"DELETE FROM {Table} WHERE TeamId = $nt AND DataSetName = $nd", new NoteScope(fromTeam), ("$nt", toTeam), ("$nd", toName));
            Execute($"UPDATE {Table} SET TeamId = $nt, DataSetName = $nd WHERE TeamId = $t AND DataSetName = $fd AND (UserId = '' OR $t = $nt)",
                new NoteScope(fromTeam), ("$nt", toTeam), ("$nd", toName), ("$fd", fromName));
            if (fromTeam != toTeam)
                Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DataSetName = $fd", new NoteScope(fromTeam), ("$fd", fromName));
        }

        private static int Count(SqliteConnection conn, NoteScope scope, SqliteTransaction? tx = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(*) FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r AND UserId = $u";
            Scope(cmd, scope);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private int Execute(string sql, NoteScope scope, params (string Name, object Value)[] extra)
        {
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                Scope(cmd, scope);
                foreach (var (name, value) in extra) cmd.Parameters.AddWithValue(name, value);
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notes: {Sql} failed for {Team}/{Dataset}", sql, scope.TeamId, scope.DataSetName);
                return 0;
            }
        }

        private static void Scope(SqliteCommand cmd, NoteScope scope)
        {
            cmd.Parameters.AddWithValue("$t", scope.TeamId);
            cmd.Parameters.AddWithValue("$d", scope.DataSetName);
            cmd.Parameters.AddWithValue("$r", scope.RecordKey);
            cmd.Parameters.AddWithValue("$u", scope.UserId);
        }

        private static Note Read(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12),
            r.IsDBNull(13) ? null : DateTime.Parse(r.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind),
            r.GetInt64(14),
            DateTime.Parse(r.GetString(15), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.Parse(r.GetString(16), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
