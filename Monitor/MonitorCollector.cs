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
        private readonly Dictionary<string, string> _lastPhase = [];
        private bool _first = true;

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

            var events = DetectTransitions(lines, now);
            return new MonitorSnapshot(now, lines, ReadProcess(), ReadFilters(), events);
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
                bool known = _lastPhase.TryGetValue(line.Key, out var previous);
                _lastPhase[line.Key] = line.Phase;

                // The first tick establishes the baseline; reporting every dataset as "appeared"
                // would fill the pane with noise at startup.
                if (_first)
                    continue;

                if (!known)
                    events.Add(new MonitorEvent(now, "system", $"{line.Key} appeared ({line.Phase})"));
                else if (previous != line.Phase)
                    events.Add(new MonitorEvent(now, "system", $"{line.Key} {previous} → {line.Phase}"));
            }

            foreach (var goneKey in _lastPhase.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _lastPhase.Remove(goneKey);
                if (!_first)
                    events.Add(new MonitorEvent(now, "system", $"{goneKey} deleted"));
            }

            _first = false;
            return events;
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
