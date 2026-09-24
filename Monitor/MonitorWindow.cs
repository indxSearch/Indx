using System.Data;
using System.Text;
using Indx.Api;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;

namespace IndxServer.Monitor
{
    /// <summary>
    /// The monitor screen: the instance across the header, the datasets, and the event stream.
    /// The divider between the two panes can be dragged, and where it ends up is remembered.
    ///
    /// <para>It never reads the engine registry itself. The collector runs on the hosted service's
    /// timer and publishes a snapshot; this window picks the latest one up on its own timer, which
    /// is the same arrangement <c>IndxWorkbench</c>'s window has with <c>EngineSession</c> and means
    /// there are no cross-thread calls in either direction.</para>
    /// </summary>
    internal sealed class MonitorWindow : Window
    {
        // The workbench palette, which is the console's: the same colour and the same icon per
        // state as the dataset chips, so a dataset looks the same in the browser and here.
        private static readonly Color Teal = new(0x72, 0xF5, 0xC6), Warn = new(0xFF, 0xC1, 0x07),
                                      Signal = new(0xFF, 0x42, 0x38), LightBlue = new(0x6B, 0x9E, 0xFF),
                                      Ink = new(0x12, 0x12, 0x15), Stone = new(0xCF, 0xCF, 0xCF),
                                      Slate = new(0x4A, 0x4A, 0x50);

        private static readonly Rune NoHotKey = (Rune)0xFFFF;

        /// <summary>Rows taken by the header: air, three rows of logo, air.</summary>
        private const int HeaderHeight = 5;
        /// <summary>Below these a pane stops being worth having.</summary>
        private const int MinDatasets = 4, MinEvents = 3;

        private readonly IApplication _app;
        private readonly Func<MonitorSnapshot?> _readLatest;
        private readonly Func<IReadOnlyList<MonitorEvent>> _readEvents;

        private readonly Label _header = new();
        private readonly Label _filters = new();
        private readonly PixlIconView _stateIcon = new();
        private readonly TableView _datasets = new();
        private readonly TableView _events = new();
        private readonly FrameView _datasetsFrame;

        private readonly MonitorLayout _layout = MonitorLayout.Load();
        private bool _dragged;

        private IReadOnlyList<DatasetLine> _rows = [];
        private DateTimeOffset _renderedAt = DateTimeOffset.MinValue;
        private int _renderedEventCount = -1;
        private string _renderedDatasets = "";

        internal MonitorWindow(IApplication app,
                               Func<MonitorSnapshot?> readLatest,
                               Func<IReadOnlyList<MonitorEvent>> readEvents)
        {
            _app = app;
            _readLatest = readLatest;
            _readEvents = readEvents;

            Title = "indx monitor";

            // ── Header ──
            // One row of air above the logo and three columns to its left; the state icon mirrors
            // it on the right, as in the workbench.
            var logo = new PixlIconView { X = 3, Y = 1, Icon = "IndxLogo" };
            int textLeft = 3 + PixlIcon.Width + 3;
            var heading = new Label { X = textLeft, Y = 1, Text = "indx monitor", HotKeySpecifier = NoHotKey };
            _header.X = textLeft; _header.Y = 2; _header.Width = Dim.Fill(PixlIcon.Width + 5); _header.Height = 1;
            _filters.X = textLeft; _filters.Y = 3; _filters.Width = Dim.Fill(PixlIcon.Width + 5); _filters.Height = 1;
            // Labels swallow '_' as a hotkey marker, and these show data.
            _header.HotKeySpecifier = NoHotKey;
            _filters.HotKeySpecifier = NoHotKey;
            _stateIcon.X = Pos.AnchorEnd(PixlIcon.Width + 3); _stateIcon.Y = 1;

            // ── Datasets ──
            // BottomResizable is the divider: dragging it sets an absolute height, which
            // TakeDraggedHeight turns into the remembered one.
            _datasetsFrame = new FrameView
            {
                Title = "Datasets",
                X = 0, Y = HeaderHeight, Width = Dim.Fill(),
                Height = Dim.Func(_ => DatasetsHeight()),
                Arrangement = ViewArrangement.BottomResizable,
            };
            _datasets.X = 0; _datasets.Y = 0; _datasets.Width = Dim.Fill(); _datasets.Height = Dim.Fill();
            _datasets.FullRowSelect = true;
            _datasets.Style.ShowHorizontalHeaderOverline = false;
            _datasets.Style.ShowVerticalCellLines = false;
            _datasets.Style.ExpandLastColumn = true;
            // A whole row in its state's colour: the table is read by scanning down it, and a
            // coloured word in one column is easy to miss at a glance.
            _datasets.Style.RowColorGetter = args => RowScheme(args.RowIndex);
            _datasetsFrame.Add(_datasets);

            // ── Events ──
            var eventsFrame = new FrameView
            {
                Title = "Events",
                X = 0, Y = Pos.Bottom(_datasetsFrame), Width = Dim.Fill(), Height = Dim.Fill(1),
            };
            _events.X = 0; _events.Y = 0; _events.Width = Dim.Fill(); _events.Height = Dim.Fill();
            _events.FullRowSelect = true;
            _events.Style.ShowHorizontalHeaderOverline = false;
            _events.Style.ShowVerticalCellLines = false;
            _events.Style.ExpandLastColumn = true;
            eventsFrame.Add(_events);

            var status = new StatusBar(
            [
                // Detach, not quit: Ctrl+C still stops the server, and an operator who wants the
                // console back must not have to kill the process to get it.
                new Shortcut(Key.Q.WithCtrl, "Detach monitor", () => _app.RequestStop()),
            ]);

            foreach (var table in new[] { _datasets, _events }) ShowSelection(table);

            Add(logo, heading, _header, _filters, _stateIcon, _datasetsFrame, eventsFrame, status);
            _datasetsFrame.FrameChanged += (_, _) => TakeDraggedHeight();

            Refresh();
            _app.AddTimeout(TimeSpan.FromMilliseconds(250), () => { TakeDraggedHeight(); Refresh(); return true; });
        }

