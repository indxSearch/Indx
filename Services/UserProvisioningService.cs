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
        InviteService invites,
        NotificationService notifications,
        ILogger<UserProvisioningService> logger)
    {
        /// <summary>
        /// Assigns the platform Identity role, accepts the address's invites, gives the user a
        /// personal team unless an invite put them on one, and notifies admins. The very first user on the instance
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

            // Invited to a team: that team, in the invited role, and no team of their own beside
            // it (Nov 2026; before, everyone got one, and an invited colleague landed in an empty
            // team named after their email). They can still make one with New team.
            var joined = await invites.AcceptAsync(user, email);
            if (joined != null)
                await teamService.SetLastTeamAsync(user.Id, joined.Id);
            else
                // A personal team, so the dashboard has a team context immediately. Blank name
                // falls back to the email prefix.
                await teamService.CreatePersonalTeamAsync(user, requestedTeamName);

            if (!isFirstUser)
            {
                await notifications.CreateForAdminsAsync(
                    NotificationType.UserRegistered,
                    $"New user registered: {email}",
                    $"{email} has created an account.");
            }

            logger.LogInformation(
                "Provisioned new user {Email} (firstUser={IsFirst}, team={Team})", email, isFirstUser, joined?.Name ?? "personal");
        }
    }
}
