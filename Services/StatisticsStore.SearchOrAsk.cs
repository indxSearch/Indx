using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>One judgement of what a visitor had typed (Search or ask, Notes/search-or-ask-server.md):
    /// the probability, answerability and the server's offer. It holds no text: <see cref="QueryId"/>
    /// names the search the page showed beside it, and the text is that search's. Recorded when the
    /// judge or its cache answered; an identifier, a refusal or a disabled dataset records nothing.
    /// Cost is the judge call's, null from the cache. AskPhrase is the Ask phrases taken off the typed
    /// text, null when none were (the search's text is then what was typed), and FocusedHits what the
    /// refocused text found: together they tell which questions no phrase catches, and which phrase
    /// left nothing to find.</summary>
    public readonly record struct AskIntentEventRow(
        string TeamId, string DataSet, string? QueryId, double P, double? Answerable, string Offer, bool Cached,
        string? Reason, double? Cost, string? Subject, string? Session, string? Source, long Timestamp,
        bool Counted = true, string? AskPhrase = null, int? FocusedHits = null);

    /// <summary>How questions start that no Ask phrase caught: their first words, how many searches
    /// began so, and one of them. What Observr suggests a lead-in from.</summary>
    public readonly record struct AskLeadInStat(string Words, long Searches, string Example);

    /// <summary>A refocus that left nothing to find: the phrase taken, and what was searched.</summary>
    public readonly record struct AskRefocusMissStat(string Phrase, string Searched, long Times);

    /// <summary>One question asked. The question is stored (it is new text, not a search); the
    /// answer is not (decided 11 Oct 2026). SourceKeys are the document keys handed to the model,
    /// comma separated, in their citation order. Outcome is answered, nothingFound, failed or
    /// stopped. A select whose queryId is <see cref="AskId"/> is a cited source opened.</summary>
    public readonly record struct AskEventRow(
        string AskId, string TeamId, string DataSet, string? QueryId, string Question, string Model,
        string SourceKeys, string Outcome, long? FirstTextMs, long TotalMs, double? Cost, double? Answerable,
        string? Subject, string? Session, string? Source, long Timestamp, bool Counted = true);

    /// <summary>A window's Search or ask totals. Offers count each search an offer was shown beside
    /// once, however many pauses judged it. Unanswered is the asks that found nothing or whose
    /// sources the judge thought could not answer. MedianFirstTextMs is read from the raw rows.</summary>
    public readonly record struct AskOverviewStat(long Judged, long Offers, long StrongOffers, long Asks,
        long Answered, long NothingFound, long Failed, long Stopped, long Unanswered, long SourcesOpened,
        double Cost, long? MedianFirstTextMs);

    /// <summary>A call taken against a dataset's daily budget, or one turned away because the day's
    /// budget was spent, on the budget's own day: UTC, days since 1970 as every statistics day. A row
    /// that is no call (<paramref name="Call"/> false) carries the cost of one with no row of its own:
    /// a filter the model made while a visitor typed.</summary>
    public readonly record struct AskBudgetRow(string TeamId, string DataSet, long Day, int Budget, bool Refused, long Timestamp,
        bool Call = true, double Cost = 0);

    /// <summary>A window's budget: the calls taken, the ones turned away, the days the budget ran
    /// out, and the busiest day's calls against that day's budget.</summary>
    public readonly record struct AskBudgetStat(long Calls, long Refused, int DaysRunOut, long PeakCalls, int PeakBudget, double Cost = 0);

    /// <summary>One question over a window, as asked (case and spacing folded).</summary>
    public readonly record struct AskQuestionStat(string Question, long Asks, long Answered, long NothingFound,
        long Unanswered, double? Answerability, long SourcesOpened, long LastAsked);

    /// <summary>One text an offer was shown beside, from the searches the intents name.</summary>
    public readonly record struct AskOfferStat(string Text, long Offers, long Strong, double MaxP, long Asked);

    public sealed partial class StatisticsStore
    {
        /// <summary>Below this the judge doubts the sources could answer; such an ask counts as
        /// unanswered, as one that found nothing does.</summary>
        public const double AnswerableFloor = 0.5;

        private static void EnsureSearchOrAskSchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS AskIntentEvents (
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    QueryId     TEXT,
    P           REAL    NOT NULL,
    Answerable  REAL,
    Offer       TEXT    NOT NULL,
    Cached      INTEGER NOT NULL,
    Reason      TEXT,
    Cost        REAL,
    Subject     TEXT,
    Session     TEXT,
    Source      TEXT,
    Timestamp   INTEGER NOT NULL,
    Counted     INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS IX_AskIntent_Ds_Ts ON AskIntentEvents (TeamId, DataSet, Timestamp);

CREATE TABLE IF NOT EXISTS AskEvents (
    AskId       TEXT    PRIMARY KEY,
    TeamId      TEXT    NOT NULL,
    DataSet     TEXT    NOT NULL,
    QueryId     TEXT,
    Question    TEXT    NOT NULL,
    Model       TEXT    NOT NULL,
    SourceKeys  TEXT    NOT NULL,
    Outcome     TEXT    NOT NULL,
    FirstTextMs INTEGER,
    TotalMs     INTEGER NOT NULL,
    Cost        REAL,
    Answerable  REAL,
    Subject     TEXT,
    Session     TEXT,
    Source      TEXT,
    Timestamp   INTEGER NOT NULL,
    Counted     INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS IX_Ask_Ds_Ts ON AskEvents (TeamId, DataSet, Timestamp);
CREATE INDEX IF NOT EXISTS IX_Ask_QueryId ON AskEvents (QueryId);

CREATE TABLE IF NOT EXISTS DailyAskStats (
    Day           INTEGER NOT NULL,
    TeamId        TEXT    NOT NULL,
    DataSet       TEXT    NOT NULL,
    Judged        INTEGER NOT NULL,
    Offers        INTEGER NOT NULL,
    StrongOffers  INTEGER NOT NULL,
    Asks          INTEGER NOT NULL,
    Answered      INTEGER NOT NULL,
    NothingFound  INTEGER NOT NULL,
    Failed        INTEGER NOT NULL,
    Stopped       INTEGER NOT NULL,
    Unanswered    INTEGER NOT NULL,
    SourcesOpened INTEGER NOT NULL,
    Cost          REAL    NOT NULL,
    PRIMARY KEY (Day, TeamId, DataSet)
);";
            cmd.ExecuteNonQuery();
            // The daily call budget, a row per dataset and UTC day (Oct 2026). The service counts in
            // memory to enforce it and reads today's row back after a restart, which used to start
            // the day over at 0 and let it run past its budget.
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS AskBudgetDays (
    TeamId   TEXT    NOT NULL,
    DataSet  TEXT    NOT NULL,
    Day      INTEGER NOT NULL,
    Calls    INTEGER NOT NULL,
    Refused  INTEGER NOT NULL,
    Budget   INTEGER NOT NULL,
    SpentAt  INTEGER NULL,
    Cost     REAL    NOT NULL DEFAULT 0,
    PRIMARY KEY (TeamId, DataSet, Day)
);";
            cmd.ExecuteNonQuery();
            // Ask phrases (Oct 2026): what was taken off the typed text, and what the rest found.
            foreach (var column in new[] { "AskPhrase TEXT NULL", "FocusedHits INTEGER NULL" })
            {
                try
                {
                    cmd.CommandText = $"ALTER TABLE AskIntentEvents ADD COLUMN {column}";
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException) { /* already there */ }
            }
            // The budget's cost column came a few hours after its table (20 Oct 2026): a table made
            // without it is given it, since CREATE TABLE IF NOT EXISTS leaves an existing one alone.
            try
            {
                cmd.CommandText = "ALTER TABLE AskBudgetDays ADD COLUMN Cost REAL NOT NULL DEFAULT 0";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* already there */ }
        }

        /// <summary>Writes Search or ask's rows in one transaction. Called only by
        /// <see cref="StatisticsWriter"/>, like <see cref="WriteBatch"/>.</summary>
        public void WriteSearchOrAsk(IReadOnlyList<AskIntentEventRow> intents, IReadOnlyList<AskEventRow> asks)
        {
            if (intents.Count == 0 && asks.Count == 0) return;
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var r in intents)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO AskIntentEvents
                    (TeamId, DataSet, QueryId, P, Answerable, Offer, Cached, Reason, Cost, Subject, Session, Source, Timestamp, Counted, AskPhrase, FocusedHits)
                    VALUES ($t, $d, $q, $p, $a, $o, $c, $r, $cost, $subj, $sess, $src, $ts, $cnt, $phrase, $fhits)";
                cmd.Parameters.AddWithValue("$t", r.TeamId);
                cmd.Parameters.AddWithValue("$d", r.DataSet);
                cmd.Parameters.AddWithValue("$q", (object?)r.QueryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$p", r.P);
                cmd.Parameters.AddWithValue("$a", (object?)r.Answerable ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$o", r.Offer);
                cmd.Parameters.AddWithValue("$c", r.Cached ? 1 : 0);
                cmd.Parameters.AddWithValue("$r", (object?)r.Reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cost", (object?)r.Cost ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$subj", (object?)r.Subject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sess", (object?)r.Session ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$src", (object?)r.Source ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", r.Timestamp);
                cmd.Parameters.AddWithValue("$cnt", r.Counted ? 1 : 0);
                cmd.Parameters.AddWithValue("$phrase", (object?)r.AskPhrase ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fhits", (object?)r.FocusedHits ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            foreach (var r in asks)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR IGNORE INTO AskEvents
                    (AskId, TeamId, DataSet, QueryId, Question, Model, SourceKeys, Outcome, FirstTextMs, TotalMs, Cost,
                     Answerable, Subject, Session, Source, Timestamp, Counted)
                    VALUES ($id, $t, $d, $q, $question, $m, $keys, $out, $first, $total, $cost, $a, $subj, $sess, $src, $ts, $cnt)";
                cmd.Parameters.AddWithValue("$id", r.AskId);
                cmd.Parameters.AddWithValue("$t", r.TeamId);
                cmd.Parameters.AddWithValue("$d", r.DataSet);
                cmd.Parameters.AddWithValue("$q", (object?)r.QueryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$question", r.Question);
                cmd.Parameters.AddWithValue("$m", r.Model);
                cmd.Parameters.AddWithValue("$keys", r.SourceKeys);
                cmd.Parameters.AddWithValue("$out", r.Outcome);
                cmd.Parameters.AddWithValue("$first", (object?)r.FirstTextMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$total", r.TotalMs);
                cmd.Parameters.AddWithValue("$cost", (object?)r.Cost ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$a", (object?)r.Answerable ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$subj", (object?)r.Subject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sess", (object?)r.Session ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$src", (object?)r.Source ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", r.Timestamp);
                cmd.Parameters.AddWithValue("$cnt", r.Counted ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // The window's sums over raw rows in [$from, $to), the one definition the rollup and the
        // live read share. An offer is a search shown with one: intents without a search id each
        // count, as nothing says two of them were the same.
        private const string AskSumsSql = @"
SELECT
  (SELECT COUNT(*) FROM AskIntentEvents i WHERE {i}),
  (SELECT COUNT(DISTINCT CASE WHEN i.Offer <> 'none' THEN COALESCE(i.QueryId, 'row' || i.rowid) END) FROM AskIntentEvents i WHERE {i}),
  (SELECT COUNT(DISTINCT CASE WHEN i.Offer = 'strong' THEN COALESCE(i.QueryId, 'row' || i.rowid) END) FROM AskIntentEvents i WHERE {i}),
  (SELECT COUNT(*) FROM AskEvents a WHERE {a}),
  (SELECT COUNT(*) FROM AskEvents a WHERE {a} AND a.Outcome = 'answered'),
  (SELECT COUNT(*) FROM AskEvents a WHERE {a} AND a.Outcome = 'nothingFound'),
  (SELECT COUNT(*) FROM AskEvents a WHERE {a} AND a.Outcome = 'failed'),
  (SELECT COUNT(*) FROM AskEvents a WHERE {a} AND a.Outcome = 'stopped'),
  (SELECT COUNT(*) FROM AskEvents a WHERE {a} AND (a.Outcome = 'nothingFound' OR a.Answerable < $floor)),
  (SELECT COUNT(*) FROM SelectEvents x JOIN AskEvents a ON x.QueryId = a.AskId WHERE {a} AND x.Counted = 1),
  COALESCE((SELECT SUM(i.Cost) FROM AskIntentEvents i WHERE {i}), 0)
    + COALESCE((SELECT SUM(a.Cost) FROM AskEvents a WHERE {a}), 0)";

        private static string AskSums(string? source)
        {
            string Where(string alias) => $"{alias}.TeamId = $t AND {alias}.DataSet = $d AND {alias}.Timestamp >= $from " +
                                          $"AND {alias}.Timestamp < $to AND {alias}.Counted = 1" +
                                          (source == null ? "" : $" AND {alias}.Source = $src");
            return AskSumsSql.Replace("{i}", Where("i")).Replace("{a}", Where("a"));
        }

        /// <summary>One dataset's local day of Search or ask, rolled from its raw rows; the day's
        /// row is replaced. Part of <see cref="RollupDataset"/>, in its transaction.</summary>
        private static void RollupSearchOrAsk(SqliteConnection conn, SqliteTransaction tx, string team, string ds,
            long day, long from, long to)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM DailyAskStats WHERE Day = $day AND TeamId = $t AND DataSet = $d;" +
                              @"INSERT INTO DailyAskStats (Day, TeamId, DataSet, Judged, Offers, StrongOffers, Asks, Answered,
                                    NothingFound, Failed, Stopped, Unanswered, SourcesOpened, Cost)
                                SELECT $day, $t, $d, * FROM (" + AskSums(null) + @")
                                 WHERE EXISTS (SELECT 1 FROM AskIntentEvents WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $to)
                                    OR EXISTS (SELECT 1 FROM AskEvents WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $to);";
            cmd.Parameters.AddWithValue("$day", day);
            cmd.Parameters.AddWithValue("$t", team);
            cmd.Parameters.AddWithValue("$d", ds);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$floor", AnswerableFloor);
            cmd.ExecuteNonQuery();
        }

        /// <summary>The window's Search or ask totals: rolled days from DailyAskStats, the rest
        /// live from the raw rows, as <see cref="Overview"/> merges searches. A per-source read uses
        /// the raw rows only (the daily table carries no source).</summary>
        /// <summary>Adds a flush's budget calls to their days: one upsert per dataset and day. A day's
        /// budget is the latest seen; it ran out at its first call turned away.</summary>
        public void WriteBudget(IReadOnlyList<AskBudgetRow> rows)
        {
            if (rows.Count == 0) return;
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var g in rows.GroupBy(r => (r.TeamId, r.DataSet, r.Day)))
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO AskBudgetDays (TeamId, DataSet, Day, Calls, Refused, Budget, SpentAt, Cost)
                    VALUES ($t, $d, $day, $calls, $refused, $budget, $spent, $cost)
                    ON CONFLICT (TeamId, DataSet, Day) DO UPDATE SET
                        Calls = Calls + excluded.Calls, Refused = Refused + excluded.Refused,
                        Budget = excluded.Budget, SpentAt = COALESCE(SpentAt, excluded.SpentAt), Cost = Cost + excluded.Cost";
                cmd.Parameters.AddWithValue("$t", g.Key.TeamId);
                cmd.Parameters.AddWithValue("$d", g.Key.DataSet);
                cmd.Parameters.AddWithValue("$day", g.Key.Day);
                cmd.Parameters.AddWithValue("$calls", g.Count(r => r.Call && !r.Refused));
                cmd.Parameters.AddWithValue("$cost", g.Sum(r => r.Cost));
                cmd.Parameters.AddWithValue("$refused", g.Count(r => r.Refused));
                cmd.Parameters.AddWithValue("$budget", g.MaxBy(r => r.Timestamp).Budget);
                cmd.Parameters.AddWithValue("$spent", g.Where(r => r.Refused).Select(r => (object)r.Timestamp).DefaultIfEmpty(DBNull.Value).Min()!);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        /// <summary>The calls a dataset's budget day holds so far, for the count after a restart.</summary>
        public long BudgetCalls(string teamId, string dataSet, long day)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Calls FROM AskBudgetDays WHERE TeamId = $t AND DataSet = $d AND Day = $day";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$day", day);
            return cmd.ExecuteScalar() is long n ? n : 0;
        }

        /// <summary>The budget over a window of days. The budget's days are UTC; the window's are the
        /// dataset's, so a day at either end can be a few hours off. Nothing here is rolled up: a
        /// row is already a day.</summary>
        public AskBudgetStat AskBudget(string teamId, string dataSet, long fromDay, long toDay)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(SUM(Calls),0), COALESCE(SUM(Refused),0), COUNT(SpentAt), COALESCE(SUM(Cost),0),
                    (SELECT Calls FROM AskBudgetDays WHERE TeamId = $t AND DataSet = $d AND Day BETWEEN $from AND $to ORDER BY Calls DESC LIMIT 1),
                    (SELECT Budget FROM AskBudgetDays WHERE TeamId = $t AND DataSet = $d AND Day BETWEEN $from AND $to ORDER BY Calls DESC LIMIT 1)
                FROM AskBudgetDays WHERE TeamId = $t AND DataSet = $d AND Day BETWEEN $from AND $to";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$from", fromDay);
            cmd.Parameters.AddWithValue("$to", toDay);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return default;
            return new AskBudgetStat(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2),
                r.IsDBNull(4) ? 0 : r.GetInt64(4), r.IsDBNull(5) ? 0 : r.GetInt32(5), r.GetDouble(3));
        }

        /// <summary>The calls against the budget per day, for the statistics chart.</summary>
        public Dictionary<long, long> BudgetCallsByDay(string teamId, string dataSet, long fromDay, long toDay)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Day, Calls FROM AskBudgetDays WHERE TeamId = $t AND DataSet = $d AND Day BETWEEN $from AND $to";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$from", fromDay);
            cmd.Parameters.AddWithValue("$to", toDay);
            var byDay = new Dictionary<long, long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) byDay[r.GetInt64(0)] = r.GetInt64(1);
            return byDay;
        }

        public AskOverviewStat AskOverview(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long rolledTo = RolledTo(fromDay, toDay, source);
            long liveFromMs = StatisticsDays.Start(tz, Math.Max(fromDay, rolledTo + 1));
            long toMsExcl = StatisticsDays.Start(tz, toDay + 1);
            var sums = new double[11];
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT COALESCE(SUM(Judged),0), COALESCE(SUM(Offers),0), COALESCE(SUM(StrongOffers),0),
                    COALESCE(SUM(Asks),0), COALESCE(SUM(Answered),0), COALESCE(SUM(NothingFound),0), COALESCE(SUM(Failed),0),
                    COALESCE(SUM(Stopped),0), COALESCE(SUM(Unanswered),0), COALESCE(SUM(SourcesOpened),0), COALESCE(SUM(Cost),0)
                    FROM DailyAskStats WHERE TeamId = $t AND DataSet = $d AND Day >= $fromDay AND Day <= $rolledTo";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$fromDay", fromDay);
                cmd.Parameters.AddWithValue("$rolledTo", rolledTo);
                using var r = cmd.ExecuteReader();
                if (r.Read()) for (int k = 0; k < sums.Length; k++) sums[k] += r.GetDouble(k);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = AskSums(source);
                AddWindow(cmd, teamId, dataSet, liveFromMs, toMsExcl, source);
                using var r = cmd.ExecuteReader();
                if (r.Read()) for (int k = 0; k < sums.Length; k++) sums[k] += r.GetDouble(k);
            }
            long? median = null;
            using (var cmd = conn.CreateCommand())
            {
                // Raw rows only: within retention, which the console's windows (7, 30, 90 days) are.
                cmd.CommandText = @"SELECT FirstTextMs FROM AskEvents a
                    WHERE a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND a.Counted = 1
                      AND a.FirstTextMs IS NOT NULL" + (source == null ? "" : " AND a.Source = $src") + " ORDER BY FirstTextMs";
                AddWindow(cmd, teamId, dataSet, StatisticsDays.Start(tz, fromDay), toMsExcl, source);
                var all = new List<long>();
                using var r = cmd.ExecuteReader();
                while (r.Read()) all.Add(r.GetInt64(0));
                if (all.Count > 0) median = all[all.Count / 2];
            }
            long L(int k) => (long)sums[k];
            return new AskOverviewStat(L(0), L(1), L(2), L(3), L(4), L(5), L(6), L(7), L(8), L(9), sums[10], median);
        }

        /// <summary>The questions asked in the window, the ones the content did not answer first,
        /// then the most asked. From the raw rows: a question list is within retention.</summary>
        public List<AskQuestionStat> TopQuestions(string teamId, string dataSet, long fromDay, long toDay,
            int limit = 50, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT lower(trim(a.Question)) AS Q, COUNT(*),
       SUM(CASE WHEN a.Outcome = 'answered' THEN 1 ELSE 0 END),
       SUM(CASE WHEN a.Outcome = 'nothingFound' THEN 1 ELSE 0 END),
       SUM(CASE WHEN a.Outcome = 'nothingFound' OR a.Answerable < $floor THEN 1 ELSE 0 END) AS Unanswered,
       AVG(a.Answerable),
       (SELECT COUNT(*) FROM SelectEvents x JOIN AskEvents b ON x.QueryId = b.AskId
         WHERE b.TeamId = $t AND b.DataSet = $d AND lower(trim(b.Question)) = lower(trim(a.Question)) AND x.Counted = 1
           AND b.Timestamp >= $from AND b.Timestamp < $to),
       MAX(a.Timestamp)
  FROM AskEvents a
 WHERE a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND a.Counted = 1" +
                (source == null ? "" : " AND a.Source = $src") + @"
 GROUP BY Q
 ORDER BY Unanswered DESC, COUNT(*) DESC, MAX(a.Timestamp) DESC
 LIMIT $limit";
            AddWindow(cmd, teamId, dataSet, StatisticsDays.Start(tz, fromDay), StatisticsDays.Start(tz, toDay + 1), source);
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<AskQuestionStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AskQuestionStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                    r.IsDBNull(5) ? null : r.GetDouble(5), r.GetInt64(6), r.GetInt64(7)));
            return list;
        }

        /// <summary>The texts an offer was shown beside, most offered first, with how often an ask
        /// followed. The text is the search's: an intent whose search was not recorded has none and
        /// is left out. From the raw rows.</summary>
        public List<AskOfferStat> TopOffers(string teamId, string dataSet, long fromDay, long toDay,
            int limit = 50, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT lower(trim(s.QueryText)) AS T, COUNT(DISTINCT i.QueryId),
       COUNT(DISTINCT CASE WHEN i.Offer = 'strong' THEN i.QueryId END), MAX(i.P),
       (SELECT COUNT(*) FROM AskEvents a WHERE a.QueryId IN (
            SELECT j.QueryId FROM AskIntentEvents j JOIN SearchEvents t ON t.QueryId = j.QueryId
             WHERE j.TeamId = $t AND j.DataSet = $d AND lower(trim(t.QueryText)) = lower(trim(s.QueryText))))
  FROM AskIntentEvents i JOIN SearchEvents s ON s.QueryId = i.QueryId
 WHERE i.TeamId = $t AND i.DataSet = $d AND i.Timestamp >= $from AND i.Timestamp < $to AND i.Counted = 1
   AND i.Offer <> 'none'" + (source == null ? "" : " AND i.Source = $src") + @"
 GROUP BY T
 ORDER BY COUNT(DISTINCT i.QueryId) DESC, MAX(i.P) DESC
 LIMIT $limit";
            AddWindow(cmd, teamId, dataSet, StatisticsDays.Start(tz, fromDay), StatisticsDays.Start(tz, toDay + 1), source);
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<AskOfferStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AskOfferStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetDouble(3), r.GetInt64(4)));
            return list;
        }

        /// <summary>How the questions start that no Ask phrase caught: searches the judge offered an
        /// answer beside, with nothing taken off, grouped by their first <paramref name="words"/>
        /// words, those that recur first. A start that recurs across many questions is a lead-in the
        /// dataset's list lacks. From the raw rows.</summary>
        public List<AskLeadInStat> LeadInCandidates(string teamId, string dataSet, long fromDay, long toDay,
            int words = 2, int minSearches = 2, int limit = 10)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT DISTINCT i.QueryId, s.QueryText
  FROM AskIntentEvents i JOIN SearchEvents s ON s.QueryId = i.QueryId
 WHERE i.TeamId = $t AND i.DataSet = $d AND i.Timestamp >= $from AND i.Timestamp < $to AND i.Counted = 1
   AND i.Offer <> 'none' AND i.AskPhrase IS NULL";
            AddWindow(cmd, teamId, dataSet, StatisticsDays.Start(tz, fromDay), StatisticsDays.Start(tz, toDay + 1), null);
            var starts = new Dictionary<string, (long Count, string Example)>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var text = r.GetString(1).Trim();
                    var parts = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    // A start needs the words before it and something after: a two-word search is a lookup.
                    if (parts.Length <= words) continue;
                    var start = string.Join(' ', parts.Take(words));
                    starts[start] = starts.TryGetValue(start, out var s) ? (s.Count + 1, s.Example) : (1, text);
                }
            return starts.Where(kv => kv.Value.Count >= minSearches)
                .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(limit).Select(kv => new AskLeadInStat(kv.Key, kv.Value.Count, kv.Value.Example)).ToList();
        }

        /// <summary>Ask phrases that took so much off that the rest found nothing, most often first:
        /// a phrase too wide for this dataset, or a question about something it does not hold.</summary>
        public List<AskRefocusMissStat> RefocusMisses(string teamId, string dataSet, long fromDay, long toDay, int limit = 10)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT i.AskPhrase, lower(trim(s.QueryText)) AS T, COUNT(DISTINCT i.QueryId)
  FROM AskIntentEvents i JOIN SearchEvents s ON s.QueryId = i.QueryId
 WHERE i.TeamId = $t AND i.DataSet = $d AND i.Timestamp >= $from AND i.Timestamp < $to AND i.Counted = 1
   AND i.AskPhrase IS NOT NULL AND i.FocusedHits = 0
 GROUP BY i.AskPhrase, T
 ORDER BY COUNT(DISTINCT i.QueryId) DESC
 LIMIT $limit";
            AddWindow(cmd, teamId, dataSet, StatisticsDays.Start(tz, fromDay), StatisticsDays.Start(tz, toDay + 1), null);
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<AskRefocusMissStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new AskRefocusMissStat(r.GetString(0), r.GetString(1), r.GetInt64(2)));
            return list;
        }

        private static void AddWindow(SqliteCommand cmd, string teamId, string dataSet, long fromMs, long toMsExcl, string? source)
        {
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMsExcl);
            cmd.Parameters.AddWithValue("$floor", AnswerableFloor);
            if (source != null) cmd.Parameters.AddWithValue("$src", source);
        }
    }
}
