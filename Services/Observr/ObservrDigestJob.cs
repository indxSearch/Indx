using IndxServer.Engine;

namespace IndxServer.Services.Observr
{
    /// <summary>
    /// Sends every dataset with Observr on its daily summary (<see cref="ObservrSummary"/>) and
    /// turns what comes back into findings. Once a day at <c>Indx:Observr:DigestHourUtc</c> (default
    /// 5), after the statistics' own day has rolled over everywhere west of UTC+5; a dataset that
    /// has Observr off, or an instance with no licence token, sends nothing. Never loads a dataset.
    /// </summary>
    internal sealed class ObservrDigestJob(ObservrClient client, ObservrFindingStore findings, ObservrSubscription subscription,
        IConfiguration configuration, ILogger<ObservrDigestJob> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var hour = Math.Clamp(configuration.GetValue<int?>("Indx:Observr:DigestHourUtc") ?? 5, 0, 23);
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var next = now.Date.AddHours(hour);
                if (next <= now) next = next.AddDays(1);
                try { await Task.Delay(next - now, stoppingToken); }
                catch (OperationCanceledException) { break; }
                await RunAsync(stoppingToken);
            }
        }

        /// <summary>One round over every dataset with Observr on. Returns how many were sent.</summary>
        public async Task<int> RunAsync(CancellationToken ct)
        {
            if (!client.HasToken) return 0;
            // The day's check of the subscription, which is also what keeps the menu's answer fresh.
            if (!(await subscription.GetAsync(force: true, ct)).Included) return 0;
            var sent = 0;
            foreach (var (dataSetName, teamId) in IndxServerInternalApi.ManagerOrNull?.GetAllDataSets() ?? [])
            {
                if (ct.IsCancellationRequested) break;
                if (!findings.IsEnabled(teamId, dataSetName)) continue;
                try
                {
                    var outcome = await client.SendDigestAsync(teamId, dataSetName, ct);
                    if (outcome.Status == ObservrStatus.Ok) sent++;
                    else logger.LogInformation("Observr digest for {Team}/{Dataset}: {Status} {Reason}", teamId, dataSetName, outcome.Status, outcome.Reason);
                    // A refusal that is the instance's, not the dataset's, ends the round.
                    if (outcome.Status is ObservrStatus.UnknownToken or ObservrStatus.NoSubscription or ObservrStatus.Unavailable) break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Observr digest failed for {Team}/{Dataset}", teamId, dataSetName);
                }
            }
            return sent;
        }
    }
}
