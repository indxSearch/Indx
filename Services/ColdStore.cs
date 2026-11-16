using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// A named item kept beside one record, never searched and not in memory, read when it is needed:
    /// a book's table of contents, a review, a whole article (Notes/cold-storage.md). Something that
    /// belongs to many records is a record of its own (an author's biography on the author's record).
    /// Until 11 Nov 2026 these were Jens's record notes, in the table RecordNotes, with two
    /// visibilities; the name and the third level, <see cref="ColdValues.Answer"/>, came then.
    /// </summary>
    public sealed record ColdItem(
        string TeamId,
        string DataSetName,
        long RecordKey,
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

    /// <summary>The record an item is of.</summary>
    public sealed record ColdScope(string TeamId, string DataSetName, long RecordKey);

    /// <summary>An item's name as the dataset's records hold it: how many records have one by that
    /// name, and the description the first of them gives.</summary>
    public sealed record ColdName(string Name, int Records, string Description);

    /// <summary>What a caller may read of cold storage, by an item's <see cref="ColdItem.Visibility"/>
    /// (<see cref="ColdValues.AccessFor"/> says who gets which).</summary>
    public enum ColdAccess
    {
        /// <summary>Public items only: a Search key over HTTP, a page in a browser.</summary>
        Public,
        /// <summary>Public and answer items: the answer for a visitor, a Read key, a Viewer.</summary>
        Answer,
        /// <summary>Every item, private included: a Full key, a Read key with the private flag, an
        /// Editor or Admin in the console.</summary>
        All,
    }

    /// <summary>The values of an item's closed fields, as stored.</summary>
    public static class ColdValues
    {
        /// <summary>Read by anyone who may read the dataset, a Search key in a browser included.</summary>
        public const string Public = "public";
        /// <summary>Read by the answer for a visitor and by the owner's keys and the console, never
        /// through a Search key: text the answer may use that a page must not hand out (a licensed
        /// review).</summary>
        public const string Answer = "answer";
        /// <summary>Read by the owner's keys and the console only, never by the answer: the default,
        /// since an item may hold customer data.</summary>
        public const string Private = "private";
        public const string Console = "console";
        public const string System = "system";
        public const string Agent = "agent";
        public static readonly string[] ContentTypes = ["text/plain", "text/markdown", "application/json"];
        public static readonly string[] Visibilities = [Public, Answer, Private];

        /// <summary>
        /// What a caller may read (11 Nov 2026): a Search key over HTTP the public items, the ones a page
        /// may show; a Search key over MCP nothing, since a key in a browser should not hand an agent
        /// anyone's cold storage; a Read key and a Viewer public and answer; a Read key with the
        /// private flag, a Full key, an Editor or Admin every item. Null: none at all. Without a key
        /// (a signed-in person) the team role decides.
        /// </summary>
        public static ColdAccess? AccessFor(ApiKeyScope? scope, string? role, bool viaMcp = false)
        {
            if (scope == null) return TeamRoles.CanWrite(role) ? ColdAccess.All : ColdAccess.Answer;
            return scope.Level switch
            {
                ApiKeyLevel.Search => viaMcp ? null : ColdAccess.Public,
                ApiKeyLevel.Read => scope.PrivateCold ? ColdAccess.All : ColdAccess.Answer,
                _ => ColdAccess.All,
            };
        }

        /// <summary>Whether <paramref name="reader"/> may read an item of <paramref name="visibility"/>.</summary>
        public static bool Reads(ColdAccess reader, string visibility) => reader switch
        {
            ColdAccess.All => true,
            ColdAccess.Answer => visibility is Public or Answer,
            _ => visibility == Public,
        };

        /// <summary>The SQL condition for <see cref="Reads"/>, on the column Visibility.</summary>
        internal static string Where(ColdAccess reader) => reader switch
        {
            ColdAccess.All => "",
            ColdAccess.Answer => $"AND Visibility IN ('{Public}', '{Answer}')",
            _ => $"AND Visibility = '{Public}'",
        };
    }

    /// <summary>
    /// The server's table of cold storage in indx.db, opened as the other stores open it (a
    /// connection of its own, <see cref="SqLiteManager"/> only to make sure the base schema is there),
    /// so the library's store of the records is not touched. A dataset's items go with it on delete,
    /// rename and transfer, called where <see cref="IndxServer.Engine.IndxServerInternalApi"/> calls
    /// the other stores; a record's when the server deletes the record, and a read leaves out the
    /// items of a record that is gone, so a delete that did not reach here shows nothing.
    /// <para>Which records hold which names is also kept in memory per dataset, built on first use
    /// and dropped on every write to that dataset, so a search can say which of its records have cold
    /// storage without a query per search (<see cref="NamesOf"/>).</para>
    /// </summary>
    public class ColdStore(ILogger<ColdStore> logger)
    {
        private const string Table = "ColdStorage";
        private const string OldTable = "RecordNotes";

        /// <summary>The longest an item's content may be, in characters: a table of contents, a review,
        /// an article.</summary>
        public const int MaxContent = 256_000;
        /// <summary>The most items a record may hold, languages counted.</summary>
        public const int MaxPerRecord = 50;
        public const int MaxName = 100;
        public const int MaxDescription = 500;

        private static string DbPath => IndxServer.Engine.IndxServerInternalApi.SearchDbConnectionString;
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Per dataset, each record's names with their visibility; built on first use.</summary>
        private readonly StoreCache<(string, string), Dictionary<long, (string Name, string Visibility)[]>> _names = new();

        /// <summary>Creates the table if the search DB exists, taking over Jens's RecordNotes where it is
        /// there (internal became private). Idempotent; called at startup and before a write.</summary>
        public void EnsureTable()
        {
            if (!DbExists()) return;
            try
            {
                using var conn = Connection();
                conn.Open();
                using (var probe = conn.CreateCommand())
                {
                    probe.CommandText = $"SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{OldTable}'), " +
                                        $"(SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{Table}')";
                    using var reader = probe.ExecuteReader();
                    reader.Read();
                    if (reader.GetInt32(0) > 0 && reader.GetInt32(1) == 0)
                    {
                        reader.Close();
                        using var move = conn.CreateCommand();
                        move.CommandText = $@"ALTER TABLE {OldTable} RENAME TO {Table};
                            DROP INDEX IF EXISTS RecordNotes_BySource;
                            UPDATE {Table} SET Visibility = '{ColdValues.Private}' WHERE Visibility = 'internal';";
                        move.ExecuteNonQuery();
                    }
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $@"CREATE TABLE IF NOT EXISTS {Table} (
                           TeamId           TEXT    NOT NULL,
                           DataSetName      TEXT    NOT NULL,
                           RecordKey        INTEGER NOT NULL,
                           Name             TEXT    NOT NULL,
                           Language         TEXT    NOT NULL DEFAULT '',
                           Content          TEXT    NOT NULL,
                           ContentType      TEXT    NOT NULL DEFAULT 'text/plain',
                           Description      TEXT    NOT NULL DEFAULT '',
                           Visibility       TEXT    NOT NULL DEFAULT '{ColdValues.Private}',
                           WrittenBy        TEXT    NOT NULL,
                           WrittenVia       TEXT    NOT NULL,
                           SourceRef        TEXT    NULL,
                           SourceUpdatedUtc TEXT    NULL,
                           Version          INTEGER NOT NULL DEFAULT 1,
                           CreatedUtc       TEXT    NOT NULL,
                           UpdatedUtc       TEXT    NOT NULL,
                           PRIMARY KEY (TeamId, DataSetName, RecordKey, Name, Language)
                       );
                       CREATE INDEX IF NOT EXISTS ColdStorage_BySource ON {Table} (TeamId, DataSetName, WrittenVia);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure {Table} table", Table);
            }
        }

        /// <summary>Why an item may not be written as it is, or null: its name, sizes and closed values.</summary>
        public static string? Objection(ColdItem item, int itemsOfRecord)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > MaxName)
                return $"name must be 1 to {MaxName} characters.";
            if (item.Name.Any(char.IsControl))
                return "name must not hold control characters.";
            if (item.Content.Length > MaxContent)
                return $"content is {item.Content.Length} characters; an item holds at most {MaxContent}.";
            if (item.Description.Length > MaxDescription)
                return $"description is at most {MaxDescription} characters.";
            if (!ColdValues.ContentTypes.Contains(item.ContentType))
                return $"contentType must be one of {string.Join(", ", ColdValues.ContentTypes)}.";
            if (!ColdValues.Visibilities.Contains(item.Visibility))
                return $"visibility must be one of {string.Join(", ", ColdValues.Visibilities)}.";
            if (item.Language.Length > 20)
                return "language is a language tag (nb, nn, en), at most 20 characters.";
            if (itemsOfRecord >= MaxPerRecord)
                return $"this record holds {MaxPerRecord} items already, the most it may.";
            return null;
        }

        /// <summary>The items of a record that <paramref name="reader"/> may read, by name and language;
        /// without content when <paramref name="withContent"/> is false (a listing). Empty when the
        /// database is not there yet.</summary>
        public List<ColdItem> List(ColdScope scope, bool withContent, ColdAccess reader, IReadOnlyCollection<string>? names = null)
        {
            var items = new List<ColdItem>();
            if (!DbExists()) return items;
            EnsureTable();
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT TeamId, DataSetName, RecordKey, Name, Language,
                        {(withContent ? "Content" : "''")}, ContentType, Description, Visibility, WrittenBy, WrittenVia,
                        SourceRef, SourceUpdatedUtc, Version, CreatedUtc, UpdatedUtc
                    FROM {Table}
                    WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r {ColdValues.Where(reader)}
                    ORDER BY Name, Language";
                Scope(cmd, scope);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var item = Read(r);
                    if (names is { Count: > 0 } && !names.Contains(item.Name)) continue;
                    items.Add(item);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list cold storage of {Team}/{Dataset}/{Record}", scope.TeamId, scope.DataSetName, scope.RecordKey);
            }
            return items;
        }

        /// <summary>The names the dataset's records hold items by, with how many records hold one, so
        /// an agent knows what it can read before it reads any (describe_dataset).</summary>
        public List<ColdName> Names(string teamId, string dataSetName, ColdAccess reader)
        {
            var names = new List<ColdName>();
            if (!DbExists()) return names;
            EnsureTable();
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT Name, COUNT(DISTINCT RecordKey), MAX(Description)
                    FROM {Table}
                    WHERE TeamId = $t AND DataSetName = $d {ColdValues.Where(reader)}
                    GROUP BY Name ORDER BY Name";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    names.Add(new ColdName(r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list the cold storage names of {Team}/{Dataset}", teamId, dataSetName);
            }
            return names;
        }

        /// <summary>
        /// For each of <paramref name="keys"/> that holds items <paramref name="reader"/> may read, their
        /// names, each once and in order: what a search says of its records. From memory: the dataset's
        /// names are read from the table once, and again after a write to it. Empty when none holds any.
        /// </summary>
        public Dictionary<long, string[]> NamesOf(string teamId, string dataSetName, IEnumerable<long> keys, ColdAccess reader)
        {
            var result = new Dictionary<long, string[]>();
            var byRecord = _names.Get((teamId, dataSetName), () => LoadNames(teamId, dataSetName));
            if (byRecord.Count == 0) return result;
            foreach (var key in keys)
            {
                if (!byRecord.TryGetValue(key, out var held)) continue;
                var names = held.Where(h => ColdValues.Reads(reader, h.Visibility)).Select(h => h.Name).Distinct().ToArray();
                if (names.Length > 0) result[key] = names;
            }
            return result;
        }

        private Dictionary<long, (string Name, string Visibility)[]> LoadNames(string teamId, string dataSetName)
        {
            var rows = new List<(long Key, string Name, string Visibility)>();
            if (!DbExists()) return [];
            EnsureTable();
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT RecordKey, Name, Visibility FROM {Table} WHERE TeamId = $t AND DataSetName = $d ORDER BY RecordKey, Name";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read the cold storage names of {Team}/{Dataset}", teamId, dataSetName);
            }
            return rows.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => (x.Name, x.Visibility)).ToArray());
        }

        /// <summary>Drops the dataset's names from memory, read again on the next search.</summary>
        private void Changed(string teamId, string dataSetName) => _names.Invalidate((teamId, dataSetName));

        /// <summary>The item by name and language, or null.</summary>
        public ColdItem? Get(ColdScope scope, string name, string language)
        {
            if (!DbExists()) return null;
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            return Get(conn, scope, name, language);
        }

        private static ColdItem? Get(SqliteConnection conn, ColdScope scope, string name, string language, SqliteTransaction? tx = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"SELECT TeamId, DataSetName, RecordKey, Name, Language, Content, ContentType,
                    Description, Visibility, WrittenBy, WrittenVia, SourceRef, SourceUpdatedUtc, Version, CreatedUtc, UpdatedUtc
                FROM {Table}
                WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r AND Name = $n AND Language = $l";
            Scope(cmd, scope);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$l", language);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }

        /// <summary>What a write came to.</summary>
        public enum WriteOutcome { Created, Updated, VersionMismatch, OlderThanStored, Refused }

        /// <summary>
        /// Writes an item: created, or the stored one replaced whole. <paramref name="ifVersion"/>, when
        /// set, must be the stored version (a write against an item another changed since is refused);
        /// an item whose <see cref="ColdItem.SourceUpdatedUtc"/> is older than the stored one's is
        /// refused, so a slow or repeated sync cannot put back what its source has since changed. An
        /// agent may not replace an item the console or a system wrote. <paramref name="objection"/>
        /// says why on anything but Created and Updated.
        /// </summary>
        public WriteOutcome Put(ColdItem item, long? ifVersion, out ColdItem? stored, out string? objection)
        {
            stored = null;
            new Indx.Storage.SqLiteManager(DbPath, createOrOpenDatabase: true); // the base schema
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var tx = conn.BeginTransaction();
            var scope = new ColdScope(item.TeamId, item.DataSetName, item.RecordKey);
            var old = Get(conn, scope, item.Name, item.Language, tx);
            if (ifVersion is long expected && (old?.Version ?? 0) != expected)
            {
                objection = old == null
                    ? $"the item '{item.Name}' does not exist; a write with a version expects one."
                    : $"the item '{item.Name}' is at version {old.Version}, not {expected}: it was changed since it was read.";
                stored = old;
                return WriteOutcome.VersionMismatch;
            }
            if (old != null && item.SourceUpdatedUtc is { } sent && old.SourceUpdatedUtc is { } had
                // As instants: the stored one is UTC, the sent one as its JSON gave it (local with an offset).
                && sent.ToUniversalTime() < had.ToUniversalTime())
            {
                objection = $"the item '{item.Name}' holds what its source changed at {had:o}; this write is of {sent:o}, older.";
                stored = old;
                return WriteOutcome.OlderThanStored;
            }
            if (old != null && item.WrittenBy == ColdValues.Agent && old.WrittenBy != ColdValues.Agent)
            {
                objection = $"the item '{item.Name}' was written by the {old.WrittenBy}; an agent does not replace it.";
                stored = old;
                return WriteOutcome.Refused;
            }
            objection = Objection(item, old == null ? Count(conn, scope, tx) : 0);
            if (objection != null)
                return WriteOutcome.Refused;

            var now = DateTime.UtcNow;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $@"INSERT INTO {Table} (TeamId, DataSetName, RecordKey, Name, Language, Content, ContentType,
                        Description, Visibility, WrittenBy, WrittenVia, SourceRef, SourceUpdatedUtc, Version, CreatedUtc, UpdatedUtc)
                    VALUES ($t, $d, $r, $n, $l, $c, $ct, $desc, $vis, $by, $via, $sref, $supd, 1, $now, $now)
                    ON CONFLICT(TeamId, DataSetName, RecordKey, Name, Language) DO UPDATE SET
                        Content = $c, ContentType = $ct, Description = $desc, Visibility = $vis, WrittenBy = $by,
                        WrittenVia = $via, SourceRef = $sref, SourceUpdatedUtc = $supd, Version = Version + 1, UpdatedUtc = $now;";
                Scope(cmd, scope);
                cmd.Parameters.AddWithValue("$n", item.Name);
                cmd.Parameters.AddWithValue("$l", item.Language);
                cmd.Parameters.AddWithValue("$c", item.Content);
                cmd.Parameters.AddWithValue("$ct", item.ContentType);
                cmd.Parameters.AddWithValue("$desc", item.Description);
                cmd.Parameters.AddWithValue("$vis", item.Visibility);
                cmd.Parameters.AddWithValue("$by", item.WrittenBy);
                cmd.Parameters.AddWithValue("$via", item.WrittenVia);
                cmd.Parameters.AddWithValue("$sref", (object?)item.SourceRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$supd", item.SourceUpdatedUtc is { } s ? s.ToUniversalTime().ToString("o") : DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            Changed(item.TeamId, item.DataSetName);
            stored = Get(conn, scope, item.Name, item.Language);
            return old == null ? WriteOutcome.Created : WriteOutcome.Updated;
        }

        /// <summary>Deletes an item; false when there was none.</summary>
        public bool Delete(ColdScope scope, string name, string language)
        {
            if (!DbExists()) return false;
            EnsureTable();
            return Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r AND Name = $n AND Language = $l",
                scope, ("$n", name), ("$l", language)) > 0;
        }

        /// <summary>
        /// A sync's batch for one dataset: every item written as <see cref="Put"/> writes it, and with
        /// <paramref name="prune"/> the dataset's items this source (<paramref name="writtenVia"/>) wrote
        /// before and did not send now are deleted, so a backend keeps its share in step without
        /// touching anyone else's. The outcome per item, in order.
        /// </summary>
        public List<(ColdItem Item, WriteOutcome Outcome, string? Objection)> PutBatch(string teamId, string dataSetName, string writtenVia,
            IReadOnlyList<ColdItem> items, bool prune)
        {
            var outcomes = new List<(ColdItem, WriteOutcome, string?)>(items.Count);
            foreach (var item in items)
            {
                var outcome = Put(item, null, out _, out var objection);
                outcomes.Add((item, outcome, objection));
            }
            if (prune && DbExists())
            {
                var kept = new HashSet<(long, string, string)>(items.Select(n => (n.RecordKey, n.Name, n.Language)));
                using var conn = Connection();
                conn.Open();
                var stale = new List<(long, string, string)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT RecordKey, Name, Language FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND WrittenVia = $via";
                    cmd.Parameters.AddWithValue("$t", teamId);
                    cmd.Parameters.AddWithValue("$d", dataSetName);
                    cmd.Parameters.AddWithValue("$via", writtenVia);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
                        if (!kept.Contains(row)) stale.Add(row);
                    }
                }
                foreach (var (record, name, language) in stale)
                    Delete(new ColdScope(teamId, dataSetName, record), name, language);
            }
            return outcomes;
        }

        /// <summary>Every item of the dataset's records, on the dataset's delete.</summary>
        public void DeleteDataset(string teamId, string dataSetName)
        {
            if (!DbExists() || dataSetName.Length == 0) return;
            EnsureTable();
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DataSetName = $d", new ColdScope(teamId, dataSetName, 0));
        }

        /// <summary>The keys of the records that hold items in the dataset.</summary>
        public List<long> RecordKeys(string teamId, string dataSetName)
        {
            var keys = new List<long>();
            if (!DbExists()) return keys;
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT RecordKey FROM {Table} WHERE TeamId = $t AND DataSetName = $d";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetInt64(0));
            return keys;
        }

        /// <summary>A record's items, on the record's delete.</summary>
        public void DeleteRecord(string teamId, string dataSetName, long recordKey)
        {
            if (!DbExists()) return;
            EnsureTable();
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r", new ColdScope(teamId, dataSetName, recordKey));
        }

        /// <summary>Re-keys a dataset's items under its new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName) => Move(teamId, dataSetName, teamId, newName);

        /// <summary>Moves a dataset's items to its new owning team.</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName) => Move(fromTeamId, dataSetName, toTeamId, dataSetName);

        private void Move(string fromTeam, string fromName, string toTeam, string toName)
        {
            if (!DbExists() || fromName.Length == 0) return;
            EnsureTable();
            Execute($"DELETE FROM {Table} WHERE TeamId = $nt AND DataSetName = $nd", new ColdScope(fromTeam, fromName, 0), ("$nt", toTeam), ("$nd", toName));
            Execute($"UPDATE {Table} SET TeamId = $nt, DataSetName = $nd WHERE TeamId = $t AND DataSetName = $d",
                new ColdScope(fromTeam, fromName, 0), ("$nt", toTeam), ("$nd", toName));
            Changed(toTeam, toName);
        }

        private static int Count(SqliteConnection conn, ColdScope scope, SqliteTransaction? tx = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(*) FROM {Table} WHERE TeamId = $t AND DataSetName = $d AND RecordKey = $r";
            Scope(cmd, scope);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>Runs a statement on the dataset of <paramref name="scope"/>, and drops its names from
        /// memory: every statement here changes the table.</summary>
        private int Execute(string sql, ColdScope scope, params (string Name, object Value)[] extra)
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
                logger.LogError(ex, "Cold storage: {Sql} failed for {Team}/{Dataset}", sql, scope.TeamId, scope.DataSetName);
                return 0;
            }
            finally
            {
                Changed(scope.TeamId, scope.DataSetName);
            }
        }

        private static void Scope(SqliteCommand cmd, ColdScope scope)
        {
            cmd.Parameters.AddWithValue("$t", scope.TeamId);
            cmd.Parameters.AddWithValue("$d", scope.DataSetName);
            cmd.Parameters.AddWithValue("$r", scope.RecordKey);
        }

        private static ColdItem Read(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : DateTime.Parse(r.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind),
            r.GetInt64(13),
            DateTime.Parse(r.GetString(14), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.Parse(r.GetString(15), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
