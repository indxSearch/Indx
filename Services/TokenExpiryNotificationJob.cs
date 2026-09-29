using IndxServer.Data;
using Microsoft.EntityFrameworkCore;

namespace IndxServer.Services
{
#pragma warning disable 1591
    internal class TokenExpiryNotificationJob(
        IServiceScopeFactory scopeFactory,
        ILogger<TokenExpiryNotificationJob> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Run once at startup (slight delay to let app finish initialising)
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckExpiryAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "TokenExpiryNotificationJob failed");
                }

                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
        }

        private async Task CheckExpiryAsync()
        {
            using var scope = scopeFactory.CreateScope();
            var db   = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var svc  = scope.ServiceProvider.GetRequiredService<NotificationService>();

            var now        = DateTime.UtcNow;
            var warnCutoff = now.AddDays(14);

            // Keys that never expire (team Search keys) have nothing to warn about.
            var keys = await db.ApiKeys
                .Where(k => !k.IsRevoked && k.ExpiresAt != null)
                .ToListAsync();

            foreach (var key in keys.Where(k => k.IsTeamKey))
                await CheckTeamKeyAsync(db, svc, key, now);

            foreach (var key in keys.Where(k => !k.IsTeamKey))
            {
                var sourceId = key.Id.ToString();
                var userId = key.UserId!;

                if (key.ExpiresAt <= now)
                {
                    if (!await svc.ExistsAsync(userId, NotificationType.ApiKeyExpired, sourceId))
                    {
                        await svc.CreateAsync(
                            userId,
                            NotificationType.ApiKeyExpired,
                            $"API key \"{key.Name}\" has expired",
                            $"Your API key \"{key.Name}\" expired on {key.ExpiresAt.Value:yyyy-MM-dd}. Create a new key to restore access.",
                            sourceId: sourceId);

                        logger.LogInformation("Sent ApiKeyExpired notification for API key \"{Name}\"", key.Name);
                    }
                }
                else if (key.ExpiresAt <= warnCutoff)
                {
                    if (!await svc.ExistsAsync(userId, NotificationType.ApiKeyExpiringSoon, sourceId))
                    {
                        var daysLeft = (int)(key.ExpiresAt!.Value - now).TotalDays;
                        await svc.CreateAsync(
                            userId,
                            NotificationType.ApiKeyExpiringSoon,
                            $"API key \"{key.Name}\" expires in {daysLeft} day{(daysLeft == 1 ? "" : "s")}",
                            $"Your API key \"{key.Name}\" will expire on {key.ExpiresAt.Value:yyyy-MM-dd}. Rotate it before then to avoid disruption.",
                            sourceId: sourceId);

                        logger.LogInformation("Sent ApiKeyExpiringSoon notification for API key \"{Name}\"", key.Name);
                    }
                }
            }
        }

        /// <summary>Warning thresholds for team keys, in days. A team key usually sits in a
        /// deployed site, so its Admins get two chances to rotate it, not one.</summary>
        internal static readonly int[] TeamKeyWarnDays = [30, 7];

        /// <summary>
        /// A team key has no owner to tell, so every current Admin of its team is told: the people
        /// who can rotate it. Each threshold is its own notification (source id "key:30", "key:7"),
        /// so the second warning is not swallowed by the first.
        /// </summary>
        private async Task CheckTeamKeyAsync(ApplicationDbContext db, NotificationService svc, ApiKey key, DateTime now)
        {
            var expiresAt = key.ExpiresAt!.Value;
            var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == key.TeamId);
            if (team == null) return;
            var admins = await db.TeamMembers
                .Where(m => m.TeamId == team.Id && m.Role == TeamRoles.Admin)
                .Select(m => m.UserId)
                .ToListAsync();

            NotificationType type;
            string sourceId, title, body;
            if (expiresAt <= now)
            {
                type = NotificationType.ApiKeyExpired;
                sourceId = key.Id.ToString();
                title = $"Team key \"{key.Name}\" in {team.Name} has expired";
                body = $"The {team.Name} team key \"{key.Name}\" expired on {expiresAt:yyyy-MM-dd}. Whatever uses it can no longer reach the team's datasets. Create a new key under Manage team.";
            }
            else
            {
                var daysLeft = (expiresAt - now).TotalDays;
                // A threshold as long as the key's whole life would fire the day it was made, so a
                // threshold counts only if the key has lived at least a day before reaching it.
                var lifetime = (expiresAt - key.CreatedAt).TotalDays;
                var threshold = TeamKeyWarnDays.Where(d => daysLeft <= d && d <= lifetime - 1).DefaultIfEmpty(0).Min();
                if (threshold == 0) return;
                var days = Math.Max(1, (int)Math.Ceiling(daysLeft));
                type = NotificationType.ApiKeyExpiringSoon;
                sourceId = $"{key.Id}:{threshold}";
                title = $"Team key \"{key.Name}\" in {team.Name} expires in {days} day{(days == 1 ? "" : "s")}";
                body = $"The {team.Name} team key \"{key.Name}\" expires on {expiresAt:yyyy-MM-dd}. Create a replacement under Manage team, deploy it, then revoke this one.";
            }

            var sent = 0;
            foreach (var adminId in admins)
            {
                if (await svc.ExistsAsync(adminId, type, sourceId)) continue;
                await svc.CreateAsync(adminId, type, title, body, sourceId: sourceId);
                sent++;
            }
            if (sent > 0)
                logger.LogInformation("Sent {Type} notification for team key \"{Name}\" in {Team} to {Count} admin(s)", type, key.Name, team.Name, sent);
        }
    }
#pragma warning restore 1591
}
