using IndxServer.Data;
using Microsoft.AspNetCore.Identity;

namespace IndxServer.Services
{
    /// <summary>
    /// Shared post-creation setup for a brand-new user, used by every registration path
    /// (email/password registration and both OAuth flows) so they cannot drift apart —
    /// past drift between these paths caused users to land with no team and left invites
    /// dangling. Call once, immediately after <c>UserManager.CreateAsync</c> succeeds.
    /// </summary>
    internal class UserProvisioningService(
        UserManager<ApplicationUser> userManager,
        TeamService teamService,
        InstanceSettingsService settings,
        NotificationService notifications,
        ILogger<UserProvisioningService> logger)
    {
        /// <summary>
        /// Assigns the platform Identity role, creates the user's personal team, notifies
        /// admins, and consumes a matching invite. The very first user on the instance
        /// becomes "Admin" and triggers no notification (there are no other admins yet);
        /// everyone else becomes "Member". <paramref name="requestedTeamName"/> is the
        /// optional team name from the registration form (OAuth has none → email prefix).
        /// </summary>
        public async Task ProvisionNewUserAsync(
            ApplicationUser user, string email, string? requestedTeamName = null)
        {
            // Called after CreateAsync, so the new user is already counted: Count()==1
            // means this is the first (and only) user. Mirrors Register's pre-creation
            // !Any() check.
            var isFirstUser = userManager.Users.Count() == 1;

            await userManager.AddToRoleAsync(user, isFirstUser ? "Admin" : "Member");

            // Every user owns datasets through a personal team, so the dashboard has a
            // team context immediately. Blank name falls back to the email prefix.
            await teamService.CreatePersonalTeamAsync(user, requestedTeamName);

            if (!isFirstUser)
            {
                await notifications.CreateForAdminsAsync(
                    NotificationType.UserRegistered,
                    $"New user registered: {email}",
                    $"{email} has created an account.");
            }

            ConsumeInvite(email);
            logger.LogInformation(
                "Provisioned new user {Email} (firstUser={IsFirst})", email, isFirstUser);
        }

        // Invited addresses are single-use: drop the email from the allow-list once the
        // account exists, so the admin's "Pending invites" list clears.
        private void ConsumeInvite(string email)
        {
            var current = settings.Load();
            if (current.RegistrationMode == RegistrationMode.Invite)
            {
                current.AllowedEmails.RemoveAll(e => e.Equals(email, StringComparison.OrdinalIgnoreCase));
                settings.Save(current);
            }
        }
    }
}
