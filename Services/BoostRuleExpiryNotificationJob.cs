using IndxCloudApi.Data;
using IndxCloudApi.Models;
using Microsoft.EntityFrameworkCore;

namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Daily: for every dataset, every enabled boost rule whose <c>ActiveUntil</c> has passed gets
    /// one notification to each Editor/Admin of the owning team — once per rule, de-duplicated by
    /// (team, dataset, rule name). An expired campaign otherwise looks live to anyone not on the
    /// Boost rules tab. Same shape as <see cref="TokenExpiryNotificationJob"/>.
    /// </summary>
    internal class BoostRuleExpiryNotificationJob(
        IServiceScopeFactory scopeFactory,
        BoostRuleStore boostStore,
        ILogger<BoostRuleExpiryNotificationJob> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); // let startup and warm-up finish

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await CheckAsync(); }
                catch (Exception ex) { logger.LogError(ex, "BoostRuleExpiryNotificationJob failed"); }

                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
        }

        internal async Task CheckAsync()
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<NotificationService>();
            var today = DateOnly.FromDateTime(DateTime.Today);

            foreach (var (dataSetName, teamId) in IndxCloudInternalApi.Manager.GetAllDataSets())
            {
                var expired = boostStore.Load(teamId, dataSetName)
                    .Where(r => r.Enabled && r.ActiveUntil is { } until && until < today)
                    .ToList();
                if (expired.Count == 0) continue;

                if (!Guid.TryParse(teamId, out var teamGuid)) continue;
                var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamGuid);
                if (team == null) continue;

                // Editors and admins can act on it; viewers cannot.
                var recipients = await db.TeamMembers
                    .Where(m => m.TeamId == teamGuid && (m.Role == TeamRoles.Admin || m.Role == TeamRoles.Editor))
                    .Select(m => m.UserId)
                    .ToListAsync();

                foreach (var rule in expired)
                {
                    var sourceId = $"{teamId}/{dataSetName}/{rule.Name}/{rule.ActiveUntil:yyyy-MM-dd}";
                    foreach (var userId in recipients)
                    {
                        if (await svc.ExistsAsync(userId, NotificationType.BoostRuleExpired, sourceId)) continue;
                        await svc.CreateAsync(
                            userId,
                            NotificationType.BoostRuleExpired,
                            $"Boost rule \"{rule.Name}\" on {dataSetName} has expired",
                            $"The rule \"{rule.Name}\" on dataset {dataSetName} (team {team.Name}) ended on {rule.ActiveUntil:yyyy-MM-dd} and no longer boosts results. " +
                            "Extend its dates, switch it off, or delete it under Boost rules.",
                            metadata: $"/datasets/{Uri.EscapeDataString(dataSetName)}",
                            sourceId: sourceId);
                    }
                    logger.LogInformation("Sent BoostRuleExpired for {Dataset}/{Rule} to {Count} member(s)", dataSetName, rule.Name, recipients.Count);
                }
            }
        }
    }
#pragma warning restore 1591
}
