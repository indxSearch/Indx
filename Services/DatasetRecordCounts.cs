using System.Collections.Concurrent;
using IndxServer.Engine;

namespace IndxServer.Services
{
    /// <summary>
    /// A dataset's records, for the admin pages, without counting them on disk each time. A loaded
    /// dataset says its own count at no cost. A sleeping one is a COUNT(*) over its JsonData rows,
    /// which on a 10 GB store took 7.5 s for every dataset together on a cold cache (Nov 2026), on
    /// every load of Admin &gt; Teams. A sleeping dataset's records change only once it is woken, and
    /// a woken one is read from its engine, so a sleeping dataset's count is kept until it wakes, is
    /// deleted, renamed or moved, and Refresh counts again.
    /// <para>Until 11 Nov 2026 a count was kept for ten minutes only. The team page, which lists every
    /// dataset, then counted a sleeping one again every ten minutes: a dataset of several hundred
    /// thousand records took 8 s over App Service's network storage once its pages had left the cache, and
    /// the cards after it waited. Nothing was gained by it: the count cannot change while asleep.</para>
    /// </summary>
    internal static class DatasetRecordCounts
    {
        private static readonly ConcurrentDictionary<(string DataSet, string Team), int> Counts = new();

        /// <summary>The records of one dataset: its engine's count when it is loaded, else the count
        /// kept since it went to sleep, else counted now. <paramref name="recount"/> counts a sleeping
        /// dataset again whatever is kept. An engine in error is counted each time: a load that failed
        /// may have written part of its rows.</summary>
        public static int Get(string dataSet, string teamId, bool recount = false)
        {
            var manager = IndxServerInternalApi.Manager;
            // Never GetState: it wakes a hibernated dataset (see Admin/Datasets.razor).
            var engine = manager.FindSearchEngine(dataSet, teamId);
            // Not when the free limit cut it: the engine then holds the first 100,000 and the store
            // the rest, and the store's count is the one that says it was cut.
            if (engine is { IsDisposed: false } && engine.Status is { SystemState: Indx.Api.SystemState.Ready } status
                && status.LicenseInfo?.DocumentLimitExceeded != true)
            {
                Counts[(dataSet, teamId)] = status.DocumentCount;
                return status.DocumentCount;
            }
            if (engine is { IsDisposed: false, Status.SystemState: Indx.Api.SystemState.Error })
                recount = true;
            if (!recount && Counts.TryGetValue((dataSet, teamId), out var kept))
                return kept;
            int counted = manager.GetKeepAliveInfo(dataSet, teamId).RecordCount;
            Counts[(dataSet, teamId)] = counted;
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
