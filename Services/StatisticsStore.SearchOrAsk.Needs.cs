using IndxServer.Services.SearchOrAsk;

namespace IndxServer.Services
{
    /// <summary>One way a need was put: the wording as first typed, how often it was asked, how often
    /// the visitor then asked again in other words, and how often its answer landed (a source opened
    /// or a conversion).</summary>
    public readonly record struct AskNeedWording(string Text, long Asked, long Reworded, long Landed);

    /// <summary>A record an answer to the need led to, and how often.</summary>
    public readonly record struct AskNeedRecord(long DocumentKey, long Times);

    /// <summary>
    /// A need: one thing visitors wanted, in all the ways they put it. Visits are the threads that
    /// asked it; each ended answered at the first try, answered after a rewording (it landed on a
    /// later wording), or given up (reworded and never landed, or found nothing). Title is the
    /// wording that landed most, else the one asked most.
    /// </summary>
    public sealed record AskNeedStat(string Title, long Visits, long FirstTry, long AfterRewording, long GaveUp,
        IReadOnlyList<AskNeedWording> Wordings, IReadOnlyList<AskNeedRecord> LandedOn, long LastAsked);

    public sealed partial class StatisticsStore
    {
        /// <summary>
        /// The window's needs (Notes/search-or-ask-server.md, "Needs"). A thread is a visit's
        /// question and the rewordings the judge linked to it (<see cref="SearchOrAskRecipes.Rephrased"/>);
        /// a follow-up builds on its answer and is no wording of it. Wordings that are the same text,
        /// or that a thread linked, are one need, across visits. A need of one wording asked once is
        /// a question, not a need, and is left to the Questions list. Those given up most come first:
        /// they are what to fix. From the raw rows, which retention keeps for the console's windows.
        /// </summary>
        public List<AskNeedStat> AskNeeds(string teamId, string dataSet, long fromDay, long toDay, int limit = 25, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long from = StatisticsDays.Start(tz, fromDay), to = StatisticsDays.Start(tz, toDay + 1);
            string window = "a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND a.Counted = 1" +
                            (source == null ? "" : " AND a.Source = $src");
            var asks = new List<(string AskId, string? Session, string Question, string Outcome, bool FollowUp, string? Of, double? P, long Ts)>();
            var landedOn = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT a.AskId, a.Session, a.Question, a.Outcome, a.FollowUp, a.RephraseOf, a.RephraseP, a.Timestamp
                    FROM AskEvents a WHERE {window} ORDER BY a.Timestamp";
                AddWindow(cmd, teamId, dataSet, from, to, source);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    asks.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) == 1,
                        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.GetInt64(7)));
            }
            // What each answer led to: a cited source opened (select) or a conversion naming the ask.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT x.QueryId, x.DocumentKey FROM SelectEvents x JOIN AskEvents a ON x.QueryId = a.AskId WHERE {window} AND x.Counted = 1
                    UNION ALL
                    SELECT c.QueryId, c.DocumentKey FROM ConvertEvents c JOIN AskEvents a ON c.QueryId = a.AskId WHERE {window} AND c.Counted = 1";
                AddWindow(cmd, teamId, dataSet, from, to, source);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!landedOn.TryGetValue(r.GetString(0), out var keys)) landedOn[r.GetString(0)] = keys = [];
                    keys.Add(r.GetInt64(1));
                }
            }

            // Threads: an ask the judge linked to the one before it joins that one's thread, and so does
            // the same text again soon after in the same visit (a double submit, never judged).
            var threadOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var threads = new List<List<int>>();
            var lastInVisit = new Dictionary<string, int>(StringComparer.Ordinal);
            long window5 = (long)SearchOrAskRephrasing.Window.TotalMilliseconds;
            for (int i = 0; i < asks.Count; i++)
            {
                var a = asks[i];
                if (a.FollowUp) continue;
                int t;
                if (a.Session != null && lastInVisit.TryGetValue(a.Session, out var prev) && a.Ts - asks[prev].Ts <= window5
                    && SearchOrAskRephrasing.Normalise(asks[prev].Question) == SearchOrAskRephrasing.Normalise(a.Question))
                    threads[t = threadOf[asks[prev].AskId]].Add(i);
                else if (a.Of != null && a.P >= SearchOrAskRephrasing.Threshold && threadOf.TryGetValue(a.Of, out t))
                    threads[t].Add(i);
                else
                {
                    t = threads.Count;
                    threads.Add([i]);
                }
                threadOf[a.AskId] = t;
                if (a.Session != null) lastInVisit[a.Session] = i;
            }

            // Needs: the wordings of a thread are one need, and the same text is the same wording.
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            string Find(string x)
            {
                while (parent.TryGetValue(x, out var p) && p != x) x = parent[x] = parent.GetValueOrDefault(p, p);
                return x;
            }
            void Union(string x, string y)
            {
                parent.TryAdd(x, x);
                parent.TryAdd(y, y);
                var (rx, ry) = (Find(x), Find(y));
                if (rx != ry) parent[ry] = rx;
            }
            foreach (var thread in threads)
            {
                var first = SearchOrAskRephrasing.Normalise(asks[thread[0]].Question);
                parent.TryAdd(first, first);
                foreach (var i in thread.Skip(1)) Union(first, SearchOrAskRephrasing.Normalise(asks[i].Question));
            }

            var needs = new Dictionary<string, (List<List<int>> Threads, Dictionary<string, (string Text, long Asked, long Reworded, long Landed)> Wordings, Dictionary<long, long> Records, long Last)>();
            foreach (var thread in threads)
            {
                var root = Find(SearchOrAskRephrasing.Normalise(asks[thread[0]].Question));
                if (!needs.TryGetValue(root, out var need))
                    needs[root] = need = ([], new(StringComparer.Ordinal), [], 0);
                need.Threads.Add(thread);
                for (int k = 0; k < thread.Count; k++)
                {
                    var a = asks[thread[k]];
                    var key = SearchOrAskRephrasing.Normalise(a.Question);
                    var landed = landedOn.TryGetValue(a.AskId, out var keys);
                    var w = need.Wordings.GetValueOrDefault(key, (a.Question, 0, 0, 0));
                    var reworded = k + 1 < thread.Count && SearchOrAskRephrasing.Normalise(asks[thread[k + 1]].Question) != key;
                    need.Wordings[key] = (w.Text, w.Asked + 1, w.Reworded + (reworded ? 1 : 0), w.Landed + (landed ? 1 : 0));
                    if (landed) foreach (var d in keys!) need.Records[d] = need.Records.GetValueOrDefault(d) + 1;
                }
                needs[root] = need with { Last = Math.Max(need.Last, asks[thread[^1]].Ts) };
            }

            var list = new List<AskNeedStat>();
            foreach (var need in needs.Values)
            {
                if (need.Threads.Count < 2 && need.Wordings.Count < 2) continue;
                long firstTry = 0, after = 0, gaveUp = 0;
                foreach (var thread in need.Threads)
                {
                    bool Landed(int i) => landedOn.ContainsKey(asks[i].AskId);
                    var firstWording = SearchOrAskRephrasing.Normalise(asks[thread[0]].Question);
                    var asFirst = thread.Where(i => SearchOrAskRephrasing.Normalise(asks[i].Question) == firstWording).ToList();
                    var later = thread.Except(asFirst).ToList();
                    if (later.Count == 0)
                    {
                        if (asFirst.Any(Landed) || asFirst.Any(i => asks[i].Outcome != "nothingFound")) firstTry++;
                        else gaveUp++;
                    }
                    else if (asFirst.Any(Landed)) firstTry++;
                    else if (later.Any(Landed)) after++;
                    else gaveUp++;
                }
                var wordings = need.Wordings.Values.OrderByDescending(w => w.Asked).ThenByDescending(w => w.Landed)
                    .Select(w => new AskNeedWording(w.Text, w.Asked, w.Reworded, w.Landed)).ToList();
                var title = need.Wordings.Values.Where(w => w.Landed > 0).OrderByDescending(w => w.Landed).ThenByDescending(w => w.Asked)
                    .Select(w => w.Text).FirstOrDefault() ?? wordings[0].Text;
                var records = need.Records.OrderByDescending(r => r.Value).Take(3).Select(r => new AskNeedRecord(r.Key, r.Value)).ToList();
                list.Add(new AskNeedStat(title, need.Threads.Count, firstTry, after, gaveUp, wordings, records, need.Last));
            }
            return list.OrderByDescending(n => n.GaveUp).ThenByDescending(n => n.Visits).ThenByDescending(n => n.LastAsked).Take(limit).ToList();
        }
    }
}
