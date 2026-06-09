using IndxCloudApi.Data;
using Microsoft.EntityFrameworkCore;

namespace IndxCloudApi.Services
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

            var keys = await db.ApiKeys
                .Where(k => !k.IsRevoked)
                .ToListAsync();

            foreach (var key in keys)
            {
                var sourceId = key.Id.ToString();

                if (key.ExpiresAt <= now)
                {
                    if (!await svc.ExistsAsync(key.UserId, NotificationType.ApiKeyExpired, sourceId))
                    {
                        await svc.CreateAsync(
                            key.UserId,
                            NotificationType.ApiKeyExpired,
                            $"API key \"{key.Name}\" has expired",
                            $"Your API key \"{key.Name}\" expired on {key.ExpiresAt:yyyy-MM-dd}. Create a new key to restore access.",
                            sourceId: sourceId);

                        logger.LogInformation("Sent ApiKeyExpired notification for key {Id}", key.Id);
                    }
                }
                else if (key.ExpiresAt <= warnCutoff)
                {
                    if (!await svc.ExistsAsync(key.UserId, NotificationType.ApiKeyExpiringSoon, sourceId))
                    {
                        var daysLeft = (int)(key.ExpiresAt - now).TotalDays;
                        await svc.CreateAsync(
                            key.UserId,
                            NotificationType.ApiKeyExpiringSoon,
                            $"API key \"{key.Name}\" expires in {daysLeft} day{(daysLeft == 1 ? "" : "s")}",
                            $"Your API key \"{key.Name}\" will expire on {key.ExpiresAt:yyyy-MM-dd}. Rotate it before then to avoid disruption.",
                            sourceId: sourceId);

                        logger.LogInformation("Sent ApiKeyExpiringSoon notification for key {Id}", key.Id);
                    }
                }
            }
        }
    }
#pragma warning restore 1591
}
