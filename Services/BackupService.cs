using IndxServer.Data;
using IndxServer.Engine;
using Microsoft.Data.Sqlite;
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
    /// (emergency brake on the folder, default 2048).
    /// </summary>
    public sealed class BackupService(
        IConfiguration configuration,
        IServiceProvider services,
        StatisticsService statistics,
        OperationalAlerts alerts,
        ILogger<BackupService> logger) : BackgroundService
    {
        /// <summary>One database's outcome of a backup run.</summary>
        public sealed record BackupResult(string Database, string? CopyPath, long Bytes, TimeSpan Duration, string? Error);

        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccess = new();

        /// <summary>Last successful backup per database — for the monitor and the admin pages.</summary>
        public IReadOnlyDictionary<string, DateTimeOffset> LastSuccess => _lastSuccess;

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
                try { await Task.Delay(next - DateTimeOffset.UtcNow, stoppingToken); }
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
            var folder = configuration["Backup:Folder"] is { Length: > 0 } f ? f : "./IndxData/backups";
            Directory.CreateDirectory(folder);

            var identity = ServerConnectionStrings.ExtractDbPath(
                ServerConnectionStrings.GetIdentityConnectionString(configuration));
            var search = IndxServerInternalApi.SearchDbConnectionString;
            var stats = statistics.Store?.DbPath;

            var results = new List<BackupResult>();
            foreach (var (name, path) in new[] { ("identity", identity), ("indx", search), ("stats", stats) })
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                ct.ThrowIfCancellationRequested();
                var result = BackupOne(path, folder, name);
                results.Add(result);
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
                await NotifyAdminsAsync(NotificationType.BackupFailed, "Database backup failed", body);
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
            await NotifyAdminsAsync(NotificationType.LowDiskSpace, "Low disk space", body);
            await alerts.RaiseAsync("low-disk", "Indx: low disk space", body);
        }

        /// <summary>
        /// One database: <c>VACUUM INTO</c> a timestamped copy, then <c>PRAGMA integrity_check</c>
        /// on the copy — which doubles as early corruption detection on the source: the day to
        /// learn a database is corrupt is backup day, not restore day. A copy that fails
        /// verification is deleted; a failed backup must never look like a good one.
        /// </summary>
        internal static BackupResult BackupOne(string sourcePath, string folder, string baseName)
        {
            var target = Path.Combine(folder, $"{baseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.db");
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var conn = new SqliteConnection($"Data Source={sourcePath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "VACUUM INTO $path";
                    cmd.Parameters.AddWithValue("$path", target);
                    cmd.CommandTimeout = 3600; // a gigabyte-class indx.db is a long rewrite
                    cmd.ExecuteNonQuery();
                }
                using (var check = new SqliteConnection($"Data Source={target};Mode=ReadOnly"))
                {
                    check.Open();
                    using var cmd = check.CreateCommand();
                    cmd.CommandText = "PRAGMA integrity_check";
                    if (cmd.ExecuteScalar() as string != "ok")
                    {
                        SqliteConnection.ClearPool(check);
                        File.Delete(target);
                        return new BackupResult(baseName, null, 0, started.Elapsed,
                            "the copy failed integrity_check — the SOURCE database may be corrupt; " +
                            "do not restart blindly, investigate first");
                    }
                }
                return new BackupResult(baseName, target, new FileInfo(target).Length, started.Elapsed, null);
            }
            catch (SqliteException ex)
            {
                try { if (File.Exists(target)) File.Delete(target); } catch (IOException) { }
                return new BackupResult(baseName, null, 0, started.Elapsed, ex.Message);
            }
        }

        /// <summary>
        /// Count-based retention per database (newest <paramref name="keep"/> stay — the
        /// timestamped names sort chronologically), then the emergency brake: while the folder
        /// exceeds <paramref name="maxTotalMb"/>, oldest copies go first, whatever database they
        /// belong to.
        /// </summary>
        internal void Rotate(string folder, int keep, int maxTotalMb)
        {
            var files = Directory.GetFiles(folder, "*.db")
                .Select(p => new FileInfo(p))
                .OrderBy(fi => fi.Name, StringComparer.Ordinal)
                .ToList();

            // The base name is everything before the first '-': ours carry none, the timestamp two.
            foreach (var group in files.GroupBy(fi => fi.Name[..fi.Name.IndexOf('-')]))
            {
                foreach (var old in group.SkipLast(keep))
                {
                    old.Delete();
                    files.Remove(old);
                    logger.LogInformation("backup rotation deleted {Name}", old.Name);
                }
            }

            long budget = (long)maxTotalMb * 1024 * 1024;
            while (files.Count > 1 && files.Sum(fi => fi.Length) > budget)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                oldest.Delete();
                logger.LogWarning("backup folder exceeded Backup:MaxTotalMB ({MaxMb} MB); deleted {Name}",
                    maxTotalMb, oldest.Name);
            }
        }

        private async Task NotifyAdminsAsync(NotificationType type, string title, string body)
        {
            try
            {
                using var scope = services.CreateScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
                await notifications.CreateForAdminsAsync(type, title, body);
            }
            catch (Exception ex)
            {
                // The failure is already in the log; a broken notification path must not hide it.
                logger.LogError(ex, "could not raise the in-app notification '{Title}'", title);
            }
        }
    }
}
