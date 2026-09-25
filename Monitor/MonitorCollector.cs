using Indx.Api;
using IndxServer.Models;

namespace IndxServer.Monitor
{
    /// <summary>
    /// Builds one <see cref="MonitorSnapshot"/> per tick, and raises an event whenever a dataset
    /// changes phase between ticks.
    ///
    /// <para><b>It must never wake a dataset.</b> <c>GetState</c> and <c>ResolveEngine</c> auto-load
    /// a hibernated timed/pinned dataset, so a monitor polling every dataset once a second through
    /// them would defeat <c>DatasetIdleSweeper</c> and hold the whole corpus in memory. Every read
    /// here is one of the three that only look at what is already in the registry:
    /// <c>GetAllDataSets</c> (a SQLite row list), <c>FindSearchEngine</c> (creates an unloaded shell
    /// at most, never loads — the same call the admin dataset list makes for the same reason), and
    /// <c>GetKeepAliveInfo</c> / <c>GetProgressPercent</c> (pure dictionary reads).
    /// <c>MonitorCollectorTests.CollectingDoesNotWakeAHibernatedDataset</c> pins it.</para>
    ///
    /// <para>The transition events exist because the engine registry logs to <b>NLog</b>, not
    /// <c>ILogger</c>, so a ring buffer over the ASP.NET logging pipeline sees none of the dataset
    /// lifecycle lines. Deriving them from the polling this class already does covers that without
    /// touching the registry at all.</para>
    /// </summary>
    internal sealed class MonitorCollector(TeamNames? teamNames = null)
    {
        // Phase per dataset key as of the previous tick. Only this class touches it, and Collect
        // is called from one timer, so no synchronisation is needed.
        // Keyed by the team id, because a rename must not read as one dataset disappearing and
        // another appearing. The label is carried alongside so a deleted dataset, which is no
        // longer in the snapshot, can still be named by its team rather than by a GUID.
        private readonly Dictionary<string, (string Phase, string Label)> _lastSeen = [];
        private bool _first = true;

        // Searches, accumulated rather than summed. SystemStatus.SearchCounter is per engine and
        // restarts at zero when a dataset is evicted and reloaded, so summing it across datasets
        // gives a total that falls. Adding deltas instead — and treating a fall as a restart —
        // gives a number that only goes up, which is what a counter on screen has to do.
        private readonly Dictionary<string, int> _lastSearchCount = [];
        private long _searchesTotal;
        private readonly List<(DateTimeOffset At, long Total)> _rateSamples = [];
        private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

        internal MonitorSnapshot Collect(DateTimeOffset now)
        {
            List<(string DataSetName, string TeamId)> all;
            try
            {
                all = IndxServerInternalApi.Manager.GetAllDataSets();
            }
            catch (InvalidOperationException)
            {
                // StartUpSystem has not run yet (or failed). Nothing to report, and the monitor
                // must not be the thing that turns that into a crash.
                return MonitorSnapshot.Empty(now);
            }

            // One identity-database read for the whole table, and usually not even that.
            var names = teamNames?.Resolve(all.Select(r => r.TeamId), now)
                        ?? new Dictionary<string, string>();

            var lines = new List<DatasetLine>(all.Count);
            foreach (var (dataSetName, teamId) in all)
                lines.Add(ReadOne(dataSetName, teamId, names.GetValueOrDefault(teamId)));

            lines.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            return Build(lines, now);
        }

        private static DatasetLine ReadOne(string dataSetName, string teamId, string? teamName)
        {
            var manager = IndxServerInternalApi.Manager;

            // Never Status on a disposed engine: that throws ObjectDisposedException.
            var engine = manager.FindSearchEngine(dataSetName, teamId);
            var status = engine is { IsDisposed: false } ? engine.Status : null;
            var keepAlive = manager.GetKeepAliveInfo(dataSetName, teamId);

            // Rows on disk with no live engine (or one still in Created) is what hibernation looks
            // like from outside: the data is persisted, the engine is not loaded.
            bool hibernated = keepAlive.RecordCount > 0
                              && status?.SystemState is null or SystemState.Created;

            return new DatasetLine(
                DataSetName: dataSetName,
                TeamId: teamId,
                TeamName: teamName,
                State: status?.SystemState,
                Hibernated: hibernated,
                DocumentCount: status?.DocumentCount ?? 0,
                RecordsOnDisk: keepAlive.RecordCount,
                Ready: keepAlive.Ready,
                LastUsedUtc: keepAlive.LastUsedUtc,
                KeepAliveRemaining: keepAlive.Remaining,
                KeepAliveHrs: keepAlive.KeepAliveTimeHrs,
                ProgressPercent: manager.GetProgressPercent(dataSetName, teamId),
                SearchCounter: status?.SearchCounter ?? 0,
                IndexedTextTruncated: status?.IndexedTextTruncated ?? false,
                ErrorMessage: string.IsNullOrWhiteSpace(status?.ErrorMessage) ? null : status!.ErrorMessage);
        }

