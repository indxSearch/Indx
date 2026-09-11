using Microsoft.AspNetCore.Identity;

namespace IndxCloudApi.Data
{
    /// <summary>
    /// Application user entity extending ASP.NET Core Identity user.
    /// Add custom profile properties here as needed.
    /// </summary>
    public class ApplicationUser : IdentityUser
    {
        /// <summary>
        /// When true the user must change their password before they can call any
        /// protected API or UI endpoint other than the password-change flow itself.
        /// Set on the seeded admin so first-login forces a rotation away from the
        /// deployment-time initial password.
        /// </summary>
        public bool MustChangePassword { get; set; }

        /// <summary>When false, no notification emails are sent to this user. Legacy global flag;
        /// now the per-type default for the email channel when no explicit preference is set.</summary>
        public bool EmailNotificationsEnabled { get; set; } = true;

        /// <summary>JSON map of per-notification-type channel preferences
        /// (see <see cref="Services.NotificationPreferences"/>). Absent entries fall back to
        /// defaults: in-app on; email per <see cref="EmailNotificationsEnabled"/>.</summary>
        public string? NotificationPreferences { get; set; }

        /// <summary>The team whose page this user opened last; the console lands there on the
        /// next visit. Validated against membership on read — a team the user has since left is
        /// ignored, so a stale value can never leak another team's datasets.</summary>
        public Guid? LastTeamId { get; set; }
    }
}
