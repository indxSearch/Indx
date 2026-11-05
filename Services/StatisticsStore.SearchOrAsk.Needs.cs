using IndxServer.Services.SearchOrAsk;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// One way a need was put: the wording as first typed, how often it was asked, how often the
    /// visitor then asked again in other words, and how often its answer landed (a source opened or a
    /// conversion). Why it belongs to the need: <paramref name="Link"/> is how it joined
    /// <paramref name="LinkedTo"/> (another wording of the need, as first typed), with the judge's
    /// probability; all three are null for the need's title, which the others are traced back to.
    /// </summary>
    public readonly record struct AskNeedWording(string Text, long Asked, long Reworded, long Landed,
        string? LinkedTo = null, string? Link = null, double? P = null);

    /// <summary>How a wording joined its need.</summary>
    public static class AskNeedLink
    {
        /// <summary>Asked again in other words in the same visit, as the judge's <see cref="SearchOrAskRecipes.Rephrased"/> said.</summary>
        public const string AskedAgain = "askedAgain";

        /// <summary>Asked in another visit, the same thing as the judge's <see cref="SearchOrAskRecipes.SameNeed"/> said.</summary>
        public const string SameNeed = "sameNeed";
    }

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
        private readonly record struct NeedAsk(string AskId, string? Session, string Question, string Outcome, bool FollowUp,
            string? Of, double? P, long Ts)
        {
            public string Key { get; } = SearchOrAskRephrasing.Normalise(Question);
        }

        /// <summary>Which wordings are one need: a union of normalised texts, keeping every link that
        /// joined two, so a need can say why each of its wordings is in it.</summary>
        private sealed class NeedGrouping
        {
            private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);
            public List<(string A, string B, string Link, double? P)> Edges { get; } = [];

            public void Add(string x) => _parent.TryAdd(x, x);

            public string Find(string x)
            {
                while (_parent.TryGetValue(x, out var p) && p != x) x = _parent[x] = _parent.GetValueOrDefault(p, p);
                return x;
            }

            public void Union(string x, string y, string link, double? p)
            {
                Add(x);
                Add(y);
                if (x == y) return;
                Edges.Add((x, y, link, p));
                var (rx, ry) = (Find(x), Find(y));
                if (rx != ry) _parent[ry] = rx;
            }
        }

        private static string NeedWindow(string? source) =>
            "a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND a.Counted = 1" +
            (source == null ? "" : " AND a.Source = $src");

        /// <summary>
        /// The window's asks, their threads and which wordings are one need. A thread is a visit's
        /// question and the rewordings the judge linked to it (<see cref="SearchOrAskRecipes.Rephrased"/>)
        /// plus a double submit (the same text again soon after in the same visit, never judged); a
        /// follow-up builds on its answer and is no wording of it. Wordings that are the same text,
        /// that a thread linked, or that the judge said ask for the same thing
        /// (<see cref="SearchOrAskRecipes.SameNeed"/>, any two visits) are one need, less the pairs an
        /// editor said are not (<see cref="DenySameNeed"/>): a denied rewording starts a thread of its own.
        /// </summary>
        private (List<NeedAsk> Asks, List<List<int>> Threads, NeedGrouping Groups) NeedThreads(SqliteConnection conn,
            string teamId, string dataSet, long from, long to, string? source)
        {
            var asks = new List<NeedAsk>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT a.AskId, a.Session, a.Question, a.Outcome, a.FollowUp, a.RephraseOf, a.RephraseP, a.Timestamp
                    FROM AskEvents a WHERE {NeedWindow(source)} ORDER BY a.Timestamp";
                AddWindow(cmd, teamId, dataSet, from, to, source);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    asks.Add(new NeedAsk(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) == 1,
                        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.GetInt64(7)));
            }
            var denied = DeniedSameNeeds(teamId, dataSet);
            var groups = new NeedGrouping();
            var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var threadOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var threads = new List<List<int>>();
            var lastInVisit = new Dictionary<string, int>(StringComparer.Ordinal);
            long window5 = (long)SearchOrAskRephrasing.Window.TotalMilliseconds;
            for (int i = 0; i < asks.Count; i++)
            {
                var a = asks[i];
                indexOf[a.AskId] = i;
                if (a.FollowUp) continue;
                int t;
                if (a.Session != null && lastInVisit.TryGetValue(a.Session, out var prev) && a.Ts - asks[prev].Ts <= window5
                    && asks[prev].Key == a.Key)
                    threads[t = threadOf[asks[prev].AskId]].Add(i);
                else if (a.Of != null && a.P >= SearchOrAskRephrasing.Threshold && threadOf.TryGetValue(a.Of, out t)
                         && indexOf.TryGetValue(a.Of, out var of) && !IsDenied(denied, asks[of].Key, a.Key))
                {
                    threads[t].Add(i);
                    groups.Union(asks[of].Key, a.Key, AskNeedLink.AskedAgain, a.P);
                }
                else
                {
                    t = threads.Count;
                    threads.Add([i]);
                }
                groups.Add(a.Key);
                threadOf[a.AskId] = t;
                if (a.Session != null) lastInVisit[a.Session] = i;
            }
            foreach (var (x, y, p) in SameNeedLinks(teamId, dataSet)) groups.Union(x, y, AskNeedLink.SameNeed, p);
            return (asks, threads, groups);
        }

        /// <summary>
        /// The window's needs (Notes/search-or-ask-server.md, "Needs"), grouped as <see cref="NeedThreads"/>
        /// says. A need of one wording asked once is a question, not a need, and is left to the
        /// Questions list. Those given up most come first: they are what to fix. From the raw rows,
        /// which retention keeps for the console's windows.
        /// </summary>
        public List<AskNeedStat> AskNeeds(string teamId, string dataSet, long fromDay, long toDay, int limit = 25, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long from = StatisticsDays.Start(tz, fromDay), to = StatisticsDays.Start(tz, toDay + 1);
            var landedOn = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            using var conn = Open();
            var (asks, threads, groups) = NeedThreads(conn, teamId, dataSet, from, to, source);
            // What each answer led to: a cited source opened (select) or a conversion naming the ask.
            using (var cmd = conn.CreateCommand())
            {
                var window = NeedWindow(source);
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

            var needs = new Dictionary<string, (List<List<int>> Threads, Dictionary<string, (string Text, long Asked, long Reworded, long Landed)> Wordings, Dictionary<long, long> Records, long Last)>();
            foreach (var thread in threads)
            {
                var root = groups.Find(asks[thread[0]].Key);
                if (!needs.TryGetValue(root, out var need))
                    needs[root] = need = ([], new(StringComparer.Ordinal), [], 0);
                need.Threads.Add(thread);
                for (int k = 0; k < thread.Count; k++)
                {
                    var a = asks[thread[k]];
                    var landed = landedOn.TryGetValue(a.AskId, out var keys);
                    var w = need.Wordings.GetValueOrDefault(a.Key, (a.Question, 0, 0, 0));
                    var reworded = k + 1 < thread.Count && asks[thread[k + 1]].Key != a.Key;
                    need.Wordings[a.Key] = (w.Text, w.Asked + 1, w.Reworded + (reworded ? 1 : 0), w.Landed + (landed ? 1 : 0));
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
                    var firstWording = asks[thread[0]].Key;
                    var asFirst = thread.Where(i => asks[i].Key == firstWording).ToList();
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
                var titleKey = need.Wordings.Where(w => w.Value.Landed > 0).OrderByDescending(w => w.Value.Landed).ThenByDescending(w => w.Value.Asked)
                    .Select(w => w.Key).FirstOrDefault() ?? need.Wordings.OrderByDescending(w => w.Value.Asked).ThenByDescending(w => w.Value.Landed).First().Key;
                var why = Explain(titleKey, need.Wordings.Keys, groups.Edges);
                var wordings = need.Wordings.OrderByDescending(w => w.Key == titleKey).ThenByDescending(w => w.Value.Asked).ThenByDescending(w => w.Value.Landed)
                    .Select(w => why.TryGetValue(w.Key, out var e)
                        ? new AskNeedWording(w.Value.Text, w.Value.Asked, w.Value.Reworded, w.Value.Landed, need.Wordings[e.To].Text, e.Link, e.P)
                        : new AskNeedWording(w.Value.Text, w.Value.Asked, w.Value.Reworded, w.Value.Landed))
                    .ToList();
                var records = need.Records.OrderByDescending(r => r.Value).Take(3).Select(r => new AskNeedRecord(r.Key, r.Value)).ToList();
                list.Add(new AskNeedStat(need.Wordings[titleKey].Text, need.Threads.Count, firstTry, after, gaveUp, wordings, records, need.Last));
            }
            return list.OrderByDescending(n => n.GaveUp).ThenByDescending(n => n.Visits).ThenByDescending(n => n.LastAsked).Take(limit).ToList();
        }

        /// <summary>
        /// The window's visits that asked something (a thread: a visit's question with its rewordings
        /// and double submits), and how many asked a need some earlier visit had already asked, in any
        /// wording, grouped as the needs are. "Earlier" reaches back over all the raw rows retention
        /// keeps, not just the window, so a one-day view does not call every need new.
        /// </summary>
        public (long Visits, long Repeated) AskRepeats(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long from = StatisticsDays.Start(tz, fromDay), to = StatisticsDays.Start(tz, toDay + 1);
            using var conn = Open();
            var (asks, threads, groups) = NeedThreads(conn, teamId, dataSet, 0, to, source);
            var asked = new HashSet<string>(StringComparer.Ordinal);
            long visits = 0, repeated = 0;
            // Threads are in the order their first ask came.
            foreach (var thread in threads)
            {
                var first = asks[thread[0]];
                var before = !asked.Add(groups.Find(first.Key));
                if (first.Ts < from) continue;
                visits++;
                if (before) repeated++;
            }
            return (visits, repeated);
        }

        /// <summary>
        /// Why each wording is in its need: the link that reaches it from the title, walking out from
        /// the title over the links between the need's own wordings, the strongest first. A wording
        /// joined only through a text outside the window is left unexplained rather than given a link
        /// it does not have.
        /// </summary>
        private static Dictionary<string, (string To, string Link, double? P)> Explain(string titleKey, IEnumerable<string> keys,
            List<(string A, string B, string Link, double? P)> edges)
        {
            var inNeed = new HashSet<string>(keys, StringComparer.Ordinal);
            var near = new Dictionary<string, List<(string Other, string Link, double? P)>>(StringComparer.Ordinal);
            foreach (var (a, b, link, p) in edges)
            {
                if (!inNeed.Contains(a) || !inNeed.Contains(b)) continue;
                (near.TryGetValue(a, out var la) ? la : near[a] = []).Add((b, link, p));
                (near.TryGetValue(b, out var lb) ? lb : near[b] = []).Add((a, link, p));
            }
            var why = new Dictionary<string, (string, string, double?)>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal) { titleKey };
            var queue = new Queue<string>([titleKey]);
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                if (!near.TryGetValue(at, out var links)) continue;
                foreach (var (other, link, p) in links.OrderByDescending(l => l.P ?? 0))
                    if (seen.Add(other))
                    {
                        why[other] = (at, link, p);
                        queue.Enqueue(other);
                    }
            }
            return why;
        }

        /// <summary>
        /// The questions asked in the window, a need's wordings as one question (Nov 2026): grouped as
        /// <see cref="NeedThreads"/> groups them, named by the wording asked most, the others listed
        /// with it. The ones the content did not answer first, then the most asked. From the raw rows:
        /// a question list is within retention.
        /// </summary>
        public List<AskQuestionStat> TopQuestions(string teamId, string dataSet, long fromDay, long toDay,
            int limit = 50, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long from = StatisticsDays.Start(tz, fromDay), to = StatisticsDays.Start(tz, toDay + 1);
            using var conn = Open();
            var groups = NeedThreads(conn, teamId, dataSet, from, to, source).Groups;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT lower(trim(a.Question)) AS Q, COUNT(*),
       SUM(CASE WHEN a.Outcome = 'answered' THEN 1 ELSE 0 END),
       SUM(CASE WHEN a.Outcome = 'nothingFound' THEN 1 ELSE 0 END),
       SUM(CASE WHEN a.Outcome = 'nothingFound' OR a.Answerable < $floor THEN 1 ELSE 0 END) AS Unanswered,
       SUM(a.Answerable), COUNT(a.Answerable),
       SUM(f.Found), COUNT(f.Found),
       (SELECT COUNT(*) FROM SelectEvents x JOIN AskEvents b ON x.QueryId = b.AskId
         WHERE b.TeamId = $t AND b.DataSet = $d AND lower(trim(b.Question)) = lower(trim(a.Question)) AND x.Counted = 1
           AND b.Timestamp >= $from AND b.Timestamp < $to),
       MAX(a.Timestamp)
  FROM AskEvents a
  -- The judge's found on the search the question was asked from (5 Nov 2026).
  LEFT JOIN (SELECT QueryId, MIN(Found) AS Found FROM AskIntentEvents
              WHERE TeamId = $t AND DataSet = $d AND Found IS NOT NULL AND QueryId IS NOT NULL GROUP BY QueryId) f
         ON f.QueryId = a.QueryId
 WHERE a.TeamId = $t AND a.DataSet = $d AND a.Timestamp >= $from AND a.Timestamp < $to AND a.Counted = 1" +
                (source == null ? "" : " AND a.Source = $src") + @"
 GROUP BY Q";
            AddWindow(cmd, teamId, dataSet, from, to, source);
            var byNeed = new Dictionary<string, List<(string Q, long Asks, long Answered, long Nothing, long Unanswered, double AnswerableSum, long Judged, double FoundSum, long FoundJudged, long Opened, long Last)>>(StringComparer.Ordinal);
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var q = r.GetString(0);
                    var root = groups.Find(SearchOrAskRephrasing.Normalise(q));
                    if (!byNeed.TryGetValue(root, out var rows)) byNeed[root] = rows = [];
                    rows.Add((q, r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.IsDBNull(5) ? 0 : r.GetDouble(5), r.GetInt64(6),
                        r.IsDBNull(7) ? 0 : r.GetDouble(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10)));
                }
            return byNeed.Values.Select(rows =>
                {
                    var ordered = rows.OrderByDescending(x => x.Asks).ThenByDescending(x => x.Last).ToList();
                    long judged = rows.Sum(x => x.Judged), foundJudged = rows.Sum(x => x.FoundJudged);
                    return new AskQuestionStat(ordered[0].Q, rows.Sum(x => x.Asks), rows.Sum(x => x.Answered), rows.Sum(x => x.Nothing),
                        rows.Sum(x => x.Unanswered), judged > 0 ? rows.Sum(x => x.AnswerableSum) / judged : null,
                        rows.Sum(x => x.Opened), rows.Max(x => x.Last), ordered.Skip(1).Select(x => x.Q).ToList(),
                        foundJudged > 0 ? rows.Sum(x => x.FoundSum) / foundJudged : null);
                })
                .OrderByDescending(q => q.Unanswered).ThenByDescending(q => q.Asks).ThenByDescending(q => q.LastAsked)
                .Take(limit).ToList();
        }
    }
}