        private List<MonitorEvent> DetectTransitions(List<DatasetLine> lines, DateTimeOffset now)
        {
            var events = new List<MonitorEvent>();
            var seen = new HashSet<string>(lines.Count);

            foreach (var line in lines)
            {
                seen.Add(line.Key);
                bool known = _lastSeen.TryGetValue(line.Key, out var previous);
                string label = $"{line.TeamLabel}/{line.DataSetName}";
                _lastSeen[line.Key] = (line.Phase, label);

                // The first tick establishes the baseline; reporting every dataset as "appeared"
                // would fill the pane with noise at startup.
                if (_first)
                    continue;

                if (!known)
                    events.Add(new MonitorEvent(now, "system", $"{label} appeared ({line.Phase})"));
                else if (previous.Phase != line.Phase)
                    events.Add(new MonitorEvent(now, "system", $"{label} {previous.Phase} → {line.Phase}"));
            }

            foreach (var goneKey in _lastSeen.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                string label = _lastSeen[goneKey].Label;
                _lastSeen.Remove(goneKey);
                if (!_first)
                    events.Add(new MonitorEvent(now, "system", $"{label} deleted"));
            }

            _first = false;
            return events;
        }

        /// <summary>
        /// What the rest of the server reported since the last tick: things no amount of polling
        /// would find, because they leave the engine looking exactly as it did.
        /// </summary>
        private static List<MonitorEvent> DrainActivity(List<DatasetLine> lines, DateTimeOffset now)
        {
            var reported = MonitorActivity.Drain();
            if (reported.Count == 0)
                return [];

            var events = new List<MonitorEvent>(reported.Count);
            foreach (var entry in reported)
            {
                // Name the team the way the table does. A dataset reported and then deleted in the
                // same tick falls back to the id, which is better than dropping the line.
                var line = lines.FirstOrDefault(d => d.DataSetName == entry.DataSetName && d.TeamId == entry.TeamId);
                string label = line is not null
                    ? $"{line.TeamLabel}/{entry.DataSetName}"
                    : $"{Shorten(entry.TeamId)}/{entry.DataSetName}";
                events.Add(new MonitorEvent(entry.At, "fields", $"{label} · {entry.Text}"));
            }
            return events;
        }

        private static string Shorten(string teamId) => teamId.Length > 8 ? teamId[..8] : teamId;

        /// <summary>
        /// Adds what each dataset has served since the last tick. A dataset seen for the first time
        /// contributes nothing: its counter may have been climbing long before the monitor started,
        /// and counting it once as a spike would be a lie. A counter that went down means the
        /// engine was reloaded, so everything it now reports is new.
        /// </summary>
        /// <summary>Everything a tick does once the registry has been read. Exposed for tests:
        /// the transition and counting rules are where the thinking is, and neither can be
        /// reached through <see cref="Collect"/> without a live registry.</summary>
        internal MonitorSnapshot Build(IReadOnlyList<DatasetLine> lines, DateTimeOffset now)
        {
            var ordered = lines.ToList();
            var events = DetectTransitions(ordered, now);
            events.AddRange(DrainActivity(ordered, now));
            CountSearches(ordered);
            return new MonitorSnapshot(now, ordered, ReadProcess(), ReadFilters(), events,
                                       _searchesTotal, RatePerMinute(now));
        }

        private void CountSearches(List<DatasetLine> lines)
        {
            foreach (var line in lines)
            {
                if (!_lastSearchCount.TryGetValue(line.Key, out int previous))
                {
                    _lastSearchCount[line.Key] = line.SearchCounter;
                    continue;
                }

                _lastSearchCount[line.Key] = line.SearchCounter;
                _searchesTotal += line.SearchCounter >= previous
                    ? line.SearchCounter - previous
                    : line.SearchCounter;
            }
        }

        /// <summary>Searches a minute over the last minute, from the oldest sample still inside the
        /// window. A rate off one tick would swing wildly at a one-second poll.</summary>
        private double RatePerMinute(DateTimeOffset now)
        {
            _rateSamples.Add((now, _searchesTotal));
            while (_rateSamples.Count > 1 && now - _rateSamples[0].At > RateWindow)
                _rateSamples.RemoveAt(0);

            var oldest = _rateSamples[0];
            double seconds = (now - oldest.At).TotalSeconds;
            if (seconds < 1)
                return 0;
            return (_searchesTotal - oldest.Total) / seconds * 60.0;
        }

        private static FilterLine ReadFilters() => new(
            SearchPathLoads: FilterLoadDiagnostics.SearchPathLoads,
            KeyResolutionLoads: FilterLoadDiagnostics.KeyResolutionLoads,
            RpnScanLoads: FilterLoadDiagnostics.RpnScanLoads,
            RpnScanDocumentsVisited: FilterLoadDiagnostics.RpnScanDocumentsVisited,
            FieldFilterUpdates: FilterUpdateDiagnostics.FieldFilterUpdates,
            DerivedRecomputes: FilterUpdateDiagnostics.DerivedRecomputes,
            DerivedRebuilds: FilterUpdateDiagnostics.DerivedRebuilds);

        private static ProcessLine ReadProcess()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return new ProcessLine(
                GcHeapMb: GC.GetTotalMemory(false) / 1024 / 1024,
                PrivateMb: process.PrivateMemorySize64 / 1024 / 1024,
                WorkingSetMb: process.WorkingSet64 / 1024 / 1024,
                Gen0: GC.CollectionCount(0),
                Gen1: GC.CollectionCount(1),
                Gen2: GC.CollectionCount(2),
                NativeBlocks: Indx.Utilities.NativeMemoryDiagnostics.OutstandingBlocks,
                NativePoolMb: Indx.Utilities.NativeMemoryDiagnostics.OutstandingPoolBytes / 1024 / 1024);
        }
    }
}
