using IndxServer.Services.SearchOrAsk;

namespace IndxServer.Services
{
    public sealed partial class StatisticsStore
    {
        /// <summary>
        /// Questions asked again in other words: the asks the judge said repeat the question before
        /// them in the same visit (<see cref="SearchOrAskRecipes.Rephrased"/>, at or over
        /// <see cref="SearchOrAskRephrasing.Threshold"/>). Counts the asks with a session, the ones
        /// asked again, and the pairs, most seen first, with how often the second answer had a source
        /// opened. From the raw rows, which retention keeps for the console's windows.
        /// </summary>
        public (AskedAgainStat Stat, List<AskRephrasingStat> Pairs) AskedAgain(string teamId, string dataSet, long fromDay, long toDay,
            int limit = 25, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long from = StatisticsDays.Start(tz, fromDay), to = StatisticsDays.Start(tz, toDay + 1);
            string window = "a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND a.Counted = 1" +
                            (source == null ? "" : " AND a.Source = $src");
            using var conn = Open();
            long asks = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM AskEvents a WHERE {window} AND a.Session IS NOT NULL";
                AddWindow(cmd, teamId, dataSet, from, to, source);
                asks = (long)cmd.ExecuteScalar()!;
            }
            var pairs = new Dictionary<(string, string), (string First, string Again, long Times, long Last, long Opened)>();
            long askedAgain = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT f.Question, a.Question, a.Timestamp,
                        EXISTS (SELECT 1 FROM SelectEvents x WHERE x.QueryId = a.AskId AND x.Counted = 1)
                    FROM AskEvents a JOIN AskEvents f ON f.AskId = a.RephraseOf
                    WHERE {window} AND a.RephraseP >= $threshold";
                AddWindow(cmd, teamId, dataSet, from, to, source);
                cmd.Parameters.AddWithValue("$threshold", SearchOrAskRephrasing.Threshold);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    askedAgain++;
                    string first = r.GetString(0), again = r.GetString(1);
                    long at = r.GetInt64(2), landed = r.GetInt64(3);
                    var key = (SearchOrAskRephrasing.Normalise(first), SearchOrAskRephrasing.Normalise(again));
                    pairs[key] = pairs.TryGetValue(key, out var p)
                        ? (p.First, p.Again, p.Times + 1, Math.Max(p.Last, at), p.Opened + landed)
                        : (first, again, 1, at, landed);
                }
            }
            var list = pairs.Values.OrderByDescending(p => p.Times).ThenByDescending(p => p.Last).Take(limit)
                .Select(p => new AskRephrasingStat(p.First, p.Again, p.Times, p.Last, p.Opened)).ToList();
            return (new AskedAgainStat(asks, askedAgain), list);
        }
    }
}
