using System.Data;
using System.Globalization;
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
        /// <summary>Invariant throughout, so a figure looks the same in the terminal, in a piped
        /// log and in a screenshot someone pastes into an issue.</summary>
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Rows taken by the header: air, three rows of logo, air.</summary>
        private const int HeaderHeight = 5;
        /// <summary>Below these a pane stops being worth having.</summary>
        private const int MinDatasets = 4, MinEvents = 3;

        private readonly IApplication _app;
        private readonly Func<MonitorSnapshot?> _readLatest;
        private readonly Func<IReadOnlyList<MonitorEvent>> _readEvents;
        private readonly Action _requestShutdown;

        private readonly Label _header = new();
        private readonly Label _filters = new();
        private readonly PixlIconView _stateIcon = new();
        private readonly TableView _datasets = new();
        private readonly TableView _events = new();
        private readonly FrameView _datasetsFrame;

        private readonly MonitorLayout _layout = MonitorLayout.Load();
        private bool _dragged;
        private bool _initialised;
        private int _appliedHeight = -1;

        private IReadOnlyList<DatasetLine> _rows = [];
        private DateTimeOffset _renderedAt = DateTimeOffset.MinValue;
        private int _renderedEventCount = -1;
        private string _renderedDatasets = "";

        internal MonitorWindow(IApplication app,
                               Func<MonitorSnapshot?> readLatest,
                               Func<IReadOnlyList<MonitorEvent>> readEvents,
                               Action requestShutdown,
                               MonitorEndpoints? endpoints = null)
        {
            _app = app;
            _readLatest = readLatest;
            _readEvents = readEvents;
            _requestShutdown = requestShutdown;

            Title = "indx monitor";

            // ── Header ──
            // One row of air above the logo and three columns to its left; the state icon mirrors
            // it on the right, as in the workbench.
            var logo = new PixlIconView { X = 3, Y = 1, Icon = "IndxLogo" };
            int textLeft = 3 + PixlIcon.Width + 3;
            var heading = new Label { X = textLeft, Y = 1, Text = "indx monitor", HotKeySpecifier = NoHotKey };

            // The two figure lines sit under the heading and stop short of the state icon.
            _header.X = textLeft; _header.Y = 2; _header.Width = Dim.Fill(PixlIcon.Width + 5); _header.Height = 1;
            _filters.X = textLeft; _filters.Y = 3; _filters.Width = Dim.Fill(PixlIcon.Width + 5); _filters.Height = 1;
            // Labels swallow '_' as a hotkey marker, and these show data.
            _header.HotKeySpecifier = NoHotKey;
            _filters.HotKeySpecifier = NoHotKey;
            // Mirrors the logo: three columns in from the right edge, level with it.
            _stateIcon.X = Pos.AnchorEnd(PixlIcon.Width + 3); _stateIcon.Y = 1;

            // ── Datasets ──
            // BottomResizable is the divider: dragging it sets an absolute height, which
            // TakeDraggedHeight turns into the remembered one.
            _datasetsFrame = new FrameView
            {
                Title = "Datasets",
                X = 0, Y = HeaderHeight, Width = Dim.Fill(),
                // An absolute height, not a Dim.Func: a drag sets an absolute height too, and
                // anything that rewrites Height on a timer is fighting the drag for it.
                Height = Dim.Absolute(_layout.DatasetsHeight ?? 10),
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
                X = 0, Y = Pos.Bottom(_datasetsFrame), Width = Dim.Fill(), Height = Dim.Fill(2),
            };
            _events.X = 0; _events.Y = 0; _events.Width = Dim.Fill(); _events.Height = Dim.Fill();
            _events.FullRowSelect = true;
            _events.Style.ShowHorizontalHeaderOverline = false;
            _events.Style.ShowVerticalCellLines = false;
            _events.Style.ExpandLastColumn = true;
            eventsFrame.Add(_events);

            // Where to point a browser and an agent, on their own row above the status bar and
            // right-aligned, away from the figures. The startup banner prints the address and this
            // screen then covers the banner with the alternate buffer, so without this the one
            // thing a developer needs on first run is gone the moment the monitor appears.
            var reachable = new Label
            {
                X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(2), Height = 1,
                TextAlignment = Alignment.End,
                HotKeySpecifier = NoHotKey,
                Text = Describe(endpoints),
            };

            var status = new StatusBar(
            [
                // Detach, not quit: Ctrl+C still stops the server, and an operator who wants the
                // console back must not have to kill the process to get it.
                new Shortcut(Key.Q.WithCtrl, "Detach monitor", () => _app.RequestStop()),
                new Shortcut(Key.F10, "Shut down server", ConfirmShutdown),
            ]);

            foreach (var table in new[] { _datasets, _events }) ShowSelection(table);

            Add(logo, heading, _header, _filters, _stateIcon, _datasetsFrame, eventsFrame, reachable, status);
            _datasetsFrame.FrameChanged += (_, _) => KeepDividerInBounds();

            Refresh();
            _app.AddTimeout(TimeSpan.FromMilliseconds(250), () => { KeepDividerInBounds(); Refresh(); return true; });
        }

        /// <summary>
        /// The endpoints line: where to point a browser, and nothing else. MCP was here too and is
        /// not any more — an agent's address is not what a developer needs on first run, and the
        /// live view of what an agent is doing belongs in the web console rather than a line here.
        /// </summary>
        internal static string Describe(MonitorEndpoints? endpoints)
            => endpoints?.Web is { Length: > 0 } web ? $"web  {web}" : "";

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

        /// <summary>
        /// Keeps the divider inside what leaves both panes a usable height, and notices where a
        /// drag left it.
        ///
        /// <para>The first version rewrote <c>Height</c> to a <c>Dim.Func</c> on every tick, the
        /// way the workbench does for its vertical border. That is why the divider showed a drag
        /// cursor and would not move: a drag sets an absolute height, and a quarter of a second
        /// later this put the calculated one back. Now <c>Height</c> is only ever written when the
        /// value is genuinely out of bounds — a terminal that got shorter — so a drag is left
        /// alone and simply read afterwards.</para>
        /// </summary>
        private void KeepDividerInBounds()
        {
            if (_datasetsFrame.Height is not DimAbsolute current)
                return;

            int available = Viewport.Height - HeaderHeight - 1;   // the status bar keeps a row
            if (available < MinDatasets + MinEvents)
                return;   // too small to split sensibly; leave whatever is there

            // The built-in split, once the terminal size is actually known. Doing this at
            // construction would divide a viewport that is still zero.
            if (!_initialised)
            {
                _initialised = true;
                if (_layout.DatasetsHeight is null)
                {
                    Apply(available / 2);
                    return;
                }
            }

            int clamped = Math.Clamp(current.Size, MinDatasets, available - MinEvents);
            if (clamped != current.Size)
            {
                Apply(clamped);
                return;
            }

            // Anything we did not put there ourselves came from a drag.
            if (current.Size != _appliedHeight)
            {
                _appliedHeight = current.Size;
                _layout.DatasetsHeight = current.Size;
                _dragged = true;
            }

            void Apply(int height)
            {
                _appliedHeight = height;
                _datasetsFrame.Height = Dim.Absolute(height);
                SetNeedsLayout();
                SetNeedsDraw();
            }
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
                // Which dataset was selected, by key rather than by row: the table is replaced
                // roughly every second (the "used" column counts in seconds), and a row index
                // would not survive a dataset being created or deleted either.
                string? selected = SelectedDatasetKey();
                _rows = snapshot.Datasets;
                _datasets.Table = new DataTableSource(table);
                RestoreDatasetSelection(selected);
                _datasets.SetNeedsDraw();
            }

            if (events.Count != _renderedEventCount)
            {
                // Events render newest first, so N new ones push the selected line N rows down.
                // Shifting by that keeps the eye on the line it was reading. The buffer drops
                // its oldest when full, which is the bottom of this view and moves nothing above.
                int row = ShiftEventSelection(_events.Value?.SelectedCell.Y ?? -1,
                                              _renderedEventCount, events.Count);
                _renderedEventCount = events.Count;
                ShowEvents(events);
                if (row >= 0) SelectRow(_events, row, events.Count);
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
            string priv = p.PrivateMb > 0 ? string.Create(Inv, $"   priv {p.PrivateMb:N0}MB") : "";
            _header.Text =
                string.Create(Inv, $"{s.TakenUtc.UtcDateTime:HH:mm:ss}   datasets {s.Datasets.Count} ({s.LoadedCount} loaded)   ") +
                string.Create(Inv, $"docs {s.TotalDocuments:N0}   {s.DescribeSearches()}   ") +
                string.Create(Inv, $"heap {p.GcHeapMb:N0}MB{priv}   ws {p.WorkingSetMb:N0}MB   ") +
                string.Create(Inv, $"gc {p.Gen0}/{p.Gen1}/{p.Gen2}   native {p.NativeBlocks:N0} blk");

            _filters.Text = s.Filters.Describe();

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
                string note = d.ProgressPercent is { } percent ? string.Create(Inv, $"{percent}%") : "";
                if (d.ErrorMessage is { } error) note = error.Length > 48 ? error[..47] + "…" : error;
                else if (d.IndexedTextTruncated) note = "text truncated";
                else if (d.FieldIndexFallback) note = "filters scanning";
                else if (d.KeepAliveRemaining is { } remaining) note = $"evict in {Short(remaining)}";

                object[] cells =
                [
                    d.DataSetName,
                    d.TeamLabel,
                    d.Phase,
                    d.DocumentCount > 0 ? d.DocumentCount.ToString("N0", Inv) : "",
                    d.RecordsOnDisk > 0 ? d.RecordsOnDisk.ToString("N0", Inv) : "",
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

        /// <summary>
        /// Where a selected event line moves to when new events arrive. They render newest first,
        /// so N new ones push the selection N rows down and the eye stays on the line it was
        /// reading. The buffer drops its oldest when full, which is the bottom of this view and
        /// moves nothing above it. Returns -1 when there is nothing to move.
        /// </summary>
        internal static int ShiftEventSelection(int selectedRow, int previousCount, int newCount)
        {
            if (selectedRow < 0 || previousCount < 0) return -1;   // nothing selected, or first draw
            int arrived = newCount - previousCount;
            if (arrived <= 0) return -1;                            // only the cap dropping old ones
            return Math.Clamp(selectedRow + arrived, 0, Math.Max(0, newCount - 1));
        }

        private string? SelectedDatasetKey()
        {
            int row = _datasets.Value?.SelectedCell.Y ?? -1;
            return row >= 0 && row < _rows.Count ? _rows[row].Key : null;
        }

        private void RestoreDatasetSelection(string? key)
        {
            if (key is null) return;
            int row = -1;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Key == key) { row = i; break; }
            if (row >= 0) SelectRow(_datasets, row, _rows.Count);
        }

        private static void SelectRow(TableView table, int row, int rowCount)
        {
            if (rowCount <= 0) return;
            row = Math.Clamp(row, 0, rowCount - 1);
            if (table.Value?.SelectedCell.Y == row) return;
            int column = table.Value?.SelectedCell.X ?? 0;
            table.Value = new TableSelection(new System.Drawing.Point(column, row));
        }

        /// <summary>
        /// The one action the monitor offers, and it stops the server, so it asks first and Cancel
        /// is what Enter does. Detaching the screen (Ctrl+Q) and stopping the process are two very
        /// different things and the wording has to keep them apart.
        /// </summary>
        private void ConfirmShutdown()
        {
            var dialog = new Dialog { Title = "Shut down server", Width = 62, Height = 10 };

            string[] lines =
            [
                "Stop this Indx server?",
                "",
                "Loaded datasets are dropped from memory and any search in",
                "flight is cut off. Data on disk is kept, and the datasets",
                "load again on the next start.",
            ];
            for (int i = 0; i < lines.Length; i++)
                dialog.Add(new Label { X = 2, Y = 1 + i, Text = lines[i], HotKeySpecifier = NoHotKey });

            var cancel = new Button { Text = "Cancel", IsDefault = true };
            cancel.Accepting += (_, e) => { e.Handled = true; _app.RequestStop(dialog); };

            var confirm = new Button { Text = "Shut down" };
            bool confirmed = false;
            confirm.Accepting += (_, e) => { e.Handled = true; confirmed = true; _app.RequestStop(dialog); };

            dialog.AddButton(cancel);
            dialog.AddButton(confirm);
            _app.Run(dialog);
            dialog.Dispose();

            if (!confirmed)
                return;

            // Bring the screen down HERE, from the UI thread and after the dialog's own loop has
            // unwound, and only then stop the host. Two things depend on that order.
            //
            // Stopping the host first, and letting it cancel the monitor's token, asks the screen
            // to stop from another thread. If the process got far enough down its shutdown before
            // that landed, Terminal.Gui never restored the terminal: mouse reporting stayed on and
            // the shell printed raw SGR sequences as gibberish ("35;4;44M35;14;43M...") at
            // whoever used it next.
            //
            // And this runs after Run(dialog) returns rather than inside the handler, so the stop
            // reaches the window. Called from inside, it would be a second RequestStop while the
            // dialog is still the running top, and might only close the dialog again.
            _app.RequestStop();
            _requestShutdown();
        }

        private static string Short(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalSeconds < 60) return string.Create(Inv, $"{span.TotalSeconds:F0}s");
            if (span.TotalMinutes < 60) return string.Create(Inv, $"{span.TotalMinutes:F0}m");
            if (span.TotalHours < 48) return string.Create(Inv, $"{span.TotalHours:F0}h");
            return string.Create(Inv, $"{span.TotalDays:F0}d");
        }
    }
}
