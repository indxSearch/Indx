using IndxServer.Engine;

namespace IndxServer.Services.Observr
{
    /// <summary>What the last round did: when, how many were sent, and whether it stopped short
    /// (the status that stopped it) or went through every dataset.</summary>
    public sealed record ObservrDigestRound(DateTime AtUtc, int Sent, ObservrStatus? StoppedBy)
    {
        public bool Complete => StoppedBy == null;
    }

    /// <summary>
    /// Sends every dataset with Observr on its daily summary (<see cref="ObservrSummary"/>) and
    /// turns what comes back into findings. Once a day at <c>Indx:Observr:DigestHourUtc</c> (default
    /// 5), after the statistics' own day has rolled over everywhere west of UTC+5; a dataset reports
    /// daily, weekly (Mondays) or not at all as its team chose, and an instance with no token, no
    /// subscription or Observr not activated sends nothing. Never loads a dataset. A round that
    /// Observr's being unreachable cut short is tried again every hour until it goes through, so an
    /// outage at the digest hour does not lose the day (16 Oct 2026); the last round is kept for the
    /// admin page.
    /// </summary>
    internal sealed class ObservrDigestJob(ObservrClient client, ObservrFindingStore findings, ObservrSubscription subscription,
        IConfiguration configuration, ILogger<ObservrDigestJob> logger) : BackgroundService
    {
        public static readonly TimeSpan Retry = TimeSpan.FromHours(1);

        /// <summary>The last round, for the admin page; null before the first.</summary>
        public ObservrDigestRound? LastRound { get; private set; }

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
                // The day's round, tried again hourly while Observr is unreachable, until the next day's is due.
                var dayEnds = next.AddDays(1);
                var round = await RunAsync(stoppingToken);
                while (round.StoppedBy == ObservrStatus.Unavailable && DateTime.UtcNow + Retry < dayEnds && !stoppingToken.IsCancellationRequested)
                {
                    try { await Task.Delay(Retry, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    round = await RunAsync(stoppingToken);
                }
            }
        }

        /// <summary>One round over every dataset with Observr on.</summary>
        public async Task<ObservrDigestRound> RunAsync(CancellationToken ct)
        {
            var sent = 0;
            ObservrStatus? stoppedBy = null;
            if (!client.HasToken) return LastRound = new ObservrDigestRound(DateTime.UtcNow, 0, ObservrStatus.NoToken);
            // The day's check of the subscription, which is also what keeps the menu's answer fresh.
            if (!(await subscription.GetAsync(force: true, ct)).Included || !subscription.Visible)
                return LastRound = new ObservrDigestRound(DateTime.UtcNow, 0, ObservrStatus.NoSubscription);
            foreach (var (dataSetName, teamId) in IndxServerInternalApi.ManagerOrNull?.GetAllDataSets() ?? [])
            {
                if (ct.IsCancellationRequested) break;
                if (!findings.Settings(teamId, dataSetName).ReportsOn(DateTime.UtcNow.DayOfWeek)) continue;
                try
                {
                    var outcome = await client.SendDigestAsync(teamId, dataSetName, ct);
                    if (outcome.Status == ObservrStatus.Ok) sent++;
                    else logger.LogInformation("Observr digest for {Team}/{Dataset}: {Status} {Reason}", teamId, dataSetName, outcome.Status, outcome.Reason);
                    // A refusal that is the instance's, not the dataset's, ends the round.
                    if (outcome.Status is ObservrStatus.UnknownToken or ObservrStatus.NoSubscription or ObservrStatus.Unavailable) { stoppedBy = outcome.Status; break; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Observr digest failed for {Team}/{Dataset}", teamId, dataSetName);
                }
            }
            return LastRound = new ObservrDigestRound(DateTime.UtcNow, sent, stoppedBy);
        }
    }
}
