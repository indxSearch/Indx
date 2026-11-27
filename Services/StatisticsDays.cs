namespace IndxServer.Services
{
    /// <summary>
    /// Days in a dataset's statistics time zone. A day number counts local calendar days since
    /// 1970-01-01, as <see cref="StatisticsStore.DayOf(long)"/> counts UTC ones, so with UTC the
    /// two agree and nothing stored before time zones existed changes meaning. A local day runs
    /// from one local midnight to the next: 23 or 25 hours across a clock change.
    /// </summary>
    public static class StatisticsDays
    {
        private static readonly DateOnly Epoch = new(1970, 1, 1);

        /// <summary>The time zone for an IANA name ("Europe/Oslo") or "UTC"; null when this host
        /// does not know it.</summary>
        public static TimeZoneInfo? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (name.Trim().Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
            return TimeZoneInfo.TryFindSystemTimeZoneById(name.Trim(), out var tz) ? tz : null;
        }

        /// <summary>The IANA name to show and store for a zone ("UTC" for UTC).</summary>
        public static string NameOf(TimeZoneInfo tz)
        {
            if (tz == TimeZoneInfo.Utc || tz.Id is "UTC" or "Etc/UTC") return "UTC";
            if (tz.HasIanaId) return tz.Id;
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : tz.Id;
        }

        /// <summary>The local day an instant falls on.</summary>
        public static long DayOf(TimeZoneInfo tz, long unixMs)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime, tz);
            return DateOnly.FromDateTime(local).DayNumber - Epoch.DayNumber;
        }

        /// <summary>The instant a local day begins, in Unix ms. Where midnight does not exist
        /// (a zone that moves its clocks at midnight), the day begins at the first local time
        /// that does.</summary>
        public static long Start(TimeZoneInfo tz, long day)
        {
            if (tz == TimeZoneInfo.Utc) return day * 86_400_000L;
            var midnight = Epoch.AddDays((int)day).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            while (tz.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(15);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, tz)).ToUnixTimeMilliseconds();
        }

        /// <summary>Today in the zone.</summary>
        public static long Today(TimeZoneInfo tz) => DayOf(tz, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
