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
    internal sealed class TuiRenderer(MonitorState state,
                                     TimeSpan fallbackStatusInterval,
                                     MonitorLoggerProvider? logProvider = null,
                                     Action? requestShutdown = null)
        : IMonitorRenderer
    {
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
                using var window = new MonitorWindow(app, () => state.Latest, state.Events,
                                                    requestShutdown ?? (() => { }), _endpoints);
                // Only now does the console belong to the screen. Until this point log lines --
                // including whatever went wrong during startup -- print normally.
                logProvider?.ScreenUp();
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
                logProvider?.ScreenDown();
                _app = null;
            }
        }

        public void Render(MonitorSnapshot snapshot)
        {
            // MonitorState already has it; the screen reads from there on its own timer. Only the
            // fallback, which writes lines rather than drawing, needs handing the snapshot.
            if (Volatile.Read(ref _fallback) is { } fallback)
                fallback.Render(snapshot);
        }

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
    }
}
