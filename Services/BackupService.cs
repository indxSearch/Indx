using Indx.Storage;
using IndxServer.Data;
using IndxServer.Engine;
using System.Collections.Concurrent;

namespace IndxServer.Services
{
    /// <summary>
    /// The app-level backup job from Notes/backup-design.md: takes complete, verified copies of
    /// the live databases (identity.db, indx.db, stats.db) with <c>VACUUM INTO</c> — WAL writers
    /// keep running — on a configurable schedule, verifies each copy with
    /// <c>PRAGMA integrity_check</c>, and rotates old copies. The copies are plain SQLite files:
    /// restore is stop-the-server, copy the file back, start. Never automatic — no rollback.
    ///
    /// This job is the consistency anchor in BOTH editions; infrastructure snapshots (Azure
    /// Files, in the Managed App) protect the backup folder, not the live files. A failed run is
    /// loud: error log plus an admin notification. Configuration (appsettings "Backup"):
    /// <c>Enabled</c> (default true), <c>Folder</c> (default ./IndxData/backups — same disk, so
    /// it protects against corruption and operator error, not disk failure; point it elsewhere
    /// for that), <c>TimeOfDay</c> (UTC, default 03:30), <c>IntervalHours</c> (default 24,
    /// anchored to TimeOfDay), <c>Keep</c> (copies per database, default 7), <c>MaxTotalMB</c>
    /// (a cap on the folder, default 2048: past it the oldest copies go, as routine rotation).
    /// </summary>
    public sealed class BackupService(
        IConfiguration configuration,
        StatisticsService statistics,
        OperationalAlerts alerts,
        ILogger<BackupService> logger) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccess = new();

        /// <summary>Last successful backup per database — for the monitor and the admin pages.</summary>
        public IReadOnlyDictionary<string, DateTimeOffset> LastSuccess => _lastSuccess;

        /// <summary>A file of this name in the backup folder asks for a run now. The deploy script
        /// (deploy/azure.sh) writes it through Kudu before a deploy and waits for the new copies:
        /// the live databases are held open, so a copy of the folder never has them, and the
        /// nightly copies can be most of a day old (22 Oct 2026). A file, not a route: the deploy
        /// is signed in to Azure, not to the server, and a file in the data folder is something only
        /// whoever controls the host can write.</summary>
        public const string RequestFile = "backup-now";
        internal static readonly TimeSpan RequestPoll = TimeSpan.FromSeconds(15);

        private string Folder => FolderFor(configuration);

        /// <summary><c>Backup:Folder</c>, else <c>backups</c> in the data folder (<see cref="DataPaths"/>).</summary>
        internal static string FolderFor(IConfiguration configuration) =>
            configuration["Backup:Folder"] is { Length: > 0 } f ? f : Path.Combine(DataPaths.Configured(configuration) ?? DataPaths.Default, "backups");

