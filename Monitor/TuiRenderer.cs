using Terminal.Gui.App;

namespace IndxServer.Monitor
{
    /// <summary>
    /// Runs the Terminal.Gui screen on its own thread and publishes snapshots to it.
    ///
    /// <para><c>Application.Run</c> blocks for the life of the screen, so it cannot run on the
    /// hosted service's loop. <see cref="Render"/> therefore only stores the newest snapshot and
    /// appends any new events; the window picks both up on its own timer. Nothing is marshalled
    /// between the two threads beyond a volatile reference and one lock around the event list.</para>
    ///
    /// <para>If the terminal turns out not to support a full-screen application, this falls back to
    /// the piped renderer rather than failing: the server was started to serve, not to draw.</para>
    /// </summary>
    internal sealed class TuiRenderer(TimeSpan fallbackStatusInterval,
                                     MonitorLoggerProvider? logProvider = null,
                                     Action? requestShutdown = null)
        : IMonitorRenderer, IMonitorLogSink
    {
        private const int MaxEvents = 500;

        private readonly Lock _eventLock = new();
        private readonly List<MonitorEvent> _events = [];
        private MonitorSnapshot? _latest;

        private IApplication? _app;
        private Thread? _thread;
        private volatile bool _stopped;
        private IMonitorRenderer? _fallback;

        private MonitorEndpoints _endpoints = new(null, null);

        public void Start(MonitorEndpoints endpoints)
        {
            _endpoints = endpoints;
            _thread = new Thread(RunScreen)
            {
                IsBackground = true,     // never hold the process open after the host has stopped
                Name = "indx-monitor-ui",
            };
            _thread.Start();
        }

        private void RunScreen()
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                using IApplication app = Application.Create().Init();
                _app = app;
                using var window = new MonitorWindow(app, ReadLatest, ReadEvents,
                                                    requestShutdown ?? (() => { }), _endpoints);
                // Only now does the console belong to the screen. Until this point log lines --
                // including whatever went wrong during startup -- print normally.
                logProvider?.AttachTo(this);
                app.Run(window);
                window.SaveLayout();
            }
            catch (Exception ex)
            {
                // The screen is gone; say why once, in plain text, and carry on without it.
                _app = null;
                if (!_stopped)
                {
                    Console.Error.WriteLine($"[indx] terminal monitor unavailable ({ex.GetType().Name}: {ex.Message});"
                                            + " falling back to periodic status lines.");
                    Volatile.Write(ref _fallback, new PipedReporter(Console.Out, fallbackStatusInterval));
                }
            }
            finally
            {
                // Hand the console back before shutdown, so the host's stopping messages are seen.
                logProvider?.Detach();
                _app = null;
            }
        }

        public void Render(MonitorSnapshot snapshot)
        {
            if (Volatile.Read(ref _fallback) is { } fallback)
            {
                fallback.Render(snapshot);
                return;
            }

            if (snapshot.NewEvents.Count > 0)
            {
                lock (_eventLock)
                {
                    _events.AddRange(snapshot.NewEvents);
                    if (_events.Count > MaxEvents)
                        _events.RemoveRange(0, _events.Count - MaxEvents);
                }
            }

            Volatile.Write(ref _latest, snapshot);
        }

        public void Stop()
        {
            _stopped = true;
            // Ctrl+C and a host stopping for any other reason arrive here, on a thread that does
            // not own the screen. Terminal.Gui's own queue is the safe way across: Invoke runs the
            // stop on the UI thread, the same place F10 does it from.
            //
            // Then wait, and wait properly. A screen that has not come down has not restored the
            // terminal either, and a shell left in mouse-reporting mode prints raw escape
            // sequences at whoever used it next -- worth several seconds of shutdown to avoid.
            try { _app?.Invoke(() => _app?.RequestStop()); }
            catch { /* the screen is already down */ }

            if (_thread is { } thread && !thread.Join(TimeSpan.FromSeconds(5)))
                RestoreTerminal();
        }

        /// <summary>A log line, once the screen owns the console.</summary>
        public void Add(MonitorEvent entry)
        {
            lock (_eventLock)
            {
                _events.Add(entry);
                if (_events.Count > MaxEvents)
                    _events.RemoveRange(0, _events.Count - MaxEvents);
            }
        }

        /// <summary>
        /// Last resort, for a screen that would not come down: put the terminal back by hand.
        ///
        /// <para>Terminal.Gui does this on dispose and this should never run. It exists because
        /// the failure is so unpleasant and so confusing: the shell keeps mouse reporting on and
        /// prints every click as raw text, with no hint of where it came from, long after the
        /// server has gone. These four are the modes Terminal.Gui turns on; disabling one that is
        /// already off does nothing.</para>
        /// </summary>
        private static void RestoreTerminal()
        {
            try
            {
                Console.Out.Write("\u001b[?1003l");   // no any-event mouse tracking
                Console.Out.Write("\u001b[?1006l");   // no SGR extended coordinates
                Console.Out.Write("\u001b[?1000l");   // no button tracking
                Console.Out.Write("\u001b[?1049l");   // leave the alternate screen buffer
                Console.Out.Write("\u001b[?25h");     // show the cursor
                Console.Out.Flush();
            }
            catch { /* nothing left to write to */ }
        }

        private MonitorSnapshot? ReadLatest() => Volatile.Read(ref _latest);

        /// <summary>Internal so the buffering can be tested without a terminal.</summary>
        internal IReadOnlyList<MonitorEvent> ReadEvents()
        {
            lock (_eventLock)
                return _events.ToArray();
        }
    }
}
