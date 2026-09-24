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

        public void Start()
        {
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
                                                    requestShutdown ?? (() => { }));
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
            // Asking from another thread, then giving up rather than waiting: the screen thread is
            // a background one, so a screen that does not come down in time dies with the process
            // and cannot hold shutdown open.
            try { _app?.RequestStop(); } catch { /* the screen is already down */ }
            _thread?.Join(TimeSpan.FromSeconds(2));
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

        private MonitorSnapshot? ReadLatest() => Volatile.Read(ref _latest);

        /// <summary>Internal so the buffering can be tested without a terminal.</summary>
        internal IReadOnlyList<MonitorEvent> ReadEvents()
        {
            lock (_eventLock)
                return _events.ToArray();
        }
    }
}