        /// <summary>
        /// The selected row, as a grey block with dark text. Stated as colours rather than taken
        /// from the scheme, which paints it white: on a terminal with a light background that is a
        /// row you cannot see.
        /// </summary>
        private static void ShowSelection(TableView table)
        {
            var scheme = table.GetScheme();
            table.SetScheme(new Scheme(scheme)
            {
                Focus = new Attribute(Ink, Stone),
                Active = new Attribute(Stone, Slate),
            });

            var heading = new Attribute(scheme.Normal.Foreground, scheme.Normal.Background, TextStyle.Bold);
            table.Style.HeaderScheme = new Scheme(heading)
            {
                Normal = heading, Focus = heading, Active = heading,
                HotNormal = heading, HotFocus = heading, HotActive = heading,
            };
        }

        // ── The draggable divider ────────────────────────────────────────────

        /// <summary>The height Datasets gets: what was dragged, or half the room, kept within what
        /// leaves Events its minimum. Clamping on every read is what makes a dragged height meet a
        /// smaller terminal gracefully and come back when it grows again.</summary>
        private int DatasetsHeight()
        {
            int available = Math.Max(MinDatasets + MinEvents, Viewport.Height - HeaderHeight - 1);
            int want = _layout.DatasetsHeight ?? available / 2;
            return Math.Clamp(want, MinDatasets, available - MinEvents);
        }

        /// <summary>A drag sets an absolute height. That number becomes what the user asked for,
        /// and the pane goes back to the calculated height. Called from the timer too, because the
        /// height a drag ends on does not always come with a frame change.</summary>
        private void TakeDraggedHeight()
        {
            if (_datasetsFrame.Height is DimAbsolute h)
            {
                int available = Math.Max(MinDatasets + MinEvents, Viewport.Height - HeaderHeight - 1);
                _layout.DatasetsHeight = Math.Clamp(h.Size, MinDatasets, available - MinEvents);
                _datasetsFrame.Height = Dim.Func(_ => DatasetsHeight());
                _dragged = true;
            }
            // A Func height does not tell the pane below it that its answer changed.
            if (_dragged) { SetNeedsLayout(); SetNeedsDraw(); }
        }

        /// <summary>Only once something was dragged: a pane on its built-in split keeps following
        /// the terminal next time.</summary>
        internal void SaveLayout()
        {
            if (_dragged) _layout.Save();
        }

        // ── Drawing ──────────────────────────────────────────────────────────

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

            // The header carries the clock and the counters, so it changes on every tick.
            ShowHeader(snapshot);

            // The tables do not. Rebuilding one replaces its source, which costs a redraw and
            // takes the selection with it, so each is rebuilt only when its content actually
            // differs -- which for datasets is rarely, and that is what lets a row stay selected.
            var (table, signature) = BuildDatasetTable(snapshot);
            if (signature != _renderedDatasets)
            {
                _renderedDatasets = signature;
                _rows = snapshot.Datasets;
                _datasets.Table = new DataTableSource(table);
                _datasets.SetNeedsDraw();
            }

