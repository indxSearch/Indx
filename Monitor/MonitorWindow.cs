using System.Data;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace IndxServer.Monitor
{
    /// <summary>
    /// The monitor screen: an instance line at the top, the datasets, and the event stream.
    ///
    /// <para>It never reads the engine registry itself. The collector runs on the hosted service's
    /// timer and publishes a snapshot; this window picks the latest one up on its own timer, which
    /// is the same arrangement <c>IndxWorkbench</c>'s window has with <c>EngineSession</c> and means
    /// there are no cross-thread calls in either direction.</para>
    /// </summary>
    internal sealed class MonitorWindow : Window
    {
        private readonly IApplication _app;
        private readonly Func<MonitorSnapshot?> _readLatest;
        private readonly Func<IReadOnlyList<MonitorEvent>> _readEvents;

        private readonly Label _header = new();
        private readonly Label _filters = new();
        private readonly TableView _datasets = new();
        private readonly TableView _events = new();

        private DateTimeOffset _renderedAt = DateTimeOffset.MinValue;
        private int _renderedEventCount = -1;

        internal MonitorWindow(IApplication app,
                               Func<MonitorSnapshot?> readLatest,
                               Func<IReadOnlyList<MonitorEvent>> readEvents)
        {
            _app = app;
            _readLatest = readLatest;
            _readEvents = readEvents;

            Title = "indx monitor";

            _header.X = 1; _header.Y = 0; _header.Width = Dim.Fill(1); _header.Height = 1;
            _filters.X = 1; _filters.Y = 1; _filters.Width = Dim.Fill(1); _filters.Height = 1;
            // Labels treat '_' as a hotkey marker and swallow it, so text that shows data must set
            // a specifier that cannot occur. Dataset and team names can contain underscores.
            _header.HotKeySpecifier = NoHotKey;
            _filters.HotKeySpecifier = NoHotKey;

            var datasetsFrame = new FrameView
            {
                Title = "Datasets",
                X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Percent(55),
            };
            _datasets.X = 0; _datasets.Y = 0; _datasets.Width = Dim.Fill(); _datasets.Height = Dim.Fill();
            _datasets.FullRowSelect = true;
            _datasets.Style.ShowHorizontalHeaderOverline = false;
            _datasets.Style.ExpandLastColumn = true;
            datasetsFrame.Add(_datasets);

            var eventsFrame = new FrameView
            {
                Title = "Events",
                X = 0, Y = Pos.Bottom(datasetsFrame), Width = Dim.Fill(), Height = Dim.Fill(1),
            };
            _events.X = 0; _events.Y = 0; _events.Width = Dim.Fill(); _events.Height = Dim.Fill();
            _events.FullRowSelect = true;
            _events.Style.ShowHorizontalHeaderOverline = false;
            _events.Style.ExpandLastColumn = true;
            eventsFrame.Add(_events);

            var status = new StatusBar(
            [
                // Detach, not quit: Ctrl+C still stops the server, and an operator who wants the
                // console back must not have to kill the process to get it.
                new Shortcut(Key.Q.WithCtrl, "Detach monitor", () => _app.RequestStop()),
            ]);

            Add(_header, _filters, datasetsFrame, eventsFrame, status);

            Refresh();
            _app.AddTimeout(TimeSpan.FromMilliseconds(250), () => { Refresh(); return true; });
        }

        private static readonly Rune NoHotKey = new('￿');

        private void Refresh()
        {
            var snapshot = _readLatest();
            if (snapshot is null)
            {
                _header.Text = "waiting for the first snapshot …";
                return;
            }

            var events = _readEvents();
            if (snapshot.TakenUtc == _renderedAt && events.Count == _renderedEventCount)
                return;
            _renderedAt = snapshot.TakenUtc;
            _renderedEventCount = events.Count;

            ShowHeader(snapshot);
            ShowDatasets(snapshot);
            ShowEvents(events);
        }

        private void ShowHeader(MonitorSnapshot s)
        {
            var p = s.Process;
            // PrivateMemorySize64 is 0 on Unix, where this runs in a container; showing a
            // confident 0MB would be worse than leaving the column out.
            string priv = p.PrivateMb > 0 ? $"  priv {p.PrivateMb:N0}MB" : "";
            _header.Text =
                $"{s.TakenUtc.UtcDateTime:HH:mm:ss}   datasets {s.Datasets.Count} ({s.LoadedCount} loaded)   " +
                $"docs {s.TotalDocuments:N0}   heap {p.GcHeapMb:N0}MB{priv}   ws {p.WorkingSetMb:N0}MB   " +
                $"gc {p.Gen0}/{p.Gen1}/{p.Gen2}   native {p.NativeBlocks:N0} blk";

            var f = s.Filters;
            // scan is the alarm: a sequential full-corpus walk with no posting index.
            _filters.Text =
                $"filters  srch {f.SearchPathLoads:N0}   key {f.KeyResolutionLoads:N0}   " +
                $"scan {f.RpnScanLoads:N0}   upkeep field {f.FieldFilterUpdates:N0} / " +
                $"derived {f.DerivedRecomputes:N0} / rebuilds {f.DerivedRebuilds:N0}";
        }

        private void ShowDatasets(MonitorSnapshot s)
        {
            var table = new DataTable();
            table.Columns.Add(" ");
            table.Columns.Add("dataset");
            table.Columns.Add("team");
            table.Columns.Add("state");
            table.Columns.Add("docs");
            table.Columns.Add("on disk");
            table.Columns.Add("used");
            table.Columns.Add("note");

            foreach (var d in s.Datasets)
            {
                string note = d.ProgressPercent is { } percent ? $"{percent}%" : "";
                if (d.ErrorMessage is { } error) note = error.Length > 48 ? error[..47] + "…" : error;
                else if (d.IndexedTextTruncated) note = "text truncated";
                else if (d.KeepAliveRemaining is { } remaining) note = $"evict in {Short(remaining)}";

                table.Rows.Add(
                    Glyph(d),
                    d.DataSetName,
                    d.TeamId.Length > 8 ? d.TeamId[..8] : d.TeamId,
                    d.Phase,
                    d.DocumentCount > 0 ? d.DocumentCount.ToString("N0") : "",
                    d.RecordsOnDisk > 0 ? d.RecordsOnDisk.ToString("N0") : "",
                    d.LastUsedUtc is { } used && d.State is not null ? Short(s.TakenUtc - used) : "",
                    note);
            }

            _datasets.Table = new DataTableSource(table);
        }

        /// <summary>
        /// One character per state, matching the dataset chips in the console: filled when it can
        /// serve, half when something is running, hollow when it is asleep.
        /// </summary>
        private static string Glyph(DatasetLine d) =>
            d.ErrorMessage is not null ? "✖"
            : d.Ready ? "●"
            : d.Hibernated ? "○"
            : d.ProgressPercent is not null ? "◐"
            : "·";

        private void ShowEvents(IReadOnlyList<MonitorEvent> events)
        {
            var table = new DataTable();
            table.Columns.Add("time");
            table.Columns.Add("source");
            table.Columns.Add("what");

            // Newest first: the pane is short and the interesting line is the one that just landed.
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                table.Rows.Add(e.AtUtc.UtcDateTime.ToString("HH:mm:ss"), e.Source, e.Text);
            }

            _events.Table = new DataTableSource(table);
        }

        private static string Short(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalSeconds < 60) return $"{span.TotalSeconds:F0}s";
            if (span.TotalMinutes < 60) return $"{span.TotalMinutes:F0}m";
            if (span.TotalHours < 48) return $"{span.TotalHours:F0}h";
            return $"{span.TotalDays:F0}d";
        }
    }
}
