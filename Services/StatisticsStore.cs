using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>One search, as the search path records it. Timestamps are Unix milliseconds UTC.
    /// Source marks non-customer traffic (null = the HTTP API, "console" = the web console's
    /// search preview). Only customer traffic (Source IS NULL) is aggregated: the rollup and the
    /// live reads both leave the console out, so an editor trying queries in the preview does
    /// not show up in the dashboard. The console rows are kept, for a later "your own testing"
    /// view if one is wanted.</summary>
    public readonly record struct SearchEventRow(
        string QueryId, string TeamId, string DataSet, string QueryText, string? FilterKey,
        int HitCount, string? Subject, long Timestamp, string? Source = null, string? Session = null);

    /// <summary>One select: the user chose a result. QueryId is an opaque reference — an orphan
    /// (unknown or expired id) is stored like any other row and simply finds no search to join.</summary>
    public readonly record struct SelectEventRow(
        string TeamId, string DataSet, string? QueryId, long DocumentKey, int Position,
        string? Subject, long Timestamp);

    /// <summary>One conversion: whatever the customer considers valuable. Type is theirs.</summary>
    public readonly record struct ConvertEventRow(
        string TeamId, string DataSet, string? QueryId, long DocumentKey, string Type,
        double? Value, string? Currency, long? Quantity, string? Subject, long Timestamp);

    /// <summary>One change the dataset's owners made (Notes/statistics-design.md, "Change events").
    /// Summary is a small JSON object of counts and field names, never content or who.</summary>
    public readonly record struct ChangeEventRow(string TeamId, string DataSet, long Timestamp, string Kind, string? Summary);

    /// <summary>Documents inserted, updated and deleted by one request, added up per day.</summary>
    public readonly record struct DocumentCountRow(string TeamId, string DataSet, long Timestamp,
        long Inserted, long Updated, long Deleted);

    /// <summary>One recorded change, as the reads return it.</summary>
    public readonly record struct ChangeStat(long Timestamp, string Kind, string? Summary);

    /// <summary>One day's document changes.</summary>
    public readonly record struct DocumentChangeStat(long Day, long Inserted, long Updated, long Deleted);

    /// <summary>One filter operand's aggregate over a window: how often people narrowed by it,
    /// and how often that came back empty. Value is empty for a range filter, which is reported
    /// by field only.</summary>
    public readonly record struct FilterStat(string Field, string Value, long Uses, long ZeroHits);

    /// <summary>One query's aggregate over a window. ClickedSearches is the CTR numerator
    /// (searches with at least one select); average click position is PositionSum / Selects.</summary>
    public readonly record struct QueryStat(string QueryText, long Searches, long ZeroHits, long Selects,
        long ClickedSearches, long PositionSum);

    /// <summary>The window's totals, for the dashboard's header numbers.</summary>
    public readonly record struct OverviewStat(long Searches, long ZeroHits, long ClickedSearches,
        long Selects, long PositionSum, long Converts, double ConvertValueSum);

    /// <summary>One day of the time series behind the charts.</summary>
    public readonly record struct DailyStat(long Day, long Searches, long ZeroHits, long ClickedSearches,
        long Selects, long Converts, double ConvertValueSum, long PositionSum);

    /// <summary>One document's aggregate over a window.</summary>
    public readonly record struct DocumentStat(long DocumentKey, long Selects, long Converts, double ConvertValueSum);

    /// <summary>One row of a subject's lifetime top-N.</summary>
    public readonly record struct SubjectDocumentStat(long DocumentKey, long Selects, long Converts, long LastSeen);

    /// <summary>
    /// Server-owned persistence for search statistics, in its own SQLite file (stats.db) next to
    /// the search database — see Notes/statistics-design.md for the model and the decisions.
    /// Rows are keyed on the customer's identities (TeamId, DataSet name, DocumentKey), so
    /// statistics survive dataset delete/recreate and document delete; purging is explicit.
    ///
    /// All writes go through <see cref="WriteBatch"/>, and only <see cref="StatisticsWriter"/>
    /// calls it — one writer connection by construction, so search traffic never contends on
    /// this file. Every access to statistics goes through this class (no SQL in controllers or
    /// pages): that is the N-tier rule from the design note, and it is load-bearing.
    /// </summary>
    public sealed class StatisticsStore(string dbPath, ILogger<StatisticsStore> logger)
    {
        private readonly string _dbPath = dbPath;

        public string DbPath => _dbPath;

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
            return conn;
        }

        /// <summary>Creates the file and schema. Idempotent; call once at startup.</summary>
        public void EnsureSchema()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath))!);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS SearchEvents (
    QueryId     TEXT    PRIMARY KEY,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    QueryText   TEXT    NOT NULL,
    FilterKey   TEXT    NULL,
    HitCount    INTEGER NOT NULL,
    Subject     TEXT    NULL,
    Timestamp   INTEGER NOT NULL,
    Source      TEXT    NULL,
    Session     TEXT    NULL,                 -- per-page-load id, for the keystroke rule
    Superseded  INTEGER NOT NULL DEFAULT 0    -- 1: the same session extended it within the window
);
CREATE INDEX IF NOT EXISTS IX_Search_TeamDsTime ON SearchEvents(TeamId, DataSet, Timestamp);
CREATE INDEX IF NOT EXISTS IX_Search_ZeroHits   ON SearchEvents(TeamId, DataSet, Timestamp) WHERE HitCount = 0;

