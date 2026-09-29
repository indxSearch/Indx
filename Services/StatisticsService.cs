using IndxServer.Engine;

namespace IndxServer.Services
{
    /// <summary>
    /// Owns the statistics feature's lifecycle: resolves the stats.db path (its own file next to
    /// the search database, overridable via <c>Statistics:DbFile</c>), ensures the schema, runs
    /// the <see cref="StatisticsWriter"/>, and rolls up and prunes on a timer. Registered as a
    /// singleton and as the hosted service over that same instance, so controllers and the search
    /// path talk to the running writer.
    ///
    /// Configuration (appsettings "Statistics"): <c>Enabled</c> (default true — the measurement
    /// in Notes/statistics-design.md is why logging every search is affordable),
    /// <c>RetentionDays</c> (default 90; raw event rows only — rollups are kept), <c>DbFile</c>.
    /// </summary>
    public sealed class StatisticsService(IConfiguration configuration, ILoggerFactory loggerFactory)
        : IHostedService, IDisposable
    {
        /// <summary>Roll up day D no earlier than D+2, so its selects and converts have arrived.</summary>
        private const int RollupLagDays = 2;

        private CancellationTokenSource? _cts;
        private Task? _rollupLoop;

        public bool Enabled { get; private set; }
        public StatisticsStore? Store { get; private set; }
        public StatisticsWriter? Writer { get; private set; }
        public int RetentionDays { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Enabled = configuration.GetValue("Statistics:Enabled", true);
            if (!Enabled)
            {
                loggerFactory.CreateLogger<StatisticsService>()
                    .LogInformation("statistics are disabled (Statistics:Enabled=false)");
                return Task.CompletedTask;
            }
            RetentionDays = configuration.GetValue("Statistics:RetentionDays", 90);

            // Default location: the search database's directory. StartUpSystem has run by now
            // (InitializeIndxSearchSystem precedes app.Run, hosted services start inside it).
            var dbFile = configuration["Statistics:DbFile"];
            if (string.IsNullOrEmpty(dbFile))
            {
                var searchDb = IndxServerInternalApi.SearchDbConnectionString;
                var dir = Path.GetDirectoryName(searchDb);
                dbFile = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "stats.db");
            }

            Store = new StatisticsStore(dbFile, loggerFactory.CreateLogger<StatisticsStore>());
            Store.EnsureSchema();
            Store.LogState();
            Writer = new StatisticsWriter(Store, loggerFactory.CreateLogger<StatisticsWriter>());
            Writer.Start();

            _cts = new CancellationTokenSource();
            _rollupLoop = Task.Run(() => RollupLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Idempotent, and safe after Dispose: the test host stops and disposes the factory in
            // an order that can run this twice around Dispose.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            Writer?.Stop();
            return Task.CompletedTask;
        }

        /// <summary>Runs a rollup pass at startup and then every six hours. Idempotent per day,
        /// so the cadence is a ceiling on staleness, not a correctness knob.</summary>
        private async Task RollupLoopAsync(CancellationToken ct)
        {
            var logger = loggerFactory.CreateLogger<StatisticsService>();
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            while (true)
            {
                try
                {
                    RollupAndPrune(logger);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "statistics rollup pass failed");
                }
                try { if (!await timer.WaitForNextTickAsync(ct)) return; }
                catch (OperationCanceledException) { return; }
            }
        }

        private void RollupAndPrune(ILogger logger)
        {
            var store = Store!;
            long today = StatisticsStore.DayOf(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            long target = today - RollupLagDays;
            long last = store.LastRolledDay();
            if (last < 0)
            {
                // No watermark (fresh store, or one from before the watermark existed): start at
                // the oldest raw event, so nothing is ever pruned without having been rolled.
                var oldest = store.OldestEventDay();
                last = (oldest < 0 ? target : Math.Min(oldest, target)) - 1;
            }
            for (long day = last + 1; day <= target; day++)
            {
                store.RollupDay(day);
                logger.LogInformation("statistics rollup covered day {Day}", day);
            }
            long pruneDay = Math.Min(today - RetentionDays, store.LastRolledDay());
            if (pruneDay >= 0)
            {
                int pruned = store.PruneRawBefore(pruneDay * 86_400_000L);
                if (pruned > 0)
                    logger.LogInformation("statistics pruned {Rows} raw rows older than day {Day}", pruned, pruneDay);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts?.Dispose();
            _cts = null;
            Writer?.Dispose();
        }
    }
}
