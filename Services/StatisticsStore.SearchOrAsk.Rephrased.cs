using IndxServer.Services.SearchOrAsk;

namespace IndxServer.Services
{
    public sealed partial class StatisticsStore
    {
        /// <summary>
        /// Questions asked again in other words (<see cref="SearchOrAskRephrasing"/>): in each session,
        /// a question followed within five minutes by another about the same thing that is not a
        /// follow-up. Counts the asks with a session, the ones followed by a rewording, and the pairs,
        /// most seen first. From the raw rows, which retention keeps for the console's windows. An ask
        /// with no session cannot be told, so it is left out of both counts.
        /// </summary>
        public (AskedAgainStat Stat, List<AskRephrasingStat> Pairs) AskedAgain(string teamId, string dataSet, long fromDay, long toDay,
            int limit = 25, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long from = StatisticsDays.Start(tz, fromDay), to = StatisticsDays.Start(tz, toDay + 1);
            var rows = new List<(string AskId, string Session, string Question, string Sources, bool FollowUp, long Ts)>();
            var opened = new HashSet<string>(StringComparer.Ordinal);
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT a.AskId, a.Session, a.Question, a.SourceKeys, a.FollowUp, a.Timestamp FROM AskEvents a
                    WHERE a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND a.Counted = 1
                      AND a.Session IS NOT NULL" + (source == null ? "" : " AND a.Source = $src") + " ORDER BY a.Session, a.Timestamp";
                AddWindow(cmd, teamId, dataSet, from, to, source);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) == 1, r.GetInt64(5)));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT DISTINCT x.QueryId FROM SelectEvents x JOIN AskEvents a ON x.QueryId = a.AskId
                    WHERE a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND x.Counted = 1";
                AddWindow(cmd, teamId, dataSet, from, to, null);
                using var r = cmd.ExecuteReader();
                while (r.Read()) opened.Add(r.GetString(0));
            }

            long window = (long)SearchOrAskRephrasing.Window.TotalMilliseconds, askedAgain = 0;
            var pairs = new Dictionary<(string, string), (string First, string Again, long Times, long Last, long Opened)>();
            for (int i = 0; i + 1 < rows.Count; i++)
            {
                var (cur, next) = (rows[i], rows[i + 1]);
                if (next.Session != cur.Session || next.FollowUp || next.Ts - cur.Ts > window) continue;
                if (!SearchOrAskRephrasing.Same(cur.Question, next.Question, cur.Sources, next.Sources)) continue;
                askedAgain++;
                var key = (SearchOrAskRephrasing.Normalise(cur.Question), SearchOrAskRephrasing.Normalise(next.Question));
                var landed = opened.Contains(next.AskId) ? 1 : 0;
                pairs[key] = pairs.TryGetValue(key, out var p)
                    ? (p.First, p.Again, p.Times + 1, Math.Max(p.Last, next.Ts), p.Opened + landed)
                    : (cur.Question, next.Question, 1, next.Ts, landed);
            }
            var list = pairs.Values.OrderByDescending(p => p.Times).ThenByDescending(p => p.Last).Take(limit)
                .Select(p => new AskRephrasingStat(p.First, p.Again, p.Times, p.Last, p.Opened)).ToList();
            return (new AskedAgainStat(rows.Count, askedAgain), list);
        }
    }
}