        /// <summary>Whether a run was asked for, taking the request so it runs once.</summary>
        internal static bool TakeRequest(string folder)
        {
            var path = Path.Combine(folder, RequestFile);
            if (!File.Exists(path)) return false;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return true;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!configuration.GetValue("Backup:Enabled", true))
            {
                logger.LogInformation("backups are disabled (Backup:Enabled=false)");
                return;
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                var timeOfDay = TimeSpan.TryParse(configuration["Backup:TimeOfDay"], out var t) ? t : new TimeSpan(3, 30, 0);
                var interval = Math.Clamp(configuration.GetValue("Backup:IntervalHours", 24), 1, 24 * 7);
                var next = NextRun(DateTimeOffset.UtcNow, timeOfDay, interval);
                // Until the scheduled run, or a request for one now, whichever comes first.
                try
                {
                    while (DateTimeOffset.UtcNow < next && !TakeRequest(Folder))
                    {
                        var wait = next - DateTimeOffset.UtcNow;
                        await Task.Delay(wait < RequestPoll ? wait : RequestPoll, stoppingToken);
                    }
                }
                catch (OperationCanceledException) { return; }
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "backup run failed before reaching any database");
                }
            }
        }

        /// <summary>The next scheduled run: the TimeOfDay anchor (UTC), stepped by the interval.</summary>
        internal static DateTimeOffset NextRun(DateTimeOffset now, TimeSpan timeOfDay, int intervalHours)
        {
            var anchor = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero) + timeOfDay;
            anchor = anchor.AddDays(-1); // start behind now so the first step lands correctly
            while (anchor <= now) anchor = anchor.AddHours(intervalHours);
            return anchor;
        }

        /// <summary>
        /// One backup pass over every database that exists, then rotation. Also callable on
        /// demand (tests, a future admin action). A failure is logged and notified, never thrown.
        /// </summary>
        public async Task<List<BackupResult>> RunOnceAsync(CancellationToken ct = default)
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);

            var results = new List<BackupResult>();
            foreach (var source in Sources())
            {
                ct.ThrowIfCancellationRequested();
                // Null: the store does not exist yet (stats.db before the first statistic).
                if (source.TryBackup(folder) is not { } result) continue;
                results.Add(result);
                var name = source.Name;
                if (result.Error == null)
                {
                    _lastSuccess[name] = DateTimeOffset.UtcNow;
                    logger.LogInformation("backup of {Database} took {Ms} ms: {Bytes} bytes, verified, at {Path}",
                        name, (long)result.Duration.TotalMilliseconds, result.Bytes, result.CopyPath);
                }
                else
                {
                    logger.LogError("backup of {Database} FAILED: {Error}", name, result.Error);
                }
            }

            Rotate(folder,
                keep: Math.Max(1, configuration.GetValue("Backup:Keep", 7)),
                maxTotalMb: Math.Max(1, configuration.GetValue("Backup:MaxTotalMB", 2048)));

            var failures = results.Where(r => r.Error != null).ToList();
            if (failures.Count > 0)
            {
                var body = string.Join(" ", failures.Select(fr => $"{fr.Database}: {fr.Error}."));
                await alerts.NotifyAdminsAsync(NotificationType.BackupFailed, "Database backup failed", body);
                await alerts.RaiseAsync("backup-failed", "Indx: database backup failed", body);
            }

            await CheckDiskSpaceAsync(folder);
            return results;
        }

        /// <summary>
        /// Disk full is the likely catastrophe, not disk death — statistics growth and backup
        /// rotation share the disk. Checked here because the backup run already has the daily
        /// cadence; below Alerts:MinFreeDiskMB it raises the in-app notification and the alert
        /// email. Restore needs headroom too, so the warning must come early.
        /// </summary>
        private async Task CheckDiskSpaceAsync(string folder)
        {
            long minFree = (long)Math.Max(1, configuration.GetValue("Alerts:MinFreeDiskMB", 1024)) * 1024 * 1024;
            long free;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (root == null) return;
                free = new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                return; // an unreadable drive is not a low-disk condition
            }
            if (free >= minFree) return;
            var body = $"The disk holding the databases and backups has {free / (1024 * 1024)} MB free, " +
                       $"below the Alerts:MinFreeDiskMB threshold of {minFree / (1024 * 1024)} MB.";
            logger.LogWarning("low disk space: {Body}", body);
            await alerts.NotifyAdminsAsync(NotificationType.LowDiskSpace, "Low disk space", body);
            await alerts.RaiseAsync("low-disk", "Indx: low disk space", body);
        }

        /// <summary>
        /// The stores this instance backs up, each copying itself (<see cref="IBackupSource"/>,
        /// backlog 26): identity.db through <see cref="IdentityBackupSource"/>, indx.db through the
        /// library's <see cref="SqLiteManager"/>, and stats.db through its store when statistics
        /// are on. Until Oct 2026 this job copied the three SQLite files itself, the one place
        /// the server reached past the storage layer.
        /// </summary>
        private IEnumerable<IBackupSource> Sources()
        {
            yield return new IdentityBackupSource(configuration);
            if (IndxServerInternalApi.SearchDbConnectionString is { Length: > 0 } search)
                yield return new SqLiteManager(search) { Name = "indx" };
            if (statistics.Store is { } stats)
                yield return stats;
        }

        /// <summary>
        /// Count-based retention per database (newest <paramref name="keep"/> stay — the
        /// timestamped names sort chronologically within a database), then the emergency brake:
        /// while the folder exceeds <paramref name="maxTotalMb"/>, the oldest copies go first by
        /// the time in their names, whatever database they belong to, and each database keeps its
        /// newest copy. Until Nov 2026 the brake went by the whole name, so <c>identity-</c> sorted
        /// before <c>indx-</c> and <c>stats-</c>: a large stats.db had every identity copy deleted,
        /// the one just made included, while week-old statistics stayed.
        /// </summary>
        internal void Rotate(string folder, int keep, int maxTotalMb)
        {
            var files = Directory.GetFiles(folder, "*.db")
                .Select(p => new FileInfo(p))
                .Where(fi => fi.Name.Contains('-'))
                .ToList();

            // The base name is everything before the first '-': ours carry none, the timestamp two.
            static string Database(FileInfo fi) => fi.Name[..fi.Name.IndexOf('-')];
            static string Stamp(FileInfo fi) => fi.Name[(fi.Name.IndexOf('-') + 1)..];

            var newest = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in files.GroupBy(Database).ToList())
            {
                var ordered = group.OrderBy(fi => fi.Name, StringComparer.Ordinal).ToList();
                newest.Add(ordered[^1].Name);
                foreach (var old in ordered.SkipLast(keep))
                {
                    old.Delete();
                    files.Remove(old);
                    logger.LogInformation("backup rotation deleted {Name}", old.Name);
                }
            }

            long budget = (long)maxTotalMb * 1024 * 1024;
            var candidates = files.Where(fi => !newest.Contains(fi.Name))
                .OrderBy(Stamp, StringComparer.Ordinal).ThenBy(fi => fi.Name, StringComparer.Ordinal)
                .ToList();
            // Routine, not a fault (15 Nov 2026): with a stats.db of a few hundred MB, seven copies pass
            // the default 2 048 MB within a week, and a warning a night read like an error. One line
            // a run says what the rotation took; the warning is kept for the newest copies not fitting.
            var removed = new List<string>();
            foreach (var oldest in candidates)
            {
                if (files.Sum(fi => fi.Length) <= budget) break;
                files.Remove(oldest);
                oldest.Delete();
                removed.Add(oldest.Name);
            }
            if (removed.Count > 0)
                logger.LogInformation("backup rotation: kept the folder within {MaxMb} MB (Backup:MaxTotalMB) by removing the {Count} oldest {Copies}: {Names}",
                    maxTotalMb, removed.Count, removed.Count == 1 ? "copy" : "copies", string.Join(", ", removed));
            if (files.Sum(fi => fi.Length) > budget)
                logger.LogWarning("backup folder exceeds Backup:MaxTotalMB ({MaxMb} MB) with only the newest copy of each database left; raise it or move Backup:Folder",
                    maxTotalMb);
        }

    }
}
