using IndxServer.Data;
using Microsoft.AspNetCore.Identity.UI.Services;
using System.Collections.Concurrent;

namespace IndxServer.Services
{
    /// <summary>
    /// Pushes operational failures out of the process by email — the in-app notification and the
    /// log only reach an admin who looks. Rides the existing <see cref="IEmailSender"/> (Console
    /// or Azure Communication Services, appsettings "Email"), so there is nothing new to set up
    /// beyond the recipients.
    ///
    /// Configuration (appsettings "Alerts"): <c>EmailTo</c> — a JSON list of recipients (a
    /// single comma-separated string also works); empty (the default) means no alert email,
    /// in-app and log only. <c>MinFreeDiskMB</c> — the low-disk threshold the backup run checks
    /// (default 1024).
    ///
    /// Each alert key is sent at most once per 20 hours: a failing nightly backup alerts daily,
    /// not per retry, and a full disk cannot cause a mail storm.
    /// </summary>
    public sealed class OperationalAlerts(
        IConfiguration configuration,
        IServiceProvider services,
        ILogger<OperationalAlerts> logger)
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromHours(20);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

        /// <summary>Alerts:EmailTo as a JSON array, or as one comma-separated string.</summary>
        internal string[] Recipients()
        {
            var section = configuration.GetSection("Alerts:EmailTo");
            var list = section.GetChildren().Select(c => c.Value).ToArray();
            var raw = list.Length > 0 ? list : [section.Value];
            return raw.Where(v => !string.IsNullOrWhiteSpace(v))
                      .SelectMany(v => v!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();
        }

        /// <summary>The in-app twin of the email: an admin notification, best-effort — a broken
        /// notification path must never mask the failure it reports.</summary>
        public async Task NotifyAdminsAsync(NotificationType type, string title, string body)
        {
            try
            {
                using var scope = services.CreateScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
                await notifications.CreateForAdminsAsync(type, title, body);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "could not raise the in-app notification '{Title}'", title);
            }
        }

        /// <summary>Sends the alert to the configured recipients, unless the same key was sent
        /// within the last 20 hours. Never throws — a broken mail path must not mask the failure
        /// it reports.</summary>
        public async Task RaiseAsync(string key, string subject, string body)
        {
            var recipients = Recipients();
            if (recipients.Length == 0) return;

            var now = DateTimeOffset.UtcNow;
            if (_lastSent.TryGetValue(key, out var last) && now - last < MinInterval) return;
            _lastSent[key] = now;

            try
            {
                using var scope = services.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                foreach (var recipient in recipients)
                    await sender.SendEmailAsync(recipient, subject, body);
                logger.LogInformation("alert email '{Key}' sent to {Count} recipient(s)", key, recipients.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "could not send the alert email '{Key}'", key);
            }
        }
    }
}
