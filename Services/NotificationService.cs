using IndxCloudApi.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    internal class NotificationService(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        IEmailSender emailSender,
        InstanceSettingsService settings,
        ILogger<NotificationService> logger)
    {
        public async Task CreateAsync(
            string userId,
            NotificationType type,
            string title,
            string body,
            string? metadata = null,
            string? sourceId = null)
        {
            var notification = new Notification
            {
                UserId    = userId,
                Type      = type,
                Title     = title,
                Body      = body,
                Metadata  = metadata,
                SourceId  = sourceId,
                IsRead    = false,
                CreatedAt = DateTime.UtcNow,
            };

            // Always store the row (preserves the background-job de-dup via ExistsAsync and the
            // user's history) even when in-app is off for this type — the feed queries filter it
            // out for display. Email is sent only when the user's per-type preference allows it.
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();

            var user = await userManager.FindByIdAsync(userId);
            if (user is { Email: not null } && NotificationPreferences.EmailEnabled(user, type))
            {
                try
                {
                    var instanceName = settings.Load().InstanceName;
                    await emailSender.SendEmailAsync(
                        user.Email,
                        EmailTemplates.Subject(instanceName, title),
                        EmailTemplates.Notice(instanceName, title, body));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send notification email to {UserId}", userId);
                }
            }
        }

        /// <summary>Creates a notification for every user in the Admin role.</summary>
        public async Task CreateForAdminsAsync(
            NotificationType type,
            string title,
            string body,
            string? metadata = null)
        {
            var admins = await userManager.GetUsersInRoleAsync("Admin");
            foreach (var admin in admins)
                await CreateAsync(admin.Id, type, title, body, metadata);
        }

        // Types the user has switched off for in-app display are hidden from their feed (the rows
        // still exist for de-dup/history). Empty list => no filtering.
        private async Task<List<NotificationType>> HiddenTypesAsync(string userId)
        {
            var user = await userManager.FindByIdAsync(userId);
            return user is null ? [] : NotificationPreferences.DisabledInApp(user);
        }

        public async Task<List<Notification>> GetRecentAsync(string userId, int count = 10)
        {
            var hidden = await HiddenTypesAsync(userId);
            return await db.Notifications
                .Where(n => n.UserId == userId && !hidden.Contains(n.Type))
                .OrderByDescending(n => n.CreatedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<Notification>> GetAllAsync(string userId)
        {
            var hidden = await HiddenTypesAsync(userId);
            return await db.Notifications
                .Where(n => n.UserId == userId && !hidden.Contains(n.Type))
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();
        }

        public async Task<int> GetUnreadCountAsync(string userId)
        {
            var hidden = await HiddenTypesAsync(userId);
            return await db.Notifications
                .CountAsync(n => n.UserId == userId && !n.IsRead && !hidden.Contains(n.Type));
        }

        public async Task MarkReadAsync(int id, string userId)
        {
            var n = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
            if (n is { IsRead: false })
            {
                n.IsRead = true;
                await db.SaveChangesAsync();
            }
        }

        public async Task MarkAllReadAsync(string userId)
        {
            await db.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        }

        /// <summary>True if an unread notification of this type+sourceId already exists for the user.</summary>
        public async Task<bool> ExistsAsync(string userId, NotificationType type, string sourceId)
            => await db.Notifications.AnyAsync(n =>
                n.UserId   == userId &&
                n.Type     == type   &&
                n.SourceId == sourceId);
    }
#pragma warning restore 1591
}
