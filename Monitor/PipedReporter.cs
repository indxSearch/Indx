using System.Globalization;
using System.Text;
using Indx.Api;

namespace IndxServer.Monitor
{
    /// <summary>
    /// The append-only renderer: a compact status block on an interval, plus every transition event
    /// the moment it happens. No cursor addressing and no ANSI, so it is readable in Azure log
    /// stream, <c>docker logs</c> and the systemd journal, none of which are terminals.
    ///
    /// <para>Descended from <c>IndxSoak</c>'s <c>Report()</c>, widened from one engine to every
    /// dataset. The choice of numbers is inherited with it, and so is the reason: heap, private and
    /// working set all move with paging and say nothing on a memory-starved host, while outstanding
    /// native blocks is exact — steady means balanced alloc/free, rising means a leak, and nothing
    /// else moves it.</para>
    /// </summary>
    internal sealed class PipedReporter(TextWriter output, TimeSpan statusInterval) : IMonitorRenderer
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private DateTimeOffset _lastStatus = DateTimeOffset.MinValue;

        public void Render(MonitorSnapshot snapshot)
        {
            foreach (var e in snapshot.NewEvents)
                output.WriteLine($"[indx {Clock(e.AtUtc)}] {e.Source}: {e.Text}");

            if (snapshot.TakenUtc - _lastStatus < statusInterval)
                return;
            _lastStatus = snapshot.TakenUtc;
            output.Write(BuildStatusBlock(snapshot));
            output.Flush();
        }

        public void Stop() => output.Flush();

        private static string Clock(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm:ss", Inv);

        /// <summary>Exposed for tests: the whole block as one string, so it can be asserted on
        /// without a console.</summary>
        internal static string BuildStatusBlock(MonitorSnapshot s)
        {
            var sb = new StringBuilder();
            var p = s.Process;

            sb.Append(Inv, $"[indx {Clock(s.TakenUtc)}] datasets {s.Datasets.Count} ({s.LoadedCount} loaded)  ");
            sb.Append(Inv, $"docs {s.TotalDocuments,12:N0}  heap {p.GcHeapMb,6:N0}MB  ");
            // Process.PrivateMemorySize64 is 0 on Unix, which is where this actually runs in a
            // container. A confident "priv 0MB" is worse than no column, so it is only shown where
            // the platform reports it.
            if (p.PrivateMb > 0)
                sb.Append(Inv, $"priv {p.PrivateMb,7:N0}MB  ");
            sb.Append(Inv, $"ws {p.WorkingSetMb,7:N0}MB  gc {p.Gen0}/{p.Gen1}/{p.Gen2}  ");
            sb.AppendLine(Inv, $"nat {p.NativeBlocks,6:N0} ({p.NativePoolMb:N0}MB pooled)");

            var f = s.Filters;
            // scan should be 0: it is a sequential full-corpus walk with no posting index.
            sb.AppendLine(Inv, $"           filters: srch={f.SearchPathLoads,8:N0} key={f.KeyResolutionLoads,8:N0} " +
                               $"scan={f.RpnScanLoads,6:N0} ({f.RpnScanDocumentsVisited,12:N0} docs walked)  " +
                               $"upkeep field={f.FieldFilterUpdates,7:N0} derived={f.DerivedRecomputes,7:N0} " +
                               $"rebuilds={f.DerivedRebuilds,5:N0}");

            foreach (var d in s.Datasets)
                sb.AppendLine("           " + DescribeDataset(d, s.TakenUtc));

            return sb.ToString();
        }

        private static string DescribeDataset(DatasetLine d, DateTimeOffset now)
        {
            var sb = new StringBuilder();
            sb.Append(Inv, $"{Truncate(d.DataSetName, 20),-20} {Truncate(d.TeamId, 8),-8} {d.Phase,-11}");

            if (d.ProgressPercent is { } percent && d.State is SystemState.Loading or SystemState.Indexing)
            {
                sb.Append(Inv, $" {percent,3}%");
                return sb.ToString();
            }

            sb.Append(Inv, $" {d.DocumentCount,11:N0} docs  disk {d.RecordsOnDisk,11:N0}");

            if (d.LastUsedUtc is { } used && d.State is not null)
                sb.Append(Inv, $"  used {Ago(now - used)}");
            if (d.KeepAliveRemaining is { } remaining)
                sb.Append(Inv, $"  evict in {Ago(remaining)}");
            if (d.SearchCounter > 0)
                sb.Append(Inv, $"  searches {d.SearchCounter:N0}");
            if (d.IndexedTextTruncated)
                sb.Append("  [text truncated]");
            if (d.ErrorMessage is { } error)
                sb.Append(Inv, $"  ERROR {Truncate(error, 60)}");

            return sb.ToString();
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..(max - 1)] + "…";

        /// <summary>Short and absolute — a piped log has no tooltip to hold the exact time, and
        /// the pane is measured in columns.</summary>
        private static string Ago(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalSeconds < 60) return $"{span.TotalSeconds:F0}s";
            if (span.TotalMinutes < 60) return $"{span.TotalMinutes:F0}m";
            if (span.TotalHours < 48) return $"{span.TotalHours:F0}h";
            return $"{span.TotalDays:F0}d";
        }
    }
}
