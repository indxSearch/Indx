namespace IndxServer.Services
{
    /// <summary>One local day of Search or ask: the searches an offer was shown beside, the
    /// questions asked, and what the day cost where the provider reports it.</summary>
    public readonly record struct AskDayStat(long Day, long Offers, long Asks, double Cost);

    /// <summary>One UTC day of the call budget: the calls taken, the ones turned away, and the
    /// budget that day had.</summary>
    public readonly record struct AskBudgetDayStat(long Day, long Calls, long Refused, int Budget);

    public sealed partial class StatisticsStore
    {
        /// <summary>
        /// Search or ask day by day in [fromDay, toDay]: offers and asks, and the cost. Rolled days
        /// come from the daily table, the rest from the raw rows placed in the dataset's local day,
        /// as the overview sums them; for one surface the whole window is raw. Days with nothing are
        /// left out.
        /// </summary>
        public List<AskDayStat> AsksByDay(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            long rolledTo = RolledTo(fromDay, toDay, source);
            long liveFrom = Math.Max(fromDay, rolledTo + 1);
            var offers = new Dictionary<long, HashSet<string>>();
            var asks = new Dictionary<long, long>();
            var cost = new Dictionary<long, double>();
            using var conn = Open();
            if (rolledTo >= fromDay)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT Day, Offers, Asks, Cost FROM DailyAskStats
                    WHERE TeamId = $t AND DataSet = $d AND Day >= $fromDay AND Day <= $rolledTo";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$fromDay", fromDay);
                cmd.Parameters.AddWithValue("$rolledTo", rolledTo);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long day = r.GetInt64(0);
                    offers[day] = new HashSet<string>(Enumerable.Range(0, (int)r.GetInt64(1)).Select(i => "rolled" + i));
                    asks[day] = r.GetInt64(2);
                    cost[day] = r.GetDouble(3);
                }
            }
            if (liveFrom <= toDay)
            {
                long from = StatisticsDays.Start(tz, liveFrom), to = StatisticsDays.Start(tz, toDay + 1);
                string src = source == null ? "" : " AND Source = $src";
                using (var cmd = conn.CreateCommand())
                {
                    // An offer is a search shown with one: intents without a search id each count.
                    cmd.CommandText = "SELECT Timestamp, COALESCE(QueryId, 'row' || rowid), COALESCE(Cost, 0) FROM AskIntentEvents" +
                        " WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $to AND Counted = 1 AND Offer <> 'none'" + src;
                    AddWindow(cmd, teamId, dataSet, from, to, source);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        long day = StatisticsDays.DayOf(tz, r.GetInt64(0));
                        (offers.TryGetValue(day, out var set) ? set : offers[day] = []).Add(r.GetString(1));
                        cost[day] = cost.GetValueOrDefault(day) + r.GetDouble(2);
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT Timestamp, COALESCE(Cost, 0) FROM AskEvents" +
                        " WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $to AND Counted = 1" + src;
                    AddWindow(cmd, teamId, dataSet, from, to, source);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        long day = StatisticsDays.DayOf(tz, r.GetInt64(0));
                        asks[day] = asks.GetValueOrDefault(day) + 1;
                        cost[day] = cost.GetValueOrDefault(day) + r.GetDouble(1);
                    }
                }
            }
            return offers.Keys.Union(asks.Keys).Union(cost.Keys).OrderBy(d => d)
                .Select(d => new AskDayStat(d, offers.TryGetValue(d, out var o) ? o.Count : 0, asks.GetValueOrDefault(d), cost.GetValueOrDefault(d)))
                .ToList();
        }

        /// <summary>The budget day by day in [fromDay, toDay]: calls, refusals and the day's budget.
        /// The budget's days are UTC, as <see cref="AskBudget"/> reads them.</summary>
        /// <summary>
        /// The calls against the budget by hour of the day, and the hour the budget ran out on each
        /// day it did: for the clock that says when a day's budget tends to go. Calls are the
        /// judgements that reached the model (the cached ones cost nothing) and the questions, from
        /// the raw rows; the filters made while typing have no row and are left out. In UTC, as the
        /// budget's day is. Spent[h] is the days whose budget ran out in hour h.
        /// </summary>
        public (long[] Calls, long[] Spent) BudgetCallsByHour(string teamId, string dataSet, long fromDay, long toDay)
        {
            const long Hour = 60 * 60 * 1000;
            var calls = new long[24];
            var spent = new long[24];
            long fromMs = fromDay * 86_400_000L, toMsExcl = (toDay + 1) * 86_400_000L;
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT (Timestamp / {Hour}) % 24 AS H, COUNT(*) FROM AskIntentEvents
 WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $toEx AND Cached = 0 AND Counted = 1
 GROUP BY H
UNION ALL
SELECT (Timestamp / {Hour}) % 24 AS H, COUNT(*) FROM AskEvents
 WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $toEx AND Counted = 1
 GROUP BY H";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$from", fromMs);
                cmd.Parameters.AddWithValue("$toEx", toMsExcl);
                using var r = cmd.ExecuteReader();
                while (r.Read()) calls[(int)r.GetInt64(0)] += r.GetInt64(1);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT SpentAt FROM AskBudgetDays WHERE TeamId = $t AND DataSet = $d AND Day BETWEEN $fromDay AND $toDay AND SpentAt IS NOT NULL";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$fromDay", fromDay);
                cmd.Parameters.AddWithValue("$toDay", toDay);
                using var r = cmd.ExecuteReader();
                while (r.Read()) spent[(int)((r.GetInt64(0) / Hour) % 24)]++;
            }
            return (calls, spent);
        }

        public List<AskBudgetDayStat> BudgetByDay(string teamId, string dataSet, long fromDay, long toDay)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Day, Calls, Refused, Budget FROM AskBudgetDays WHERE TeamId = $t AND DataSet = $d AND Day BETWEEN $from AND $to ORDER BY Day";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$from", fromDay);
            cmd.Parameters.AddWithValue("$to", toDay);
            var result = new List<AskBudgetDayStat>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new AskBudgetDayStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3)));
            return result;
        }

        /// <summary>Counted asks in [fromDay, toDay] by the dataset's local weekday and hour, as
        /// <see cref="SearchesByWeekHour"/> places searches: when people ask.</summary>
        public WeekHourStat AsksByWeekHour(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            const long Quarter = 15 * 60 * 1000;
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Timestamp / {Quarter} AS Q, COUNT(*) FROM AskEvents" +
                " WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $from AND Timestamp < $to AND Counted = 1" +
                (source == null ? "" : " AND Source = $src") + " GROUP BY Q";
            AddWindow(cmd, teamId, dataSet, StatisticsDays.Start(tz, fromDay), StatisticsDays.Start(tz, toDay + 1), source);
            var grid = new long[7, 24];
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0) * Quarter).UtcDateTime, tz);
                grid[((int)local.DayOfWeek + 6) % 7, local.Hour] += r.GetInt64(1);
            }
            return new WeekHourStat(grid);
        }
    }
}
