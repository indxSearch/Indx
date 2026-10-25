namespace IndxServer.Services
{
    /// <summary>The counted searches of a window by local weekday and hour: [weekday, hour], Monday
    /// first. What "when do people search" is read from: nights, weekends, the evening peak.</summary>
    public sealed record WeekHourStat(long[,] Searches)
    {
        public long Total { get { long n = 0; foreach (var v in Searches) n += v; return n; } }
    }

    /// <summary>The counted selects of a window by the position of what was chosen: first, second,
    /// third, fourth to tenth, below tenth. A share chosen from far down says the ranking can do
    /// better.</summary>
    public sealed record PositionStat(long First, long Second, long Third, long FourToTen, long BelowTen)
    {
        public long Total => First + Second + Third + FourToTen + BelowTen;
    }

    public sealed partial class StatisticsStore
    {
        /// <summary>
        /// Counted searches with text in [fromDay, toDay], by the dataset's local weekday and hour.
        /// From the raw rows, so it reaches back as far as they are kept (<see cref="RetentionDays"/>):
        /// the daily tables have no time of day. The rows are counted per quarter of an hour in UTC
        /// in SQL, and each quarter is placed in local time here: exact across a clock change and in
        /// a zone half or three quarters of an hour off UTC, at a few thousand rows for 90 days.
        /// </summary>
        public WeekHourStat SearchesByWeekHour(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            const long Quarter = 15 * 60 * 1000;
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT c.Timestamp / {Quarter} AS Q, COUNT(*) FROM {CountedSearches(LiveWhereFor(source))} c
 WHERE trim(c.QueryText) <> ''
 GROUP BY Q";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(cmd, source);
            var grid = new long[7, 24];
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0) * Quarter).UtcDateTime, tz);
                int weekday = ((int)local.DayOfWeek + 6) % 7; // Monday 0
                grid[weekday, local.Hour] += r.GetInt64(1);
            }
            return new WeekHourStat(grid);
        }

        /// <summary>
        /// Counted searches with text in [fromDay, toDay], by local day and hour: [day - fromDay,
        /// hour]. The window itself hour by hour, where <see cref="SearchesByWeekHour"/> folds it
        /// onto one week. Counted per quarter of an hour in UTC and placed in local time here, as
        /// that one is; on the day a clock goes back the repeated hour adds into one.
        /// </summary>
        public long[,] SearchesByDayHour(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            const long Quarter = 15 * 60 * 1000;
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT c.Timestamp / {Quarter} AS Q, COUNT(*) FROM {CountedSearches(LiveWhereFor(source))} c
 WHERE trim(c.QueryText) <> ''
 GROUP BY Q";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(cmd, source);
            var grid = new long[Math.Max(0, toDay - fromDay + 1), 24];
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long ms = r.GetInt64(0) * Quarter;
                long day = StatisticsDays.DayOf(tz, ms) - fromDay;
                if (day < 0 || day >= grid.GetLength(0)) continue;
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, tz);
                grid[day, local.Hour] += r.GetInt64(1);
            }
            return grid;
        }

        /// <summary>
        /// Counted selects in [fromDay, toDay] by the 1-based position of what was chosen. From the
        /// raw rows, as far back as they are kept; the daily tables hold only the sum of positions.
        /// For one surface, the selects whose search came from it.
        /// </summary>
        public PositionStat SelectPositions(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT SUM(Position = 1), SUM(Position = 2), SUM(Position = 3),
       SUM(Position BETWEEN 4 AND 10), SUM(Position > 10)
  FROM SelectEvents
 WHERE TeamId = $t AND DataSet = $d AND Timestamp >= $liveFrom AND Timestamp < $toEx AND Counted = 1
   AND Position >= 1{EventFromSource("SelectEvents", source)}";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
            cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            AddSource(cmd, source);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return new PositionStat(0, 0, 0, 0, 0);
            return new PositionStat(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
        }
    }
}
