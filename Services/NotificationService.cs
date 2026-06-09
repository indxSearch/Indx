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

            db.Notifications.Add(notification);
            await db.SaveChangesAsync();

            var user = await userManager.FindByIdAsync(userId);
            if (user is { Email: not null, EmailNotificationsEnabled: true })
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

        public async Task<List<Notification>> GetRecentAsync(string userId, int count = 10)
            => await db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(count)
                .ToListAsync();

        public async Task<List<Notification>> GetAllAsync(string userId)
            => await db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync();

        public async Task<int> GetUnreadCountAsync(string userId)
            => await db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

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
