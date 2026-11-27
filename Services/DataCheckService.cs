using System.Collections.Concurrent;
using IndxServer.Engine;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// Runs data checks (<see cref="DataCheck"/>), one per dataset at a time, on a thread of their
    /// own, and keeps how far each has come for the page that started it, or any page that opens
    /// later. The records are read from the store, not the engine, as the export reads them: a
    /// check never wakes a dataset and works whatever state the engine is in. A finished check is
    /// saved in <see cref="DataCheckStore"/>; the progress stays a while for the page to read.
    /// </summary>
    public sealed class DataCheckService(DataCheckStore store, ILogger<DataCheckService> logger)
    {
        /// <summary>How far a check has come. <see cref="Total"/> is the record count when it started.</summary>
        public sealed record Progress(long Read, long Total, bool Running, string? Error)
        {
            public double Percent => Total <= 0 ? (Running ? 0 : 100) : Math.Min(100, 100.0 * Read / Total);
        }

        private sealed class Job
        {
            public long Read;
            public long Total;
            public volatile bool Running = true;
            public string? Error;
            public readonly CancellationTokenSource Cts = new();
        }

        private readonly ConcurrentDictionary<string, Job> _jobs = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";

        /// <summary>The dataset's last report and what has happened since, or null when never checked.</summary>
        public DataCheckStanding? Standing(string teamId, string dataSetName) => store.Load(teamId, dataSetName);

        /// <summary>The check running or last run in this process, or null when there is none.</summary>
        public Progress? Get(string teamId, string dataSetName) =>
            _jobs.TryGetValue(Key(teamId, dataSetName), out var j)
                ? new Progress(Interlocked.Read(ref j.Read), Interlocked.Read(ref j.Total), j.Running, j.Error)
                : null;

        /// <summary>Starts a check over the dataset's records. False when one is already running.</summary>
        public bool Start(string teamId, string dataSetName, IReadOnlyList<DataCheck.CheckedField> fields) =>
            Start(teamId, dataSetName, fields, () => CountStored(teamId, dataSetName), () => ReadStored(teamId, dataSetName));

        /// <summary>The same, over records from elsewhere: the tests' seam.</summary>
        internal bool Start(string teamId, string dataSetName, IReadOnlyList<DataCheck.CheckedField> fields,
            Func<long> count, Func<IEnumerable<(long, string)>> records)
        {
            var key = Key(teamId, dataSetName);
            var job = new Job();
            while (true)
            {
                if (_jobs.TryGetValue(key, out var running))
                {
                    if (running.Running) return false;
                    if (!_jobs.TryUpdate(key, job, running)) continue;
                }
                else if (!_jobs.TryAdd(key, job)) continue;
                break;
            }
            var ceiling = DataCheck.TextCeiling;
            _ = Task.Factory.StartNew(() =>
            {
                try
                {
                    Interlocked.Exchange(ref job.Total, count());
                    var report = DataCheck.Run(records(), fields, ceiling, n => Interlocked.Exchange(ref job.Read, n), job.Cts.Token);
                    store.Save(teamId, dataSetName, report);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    job.Error = "The check could not read the records.";
                    logger.LogError(ex, "Data check failed for {Team}/{Dataset}", teamId, dataSetName);
                }
                finally
                {
                    job.Running = false;
                }
            }, job.Cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return true;
        }

        /// <summary>Forgets the dataset's report and any finished check: the page is as before a first
        /// scan. A running check is stopped first. The records are not touched.</summary>
        public void Clear(string teamId, string dataSetName)
        {
            Stop(teamId, dataSetName);
            store.Delete(teamId, dataSetName);
        }

        /// <summary>Stops a running check. The last report, if any, stays as it was.</summary>
        public void Stop(string teamId, string dataSetName)
        {
            if (_jobs.TryRemove(Key(teamId, dataSetName), out var job)) job.Cts.Cancel();
        }

        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;

        private static SqliteConnection OpenReadOnly()
        {
            var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
            conn.Open();
            return conn;
        }

        private static long CountStored(string teamId, string dataSetName)
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM JsonData WHERE Name = $d AND UserName = $t";
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$t", teamId);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        /// <summary>The stored records one at a time, unordered: ordering 2M rows by key would be most
        /// of the wait, and the report keeps its lists in key order itself.</summary>
        private static IEnumerable<(long, string)> ReadStored(string teamId, string dataSetName)
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, RawText FROM JsonData WHERE Name = $d AND UserName = $t";
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$t", teamId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                yield return (reader.GetInt64(0), reader.GetString(1));
        }
    }
}
