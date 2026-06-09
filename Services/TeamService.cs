using IndxCloudApi.Data;
using Microsoft.EntityFrameworkCore;

namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Server-side team and membership management. Team CRUD is intentionally NOT exposed over
    /// HTTP — the Blazor pages (and registration) call this service directly. The team
    /// <see cref="Team.Name"/> is the only human-facing name; it is globally unique and URL-safe.
    /// </summary>
    public class TeamService(ApplicationDbContext db)
    {
        /// <summary>Thrown when an operation would violate a team invariant (name taken, last admin, etc.).</summary>
        public sealed class TeamException(string message) : Exception(message);

        // ---- Reads -------------------------------------------------------------------------

        public Task<Team?> GetByNameAsync(string name) =>
            db.Teams.FirstOrDefaultAsync(t => t.Name == name);

        public Task<Team?> GetByIdAsync(Guid teamId) =>
            db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);

        // Synchronous variants for the request-path resolver. SQLite is a synchronous provider,
        // so these don't block a thread-pool thread on real I/O.
        public Team? GetByName(string name) =>
            db.Teams.FirstOrDefault(t => t.Name == name);

        public string? GetRole(Guid teamId, string userId) =>
            db.TeamMembers.FirstOrDefault(m => m.TeamId == teamId && m.UserId == userId)?.Role;

        /// <summary>All teams the user belongs to, with the user's role on each.</summary>
        public async Task<List<(Team Team, string Role)>> GetTeamsForUserAsync(string userId)
        {
            var rows = await db.TeamMembers
                .Where(m => m.UserId == userId)
                .Join(db.Teams, m => m.TeamId, t => t.Id, (m, t) => new { t, m.Role })
                .OrderBy(x => x.t.Name)
                .ToListAsync();
            return rows.Select(x => (x.t, x.Role)).ToList();
        }

        public async Task<string?> GetRoleAsync(Guid teamId, string userId) =>
            (await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId))?.Role;

        public Task<List<TeamMember>> GetMembersAsync(Guid teamId) =>
            db.TeamMembers.Where(m => m.TeamId == teamId).ToListAsync();

        /// <summary>All teams (admin view).</summary>
        public Task<List<Team>> GetAllAsync() =>
            db.Teams.OrderBy(t => t.Name).ToListAsync();

        /// <summary>All teams with their member counts, ordered by name (admin view).</summary>
        public async Task<List<(Team Team, int MemberCount)>> GetAllWithMemberCountsAsync()
        {
            var teams = await db.Teams.OrderBy(t => t.Name).ToListAsync();
            var counts = await db.TeamMembers
                .GroupBy(m => m.TeamId)
                .Select(g => new { TeamId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TeamId, x => x.Count);
            return teams.Select(t => (t, counts.GetValueOrDefault(t.Id))).ToList();
        }

        // ---- Team CRUD ---------------------------------------------------------------------

        /// <summary>
        /// Creates a team with the given (raw) name — sanitised to a URL-safe, globally-unique
        /// slug — and makes <paramref name="ownerUserId"/> its first Admin. Both rows commit together.
        /// </summary>
        public async Task<Team> CreateTeamAsync(string rawName, string ownerUserId)
        {
            var name = await EnsureUniqueNameAsync(TeamSlug.Sanitize(rawName));
            var team = new Team { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow };
            db.Teams.Add(team);
            db.TeamMembers.Add(new TeamMember
            {
                TeamId = team.Id, UserId = ownerUserId, Role = TeamRoles.Admin, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return team;
        }

        /// <summary>
        /// Creates the personal team for a freshly-registered user. Name comes from the optional
        /// form field, else the email prefix; always sanitised and made unique.
        /// </summary>
        public Task<Team> CreatePersonalTeamAsync(ApplicationUser user, string? requestedName)
        {
            var basis = !string.IsNullOrWhiteSpace(requestedName)
                ? requestedName
                : (user.Email ?? user.UserName ?? "team").Split('@')[0];
            return CreateTeamAsync(basis, user.Id);
        }

        /// <summary>Renames a team. Surrogate Id is the key, so no references need rewriting.</summary>
        public async Task RenameTeamAsync(Guid teamId, string newRawName)
        {
            var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId)
                       ?? throw new TeamException("Team not found.");
            var newName = TeamSlug.Sanitize(newRawName);
            if (newName != team.Name)
                team.Name = await EnsureUniqueNameAsync(newName);
            await db.SaveChangesAsync();
        }

        /// <summary>Deletes a team. Membership rows cascade; dataset reassignment is the caller's concern.</summary>
        public async Task DeleteTeamAsync(Guid teamId)
        {
            var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
            if (team == null) return;
            db.Teams.Remove(team);
            await db.SaveChangesAsync();
        }

        // ---- Membership --------------------------------------------------------------------

        public async Task AddMemberAsync(Guid teamId, string userId, string role)
        {
            if (!TeamRoles.IsValid(role))
                throw new TeamException($"Invalid role '{role}'.");

            var existing = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId);
            if (existing != null)
            {
                existing.Role = role; // idempotent add doubles as role change
            }
            else
            {
                db.TeamMembers.Add(new TeamMember
                {
                    TeamId = teamId, UserId = userId, Role = role, CreatedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        public async Task ChangeRoleAsync(Guid teamId, string userId, string role)
        {
            if (!TeamRoles.IsValid(role))
                throw new TeamException($"Invalid role '{role}'.");

            var member = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId)
                         ?? throw new TeamException("User is not a member of this team.");

            if (member.Role == TeamRoles.Admin && role != TeamRoles.Admin && await IsLastAdminAsync(teamId, userId))
                throw new TeamException("Cannot demote the last Admin of the team.");

            member.Role = role;
            await db.SaveChangesAsync();
        }

        public async Task RemoveMemberAsync(Guid teamId, string userId)
        {
            var member = await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId);
            if (member == null) return;

            if (member.Role == TeamRoles.Admin && await IsLastAdminAsync(teamId, userId))
                throw new TeamException("Cannot remove the last Admin of the team.");

            db.TeamMembers.Remove(member);
            await db.SaveChangesAsync();
        }

        // ---- Helpers -----------------------------------------------------------------------

        /// <summary>True if <paramref name="userId"/> is the only Admin left on the team.</summary>
        public async Task<bool> IsLastAdminAsync(Guid teamId, string userId)
        {
            var otherAdmins = await db.TeamMembers
                .CountAsync(m => m.TeamId == teamId && m.Role == TeamRoles.Admin && m.UserId != userId);
            return otherAdmins == 0;
        }

        /// <summary>
        /// Returns <paramref name="baseName"/> if free, else appends -2, -3, … until unique.
        /// The unique index on Team.Name is the final guard against races.
        /// </summary>
        private async Task<string> EnsureUniqueNameAsync(string baseName)
        {
            if (!await db.Teams.AnyAsync(t => t.Name == baseName))
                return baseName;

            for (var i = 2; ; i++)
            {
                var suffix = "-" + i;
                var candidate = baseName.Length + suffix.Length > TeamSlug.MaxLength
                    ? baseName[..(TeamSlug.MaxLength - suffix.Length)] + suffix
                    : baseName + suffix;
                if (!await db.Teams.AnyAsync(t => t.Name == candidate))
                    return candidate;
            }
        }
    }
#pragma warning restore 1591
}
