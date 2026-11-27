namespace IndxServer.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Periodically re-fetches the license file from the configured portal so a long-running
    /// instance never lets its on-disk license go stale. Pro licenses are meant to refresh
    /// monthly even though the file is valid for ~90 days; a daily fetch keeps the file fresh
    /// with a large retry margin if the portal is briefly unreachable.
    ///
    /// This keeps the file on disk valid — it does not live-relicense already-running engines
    /// (those validate once at construction), but it guarantees newly-created datasets and every
    /// restart pick up a valid license. It is a no-op until auto-fetch is configured: with no URL
    /// the bootstrapper returns <see cref="LicenseFetchOutcome.NotConfigured"/> and without a token
    /// it only downloads when the file is missing.
    /// </summary>
    internal class LicenseRefreshJob(
        ILicenseBootstrapper bootstrapper,
        ILogger<LicenseRefreshJob> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Startup already fetches once (Program.cs), so wait a full interval before refreshing.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    var result = await bootstrapper.EnsureLocalLicenseAsync(stoppingToken);
                    if (result.Outcome == LicenseFetchOutcome.Failed)
                        logger.LogWarning("Scheduled license refresh failed: {Message}", result.Message);
                    else if (result.Outcome == LicenseFetchOutcome.Fetched)
                        logger.LogInformation("Scheduled license refresh: {Message}", result.Message);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "LicenseRefreshJob failed");
                }
            }
        }
    }
}
