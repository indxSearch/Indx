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
        OnboardingService onboarding,
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
            // Only for an address the registrant has shown to be theirs: registered through the
            // invite's link, or signed in with a provider that vouches for it (both arrive
            // confirmed). Unconfirmed, the invite waits for AddressConfirmedAsync and the account
            // gets a team of its own.
            var joined = user.EmailConfirmed ? await invites.AcceptAsync(user, email) : null;
            if (joined != null)
            {
                await teamService.SetLastTeamAsync(user.Id, joined.Id);
                // Get started finds their way around the team they joined (OnboardingService).
                await onboarding.StartAsync(user.Id, joined.Id, Onboarding.KindInvited);
            }
            else
            {
                // A personal team, so the dashboard has a team context immediately. Blank name
                // falls back to the email prefix. Get started sets search up on it.
                var own = await teamService.CreatePersonalTeamAsync(user, requestedTeamName);
                await onboarding.StartAsync(user.Id, own.Id, Onboarding.KindFirst);
            }

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

        /// <summary>
        /// The account's address has just been confirmed (the link in the confirmation email): the
        /// invites that waited for that are accepted now, and the user lands on the team joined.
        /// </summary>
        public async Task AddressConfirmedAsync(ApplicationUser user)
        {
            if (string.IsNullOrEmpty(user.Email)) return;
            var joined = await invites.AcceptAsync(user, user.Email);
            if (joined == null) return;
            await teamService.SetLastTeamAsync(user.Id, joined.Id);
            logger.LogInformation("{Email} confirmed their address and joined {Team} by its invite", user.Email, joined.Name);
        }
    }
}
