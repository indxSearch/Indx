using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>How people type: the counted searches of a window by how many words they are, how
    /// many came with a filter, and the median length in characters. Says how the box is used: one
    /// word and a filter is a catalogue, three words is a question.</summary>
    public sealed record TypingStat(long OneWord, long TwoWords, long ThreeOrMore, long WithFilter, long MedianLength)
    {
        public long Total => OneWord + TwoWords + ThreeOrMore;
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
            // Words: one more than the single spaces left once runs are collapsed (three passes
            // fold up to eight spaces, which is more than a query has).
            const string Words = "(length(trim(c.QueryText)) - length(replace(replace(replace(replace(trim(c.QueryText), '    ', ' '), '   ', ' '), '  ', ' '), ' ', '')) + 1)";
            cmd.CommandText = $@"
SELECT SUM({Words} = 1), SUM({Words} = 2), SUM({Words} >= 3), SUM(c.FilterKey IS NOT NULL AND c.FilterKey <> '')
  FROM {CountedSearches(LiveWhereFor(source))} c
 WHERE trim(c.QueryText) <> ''";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(cmd, source);
            long one = 0, two = 0, more = 0, filtered = 0;
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read() && !r.IsDBNull(0))
                    (one, two, more, filtered) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
            }
            long total = one + two + more;
            if (total == 0) return new TypingStat(0, 0, 0, 0, 0);

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
            return new TypingStat(one, two, more, filtered, mid);
        }

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
