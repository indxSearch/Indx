using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>How people type: the counted searches of a window by how many words they are (one
    /// to four, and five or more), how many came with a filter, the median length in characters,
    /// and two things about typing. <see cref="MidWord"/> is the searches that settled on a partial
    /// word, "pika" when someone else searched "pikachu": search-as-you-type paying off, a lower
    /// bound, since a word no one completed cannot be told from a whole one. <see cref="Keystrokes"/>
    /// is the median number of keystroke searches before a settled one, over the
    /// <see cref="WithSession"/> searches that carried a session; 0 with none.</summary>
    public sealed record TypingStat(long[] Words, long WithFilter, long MedianLength, long MidWord, long WithSession, long Keystrokes)
    {
        /// <summary>False when the window held more than <see cref="StatisticsStore.TypingDetailMaxSearches"/>
        /// searches, so <see cref="MidWord"/> and <see cref="Keystrokes"/> were left unmeasured: both are
        /// a pass over the texts per text, worth seconds on a busy shop's quarter, and nice to have.</summary>
        public bool DetailMeasured { get; init; } = true;
        public long Total { get { long n = 0; foreach (var w in Words) n += w; return n; } }
        public long OneWord => Words[0];
        public long TwoWords => Words[1];
        public long ThreeOrMore => Words[2] + Words[3] + Words[4];
        public static TypingStat Empty => new(new long[5], 0, 0, 0, 0, 0);
    }

    /// <summary>The counted searches of a window from one language, a BCP 47 tag as the storefront
    /// sent it (?locale=), null for the searches that named none.</summary>
    public readonly record struct LocaleStat(string? Locale, long Searches);

    public sealed partial class StatisticsStore
    {
        /// <summary>
        /// Counted searches with text in [fromDay, toDay] by their word count and whether a filter
        /// came along, and the median length. From the raw rows, as far back as they are kept: the
        /// daily tables hold counts, not texts. A word is what a space separates; runs of spaces
        /// count as one.
        /// </summary>
        public TypingStat Typing(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // Words: one more than the spaces, runs of spaces collapsed first (three passes fold up
            // to eight, which is more than a query has). The folding costs three replaces per row,
            // a second over a busy shop's quarter, so only a text with a run gets it.
            const string Folded = "replace(replace(replace(trim(c.QueryText), '    ', ' '), '   ', ' '), '  ', ' ')";
            const string Words = $"(CASE WHEN c.QueryText LIKE '%  %' THEN length({Folded}) - length(replace({Folded}, ' ', '')) ELSE length(trim(c.QueryText)) - length(replace(trim(c.QueryText), ' ', '')) END + 1)";
            cmd.CommandText = $@"
SELECT SUM({Words} = 1), SUM({Words} = 2), SUM({Words} = 3), SUM({Words} = 4), SUM({Words} >= 5),
       SUM(c.FilterKey IS NOT NULL AND c.FilterKey <> '')
  FROM {CountedSearches(LiveWhereFor(source))} c
 WHERE trim(c.QueryText) <> ''";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(cmd, source);
            var words = new long[5];
            long filtered = 0;
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read() && !r.IsDBNull(0))
                {
                    for (int i = 0; i < 5; i++) words[i] = r.GetInt64(i);
                    filtered = r.GetInt64(5);
                }
            }
            long total = words.Sum();
            if (total == 0) return TypingStat.Empty;
            bool detail = total <= TypingDetailMaxSearches;

            using var median = conn.CreateCommand();
            median.CommandText = $@"
SELECT length(trim(c.QueryText)) FROM {CountedSearches(LiveWhereFor(source))} c
 WHERE trim(c.QueryText) <> ''
 ORDER BY 1 LIMIT 1 OFFSET $mid";
            median.Parameters.AddWithValue("$t", teamId);
            median.Parameters.AddWithValue("$d", dataSet);
            median.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            median.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            median.Parameters.AddWithValue("$mid", total / 2);
            AddSource(median, source);
            long mid = median.ExecuteScalar() is long m ? m : 0;

            // Settled mid-word: a search whose text some longer search continues without a space
            // at the join ("pika" and "pikachu", not "pika" and "pika evolution"). Over the distinct
            // texts, each weighted by its searches, so the comparison is texts by texts rather than
            // searches by searches; and only up to a size where that is cheap.
            long midWordCount = 0, withSession = 0, keystrokes = 0;
            if (!detail) return new TypingStat(words, filtered, mid, 0, 0, 0) { DetailMeasured = false };
            using var midWord = conn.CreateCommand();
            midWord.CommandText = $@"
WITH q AS (SELECT lower(trim(c.QueryText)) AS t, COUNT(*) AS n
             FROM {CountedSearches(LiveWhereFor(source))} c
            WHERE trim(c.QueryText) <> ''
            GROUP BY lower(trim(c.QueryText)))
SELECT (SELECT COUNT(*) FROM q),
       (SELECT SUM(a.n) FROM q a
         WHERE (SELECT COUNT(*) FROM q) <= {MidWordMaxTexts}
           AND EXISTS (SELECT 1 FROM q b
                        WHERE length(b.t) > length(a.t)
                          AND substr(b.t, 1, length(a.t)) = a.t
                          AND substr(b.t, length(a.t) + 1, 1) <> ' '))";
            midWord.Parameters.AddWithValue("$t", teamId);
            midWord.Parameters.AddWithValue("$d", dataSet);
            midWord.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            midWord.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(midWord, source);
            using (var r = midWord.ExecuteReader())
                if (r.Read() && !r.IsDBNull(1)) midWordCount = r.GetInt64(1);

            // Keystrokes: the searches a session sent and superseded before each settled one, the
            // median over the settled searches that carried a session.
            using var keys = conn.CreateCommand();
            keys.CommandText = $@"
WITH k AS (SELECT (SELECT COUNT(*) FROM SearchEvents x
                    WHERE x.TeamId = c.TeamId AND x.DataSet = c.DataSet AND x.Session = s.Session
                      AND x.Superseded = 1 AND x.Timestamp <= c.Timestamp) AS n
             FROM {CountedSearches(LiveWhereFor(source))} c
             JOIN SearchEvents s ON s.QueryId = c.QueryId
            WHERE trim(c.QueryText) <> '' AND s.Session IS NOT NULL)
SELECT (SELECT COUNT(*) FROM k),
       (SELECT n FROM k ORDER BY n LIMIT 1 OFFSET (SELECT COUNT(*) FROM k) / 2)";
            keys.Parameters.AddWithValue("$t", teamId);
            keys.Parameters.AddWithValue("$d", dataSet);
            keys.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            keys.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(keys, source);
            using (var r = keys.ExecuteReader())
                if (r.Read()) { withSession = r.GetInt64(0); keystrokes = withSession > 0 && !r.IsDBNull(1) ? r.GetInt64(1) : 0; }

            return new TypingStat(words, filtered, mid, midWordCount, withSession, keystrokes);
        }

        /// <summary>Past this many distinct texts the mid-word comparison, texts by texts, is left
        /// out rather than made slow: it reads 0 then.</summary>
        private const int MidWordMaxTexts = 5000;

        /// <summary>Counted searches in a window past which the mid-word and keystroke rules are
        /// skipped: furniture's quarter, 290k, cost 2.3 s of a 3 s tab load (25 Oct 2026).</summary>
        public const long TypingDetailMaxSearches = 50_000;

        /// <summary>
        /// Counted searches with text in [fromDay, toDay] by the language the page reported, most
        /// first, the unnamed last. From the raw rows, like every read that needs a column the
        /// daily tables do not carry.
        /// </summary>
        public List<LocaleStat> Locales(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT c.Locale, COUNT(*) AS N FROM {CountedSearches(LiveWhereFor(source))} c
 WHERE trim(c.QueryText) <> ''
 GROUP BY c.Locale
 ORDER BY N DESC, c.Locale IS NULL, c.Locale";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(cmd, source);
            var result = new List<LocaleStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new LocaleStat(r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1)));
            return result;
        }
    }
}
