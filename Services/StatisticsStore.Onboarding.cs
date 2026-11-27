namespace IndxServer.Services
{
    public sealed partial class StatisticsStore
    {
        /// <summary>Whether any of the team's datasets has had a search from outside the console:
        /// one through the HTTP API, which is what a site or an app makes (the console's preview
        /// searches the engine directly and records nothing). Get started's Connect your site.</summary>
        public bool TeamHasSearches(string teamId)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT EXISTS (SELECT 1 FROM SearchEvents WHERE TeamId = $t)
                OR EXISTS (SELECT 1 FROM DailyQueryStats WHERE TeamId = $t)";
            cmd.Parameters.AddWithValue("$t", teamId);
            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }
    }
}
