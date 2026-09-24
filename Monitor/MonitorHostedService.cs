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
        ILogger<MonitorHostedService> logger) : BackgroundService
    {
        private const int MaxConsecutiveFailures = 5;
        private readonly MonitorCollector _collector = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (options.Mode == MonitorMode.Off)
                return;

            logger.LogInformation("Indx monitor starting in {Mode} mode, status every {Seconds:F0}s",
                options.Mode, options.StatusInterval.TotalSeconds);

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

            try { renderer.Stop(); }
            catch (Exception ex) { logger.LogWarning(ex, "Indx monitor renderer failed to stop cleanly"); }
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
        /// Registers the monitor when the environment can show it. Call it late in service
        /// registration; it reads configuration and the command line only, and touches no engine.
        /// </summary>
        internal static IServiceCollection AddIndxMonitor(
            this IServiceCollection services, IConfiguration configuration, string[] args)
        {
            var options = MonitorOptions.Resolve(configuration, args);
            if (options.Mode == MonitorMode.Off)
                return services;

            services.AddSingleton(options);
            // Stage one renders the text block in both modes. The Terminal.Gui renderer replaces
            // this registration for Interactive and nothing else changes.
            services.AddSingleton<IMonitorRenderer>(
                _ => new PipedReporter(Console.Out, options.StatusInterval));
            services.AddHostedService<MonitorHostedService>();
            return services;
        }
    }
}
