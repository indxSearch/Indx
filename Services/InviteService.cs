using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using IndxServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IndxServer.Services
{
    /// <summary>
    /// Invitations to the instance, and to a team on it (<see cref="Invite"/>, 1 Nov 2026). One
    /// action covers both cases: an address with an account is added to the team at once, one
    /// without gets an email with a link to register, and joins the team in the invited role when
    /// it does, instead of getting a team of its own (<see cref="UserProvisioningService"/>).
    /// An invite admits its address in every registration mode; the mode only governs who may
    /// register without one (<see cref="RegistrationValidator"/>). It lasts <see cref="Lifetime"/>,
    /// and Resend renews it.
    ///
    /// Who may invite is decided here, not by the pages: an instance admin to any team or none; a
    /// team Editor or Admin to their team, but someone without an account only as the team's Admin
    /// and only while <see cref="InstanceSettings.TeamAdminsCanInvite"/> is on, since an account
    /// on the instance is the instance's decision.
    /// </summary>
    internal sealed class InviteService(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        TeamService teams,
        InstanceSettingsService settings,
        IEditionService edition,
        IEmailSender emailSender,
        ILogger<InviteService> logger)
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

        /// <summary>The query parameter of the register link that carries the token.</summary>
        public const string TokenParameter = "invite";

        public sealed class InviteException(string message) : Exception(message);

        public enum Outcome { Invited, Renewed, AddedToTeam }

        /// <summary>What an invite led to; <see cref="EmailSent"/> false when the email failed (the
        /// invite stands, and Resend tries again).</summary>
        public sealed record Result(Outcome Outcome, bool EmailSent);

        /// <summary>A pending invite as the pages list it.</summary>
        public sealed record Pending(Guid Id, string Email, Guid? TeamId, string? TeamName, string? Role,
            string? InvitedBy, DateTime CreatedAt, DateTime ExpiresAt)
        {
            public bool Expired => ExpiresAt <= DateTime.UtcNow;
        }

        // ---- Inviting ----------------------------------------------------------------------

        /// <summary>
        /// Invites <paramref name="email"/>, to <paramref name="teamId"/> in <paramref name="role"/>
        /// when given. <paramref name="registerUrl"/> is the absolute address of the register page,
        /// which the link is built on.
        /// </summary>
        public async Task<Result> InviteAsync(string email, Guid? teamId, string? role, string inviterUserId, string registerUrl)
        {
            email = email.Trim();
            if (!IsEmail(email)) throw new InviteException("Enter a valid email address.");
            if (!edition.IsEnabled(EditionFeature.MultipleUsers))
                throw new InviteException("Inviting people requires the Professional plan.");

            Team? team = null;
            if (teamId is { } id)
            {
                team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id) ?? throw new InviteException("The team no longer exists.");
                role ??= TeamRoles.Viewer;
                if (!TeamRoles.IsValid(role)) throw new InviteException($"Invalid role '{role}'.");
            }
            else role = null;

            var inviter = await Inviter(inviterUserId, teamId);
            if (team != null && !inviter.CanAddToTeam)
                throw new InviteException("Only the team's editors and admins can invite people to it.");
            if (team == null && !inviter.IsInstanceAdmin)
                throw new InviteException("Only an admin of this instance can invite people to it.");
            if (role == TeamRoles.Admin && !inviter.CanGrantAdmin)
                throw new InviteException("Only a team admin can make someone an admin.");

            var existing = await userManager.FindByEmailAsync(email);
            if (existing != null)
            {
                if (team == null) throw new InviteException($"{email} already has an account.");
                if (await db.TeamMembers.AnyAsync(m => m.TeamId == team.Id && m.UserId == existing.Id))
                    throw new InviteException($"{email} is already on {team.Name}.");
                try { await teams.AddMemberAsync(team.Id, existing.Id, role!); }
                catch (TeamService.TeamException ex) { throw new InviteException(ex.Message); }
                var told = await SendAsync(email,
                    EmailTemplates.Subject(InstanceName, $"You've been added to team \"{team.Name}\""),
                    EmailTemplates.Notice(InstanceName, $"Added to {team.Name}",
                        $"You have been added to the team <strong>{WebUtility.HtmlEncode(team.Name)}</strong> as <strong>{role}</strong>."));
                logger.LogInformation("{Email} added to team {Team} as {Role} by invite", email, team.Name, role);
                return new Result(Outcome.AddedToTeam, told);
            }

            if (!inviter.CanInviteNewUsers)
                throw new InviteException(inviter.IsTeamAdmin
                    ? $"{email} has no account here, and only the instance's admins can invite new people to it."
                    : $"{email} has no account here. Only a team admin can invite someone new.");

            var normalized = Normalize(email);
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.NormalizedEmail == normalized && i.TeamId == teamId);
            var outcome = invite == null ? Outcome.Invited : Outcome.Renewed;
            if (invite == null)
            {
                invite = new Invite { Id = Guid.NewGuid(), Email = email, NormalizedEmail = normalized, TeamId = teamId };
                db.Invites.Add(invite);
            }
            invite.Role = role;
            invite.InvitedByUserId = inviterUserId;
            var token = Renew(invite);
            await db.SaveChangesAsync();
            var sent = await SendInviteAsync(invite, team, inviter.Email, token, registerUrl);
            logger.LogInformation("Invite to {Email} ({Team}) {Outcome}", email, team?.Name ?? "instance", outcome);
            return new Result(outcome, sent);
        }

        /// <summary>A new link and a new <see cref="Lifetime"/>, sent again.</summary>
        public async Task<bool> ResendAsync(Guid inviteId, string inviterUserId, string registerUrl)
        {
            var invite = await Managed(inviteId, inviterUserId);
            var token = Renew(invite);
            await db.SaveChangesAsync();
            var team = invite.TeamId is { } t ? await db.Teams.FirstOrDefaultAsync(x => x.Id == t) : null;
            var inviter = await userManager.FindByIdAsync(inviterUserId);
            return await SendInviteAsync(invite, team, inviter?.Email, token, registerUrl);
        }

        public async Task RevokeAsync(Guid inviteId, string inviterUserId)
        {
            var invite = await Managed(inviteId, inviterUserId);
            db.Invites.Remove(invite);
            await db.SaveChangesAsync();
        }

        // ---- Reading -----------------------------------------------------------------------

        public Task<List<Pending>> ForTeamAsync(Guid teamId) => List(db.Invites.Where(i => i.TeamId == teamId));

        public Task<List<Pending>> AllAsync() => List(db.Invites);

        /// <summary>The invite a register link carries, while it lasts; null otherwise.</summary>
        public async Task<Invite?> FindByTokenAsync(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var hash = Hash(token);
            var now = DateTime.UtcNow;
            return await db.Invites.Include(i => i.Team).FirstOrDefaultAsync(i => i.TokenHash == hash && i.ExpiresAt > now);
        }

        /// <summary>Whether the address holds an invite that lasts. Synchronous, for the
        /// registration checks, which are.</summary>
        public bool IsInvited(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            var normalized = Normalize(email);
            var now = DateTime.UtcNow;
            return db.Invites.Any(i => i.NormalizedEmail == normalized && i.ExpiresAt > now);
        }

        public int CountPending()
        {
            var now = DateTime.UtcNow;
            return db.Invites.Count(i => i.ExpiresAt > now);
        }

        // ---- Accepting ---------------------------------------------------------------------

        /// <summary>
        /// For a user just created: joins the teams the address was invited to, in the invited
        /// roles, and removes its invites (lasting or not, so none lingers in a list). Returns the
        /// team to land on, the first invited to, or null when no invite named a team.
        /// </summary>
        public async Task<Team?> AcceptAsync(ApplicationUser user, string email)
        {
            var normalized = Normalize(email);
            var invites = await db.Invites.Where(i => i.NormalizedEmail == normalized).OrderBy(i => i.CreatedAt).ToListAsync();
            var now = DateTime.UtcNow;
            Team? landing = null;
            foreach (var invite in invites.Where(i => i.ExpiresAt > now && i.TeamId != null))
            {
                var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == invite.TeamId);
                if (team == null) continue;
                try
                {
                    await teams.AddMemberAsync(team.Id, user.Id, invite.Role ?? TeamRoles.Viewer);
                    landing ??= team;
                }
                catch (TeamService.TeamException ex)
                {
                    logger.LogWarning("Invite of {Email} to {Team} not honoured: {Reason}", email, team.Name, ex.Message);
                }
            }
            db.Invites.RemoveRange(invites);
            await db.SaveChangesAsync();
            return landing;
        }

        /// <summary>
        /// The invite list of settings.json before Nov 2026: each address becomes an invite to the
        /// instance, lasting <see cref="Lifetime"/> from now (they never expired before), and the
        /// list is emptied. Run at startup; does nothing once the list is empty.
        /// </summary>
        public async Task<int> MoveSettingsListAsync()
        {
            var current = settings.Load();
            if (current.AllowedEmails.Count == 0) return 0;
            var moved = 0;
            foreach (var email in current.AllowedEmails.Where(IsEmail).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = Normalize(email);
                if (await db.Invites.AnyAsync(i => i.NormalizedEmail == normalized && i.TeamId == null)) continue;
                var invite = new Invite { Id = Guid.NewGuid(), Email = email.Trim(), NormalizedEmail = normalized };
                Renew(invite);
                db.Invites.Add(invite);
                moved++;
            }
            await db.SaveChangesAsync();
            current.AllowedEmails = [];
            settings.Save(current);
            return moved;
        }

        // ---- Who may ----------------------------------------------------------------------

        private sealed record InviterRights(string? Email, bool IsInstanceAdmin, string? TeamRole, bool TeamAdminsCanInvite)
        {
            public bool IsTeamAdmin => TeamRoles.CanAdmin(TeamRole);
            public bool CanAddToTeam => IsInstanceAdmin || TeamRoles.CanWrite(TeamRole);
            public bool CanGrantAdmin => IsInstanceAdmin || IsTeamAdmin;
            public bool CanInviteNewUsers => IsInstanceAdmin || (IsTeamAdmin && TeamAdminsCanInvite);
        }

        private async Task<InviterRights> Inviter(string userId, Guid? teamId)
        {
            var user = await userManager.FindByIdAsync(userId) ?? throw new InviteException("Sign in again to invite.");
            var isAdmin = await userManager.IsInRoleAsync(user, "Admin");
            var role = teamId is { } t ? await teams.GetRoleAsync(t, userId) : null;
            return new InviterRights(user.Email, isAdmin, role, settings.Load().TeamAdminsCanInvite);
        }

        /// <summary>Whether the user may see and manage pending invites to new people on this team:
        /// the same rule as sending one.</summary>
        public async Task<bool> CanInviteNewUsersAsync(string userId, Guid? teamId)
        {
            try { return (await Inviter(userId, teamId)).CanInviteNewUsers; }
            catch (InviteException) { return false; }
        }

        /// <summary>An invite the user may resend or revoke: an instance admin any; a team's
        /// editors and admins those to their team.</summary>
        private async Task<Invite> Managed(Guid inviteId, string userId)
        {
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.Id == inviteId) ?? throw new InviteException("The invite no longer exists.");
            var inviter = await Inviter(userId, invite.TeamId);
            if (!(invite.TeamId == null ? inviter.IsInstanceAdmin : inviter.CanAddToTeam))
                throw new InviteException("You can't change this invite.");
            return invite;
        }

        // ---- Plumbing ----------------------------------------------------------------------

        private string InstanceName => settings.Load().InstanceName;

        private string Normalize(string email) => userManager.NormalizeEmail(email.Trim()) ?? email.Trim().ToUpperInvariant();

        private static bool IsEmail(string email) => MailAddress.TryCreate(email.Trim(), out var a) && a.Address == email.Trim();

        /// <summary>A new token and expiry on the invite; returns the token, which only the email carries.</summary>
        private static string Renew(Invite invite)
        {
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            invite.TokenHash = Hash(token);
            invite.CreatedAt = DateTime.UtcNow;
            invite.ExpiresAt = invite.CreatedAt + Lifetime;
            return token;
        }

        private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        private async Task<List<Pending>> List(IQueryable<Invite> invites)
        {
            var rows = await invites.Include(i => i.Team).OrderBy(i => i.Email).ToListAsync();
            var inviterIds = rows.Select(i => i.InvitedByUserId).OfType<string>().Distinct().ToList();
            var emails = await db.Users.Where(u => inviterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email);
            return rows.Select(i => new Pending(i.Id, i.Email, i.TeamId, i.Team?.Name, i.Role,
                i.InvitedByUserId is { } by ? emails.GetValueOrDefault(by) : null, i.CreatedAt, i.ExpiresAt)).ToList();
        }

        private Task<bool> SendInviteAsync(Invite invite, Team? team, string? inviterEmail, string token, string registerUrl)
        {
            var link = QueryHelpers.AddQueryString(registerUrl, TokenParameter, token);
            return SendAsync(invite.Email,
                EmailTemplates.Subject(InstanceName, team == null ? "You have been invited" : $"You have been invited to team \"{team.Name}\""),
                EmailTemplates.Invite(InstanceName, link, teamName: team?.Name, inviterEmail: inviterEmail));
        }

        private async Task<bool> SendAsync(string to, string subject, string html)
        {
            try
            {
                await emailSender.SendEmailAsync(to, subject, html);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not send email to {Email}", to);
                return false;
            }
        }
    }
}