            if (events.Count != _renderedEventCount)
            {
                _renderedEventCount = events.Count;
                ShowEvents(events);
                _events.SetNeedsDraw();
            }

            // Assigning TableView.Table does not itself ask for a repaint, and neither pane is
            // otherwise redrawn until a key or the mouse forces it -- which looked like a monitor
            // that refreshed every few seconds at random.
            _header.SetNeedsDraw();
            _filters.SetNeedsDraw();
            _stateIcon.SetNeedsDraw();
        }

        private void ShowHeader(MonitorSnapshot s)
        {
            var p = s.Process;
            // PrivateMemorySize64 is 0 on Unix, where this runs in a container; a confident 0MB
            // would be worse than leaving the column out.
            string priv = p.PrivateMb > 0 ? $"   priv {p.PrivateMb:N0}MB" : "";
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

            // The header icon reports the instance, so it shows the most notable dataset: an
            // error outranks work in progress, which outranks serving.
            var (icon, color) = Worst(s.Datasets);
            _stateIcon.Icon = icon;
            _stateIcon.Color = color;
        }

        /// <summary>The same icon and colour per state as the console's dataset chips.</summary>
        private static (string Icon, Color Color) Appearance(DatasetLine d) =>
            d.ErrorMessage is not null || d.State == SystemState.Error ? ("Warning", Signal)
            : d.State == SystemState.Loading ? ("Trolley", Warn)
            : d.State == SystemState.Indexing ? ("HourGlass", Warn)
            : d.Ready ? ("Flag", Teal)
            : d.Hibernated ? ("Empty", LightBlue)
            : ("Empty", Slate);

        private static (string Icon, Color Color) Worst(IReadOnlyList<DatasetLine> datasets)
        {
            if (datasets.Any(d => d.ErrorMessage is not null || d.State == SystemState.Error))
                return ("Warning", Signal);
            if (datasets.Any(d => d.State is SystemState.Loading or SystemState.Indexing))
                return ("HourGlass", Warn);
            if (datasets.Any(d => d.Ready))
                return ("Flag", Teal);
            return ("Empty", LightBlue);
        }

        private Scheme? RowScheme(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count)
                return null;
            var color = Appearance(_rows[rowIndex]).Color;
            var normal = new Attribute(color, GetScheme().Normal.Background);
            return new Scheme(normal)
            {
                Normal = normal,
                // The selection stays readable: a grey block with dark text, as elsewhere.
                Focus = new Attribute(Ink, Stone),
                Active = new Attribute(Stone, Slate),
            };
        }

        /// <summary>
        /// The dataset table, and a signature of exactly what it would show. Taking the signature
        /// from the rendered cells rather than from the data means it changes when and only when
        /// the screen would differ -- including the "used" column ticking through its seconds, and
        /// not including the parts of a timestamp that never reach a column.
        /// </summary>
        internal static (DataTable Table, string Signature) BuildDatasetTable(MonitorSnapshot s)
        {
            var table = new DataTable();
            table.Columns.Add("dataset");
            table.Columns.Add("team");
            table.Columns.Add("state");
            table.Columns.Add("docs");
            table.Columns.Add("on disk");
            table.Columns.Add("used");
            table.Columns.Add("note");

            var signature = new StringBuilder();
            foreach (var d in s.Datasets)
            {
                string note = d.ProgressPercent is { } percent ? $"{percent}%" : "";
                if (d.ErrorMessage is { } error) note = error.Length > 48 ? error[..47] + "…" : error;
                else if (d.IndexedTextTruncated) note = "text truncated";
                else if (d.KeepAliveRemaining is { } remaining) note = $"evict in {Short(remaining)}";

                object[] cells =
                [
                    d.DataSetName,
                    d.TeamId.Length > 8 ? d.TeamId[..8] : d.TeamId,
                    d.Phase,
                    d.DocumentCount > 0 ? d.DocumentCount.ToString("N0") : "",
                    d.RecordsOnDisk > 0 ? d.RecordsOnDisk.ToString("N0") : "",
                    d.LastUsedUtc is { } used && d.State is not null ? Short(s.TakenUtc - used) : "",
                    note,
                ];
                table.Rows.Add(cells);

                foreach (var cell in cells) signature.Append(cell).Append('\u0001');
                // The row colour is not a cell, and a dataset can change colour without any
                // column changing -- Ready to Error keeps its counts.
                signature.Append(Appearance(d).Icon).Append('\u0002');
            }

            return (table, signature.ToString());
        }

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
