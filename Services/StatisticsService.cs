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
    public sealed class StatisticsService(IConfiguration configuration, ILoggerFactory loggerFactory,
        BoostRuleStore? boostRules = null)
        : IHostedService, IDisposable, IDatasetChangeSink
    {
        /// <summary>Roll up day D no earlier than D+2, so its selects and converts have arrived.</summary>
        private const int RollupLagDays = 2;

        private CancellationTokenSource? _cts;
        private Task? _rollupLoop;

        public bool Enabled { get; private set; }

        /// <summary>Whether a search is worth recording: it has text, or a filter. An empty search
        /// without one is a page load - a browse page opening, a storefront listing its catalogue -
        /// and counting those would bury the searches people typed. An empty search with a filter
        /// is browsing: it is recorded with its filter key, and the reads count it under Browsing,
        /// never as a query (Notes/statistics-design.md, "What counts as a search"). Both recording
        /// paths (the HTTP endpoint and the console preview) ask this, so they agree.
        ///
        /// <para>And only a search that ran. A refused one (<c>Reason</c> set: an unknown or
        /// negative fieldBoosts key, text over the length ceiling, a disposed engine) or one that
        /// could not be served in time (<c>DidTimeOut</c>) also comes back empty, and recorded it
        /// would read as the shop having nothing: an integration bug or a busy moment showing up
        /// as the zero-hit rate. Errors are not search data; the caller has the reason in the
        /// response.</para>
        ///
        /// <para>And only a search that asked for records. A request for none is a front-end's
        /// helper - intrface fetches the facet counts of an OR field that way - and it showed no
        /// one anything, so it is not a search a visitor made.</para></summary>
        public static bool ShouldRecord(Indx.Http.QueryProxy query, string? filterKey, Indx.Api.Result result) =>
            result.Reason == null && !result.DidTimeOut && query.MaxNumberOfRecordsToReturn > 0
            && (!string.IsNullOrWhiteSpace(query.Text) || filterKey != null);

        /// <summary>A caller's <c>?source=</c>, as stored: trimmed, at most 40 characters, null when
        /// blank. Any non-null source keeps the search out of the aggregates.</summary>
        public static string? CleanSource(string? source)
        {
            var s = source?.Trim();
            return string.IsNullOrEmpty(s) ? null : s.Length <= 40 ? s : s[..40];
        }

        /// <summary>Whether coverage confirmed any result: the truncation index marks where the
        /// confirmed results end, and -1 is none, so what was shown came from fuzzy matching
        /// alone. Null with coverage off, where the index is always 0 and says nothing.</summary>
        public static bool? CoverageConfirmed(Indx.Http.QueryProxy query, Indx.Api.Result result) =>
            query.EnableCoverage ? result.TruncationIndex >= 0 : null;

        /// <summary>The filter key to store with a search: the query's token (which IS the key),
        /// or null when it has none or the dataset has the Browsing report switched off. With it
        /// off, an empty search with a filter is not recorded at all - without its key it is a
        /// page load.</summary>
        public string? FilterKeyToRecord(Indx.Http.QueryProxy query, string teamId, string dataSet) =>
            string.IsNullOrWhiteSpace(query.Filter?.HashString) || Store?.RecordsFilters(teamId, dataSet) != true
                ? null : query.Filter.HashString;
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

            // The places changes happen report them here (Notes/statistics-design.md, "Change
            // events"). Attached only when statistics are on, so off records nothing.
            IndxServerInternalApi.ManagerOrNull?.AttachChangeSink(this);
            if (boostRules != null) boostRules.ChangeSink = this;

            _cts = new CancellationTokenSource();
            _rollupLoop = Task.Run(() => RollupLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Idempotent, and safe after Dispose: the test host stops and disposes the factory in
            // an order that can run this twice around Dispose.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            IndxServerInternalApi.ManagerOrNull?.DetachChangeSink(this);
            Writer?.Stop();
            return Task.CompletedTask;
        }

        // ── Change events ────────────────────────────────────────────────────────────

        private static readonly System.Text.Json.JsonSerializerOptions SummaryJson =
            new(System.Text.Json.JsonSerializerDefaults.Web);

        /// <inheritdoc/>
        public void Changed(string teamId, string dataSet, string kind, object? summary = null)
        {
            if (!Enabled || Writer == null) return;
            Writer.RecordChange(new ChangeEventRow(teamId, dataSet,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind,
                summary == null ? null : System.Text.Json.JsonSerializer.Serialize(summary, SummaryJson)));
        }

        /// <summary>A single request this large is a change worth a marker of its own, beside the
        /// day's totals: a nightly sync gone wrong, a delete-by-filter that matched more than
        /// meant. The smaller of a thousand documents and a tenth of the dataset, never below ten.</summary>
        internal static long MassChangeThreshold(long datasetSize) =>
            Math.Min(1_000, Math.Max(10, datasetSize / 10));

        /// <summary>
        /// Counts one request's document changes into the day's totals, and records it as a change
        /// event of its own when it is a mass change (<see cref="MassChangeThreshold"/>) or a
        /// by-filter operation, whose size nobody knew until it ran. Every document route calls
        /// this once, after it succeeded.
        /// </summary>
        /// <param name="teamId">The team that owns the dataset.</param>
        /// <param name="dataSet">The dataset's name.</param>
        /// <param name="inserted">Documents the request added.</param>
        /// <param name="updated">Documents the request changed.</param>
        /// <param name="deleted">Documents the request removed.</param>
        /// <param name="byFilter">A by-filter operation: always an event, its size unknown beforehand.</param>
        /// <param name="operation">The route's verb ("insert", "update", "delete", "updateField",
        /// "deleteByFilter", "updateByFilter"), for the event's summary.</param>
        /// <param name="datasetSize">Documents in the dataset after the change, for the threshold.</param>
        public void RecordDocuments(string teamId, string dataSet, string operation,
            long inserted, long updated, long deleted, long datasetSize, bool byFilter = false)
        {
            if (!Enabled || Writer == null) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (inserted + updated + deleted > 0)
                Writer.RecordDocuments(new DocumentCountRow(teamId, dataSet, now, inserted, updated, deleted));
            if (byFilter || inserted + updated + deleted >= MassChangeThreshold(datasetSize))
                Changed(teamId, dataSet, DatasetChangeKind.Documents,
                    new { operation, inserted, updated, deleted, documents = datasetSize });
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
