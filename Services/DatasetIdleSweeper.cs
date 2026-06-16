using IndxCloudApi.Models;

namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Periodically disposes loaded search-engine instances whose idle time has passed their
    /// per-dataset <c>KeepAliveTimeHrs</c> countdown. Datasets pinned with <see cref="int.MaxValue"/>
    /// or opted out with <c>0</c> are never touched. The actual eviction logic lives in
    /// <see cref="IndxCloudInternalApi.SweepIdleInstances"/>; this service just calls it on an
    /// interval (configurable via <c>DatasetSweep:IntervalMinutes</c>, default 5). An evicted dataset
    /// is transparently re-loaded on its next request (see <see cref="IndxCloudInternalApi.ResolveEngine"/>).
    /// </summary>
    internal class DatasetIdleSweeper(
        IConfiguration configuration,
        ILogger<DatasetIdleSweeper> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var minutes = configuration.GetValue<int?>("DatasetSweep:IntervalMinutes") ?? 5;
            if (minutes < 1)
                minutes = 1;
            var interval = TimeSpan.FromMinutes(minutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    int evicted = IndxCloudInternalApi.Manager.SweepIdleInstances();
                    if (evicted > 0)
                        logger.LogInformation("DatasetIdleSweeper evicted {Count} idle dataset instance(s)", evicted);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DatasetIdleSweeper sweep failed");
                }
            }
        }
    }
}
