using System.Security.Claims;
using IndxServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IndxServer.Services
{
    /// <summary>Where Entra ID sign-in is pointed (the "Entra" configuration section). On when both
    /// ids are set: the customer's directory, whose sign-ins alone are accepted, and the app
    /// registration people sign in through. A Managed App gets both from its deployment template:
    /// the directory of the subscription it is deployed in, and Indx's own multi-tenant
    /// registration.</summary>
    internal sealed record EntraOptions(string TenantId, string ClientId)
    {
        public bool Enabled => Guid.TryParse(TenantId, out _) && Guid.TryParse(ClientId, out _);

        /// <summary>The issuer a token from the customer's directory carries; any other is refused.</summary>
        public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

        public static EntraOptions From(IConfiguration configuration) =>
            new((configuration["Entra:TenantId"] ?? "").Trim(), (configuration["Entra:ClientId"] ?? "").Trim());
    }

    /// <summary>What a sign-in with Entra ID came to: the user to sign in, or why not.</summary>
    internal sealed record EntraSignInResult(ApplicationUser? User, string? Refusal)
    {
        public static EntraSignInResult Refused(string why) => new(null, why);
    }

    /// <summary>
    /// Sign-in with the customer's Entra ID (Nov 2026). The account is the pair of directory and
    /// object id (<c>tid</c>, <c>oid</c>), never the email alone; the role is the app role Entra
    /// gives (Indx Admin, Editor, Viewer), applied at every sign-in, so the customer manages who
    /// may do what in Entra and nowhere else. Someone with no role is not let in.
    /// </summary>
    internal sealed class EntraSignIn(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        TeamService teams,
        InviteService invites,
        OnboardingService onboarding,
        NotificationService notifications,
        InstanceSettingsService settings,
        IConfiguration configuration,
        ILogger<EntraSignIn> logger)
    {
        public const string Scheme = "Entra";
        public const string DisplayName = "Microsoft Entra ID";
        public const string CallbackPath = "/signin-oidc";

        /// <summary>The app roles on the registration, as the <c>roles</c> claim carries them.</summary>
        public const string AdminRole = "Indx.Admin";
        public const string EditorRole = "Indx.Editor";
        public const string ViewerRole = "Indx.Viewer";

        /// <summary>The claims the handler leaves for Identity, set from the ID token in
        /// <see cref="Normalize"/>: the key Identity files the login under, and the address.</summary>
        public const string TenantClaim = "tid";
        public const string ObjectIdClaim = "oid";
        public const string RolesClaim = "roles";

        public const string NoRole =
            "You have no role in Indx yet. Ask your administrator to assign you Indx Admin, Editor or Viewer in Microsoft Entra ID.";

        /// <summary>The team role an Entra role stands for, highest first; null when it carries none
        /// of the three.</summary>
        public static string? TeamRoleFrom(ClaimsPrincipal principal)
        {
            var roles = principal.FindAll(RolesClaim).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (roles.Contains(AdminRole)) return TeamRoles.Admin;
            if (roles.Contains(EditorRole)) return TeamRoles.Editor;
            if (roles.Contains(ViewerRole)) return TeamRoles.Viewer;
            return null;
        }

        /// <summary>The address to know someone by: <c>email</c> when the directory has one, else the
        /// sign-in name (<c>preferred_username</c>, the UPN), which in a work directory is an address
        /// the organisation controls.</summary>
        public static string? EmailFrom(ClaimsPrincipal principal)
        {
            var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                email = principal.FindFirst("preferred_username")?.Value;
            return string.IsNullOrWhiteSpace(email) || !email.Contains('@') ? null : email.Trim();
        }

        /// <summary>Run when the handler has validated the ID token: adds the claims Identity reads
        /// for an external login. The key is <c>tid:oid</c>, which is the person in that directory and
        /// stays so if their address changes; the token's own <c>sub</c> is not used.</summary>
        public static void Normalize(ClaimsPrincipal principal)
        {
            if (principal.Identity is not ClaimsIdentity identity) return;
            var tid = principal.FindFirst(TenantClaim)?.Value;
            var oid = principal.FindFirst(ObjectIdClaim)?.Value;
            if (tid != null && oid != null && principal.FindFirst(ClaimTypes.NameIdentifier) == null)
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, $"{tid}:{oid}"));
            var email = EmailFrom(principal);
            if (email != null && principal.FindFirst(ClaimTypes.Email) == null)
                identity.AddClaim(new Claim(ClaimTypes.Email, email));
            var name = principal.FindFirst("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name) && principal.FindFirst(ClaimTypes.Name) == null)
                identity.AddClaim(new Claim(ClaimTypes.Name, name));
        }

        /// <summary>Finds or makes the account for a validated Entra sign-in and applies its role.
        /// The caller signs the returned user in.</summary>
        public async Task<EntraSignInResult> SignInAsync(ExternalLoginInfo info)
        {
            var options = EntraOptions.From(configuration);
            var principal = info.Principal;

            // The handler already checked the issuer; this is the same rule kept where the account
            // is decided, so a wrongly configured handler cannot let another directory in.
            if (!options.Enabled || !string.Equals(principal.FindFirst(TenantClaim)?.Value, options.TenantId, StringComparison.OrdinalIgnoreCase))
                return EntraSignInResult.Refused("This sign-in is from another organisation's directory.");

            var teamRole = TeamRoleFrom(principal);
            if (teamRole == null) return EntraSignInResult.Refused(NoRole);

            var email = EmailFrom(principal);
            if (email == null) return EntraSignInResult.Refused("Microsoft Entra ID sent no email address for this account.");

            var created = false;
            var user = await userManager.FindByLoginAsync(Scheme, info.ProviderKey);
            if (user == null)
            {
                // Linking by address is safe here and only here: the directory is fixed to the
                // customer's own, so the address is one their IT controls. (The OAuth providers
                // accept anyone's directory, which is why an Entra account is never found that way.)
                user = await userManager.FindByEmailAsync(email);
                if (user != null && !user.EmailConfirmed)
                {
                    // Registered without proving the address; the directory has now proven it.
                    user.EmailConfirmed = true;
                    await userManager.UpdateAsync(user);
                    await userManager.RemovePasswordAsync(user);
                    logger.LogWarning("Took over unverified account for {Email} via Entra ID", email);
                }
                if (user == null)
                {
                    user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
                    var create = await userManager.CreateAsync(user);
                    if (!create.Succeeded)
                        return EntraSignInResult.Refused(string.Join(" ", create.Errors.Select(e => e.Description)));
                    created = true;
                }
                var link = await userManager.AddLoginAsync(user, info);
                if (!link.Succeeded)
                {
                    if (created) await userManager.DeleteAsync(user);
                    return EntraSignInResult.Refused("Could not link Microsoft Entra ID to the account.");
                }
            }

            if (await userManager.IsLockedOutAsync(user))
                return EntraSignInResult.Refused("This account is locked.");

            try
            {
                await ApplyRoleAsync(user, email, teamRole, created);
            }
            catch (TeamService.TeamException e)
            {
                // The plan's user limit (Free is one user): nobody new gets in, and nothing is left behind.
                if (created) await userManager.DeleteAsync(user);
                logger.LogWarning("Entra sign-in for {Email} refused: {Reason}", email, e.Message);
                return EntraSignInResult.Refused(e.Message);
            }

            if (created)
            {
                await notifications.CreateForAdminsAsync(
                    NotificationType.UserRegistered,
                    $"New user signed in with Entra ID: {email}",
                    $"{email} signed in with Microsoft Entra ID as {teamRole}.");
            }
            logger.LogInformation("Entra sign-in {Email} as {Role} (new account: {Created})", email, teamRole, created);
            return new EntraSignInResult(user, null);
        }

        /// <summary>Entra decides: Indx Admin is an instance admin, and the role is the user's role
        /// in the organisation's team. An account that also has a password (the template's admin,
        /// kept for when Entra cannot be reached) keeps the instance role it has locally; Entra only
        /// adds to it.</summary>
        private async Task ApplyRoleAsync(ApplicationUser user, string email, string teamRole, bool created)
        {
            var isAdmin = teamRole == TeamRoles.Admin;
            var hasPassword = await userManager.HasPasswordAsync(user);
            if (isAdmin)
            {
                if (!await userManager.IsInRoleAsync(user, "Admin")) await userManager.AddToRoleAsync(user, "Admin");
                if (!hasPassword && await userManager.IsInRoleAsync(user, "Member")) await userManager.RemoveFromRoleAsync(user, "Member");
            }
            else if (!hasPassword || created)
            {
                if (await userManager.IsInRoleAsync(user, "Admin")) await userManager.RemoveFromRoleAsync(user, "Admin");
                if (!await userManager.IsInRoleAsync(user, "Member")) await userManager.AddToRoleAsync(user, "Member");
            }

            // Invites still count: a colleague invited to a team before Entra was switched on keeps it.
            if (created) await invites.AcceptAsync(user, email);

            var team = await OrganisationTeamAsync(user, email);
            var current = await teams.GetRoleAsync(team.Id, user.Id);
            if (hasPassword && !created && current != null && Rank(current) > Rank(teamRole)) teamRole = current;
            if (current != teamRole) await teams.AddMemberAsync(team.Id, user.Id, teamRole);
            if (await teams.GetLastTeamIdAsync(user.Id) == null) await teams.SetLastTeamAsync(user.Id, team.Id);
            if (created) await onboarding.StartAsync(user.Id, team.Id, Onboarding.KindInvited);
        }

        private static int Rank(string role) => role switch
        {
            TeamRoles.Admin => 3,
            TeamRoles.Editor => 2,
            TeamRoles.Viewer => 1,
            _ => 0,
        };

        /// <summary>The team Entra users share. Chosen once and kept in settings: the instance's
        /// oldest team (in a Managed App, the customer's first), or, on an instance with none, a new
        /// one named after the organisation's domain, with this user in it.</summary>
        private async Task<Team> OrganisationTeamAsync(ApplicationUser user, string email)
        {
            var s = settings.Load();
            if (s.EntraTeamId is { } id && await teams.GetByIdAsync(id) is { } kept) return kept;

            var team = await db.Teams.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync()
                       ?? await teams.CreateTeamAsync(email.Split('@')[1].Split('.')[0], user.Id, enforcePlanLimit: false);
            s = settings.Load();
            s.EntraTeamId = team.Id;
            settings.Save(s);
            logger.LogInformation("Entra ID users join team {Team}", team.Name);
            return team;
        }
    }
}
