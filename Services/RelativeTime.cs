namespace IndxServer.Services
{
    /// <summary>
    /// A point in time as a person would say it: "5 minutes ago", "2 weeks ago", "in 3 days".
    /// The console shows this everywhere a timestamp used to be printed raw, with the exact time
    /// one hover away (the <c>TimeAgo</c> component). Two pages had each grown an abbreviated
    /// helper of their own ("5m ago"); this replaces both.
    ///
    /// Units step up as soon as the larger one reads naturally (14 days is "2 weeks"), and the
    /// number is always rounded down, so "2 weeks ago" never describes something 13 days old.
    /// Months and years are calendar-blind (30 and 365 days): at that distance the exact time in
    /// the tooltip is what anyone checking a date will read.
    /// </summary>
    public static class RelativeTime
    {
        /// <summary>The format of the exact time shown on hover. UTC and labelled: a server-rendered
        /// page does not know the browser's time zone, and the server's own zone is nobody's.</summary>
        public const string ExactFormat = "yyyy-MM-dd HH:mm 'UTC'";

        public static string Format(DateTimeOffset when, DateTimeOffset now)
        {
            var delta = now - when;
            bool past = delta >= TimeSpan.Zero;
            var span = delta.Duration();

            if (span.TotalSeconds < 45) return past ? "just now" : "in a moment";

            string amount;
            if (span.TotalMinutes < 60) amount = Unit(Math.Max(1, (int)span.TotalMinutes), "minute");
            else if (span.TotalHours < 24) amount = Unit((int)span.TotalHours, "hour");
            else if (span.TotalDays < 2) return past ? "yesterday" : "tomorrow";
            else if (span.TotalDays < 14) amount = Unit((int)span.TotalDays, "day");
            else if (span.TotalDays < 60) amount = Unit((int)(span.TotalDays / 7), "week");
            else if (span.TotalDays < 365) amount = Unit((int)(span.TotalDays / 30), "month");
            else amount = Unit((int)(span.TotalDays / 365), "year");

            return past ? $"{amount} ago" : $"in {amount}";
        }

        public static string Exact(DateTimeOffset when) => when.UtcDateTime.ToString(ExactFormat);

        /// <summary>A <see cref="DateTime"/> as an instant. Local and Utc kinds mean what they say;
        /// Unspecified is what the database hands back for the UTC values it stores, so it is
        /// read as UTC.</summary>
        public static DateTimeOffset ToInstant(DateTime value) => value.Kind switch
        {
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        };

        private static string Unit(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
    }
}