CREATE TABLE IF NOT EXISTS SelectEvents (
    Id          INTEGER PRIMARY KEY,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    QueryId     TEXT    NULL,
    DocumentKey INTEGER NOT NULL,
    Position    INTEGER NOT NULL,
    Subject     TEXT    NULL,
    Timestamp   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Select_TeamDsDoc ON SelectEvents(TeamId, DataSet, DocumentKey);
CREATE INDEX IF NOT EXISTS IX_Select_QueryId   ON SelectEvents(QueryId) WHERE QueryId IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_Select_Subject   ON SelectEvents(TeamId, DataSet, Subject) WHERE Subject IS NOT NULL;

CREATE TABLE IF NOT EXISTS ConvertEvents (
    Id          INTEGER PRIMARY KEY,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    QueryId     TEXT    NULL,
    DocumentKey INTEGER NOT NULL,
    Type        TEXT    NOT NULL,
    Value       REAL    NULL,
    Currency    TEXT    NULL,
    Quantity    INTEGER NULL,
    Subject     TEXT    NULL,
    Timestamp   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Convert_TeamDsDoc ON ConvertEvents(TeamId, DataSet, DocumentKey);
CREATE INDEX IF NOT EXISTS IX_Convert_QueryId   ON ConvertEvents(QueryId) WHERE QueryId IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_Convert_Subject   ON ConvertEvents(TeamId, DataSet, Subject) WHERE Subject IS NOT NULL;

CREATE TABLE IF NOT EXISTS DailyQueryStats (
    Day         INTEGER NOT NULL,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    QueryText   TEXT    NOT NULL,
    Searches    INTEGER NOT NULL,
    ZeroHits    INTEGER NOT NULL,
    Selects     INTEGER NOT NULL,           -- select events joined to these searches
    ClickedSearches INTEGER NOT NULL,       -- searches with at least one select (the CTR numerator)
    PositionSum INTEGER NOT NULL,           -- sum of 1-based select positions (avg = / Selects)
    PRIMARY KEY (Day, TeamId, DataSet, QueryText)
);

CREATE TABLE IF NOT EXISTS DailyDocumentStats (
    Day         INTEGER NOT NULL,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    DocumentKey INTEGER NOT NULL,
    Selects     INTEGER NOT NULL,
    Converts    INTEGER NOT NULL,
    ConvertValueSum REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (Day, TeamId, DataSet, DocumentKey)
);

CREATE TABLE IF NOT EXISTS DailyFilterStats (
    Day         INTEGER NOT NULL,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    Field       TEXT    NOT NULL,
    Value       TEXT    NOT NULL,           -- '' for a range filter
    Uses        INTEGER NOT NULL,
    ZeroHits    INTEGER NOT NULL,
    PRIMARY KEY (Day, TeamId, DataSet, Field, Value)
);

CREATE TABLE IF NOT EXISTS DatasetChanges (
    Id          INTEGER PRIMARY KEY,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    Timestamp   INTEGER NOT NULL,
    Kind        TEXT    NOT NULL,           -- DatasetChangeKind
    Summary     TEXT    NULL                -- small JSON: counts, field names; never content or who
);
CREATE INDEX IF NOT EXISTS IX_Changes_TeamDsTime ON DatasetChanges(TeamId, DataSet, Timestamp);

CREATE TABLE IF NOT EXISTS DailyDocumentChanges (
    Day         INTEGER NOT NULL,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    Inserted    INTEGER NOT NULL,
    Updated     INTEGER NOT NULL,
    Deleted     INTEGER NOT NULL,
    PRIMARY KEY (Day, TeamId, DataSet)
);

CREATE TABLE IF NOT EXISTS DatasetSettings (
    TeamId        TEXT    NOT NULL,
    DataSet       TEXT    NOT NULL,
    RecordFilters INTEGER NOT NULL DEFAULT 1, -- 0: no filter key is stored (Browsing is off)
    PRIMARY KEY (TeamId, DataSet)
);

CREATE TABLE IF NOT EXISTS RollupState (
    Id            INTEGER PRIMARY KEY CHECK (Id = 1),
    LastRolledDay INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS SubjectDocumentStats (
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    Subject     TEXT    NOT NULL,
    DocumentKey INTEGER NOT NULL,
    Selects     INTEGER NOT NULL,
    Converts    INTEGER NOT NULL,
    LastSeen    INTEGER NOT NULL,
    PRIMARY KEY (TeamId, DataSet, Subject, DocumentKey)
);";
            cmd.ExecuteNonQuery();
            // Dev-file guard: adds the two rollup columns to a stats.db created in the days
            // before they existed. They shipped in no release, so this can go after one.
            foreach (var col in new[] { "ClickedSearches", "PositionSum" })
            {
                try
                {
                    cmd.CommandText = $"ALTER TABLE DailyQueryStats ADD COLUMN {col} INTEGER NOT NULL DEFAULT 0";
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException) { /* already there */ }
            }
            try
            {
                cmd.CommandText = "ALTER TABLE SearchEvents ADD COLUMN Source TEXT NULL";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* already there */ }
            try
            {
                cmd.CommandText = "ALTER TABLE SearchEvents ADD COLUMN Session TEXT NULL";
                cmd.ExecuteNonQuery();
                // The column arriving means this file predates "what counts as a search"
                // (Notes/statistics-design.md): its rolled days counted every keystroke, empty
                // searches and the console. Clearing the watermark makes the next rollup pass
                // re-roll every day from the oldest raw row still kept, under the one definition.
                // Rollup is idempotent, so this is safe; days older than retention keep what they
                // were rolled with. Once per file: the next start finds the column there.
                cmd.CommandText = "DELETE FROM RollupState";
                if (cmd.ExecuteNonQuery() > 0)
                    logger.LogInformation("statistics: the counting rules changed; re-rolling the kept days");
            }
            catch (SqliteException) { /* already there */ }
            try
            {
                cmd.CommandText = "ALTER TABLE SearchEvents ADD COLUMN Superseded INTEGER NOT NULL DEFAULT 0";
                cmd.ExecuteNonQuery();
                // Rows written before the writer marked keystrokes: mark them once, by the same
                // rule, so a file that already holds sessions reads the same as a new one.
                cmd.CommandText = MarkSupersededSql;
                int marked = cmd.ExecuteNonQuery();
                cmd.CommandText = "DELETE FROM RollupState";
                if (cmd.ExecuteNonQuery() > 0 || marked > 0)
                    logger.LogInformation("statistics: marked {Marked} keystroke searches; re-rolling the kept days", marked);
            }
            catch (SqliteException) { /* already there */ }
        }

        /// <summary>The keystroke rule in SQL, for rows the writer did not mark: a search with
        /// text is superseded when the same session's next search with text, within the window,
        /// extends it. The writer applies the same rule in <see cref="StatisticsWriter"/>.</summary>
        private static readonly string MarkSupersededSql = $@"
UPDATE SearchEvents SET Superseded = 1 WHERE QueryId IN (
    SELECT QueryId FROM (
        SELECT QueryId, QueryText, Timestamp,
               LEAD(lower(QueryText)) OVER w AS NextText, LEAD(Timestamp) OVER w AS NextTs
          FROM SearchEvents
         WHERE Session IS NOT NULL AND trim(QueryText) <> ''
        WINDOW w AS (PARTITION BY TeamId, DataSet, Session ORDER BY Timestamp, rowid))
     WHERE NextTs - Timestamp <= {KeystrokeWindowMs}
       AND length(NextText) > length(QueryText)
       AND substr(NextText, 1, length(QueryText)) = lower(QueryText))
  AND NOT EXISTS (SELECT 1 FROM SelectEvents x WHERE x.QueryId = SearchEvents.QueryId)";

        /// <summary>
        /// Writes one batch in one transaction. The subject↔document edge
        /// (<c>SubjectDocumentStats</c>) is maintained here at write time, not by the rollup:
        /// lifetime counters upserted from a recomputable rollup would double-count on a re-run,
        /// and maintained here the daily rollup stays idempotent.
        /// </summary>
        public void WriteBatch(
            IReadOnlyList<SearchEventRow> searches,
            IReadOnlyList<SelectEventRow> selects,
            IReadOnlyList<ConvertEventRow> converts,
            IReadOnlyCollection<string>? superseded = null)
        {
            if (searches.Count == 0 && selects.Count == 0 && converts.Count == 0 && (superseded?.Count ?? 0) == 0) return;
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            if (searches.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR IGNORE INTO SearchEvents
                    (QueryId, TeamId, DataSet, QueryText, FilterKey, HitCount, Subject, Timestamp, Source, Session)
                    VALUES ($qid, $team, $ds, $text, $filter, $hits, $subj, $ts, $src, $sess)";
                var qid = cmd.Parameters.Add("$qid", SqliteType.Text);
                var team = cmd.Parameters.Add("$team", SqliteType.Text);
                var ds = cmd.Parameters.Add("$ds", SqliteType.Text);
                var text = cmd.Parameters.Add("$text", SqliteType.Text);
                var filter = cmd.Parameters.Add("$filter", SqliteType.Text);
                var hits = cmd.Parameters.Add("$hits", SqliteType.Integer);
                var subj = cmd.Parameters.Add("$subj", SqliteType.Text);
                var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
                var src = cmd.Parameters.Add("$src", SqliteType.Text);
                var sess = cmd.Parameters.Add("$sess", SqliteType.Text);
                foreach (var e in searches)
                {
                    qid.Value = e.QueryId; team.Value = e.TeamId; ds.Value = e.DataSet;
                    text.Value = e.QueryText; filter.Value = (object?)e.FilterKey ?? DBNull.Value;
                    hits.Value = e.HitCount; subj.Value = (object?)e.Subject ?? DBNull.Value;
                    ts.Value = e.Timestamp; src.Value = (object?)e.Source ?? DBNull.Value;
                    sess.Value = (object?)e.Session ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            // Keystrokes the writer saw superseded, in this batch or an earlier one: a typing
            // sequence crosses flushes, so this is an update by id rather than a column on the row.
            if (superseded is { Count: > 0 })
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                // Never a search someone already clicked: a click makes it a search.
                cmd.CommandText = @"UPDATE SearchEvents SET Superseded = 1 WHERE QueryId = $qid
                    AND NOT EXISTS (SELECT 1 FROM SelectEvents x WHERE x.QueryId = $qid)";
                var qid = cmd.Parameters.Add("$qid", SqliteType.Text);
                foreach (var id in superseded)
                {
                    qid.Value = id;
                    cmd.ExecuteNonQuery();
                }
            }

            if (selects.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO SelectEvents
                    (TeamId, DataSet, QueryId, DocumentKey, Position, Subject, Timestamp)
                    VALUES ($team, $ds, $qid, $key, $pos, $subj, $ts)";
                var team = cmd.Parameters.Add("$team", SqliteType.Text);
                var ds = cmd.Parameters.Add("$ds", SqliteType.Text);
                var qid = cmd.Parameters.Add("$qid", SqliteType.Text);
                var key = cmd.Parameters.Add("$key", SqliteType.Integer);
                var pos = cmd.Parameters.Add("$pos", SqliteType.Integer);
                var subj = cmd.Parameters.Add("$subj", SqliteType.Text);
                var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
                foreach (var e in selects)
                {
                    team.Value = e.TeamId; ds.Value = e.DataSet;
                    qid.Value = (object?)e.QueryId ?? DBNull.Value; key.Value = e.DocumentKey;
                    pos.Value = e.Position; subj.Value = (object?)e.Subject ?? DBNull.Value;
                    ts.Value = e.Timestamp;
                    cmd.ExecuteNonQuery();
                }
                // A click makes its search a search, even one already marked a keystroke - the
                // visitor chose a result before typing on. The other order (the click first) is
                // the NOT EXISTS on the supersede above.
                using (var unmark = conn.CreateCommand())
                {
                    unmark.Transaction = tx;
                    unmark.CommandText = "UPDATE SearchEvents SET Superseded = 0 WHERE QueryId = $qid AND Superseded = 1";
                    var uq = unmark.Parameters.Add("$qid", SqliteType.Text);
                    foreach (var e in selects.Where(s => s.QueryId != null))
                    {
                        uq.Value = e.QueryId;
                        unmark.ExecuteNonQuery();
                    }
                }
                UpsertSubjectEdges(conn, tx, selects.Where(s => s.Subject != null)
                    .Select(s => (s.TeamId, s.DataSet, s.Subject!, s.DocumentKey, s.Timestamp, Selects: 1, Converts: 0)));
            }

            if (converts.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO ConvertEvents
                    (TeamId, DataSet, QueryId, DocumentKey, Type, Value, Currency, Quantity, Subject, Timestamp)
                    VALUES ($team, $ds, $qid, $key, $type, $val, $cur, $qty, $subj, $ts)";
                var team = cmd.Parameters.Add("$team", SqliteType.Text);
                var ds = cmd.Parameters.Add("$ds", SqliteType.Text);
                var qid = cmd.Parameters.Add("$qid", SqliteType.Text);
                var key = cmd.Parameters.Add("$key", SqliteType.Integer);
                var type = cmd.Parameters.Add("$type", SqliteType.Text);
                var val = cmd.Parameters.Add("$val", SqliteType.Real);
                var cur = cmd.Parameters.Add("$cur", SqliteType.Text);
                var qty = cmd.Parameters.Add("$qty", SqliteType.Integer);
                var subj = cmd.Parameters.Add("$subj", SqliteType.Text);
                var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
                foreach (var e in converts)
                {
                    team.Value = e.TeamId; ds.Value = e.DataSet;
                    qid.Value = (object?)e.QueryId ?? DBNull.Value; key.Value = e.DocumentKey;
                    type.Value = e.Type; val.Value = (object?)e.Value ?? DBNull.Value;
                    cur.Value = (object?)e.Currency ?? DBNull.Value;
                    qty.Value = (object?)e.Quantity ?? DBNull.Value;
                    subj.Value = (object?)e.Subject ?? DBNull.Value; ts.Value = e.Timestamp;
                    cmd.ExecuteNonQuery();
                }
                UpsertSubjectEdges(conn, tx, converts.Where(c => c.Subject != null)
                    .Select(c => (c.TeamId, c.DataSet, c.Subject!, c.DocumentKey, c.Timestamp, Selects: 0, Converts: 1)));
            }

            tx.Commit();
        }

        /// <summary>
        /// Writes change events and document counts, in their own short transaction beside the
        /// event batch: they are rare, and the search write path stays as it was. Document counts
        /// are upserted (added to the day's row), so several writers - several instances - add up
        /// rather than overwrite each other.
        /// </summary>
        public void WriteChanges(IReadOnlyList<ChangeEventRow> changes, IReadOnlyList<DocumentCountRow> documents)
        {
            if (changes.Count == 0 && documents.Count == 0) return;
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            if (changes.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO DatasetChanges (TeamId, DataSet, Timestamp, Kind, Summary)
                    VALUES ($t, $d, $ts, $k, $s)";
                var t = cmd.Parameters.Add("$t", SqliteType.Text);
                var d = cmd.Parameters.Add("$d", SqliteType.Text);
                var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
                var k = cmd.Parameters.Add("$k", SqliteType.Text);
                var sm = cmd.Parameters.Add("$s", SqliteType.Text);
                foreach (var c in changes)
                {
                    t.Value = c.TeamId; d.Value = c.DataSet; ts.Value = c.Timestamp; k.Value = c.Kind;
                    sm.Value = (object?)c.Summary ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
            if (documents.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO DailyDocumentChanges (Day, TeamId, DataSet, Inserted, Updated, Deleted)
                    VALUES ($day, $t, $d, $i, $u, $x)
                    ON CONFLICT(Day, TeamId, DataSet) DO UPDATE SET
                        Inserted = Inserted + excluded.Inserted,
                        Updated = Updated + excluded.Updated,
                        Deleted = Deleted + excluded.Deleted";
                var day = cmd.Parameters.Add("$day", SqliteType.Integer);
                var t = cmd.Parameters.Add("$t", SqliteType.Text);
                var d = cmd.Parameters.Add("$d", SqliteType.Text);
                var i = cmd.Parameters.Add("$i", SqliteType.Integer);
                var u = cmd.Parameters.Add("$u", SqliteType.Integer);
                var x = cmd.Parameters.Add("$x", SqliteType.Integer);
                foreach (var g in documents.GroupBy(r => (Day: DayOf(r.Timestamp), r.TeamId, r.DataSet)))
                {
                    day.Value = g.Key.Day; t.Value = g.Key.TeamId; d.Value = g.Key.DataSet;
                    i.Value = g.Sum(r => r.Inserted); u.Value = g.Sum(r => r.Updated); x.Value = g.Sum(r => r.Deleted);
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }

        /// <summary>The dataset's recorded changes in [fromDay, toDay], oldest first.</summary>
        public List<ChangeStat> Changes(string teamId, string dataSet, long fromDay, long toDay)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT Timestamp, Kind, Summary FROM DatasetChanges
                WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $to
                ORDER BY Timestamp, Id";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$from", fromDay * 86_400_000L);
            cmd.Parameters.AddWithValue("$to", (toDay + 1) * 86_400_000L);
            var result = new List<ChangeStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new ChangeStat(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            return result;
        }

        /// <summary>The dataset's document changes per day in [fromDay, toDay], days with none left out.</summary>
        public List<DocumentChangeStat> DocumentChanges(string teamId, string dataSet, long fromDay, long toDay)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT Day, Inserted, Updated, Deleted FROM DailyDocumentChanges
                WHERE TeamId = $t AND DataSet = $d AND Day >= $from AND Day <= $to ORDER BY Day";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$from", fromDay);
            cmd.Parameters.AddWithValue("$to", toDay);
            var result = new List<DocumentChangeStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new DocumentChangeStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
            return result;
        }

        private static void UpsertSubjectEdges(SqliteConnection conn, SqliteTransaction tx,
            IEnumerable<(string TeamId, string DataSet, string Subject, long DocumentKey, long Timestamp, int Selects, int Converts)> edges)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO SubjectDocumentStats
                (TeamId, DataSet, Subject, DocumentKey, Selects, Converts, LastSeen)
                VALUES ($team, $ds, $subj, $key, $sel, $conv, $ts)
                ON CONFLICT(TeamId, DataSet, Subject, DocumentKey) DO UPDATE SET
                    Selects  = Selects + excluded.Selects,
                    Converts = Converts + excluded.Converts,
                    LastSeen = max(LastSeen, excluded.LastSeen)";
            var team = cmd.Parameters.Add("$team", SqliteType.Text);
            var ds = cmd.Parameters.Add("$ds", SqliteType.Text);
            var subj = cmd.Parameters.Add("$subj", SqliteType.Text);
            var key = cmd.Parameters.Add("$key", SqliteType.Integer);
            var sel = cmd.Parameters.Add("$sel", SqliteType.Integer);
            var conv = cmd.Parameters.Add("$conv", SqliteType.Integer);
            var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
            foreach (var e in edges)
            {
                team.Value = e.TeamId; ds.Value = e.DataSet; subj.Value = e.Subject;
                key.Value = e.DocumentKey; sel.Value = e.Selects; conv.Value = e.Converts;
                ts.Value = e.Timestamp;
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Unix day number (UTC) for a Unix-millisecond timestamp.</summary>
        public static long DayOf(long unixMs) => unixMs / 86_400_000L;

        /// <summary>
        /// Recomputes the two daily tables for one Unix day. Idempotent: the day's rows are
        /// replaced wholesale, so re-running it is safe. Roll up day D no earlier than D+2, so
        /// its selects and converts have had time to arrive and join.
        /// </summary>
        public void RollupDay(long day)
        {
            long from = day * 86_400_000L, to = from + 86_400_000L;
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
DELETE FROM DailyQueryStats WHERE Day = $day;
INSERT INTO DailyQueryStats (Day, TeamId, DataSet, QueryText, Searches, ZeroHits, Selects, ClickedSearches, PositionSum)
SELECT $day, c.TeamId, c.DataSet, lower(c.QueryText),
       COUNT(*),
       SUM(CASE WHEN c.HitCount = 0 THEN 1 ELSE 0 END),
       COALESCE(SUM(c.Cnt), 0),
       SUM(CASE WHEN c.Cnt > 0 THEN 1 ELSE 0 END),
       COALESCE(SUM(c.PosSum), 0)
FROM " + CountedSearches("s.Timestamp >= $from AND s.Timestamp < $to") + @" c
WHERE trim(c.QueryText) <> ''
GROUP BY c.TeamId, c.DataSet, lower(c.QueryText);

DELETE FROM DailyDocumentStats WHERE Day = $day;
INSERT INTO DailyDocumentStats (Day, TeamId, DataSet, DocumentKey, Selects, Converts, ConvertValueSum)
SELECT $day, TeamId, DataSet, DocumentKey, SUM(Sel), SUM(Conv), SUM(Val)
FROM (
    SELECT TeamId, DataSet, DocumentKey, 1 AS Sel, 0 AS Conv, 0.0 AS Val
    FROM SelectEvents WHERE Timestamp >= $from AND Timestamp < $to
    UNION ALL
    SELECT TeamId, DataSet, DocumentKey, 0, 1, COALESCE(Value, 0)
    FROM ConvertEvents WHERE Timestamp >= $from AND Timestamp < $to
)
GROUP BY TeamId, DataSet, DocumentKey;";
            cmd.Parameters.AddWithValue("$day", day);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            cmd.ExecuteNonQuery();
            RollupFilters(conn, tx, day, from, to);
            using (var mark = conn.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = @"INSERT INTO RollupState (Id, LastRolledDay) VALUES (1, $day)
                    ON CONFLICT(Id) DO UPDATE SET LastRolledDay = max(LastRolledDay, $day)";
                mark.Parameters.AddWithValue("$day", day);
                mark.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // ── Per-dataset settings ─────────────────────────────────────────────────────────

        private readonly ConcurrentDictionary<(string Team, string DataSet), bool> _recordFilters = new();

        /// <summary>Whether the dataset stores the filter of each search (the Browsing report).
        /// On unless switched off. Asked on the search path, so it is cached: one dictionary read
        /// after the first.</summary>
        public bool RecordsFilters(string teamId, string dataSet) =>
            _recordFilters.GetOrAdd((teamId, dataSet), key =>
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT RecordFilters FROM DatasetSettings WHERE TeamId = $t AND DataSet = $d";
                cmd.Parameters.AddWithValue("$t", key.Team);
                cmd.Parameters.AddWithValue("$d", key.DataSet);
                return cmd.ExecuteScalar() is not long v || v != 0;
            });

        /// <summary>
        /// Switches the Browsing report on or off for a dataset. Off also erases the filter values
        /// already recorded - from the raw rows and the daily table - because a filter value can
        /// be personal data (a name or a customer number on a dataset of people), and switching
        /// off for that reason must not leave 90 days of them behind.
        /// </summary>
        public void SetRecordsFilters(string teamId, string dataSet, bool on)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$on", on ? 1 : 0);
            cmd.CommandText = @"INSERT INTO DatasetSettings (TeamId, DataSet, RecordFilters) VALUES ($t, $d, $on)
                ON CONFLICT(TeamId, DataSet) DO UPDATE SET RecordFilters = $on";
            cmd.ExecuteNonQuery();
            if (!on)
            {
                cmd.CommandText = "UPDATE SearchEvents SET FilterKey = NULL WHERE TeamId = $t AND DataSet = $d AND FilterKey IS NOT NULL";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "DELETE FROM DailyFilterStats WHERE TeamId = $t AND DataSet = $d";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            _recordFilters[(teamId, dataSet)] = on;
        }

        // ── What counts as a search (Notes/statistics-design.md, "What counts as a search") ──

        /// <summary>A search the same visitor extends within this long was a keystroke on the way
        /// to the one they settled on.</summary>
        internal const long KeystrokeWindowMs = 3_000;

        /// <summary>Without a visitor to follow, the shortest text that counts: one letter is
        /// almost always a keystroke.</summary>
        internal const int MinAnonymousQueryLength = 2;

        /// <summary>The same session sending the same text and filter again within this long is
        /// the same search: a front-end re-runs it with facets after a debounce, for a new sort,
        /// or to load more.</summary>
        internal const long RepeatWindowMs = 30_000;

        /// <summary>The live reads' window: one dataset, the days the rollup has not covered.</summary>
        private const string LiveWhere =
            "s.TeamId = $t AND s.DataSet = $d AND s.Timestamp >= $liveFrom AND s.Timestamp < $toEx";

        /// <summary>
        /// The searches that count, as a derived table over <c>SearchEvents</c> rows matching
        /// <paramref name="where"/> (written against alias <c>s</c>), with their selects joined
        /// (<c>Cnt</c>, <c>PosSum</c>). One definition for the rollup and every live read, so a
        /// rolled day and a live day cannot disagree about what a search is.
        /// <list type="bullet">
        /// <item>Customer traffic only: the console's preview is left out.</item>
        /// <item>With a session, a search the same session extends within
        ///   <see cref="KeystrokeWindowMs"/> is superseded: "o", "os", "osl", "oslo" count as
        ///   "oslo". The writer marks it (<c>Superseded</c>) as it writes the batch, so a read
        ///   filters a column instead of sorting a day of keystrokes per session - measured, that
        ///   sort was a tab load of seconds. The session only, never the subject: a subject may be
        ///   a customer segment, and two members of one typing at once would swallow each other's
        ///   searches.</item>
        /// <item>Without one, text shorter than <see cref="MinAnonymousQueryLength"/> is left out.</item>
        /// <item>A search that got a click counts whatever else is true: someone chose a result
        ///   from it.</item>
        /// <item>An empty-text row is kept; it exists only with a filter (browsing), and the
        ///   query reads leave it out while the filter reads count it.</item>
        /// </list>
        /// </summary>
        private static string CountedSearches(string where) => $@"(
SELECT s.QueryId, s.TeamId, s.DataSet, s.QueryText, s.FilterKey, s.HitCount, s.Timestamp,
       -- Per counted search through IX_Select_QueryId, after the WHERE has dropped the
       -- keystrokes. Not a GROUP BY over SelectEvents joined in: that grouped every select ever
       -- kept on every read (StatisticsReadCostProbeTests).
       (SELECT COUNT(*) FROM SelectEvents x WHERE x.QueryId = s.QueryId) AS Cnt,
       (SELECT SUM(x.Position) FROM SelectEvents x WHERE x.QueryId = s.QueryId) AS PosSum
  FROM SearchEvents s
 WHERE {where} AND s.Source IS NULL
   AND (trim(s.QueryText) = ''
        -- A clicked search is never marked superseded (the writer sees to it both ways round),
        -- so with a session the mark alone decides.
        OR (s.Session IS NOT NULL AND s.Superseded = 0)
        OR (s.Session IS NULL AND length(trim(s.QueryText)) >= {MinAnonymousQueryLength})
        OR (s.Session IS NULL AND EXISTS (SELECT 1 FROM SelectEvents x WHERE x.QueryId = s.QueryId)))
)";

        /// <summary>Adds each operand of one search's filter to <paramref name="counts"/>, once per
        /// search however often the key repeats it.</summary>
        private static void CountOperands(
            Dictionary<(string Team, string Ds, string Field, string Value), (long Uses, long Zero)> counts,
            string team, string ds, string key, int hitCount)
        {
            foreach (var op in FilterKeyOperands.Parse(key).Distinct())
            {
                var k = (team, ds, op.Field, op.Value);
                var (uses, zero) = counts.GetValueOrDefault(k);
                counts[k] = (uses + 1, zero + (hitCount == 0 ? 1 : 0));
            }
        }

        /// <summary>The day's filter use, per operand. In C# rather than SQL because splitting a
        /// key is a parse of our own grammar (<see cref="FilterKeyOperands"/>), not a string op.</summary>
        private static void RollupFilters(SqliteConnection conn, SqliteTransaction tx, long day, long from, long to)
        {
            var counts = new Dictionary<(string Team, string Ds, string Field, string Value), (long Uses, long Zero)>();
            using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = $"SELECT c.TeamId, c.DataSet, c.FilterKey, c.HitCount FROM " +
                    CountedSearches("s.Timestamp >= $from AND s.Timestamp < $to") + " c WHERE c.FilterKey IS NOT NULL";
                read.Parameters.AddWithValue("$from", from);
                read.Parameters.AddWithValue("$to", to);
                using var r = read.ExecuteReader();
                while (r.Read())
                    CountOperands(counts, r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3));
            }
            using var write = conn.CreateCommand();
            write.Transaction = tx;
            write.CommandText = "DELETE FROM DailyFilterStats WHERE Day = $day";
            write.Parameters.AddWithValue("$day", day);
            write.ExecuteNonQuery();
            write.CommandText = @"INSERT INTO DailyFilterStats (Day, TeamId, DataSet, Field, Value, Uses, ZeroHits)
                VALUES ($day, $t, $d, $f, $v, $u, $z)";
            var t = write.Parameters.Add("$t", SqliteType.Text);
            var d = write.Parameters.Add("$d", SqliteType.Text);
            var f = write.Parameters.Add("$f", SqliteType.Text);
            var v = write.Parameters.Add("$v", SqliteType.Text);
            var u = write.Parameters.Add("$u", SqliteType.Integer);
            var z = write.Parameters.Add("$z", SqliteType.Integer);
            foreach (var ((team, ds, field, value), (uses, zero)) in counts)
            {
                t.Value = team; d.Value = ds; f.Value = field; v.Value = value; u.Value = uses; z.Value = zero;
                write.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Filter use in [fromDay, toDay]: how often people narrowed by each field value (a range
        /// by its field), with or without text, and how often it came back empty. Rolled days from
        /// <c>DailyFilterStats</c>, the rest parsed live - the same merge as the query reads.
        /// </summary>
        public List<FilterStat> TopFilters(string teamId, string dataSet, long fromDay, long toDay, int limit)
        {
            long rolledTo = Math.Min(toDay, LastRolledDay());
            var counts = new Dictionary<(string Team, string Ds, string Field, string Value), (long Uses, long Zero)>();
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT Field, Value, SUM(Uses), SUM(ZeroHits) FROM DailyFilterStats
                    WHERE TeamId = $t AND DataSet = $d AND Day >= $fromDay AND Day <= $rolledTo
                    GROUP BY Field, Value";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$fromDay", fromDay);
                cmd.Parameters.AddWithValue("$rolledTo", rolledTo);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    counts[(teamId, dataSet, r.GetString(0), r.GetString(1))] = (r.GetInt64(2), r.GetInt64(3));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT c.FilterKey, c.HitCount FROM " + CountedSearches(LiveWhere) +
                    " c WHERE c.FilterKey IS NOT NULL";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$liveFrom", Math.Max(fromDay, rolledTo + 1) * 86_400_000L);
                cmd.Parameters.AddWithValue("$toEx", (toDay + 1) * 86_400_000L);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    CountOperands(counts, teamId, dataSet, r.GetString(0), r.GetInt32(1));
            }
            return counts
                .Select(kv => new FilterStat(kv.Key.Field, kv.Key.Value, kv.Value.Uses, kv.Value.Zero))
                .OrderByDescending(f => f.Uses).ThenBy(f => f.Field).ThenBy(f => f.Value)
                .Take(limit).ToList();
        }

        /// <summary>The newest Unix day the rollup has covered, or -1 when it never ran. Reads
        /// merge the daily tables up to here with the raw rows after it.</summary>
        public long LastRolledDay()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT LastRolledDay FROM RollupState WHERE Id = 1";
            return cmd.ExecuteScalar() is long d ? d : -1;
        }

        /// <summary>The Unix day of the oldest raw event, or -1 when there are none. The rollup
        /// uses it after a lost watermark, so old raw days are rolled before they can be pruned.</summary>
        public long OldestEventDay()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT MIN(t) FROM (
                SELECT MIN(Timestamp) AS t FROM SearchEvents
                UNION ALL SELECT MIN(Timestamp) FROM SelectEvents
                UNION ALL SELECT MIN(Timestamp) FROM ConvertEvents)";
            return cmd.ExecuteScalar() is long ms ? DayOf(ms) : -1;
        }

        /// <summary>Deletes raw event rows older than the given Unix-ms cutoff. Call only for
        /// days the rollup has already covered.</summary>
        public int PruneRawBefore(long unixMsCutoff)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM SearchEvents  WHERE Timestamp < $cut;";
            cmd.Parameters.AddWithValue("$cut", unixMsCutoff);
            int n = cmd.ExecuteNonQuery();
            cmd.CommandText = @"DELETE FROM SelectEvents  WHERE Timestamp < $cut;";
            n += cmd.ExecuteNonQuery();
            cmd.CommandText = @"DELETE FROM ConvertEvents WHERE Timestamp < $cut;";
            n += cmd.ExecuteNonQuery();
            return n;
        }

        /// <summary>Row count of one statistics table, for diagnostics and tests.</summary>
        public long Count(string table)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = table switch
            {
                "SearchEvents" => "SELECT COUNT(*) FROM SearchEvents",
                "SelectEvents" => "SELECT COUNT(*) FROM SelectEvents",
                "ConvertEvents" => "SELECT COUNT(*) FROM ConvertEvents",
                "DailyQueryStats" => "SELECT COUNT(*) FROM DailyQueryStats",
                "DailyDocumentStats" => "SELECT COUNT(*) FROM DailyDocumentStats",
                "DailyFilterStats" => "SELECT COUNT(*) FROM DailyFilterStats",
                "DatasetChanges" => "SELECT COUNT(*) FROM DatasetChanges",
                "DailyDocumentChanges" => "SELECT COUNT(*) FROM DailyDocumentChanges",
                "SubjectDocumentStats" => "SELECT COUNT(*) FROM SubjectDocumentStats",
                _ => throw new ArgumentException($"Unknown statistics table '{table}'.", nameof(table)),
            };
            return (long)cmd.ExecuteScalar()!;
        }

        /// <summary>
        /// Top queries in [fromDay, toDay], rolled days from DailyQueryStats and the rest live
        /// from the raw rows, merged. With <paramref name="zeroHitsOnly"/> the list is the
        /// zero-hit report, ordered by how often the query found nothing.
        /// </summary>
        public List<QueryStat> TopQueries(string teamId, string dataSet, long fromDay, long toDay,
            int limit, bool zeroHitsOnly = false)
        {
            long rolledTo = Math.Min(toDay, LastRolledDay());
            long liveFromMs = Math.Max(fromDay, rolledTo + 1) * 86_400_000L;
            long toMsExcl = (toDay + 1) * 86_400_000L;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var order = zeroHitsOnly ? "HAVING SUM(ZeroHits) > 0 ORDER BY Z DESC, S DESC" : "ORDER BY S DESC";
            cmd.CommandText = $@"
SELECT QueryText, SUM(Searches) AS S, SUM(ZeroHits) AS Z, SUM(Selects) AS C,
       SUM(Clicked) AS K, SUM(PosSum) FROM (
    SELECT QueryText, Searches, ZeroHits, Selects, ClickedSearches AS Clicked, PositionSum AS PosSum
      FROM DailyQueryStats
     WHERE TeamId = $t AND DataSet = $d AND Day >= $fromDay AND Day <= $rolledTo
    UNION ALL
    SELECT lower(c.QueryText), 1, CASE WHEN c.HitCount = 0 THEN 1 ELSE 0 END, COALESCE(c.Cnt, 0),
           CASE WHEN c.Cnt > 0 THEN 1 ELSE 0 END, COALESCE(c.PosSum, 0)
      FROM {CountedSearches(LiveWhere)} c
     WHERE trim(c.QueryText) <> ''
)
GROUP BY QueryText
{order}
LIMIT $n";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$fromDay", fromDay);
            cmd.Parameters.AddWithValue("$rolledTo", rolledTo);
            cmd.Parameters.AddWithValue("$liveFrom", liveFromMs);
            cmd.Parameters.AddWithValue("$toEx", toMsExcl);
            cmd.Parameters.AddWithValue("$n", limit);
            var result = new List<QueryStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new QueryStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
                    r.GetInt64(4), r.GetInt64(5)));
            return result;
        }

        /// <summary>Top documents by selects in [fromDay, toDay], with converts and value summed
        /// the same way - rolled days plus live raw rows.</summary>
        public List<DocumentStat> TopDocuments(string teamId, string dataSet, long fromDay, long toDay, int limit)
        {
            long rolledTo = Math.Min(toDay, LastRolledDay());
            long liveFromMs = Math.Max(fromDay, rolledTo + 1) * 86_400_000L;
            long toMsExcl = (toDay + 1) * 86_400_000L;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT DocumentKey, SUM(Sel) AS S, SUM(Conv) AS C, SUM(Val) FROM (
    SELECT DocumentKey, Selects AS Sel, Converts AS Conv, ConvertValueSum AS Val FROM DailyDocumentStats
     WHERE TeamId = $t AND DataSet = $d AND Day >= $fromDay AND Day <= $rolledTo
    UNION ALL
    SELECT DocumentKey, 1, 0, 0.0 FROM SelectEvents
     WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $liveFrom AND Timestamp < $toEx
    UNION ALL
    SELECT DocumentKey, 0, 1, COALESCE(Value, 0) FROM ConvertEvents
     WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $liveFrom AND Timestamp < $toEx
)
GROUP BY DocumentKey
ORDER BY S DESC, C DESC
LIMIT $n";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$fromDay", fromDay);
            cmd.Parameters.AddWithValue("$rolledTo", rolledTo);
            cmd.Parameters.AddWithValue("$liveFrom", liveFromMs);
            cmd.Parameters.AddWithValue("$toEx", toMsExcl);
            cmd.Parameters.AddWithValue("$n", limit);
            var result = new List<DocumentStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new DocumentStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetDouble(3)));
            return result;
        }

        /// <summary>
        /// The per-day series behind the charts: searches, zero-hits, clicked searches and
        /// selects on the search's day, converts and their value on their own day. Rolled days
        /// come from the daily tables, the rest live from the raw rows.
        /// </summary>
        public List<DailyStat> TimeSeries(string teamId, string dataSet, long fromDay, long toDay)
        {
            var rows = TimeSeriesInternal(teamId, dataSet, fromDay, toDay, perDay: true);
            return rows;
        }

        /// <summary>The window's totals - the dashboard's header numbers. Same merge as the
        /// time series, summed.</summary>
        public OverviewStat Overview(string teamId, string dataSet, long fromDay, long toDay)
        {
            var rows = TimeSeriesInternal(teamId, dataSet, fromDay, toDay, perDay: false);
            var r = rows.Count == 0 ? default : rows[0];
            return new OverviewStat(r.Searches, r.ZeroHits, r.ClickedSearches, r.Selects,
                r.PositionSum, r.Converts, r.ConvertValueSum);
        }

        private List<DailyStat> TimeSeriesInternal(string teamId, string dataSet, long fromDay, long toDay, bool perDay)
        {
            long rolledTo = Math.Min(toDay, LastRolledDay());
            long liveFromMs = Math.Max(fromDay, rolledTo + 1) * 86_400_000L;
            long toMsExcl = (toDay + 1) * 86_400_000L;
            var group = perDay ? "GROUP BY Day ORDER BY Day" : "";
            var dayCol = perDay ? "Day" : "0";
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT {dayCol}, SUM(Searches), SUM(ZeroHits), SUM(Clicked), SUM(Sel), SUM(Conv), SUM(Val), SUM(PosSum) FROM (
    SELECT Day, Searches, ZeroHits, ClickedSearches AS Clicked, Selects AS Sel,
           0 AS Conv, 0.0 AS Val, PositionSum AS PosSum
      FROM DailyQueryStats
     WHERE TeamId = $t AND DataSet = $d AND Day >= $fromDay AND Day <= $rolledTo
    UNION ALL
    SELECT Day, 0, 0, 0, 0, Converts, ConvertValueSum, 0 FROM DailyDocumentStats
     WHERE TeamId = $t AND DataSet = $d AND Day >= $fromDay AND Day <= $rolledTo
    UNION ALL
    SELECT c.Timestamp / 86400000, 1, CASE WHEN c.HitCount = 0 THEN 1 ELSE 0 END,
           CASE WHEN c.Cnt > 0 THEN 1 ELSE 0 END, COALESCE(c.Cnt, 0), 0, 0.0, COALESCE(c.PosSum, 0)
      FROM {CountedSearches(LiveWhere)} c
     WHERE trim(c.QueryText) <> ''
    UNION ALL
    SELECT Timestamp / 86400000, 0, 0, 0, 0, 1, COALESCE(Value, 0), 0 FROM ConvertEvents
     WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $liveFrom AND Timestamp < $toEx
)
{group}";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$fromDay", fromDay);
            cmd.Parameters.AddWithValue("$rolledTo", rolledTo);
            cmd.Parameters.AddWithValue("$liveFrom", liveFromMs);
            cmd.Parameters.AddWithValue("$toEx", toMsExcl);
            var result = new List<DailyStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue; // the no-rows aggregate row of the totals query
                result.Add(new DailyStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
                    r.GetInt64(4), r.GetInt64(5), r.GetDouble(6), r.GetInt64(7)));
            }
            return result;
        }

        /// <summary>One subject's top documents, lifetime - the personalization read.</summary>
        public List<SubjectDocumentStat> TopForSubject(string teamId, string dataSet, string subject, int limit)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT DocumentKey, Selects, Converts, LastSeen FROM SubjectDocumentStats
                WHERE TeamId = $t AND DataSet = $d AND Subject = $s
                ORDER BY (Selects + Converts) DESC, LastSeen DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$s", subject);
            cmd.Parameters.AddWithValue("$n", limit);
            var result = new List<SubjectDocumentStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new SubjectDocumentStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
            return result;
        }

        /// <summary>Deletes every statistics row of one dataset - the explicit purge. Dataset
        /// delete does NOT call this: statistics survive delete/recreate by design.</summary>
        public int PurgeDataset(string teamId, string dataSet) =>
            ExecutePerTable("WHERE TeamId = $a AND DataSet = $b", teamId, dataSet);

        /// <summary>Deletes every statistics row of one team. Called when the team is deleted -
        /// the teamId is ours and nothing can resume it.</summary>
        public int PurgeTeam(string teamId)
        {
            int n = ExecutePerTable("WHERE TeamId = $a", teamId, null);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM DatasetSettings WHERE TeamId = $a";
            cmd.Parameters.AddWithValue("$a", teamId);
            n += cmd.ExecuteNonQuery();
            _recordFilters.Clear();
            return n;
        }

        /// <summary>
        /// The GDPR erasure of one subject within a dataset: the subject-document edge is
        /// deleted, and the raw rows are anonymised (Subject set to NULL) rather than deleted, so
        /// the aggregate counts stay true. The daily tables carry no subject.
        /// </summary>
        public int EraseSubject(string teamId, string dataSet, string subject)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$s", subject);
            int n = 0;
            foreach (var table in new[] { "SearchEvents", "SelectEvents", "ConvertEvents" })
            {
                cmd.CommandText = $"UPDATE {table} SET Subject = NULL WHERE TeamId = $t AND DataSet = $d AND Subject = $s";
                n += cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "DELETE FROM SubjectDocumentStats WHERE TeamId = $t AND DataSet = $d AND Subject = $s";
            n += cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }

        /// <summary>Moves a dataset's statistics to its new name. Called from the rename path -
        /// keyed on the customer's name, the rows must follow it.</summary>
        public int RenameDataset(string teamId, string oldName, string newName)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$old", oldName);
            cmd.Parameters.AddWithValue("$new", newName);
            int n = 0;
            // The dataset's settings move with it; AllTables leaves them out because a purge must
            // not quietly switch a privacy setting back on.
            foreach (var table in AllTables.Append("DatasetSettings"))
            {
                cmd.CommandText = $"UPDATE {table} SET DataSet = $new WHERE TeamId = $t AND DataSet = $old";
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            _recordFilters.TryRemove((teamId, oldName), out _);
            _recordFilters.TryRemove((teamId, newName), out _);
            return n;
        }

        private static readonly string[] AllTables =
            ["SearchEvents", "SelectEvents", "ConvertEvents",
             "DailyQueryStats", "DailyDocumentStats", "DailyFilterStats", "SubjectDocumentStats",
             // Deleting the statistics deletes their explanation too: starting the numbers over
             // starts the history of what moved them over (decided 1 Oct 2026).
             "DatasetChanges", "DailyDocumentChanges"];

        private int ExecutePerTable(string where, string a, string? b)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$a", a);
            if (b != null) cmd.Parameters.AddWithValue("$b", b);
            int n = 0;
            foreach (var table in AllTables)
            {
                cmd.CommandText = $"DELETE FROM {table} {where}";
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n;
        }

        /// <summary>Logs a one-line summary of the file and its row counts, for startup.</summary>
        public void LogState()
        {
            var size = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
            logger.LogInformation("statistics store at {Path}: {Bytes} bytes, {Searches} searches, {Selects} selects, {Converts} converts",
                _dbPath, size, Count("SearchEvents"), Count("SelectEvents"), Count("ConvertEvents"));
        }
    }
}
