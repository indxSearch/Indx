using IndxServer.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace IndxServer.Monitor
{
    /// <summary>
    /// Drives the collector on a timer and hands each snapshot to the renderer.
    ///
    /// <para><b>Nothing here may take the server down.</b> An unhandled exception from a
    /// <see cref="BackgroundService"/> stops the host by default, so every tick is wrapped: a
    /// failure is logged and the loop continues, and a run of consecutive failures switches the
    /// monitor off rather than escalating. It is a view onto the server and must never be the
    /// reason one stops serving.</para>
    /// </summary>
    internal sealed class MonitorHostedService(
        MonitorOptions options,
        IMonitorRenderer renderer,
        IHostApplicationLifetime lifetime,
        IServer server,
        IServiceScopeFactory scopes,
        ILogger<MonitorHostedService> logger) : BackgroundService
    {
        private const int MaxConsecutiveFailures = 5;
        private readonly MonitorCollector _collector = new(new TeamNames(scopes));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (options.Mode == MonitorMode.Off)
                return;

            // Nothing is drawn until the server is actually listening. A startup that fails --
            // the port already in use is the one that found this -- must leave the console alone
            // so its error is readable, and ApplicationStarted is the signal that says startup
            // got all the way through. Relying on hosted-service registration order instead would
            // be guessing about a race.
            if (!await WaitForApplicationStartedAsync(stoppingToken))
                return;

            logger.LogInformation("Indx monitor starting in {Mode} mode, status every {Seconds:F0}s",
                options.Mode, options.StatusInterval.TotalSeconds);

            // Nothing reports activity until something is watching for it.
            MonitorActivity.Enabled = true;
            renderer.Start(Endpoints());

            using var timer = new PeriodicTimer(options.PollInterval);
            int consecutiveFailures = 0;

            while (await SafeWaitAsync(timer, stoppingToken))
            {
                try
                {
                    renderer.Render(_collector.Collect(DateTimeOffset.UtcNow));
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    logger.LogWarning(ex, "Indx monitor tick failed ({Count}/{Max})",
                        consecutiveFailures, MaxConsecutiveFailures);

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        logger.LogError("Indx monitor stopping after {Count} consecutive failures; " +
                                        "the server is unaffected", consecutiveFailures);
                        break;
                    }
                }
            }

            MonitorActivity.Enabled = false;
            try { renderer.Stop(); }
            catch (Exception ex) { logger.LogWarning(ex, "Indx monitor renderer failed to stop cleanly"); }
        }

        /// <summary>
        /// Where the browser console is served, for the screen to show: the startup banner prints
        /// it and the screen then covers the banner, so without this the address is gone the
        /// moment the monitor appears.
        ///
        /// <para>Read from the server rather than from configuration, because configuration is
        /// what was asked for and this is what was bound — they differ on <c>--urls</c>, on a
        /// port clash, and on port 0. Only valid after ApplicationStarted, which is where this
        /// is called.</para>
        /// </summary>
        private MonitorEndpoints Endpoints()
        {
            string? web = null;
            try { web = PickAddress(server.Features.Get<IServerAddressesFeature>()?.Addresses); }
            catch { /* an address that cannot be read shows nothing */ }

            return new MonitorEndpoints(web, web is null ? null : McpUrl(web));
        }

        /// <summary>
        /// The MCP endpoint, or null when an admin has switched it off — offering a URL that 404s
        /// would be worse than saying it is off. Read once through a scope so the service stays
        /// out of this class's constructor, and because settings.json is re-read on every Load:
        /// fine once at startup, wasteful at one hertz.
        /// </summary>
        private string? McpUrl(string web)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<InstanceSettingsService>();
                return settings.Load().McpEnabled ? web + "/mcp" : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// One address out of what Kestrel bound, preferring https. A wildcard host is what the
        /// server listens on, not somewhere anyone can point a browser, so it becomes localhost.
        /// </summary>
        internal static string? PickAddress(IEnumerable<string>? addresses)
        {
            var all = addresses?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (all is not { Count: > 0 })
                return null;

            var chosen = all.FirstOrDefault(a => a.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                         ?? all[0];

            return chosen.Replace("://0.0.0.0", "://localhost")
                         .Replace("://[::]", "://localhost")
                         .Replace("://+", "://localhost")
                         .Replace("://*", "://localhost")
                         .TrimEnd('/');
        }

        /// <summary>True when the application finished starting; false when it stopped first,
        /// which is what a failed startup looks like from here.</summary>
        private async Task<bool> WaitForApplicationStartedAsync(CancellationToken stoppingToken)
        {
            if (lifetime.ApplicationStarted.IsCancellationRequested)
                return true;

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            using var onStopping = lifetime.ApplicationStopping.Register(() => started.TrySetCanceled());
            using var onStopped = stoppingToken.Register(() => started.TrySetCanceled());

            try
            {
                await started.Task;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
        {
            try { return await timer.WaitForNextTickAsync(token); }
            catch (OperationCanceledException) { return false; }
        }
    }

    internal static class MonitorRegistration
    {
        /// <summary>
        /// Registers the monitor when the environment can show it. Reads configuration and the
        /// command line only, and touches no engine.
        ///
        /// <para>It takes the builder rather than the service collection because of one thing the
        /// Terminal.Gui screen needs: <b>the console logger has to go.</b> Both write to stdout,
        /// and a log line arriving mid-frame smears the screen with text the redraw does not know
        /// about. Those lines are not lost — NLog still writes <c>IndxServer.log</c>, which is the
        /// durable trail either way.</para>
        /// </summary>
        internal static WebApplicationBuilder AddIndxMonitor(this WebApplicationBuilder builder, string[] args)
        {
            var options = MonitorOptions.Resolve(builder.Configuration, args);
            if (options.Mode == MonitorMode.Off)
                return builder;

            builder.Services.AddSingleton(options);

            if (options.Mode == MonitorMode.Interactive)
            {
                // Not ClearProviders() alone: that leaves a failed startup with nowhere to print.
                // This provider IS the console logger until the screen is up, then becomes the
                // event pane, then is the console logger again once it comes down.
                var logProvider = new MonitorLoggerProvider();
                builder.Logging.ClearProviders();
                builder.Logging.AddProvider(logProvider);
                builder.Services.AddSingleton<IMonitorRenderer>(sp =>
                    new TuiRenderer(options.StatusInterval, logProvider,
                        () => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication()));
            }
            else
            {
                builder.Services.AddSingleton<IMonitorRenderer>(
                    _ => new PipedReporter(Console.Out, options.StatusInterval));
            }

            builder.Services.AddHostedService<MonitorHostedService>();
            return builder;
        }
    }
}
