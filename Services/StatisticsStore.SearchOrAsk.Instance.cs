namespace IndxServer.Services
{
    /// <summary>What Search or ask's model calls cost and how many there were, across every dataset of
    /// the instance, from a UTC day on: the provider's own figure where it reports one (OpenRouter;
    /// Anthropic's is estimated), nothing where it does not.</summary>
    public readonly record struct AskSpendStat(long Calls, long Refused, double Cost);

    public sealed partial class StatisticsStore
    {
        /// <summary>
        /// Search or ask across the instance, for Admin's view of the provider (Nov 2026): the calls
        /// against the datasets' budgets and what the judge, the answers and the filters cost, from
        /// <paramref name="fromDay"/> (a UTC day, the budget's) to now. Costs from the raw rows, so a
        /// window is within retention.
        /// </summary>
        public AskSpendStat InstanceAskSpend(long fromDay)
        {
            long fromMs = fromDay * 86_400_000L;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT
                (SELECT COALESCE(SUM(Calls), 0) FROM AskBudgetDays WHERE Day >= $day),
                (SELECT COALESCE(SUM(Refused), 0) FROM AskBudgetDays WHERE Day >= $day),
                (SELECT COALESCE(SUM(Cost), 0) FROM AskBudgetDays WHERE Day >= $day)
                + (SELECT COALESCE(SUM(Cost), 0) FROM AskIntentEvents WHERE Timestamp >= $ms)
                + (SELECT COALESCE(SUM(Cost), 0) FROM AskEvents WHERE Timestamp >= $ms)";
            cmd.Parameters.AddWithValue("$day", fromDay);
            cmd.Parameters.AddWithValue("$ms", fromMs);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new AskSpendStat(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2)) : default;
        }
    }
}
