using System.Collections.Concurrent;
using IndxServer.Engine;

namespace IndxServer.Services
{
    /// <summary>
    /// A dataset's records, for the admin pages, without counting them on disk each time. A loaded
    /// dataset says its own count at no cost. A sleeping one is a COUNT(*) over its JsonData rows,
    /// which on a 10 GB store took 7.5 s for every dataset together on a cold cache (Nov 2026), on
    /// every load of Admin &gt; Teams. A sleeping dataset's records change only once it is woken, and
    /// a woken one is read from its engine, so a count is kept for <see cref="Fresh"/> and Refresh
    /// counts again.
    /// </summary>
    internal static class DatasetRecordCounts
    {
        /// <summary>How long a count of a sleeping dataset is used before it is taken again.</summary>
        public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

        private static readonly ConcurrentDictionary<(string DataSet, string Team), (int Count, DateTime At)> Counts = new();

        /// <summary>The records of one dataset: its engine's count when it is loaded, else the count
        /// kept from the last <see cref="Fresh"/>, else counted now. <paramref name="recount"/> counts
        /// a sleeping dataset again whatever is kept.</summary>
        public static int Get(string dataSet, string teamId, bool recount = false)
        {
            var manager = IndxServerInternalApi.Manager;
            var now = DateTime.UtcNow;
            // Never GetState: it wakes a hibernated dataset (see Admin/Datasets.razor).
            var engine = manager.FindSearchEngine(dataSet, teamId);
            // Not when the free limit cut it: the engine then holds the first 100,000 and the store
            // the rest, and the store's count is the one that says it was cut.
            if (engine is { IsDisposed: false } && engine.Status is { SystemState: Indx.Api.SystemState.Ready } status
                && status.LicenseInfo?.DocumentLimitExceeded != true)
            {
                Counts[(dataSet, teamId)] = (status.DocumentCount, now);
                return status.DocumentCount;
            }
            if (!recount && Counts.TryGetValue((dataSet, teamId), out var kept) && now - kept.At < Fresh)
                return kept.Count;
            int counted = manager.GetKeepAliveInfo(dataSet, teamId).RecordCount;
            Counts[(dataSet, teamId)] = (counted, now);
            return counted;
        }

        /// <summary>The engine's keep-alive view with its record count from here, not a COUNT(*) of
        /// its own: what every page that opens a dataset or lists a team's asks for, often once a
        /// second while a page is open.</summary>
        public static IndxServerInternalApi.KeepAliveInfo KeepAlive(string dataSet, string teamId) =>
            IndxServerInternalApi.Manager.GetKeepAliveInfo(dataSet, teamId, countRecordsOnDisk: false)
                with { RecordCount = Get(dataSet, teamId) };

        /// <summary>The count kept for a dataset is dropped when it is put to sleep, disposed or
        /// deleted: the next read counts it again, once. A sleeping dataset's rows cannot change, so
        /// after that the count is right until it wakes.</summary>
        public static void Forget(string dataSet, string teamId) => Counts.TryRemove((dataSet, teamId), out _);
    }
}
