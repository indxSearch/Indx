using System.Globalization;
using Indx.Api;

namespace IndxServer.Monitor
{
    /// <summary>
    /// One dataset as the monitor sees it: everything readable without resolving an engine.
    /// <para><see cref="State"/> is null when no engine instance is in memory at all. A dataset
    /// with rows on disk but no loaded engine (or one still in <see cref="SystemState.Created"/>)
    /// is <see cref="Hibernated"/> — the same derivation the admin dataset list makes.</para>
    /// </summary>
    internal sealed record DatasetLine(
        string DataSetName,
        string TeamId,
        string? TeamName,
        SystemState? State,
        bool Hibernated,
        int DocumentCount,
        int RecordsOnDisk,
        bool Ready,
        DateTimeOffset? LastUsedUtc,
        TimeSpan? KeepAliveRemaining,
        int KeepAliveHrs,
        int? ProgressPercent,
        int SearchCounter,
        bool IndexedTextTruncated,
        bool FieldIndexFallback,
        string? ErrorMessage)
    {
        /// <summary>Stable identity across ticks, for transition detection. The id, not the
        /// name: a rename must not read as a dataset appearing and another disappearing.</summary>
        internal string Key => TeamId + "/" + DataSetName;

        /// <summary>What to put in a "team" column: the name when it is known, else the id, cut
        /// short because a GUID is all column and no information.</summary>
        internal string TeamLabel =>
            !string.IsNullOrEmpty(TeamName) ? TeamName
            : TeamId.Length > 8 ? TeamId[..8] : TeamId;

        /// <summary>
        /// The one word shown in a table and compared between ticks to raise a transition event.
        /// Hibernated is checked before State because a hibernated dataset reports Created.
        /// </summary>
        internal string Phase =>
            Hibernated ? "Hibernated"
            : State?.ToString() ?? "Unloaded";
    }

    /// <summary>
    /// Process-wide numbers, all cheap reads. The native block count is the one that is exact:
    /// heap, private and working set all move with paging and say nothing on a memory-starved
    /// host, where a steady <see cref="NativeBlocks"/> still means balanced alloc/free.
    /// </summary>
    internal sealed record ProcessLine(
        long GcHeapMb,
        long PrivateMb,
        long WorkingSetMb,
        int Gen0,
        int Gen1,
        int Gen2,
        long NativeBlocks,
        long NativePoolMb);

    /// <summary>
    /// How unloaded filters are being materialised, and what dynamic ops cost the filter cache.
    /// Free to read in-process and readable nowhere else: these are static counters that nothing
    /// in IndxServer has ever surfaced. <see cref="RpnScanLoads"/> is the alarm — a sequential
    /// full-corpus walk with no posting index, which should stay at zero.
    /// </summary>
    internal sealed record FilterLine(
        long SearchPathLoads,
        long KeyResolutionLoads,
        long RpnScanLoads,
        long RpnScanDocumentsVisited,
        long FieldFilterUpdates,
        long DerivedRecomputes,
        long DerivedRebuilds)
    {
        /// <summary>
        /// One line, and deliberately not three counts.
        ///
        /// <para><c>srch</c> is left out: route 1 is the lazy load inside Search, and over HTTP the
        /// filter is already resolved before Search runs, so on a server it is structurally zero
        /// and spends a column saying nothing. It is the in-process route, which is why IndxSoak
        /// needs --cloud-shape to reach route 2 at all.</para>
        ///
        /// <para><c>scan</c> appears only when it has fired, because it should never fire: it is
        /// FilterBuilder's fallback for an operand the key route was meant to have loaded, and it
        /// consults no posting index. The documents-walked figure sits next to it because the
        /// number of scans understates the cost on a large corpus; the walk is the cost.</para>
        /// </summary>
        internal string Describe()
        {
            // Invariant throughout the monitor: the piped block goes into logs that anyone reads,
            // and a figure should not change shape with the server's locale.
            var inv = CultureInfo.InvariantCulture;
            string scan = RpnScanLoads > 0
                ? string.Create(inv, $"   scan {RpnScanLoads:N0} ({RpnScanDocumentsVisited:N0} docs walked)")
                : "";
            return string.Create(inv, $"filters  key {KeyResolutionLoads:N0}{scan}   ")
                 + string.Create(inv, $"upkeep field {FieldFilterUpdates:N0} / derived {DerivedRecomputes:N0} ")
                 + string.Create(inv, $"/ rebuilds {DerivedRebuilds:N0}");
        }
    }

    /// <summary>Something worth a line in the stream. Stage one raises these from state
    /// transitions the collector notices between ticks; later stages add actors.</summary>
    internal sealed record MonitorEvent(DateTimeOffset AtUtc, string Source, string Text);

    /// <summary>
    /// One tick. Immutable, so a renderer can hold it while the next one is being built.
    /// </summary>
    internal sealed record MonitorSnapshot(
        DateTimeOffset TakenUtc,
        IReadOnlyList<DatasetLine> Datasets,
        ProcessLine Process,
        FilterLine Filters,
        IReadOnlyList<MonitorEvent> NewEvents,
        long SearchesTotal = 0,
        double SearchesPerMinute = 0)
    {
        /// <summary>Searches counted since the monitor started, and the recent rate. Not
        /// <c>SearchCounter</c> summed: that is per engine and restarts at zero when a dataset is
        /// evicted and reloaded, so the sum would fall. See <c>MonitorCollector</c>.</summary>
        internal string DescribeSearches() =>
            SearchesPerMinute >= 0.05
                ? string.Create(CultureInfo.InvariantCulture, $"searches {SearchesTotal:N0} ({SearchesPerMinute:N0}/min)")
                : string.Create(CultureInfo.InvariantCulture, $"searches {SearchesTotal:N0}");

        internal int TotalDocuments => Datasets.Sum(d => d.DocumentCount);
        internal int LoadedCount => Datasets.Count(d => d.Ready);

        internal static MonitorSnapshot Empty(DateTimeOffset at) =>
            new(at, [], new ProcessLine(0, 0, 0, 0, 0, 0, 0, 0), new FilterLine(0, 0, 0, 0, 0, 0, 0), []);
    }
}
