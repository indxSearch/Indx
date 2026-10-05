using IndxServer.Engine;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IndxServer.Data;
using IndxServer.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace IndxServer.Services
{
    /// <summary>
    /// Creates named API keys. Every key made here is scoped: one team, optionally a list of
    /// datasets in it, and a level (<see cref="ApiKeyLevel"/>). The scope is signed into the JWT,
    /// so <see cref="ApiKeyScopeFilter"/> can enforce it without a database round-trip; the
    /// <see cref="ApiKey"/> row keeps a copy for display and the jti for revocation.
    /// </summary>
    public sealed class ApiKeyService(ApplicationDbContext db, UserManager<ApplicationUser> users, TeamService teams,
        IConfiguration config, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, ILogger<ApiKeyService> logger)
    {
        /// <summary>
        /// A key that "never expires" is still a JWT, and the bearer handler requires an expiry:
        /// it gets one a century out rather than a change to how every other token is validated.
        /// </summary>
        private const int NeverExpiresDays = 36500;

        public sealed record Created(string Token, ApiKey Record);

        /// <summary>
        /// Creates a key, or throws <see cref="ArgumentException"/> with a message fit to show the
        /// user when the request cannot be honoured.
        /// </summary>
        public async Task<Created> CreateAsync(ApplicationUser user, string name, int expirationDays,
            ApiKeyLevel level, Guid teamId, IReadOnlyCollection<string>? datasets)
        {
            name = name?.Trim() ?? "";
            if (name.Length == 0) throw new ArgumentException("Enter a name for the key.");
            if (name.Length > 100) throw new ArgumentException("The name can be at most 100 characters.");
            if (expirationDays is < 1 or > 366) throw new ArgumentException("Expiration must be between 1 and 366 days.");
            if (!Enum.IsDefined(level)) throw new ArgumentException("Unknown access level.");

            var role = teams.GetRole(teamId, user.Id)
                ?? throw new ArgumentException("You are not a member of that team.");

            // A key never exceeds its owner's role — the role check runs on every request anyway —
            // but refusing a Full key a Viewer could never use keeps the key list honest.
            if (level == ApiKeyLevel.Full && !TeamRoles.CanWrite(role))
                throw new ArgumentException("A Full access key needs the Editor or Admin role in the team. Viewers can create Search or Read keys.");

            var names = ValidDatasets(teamId, datasets);
            var jti = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;   // one reading: CreatedAt and ExpiresAt must be exactly the lifetime apart
            var token = await MintAsync(user, expirationDays, jti, level, teamId, names);
            var record = new ApiKey
            {
                UserId = user.Id,
                Name = name,
                KeyPrefix = token[..Math.Min(24, token.Length)],
                KeySuffix = token[^4..],
                SealedToken = CanBeShownAgain(level) ? ApiKeySealer.Seal(token, JwtKey) : null,
                Jti = jti,
                Level = level.ToString(),
                TeamId = teamId,
                Datasets = names == null ? null : JsonSerializer.Serialize(names),
                CreatedAt = now,
                ExpiresAt = now.AddDays(expirationDays),
                IsRevoked = false,
            };
            db.ApiKeys.Add(record);
            await db.SaveChangesAsync();
            logger.LogInformation("API key \"{Name}\" created by {User}: {Level}, team {Team}, expires {Expires:yyyy-MM-dd}",
                record.Name, user.Email, record.Level, await TeamNameAsync(teamId), record.ExpiresAt);
            return new Created(token, record);
        }

        /// <summary>
        /// Creates a team key: owned by the team, not by <paramref name="creator"/>, so it keeps
        /// working when the creator leaves or changes role. Only a team Admin may create one, and it
        /// acts with <see cref="ApiKeyScope.TeamKeyRole"/>. <paramref name="expirationDays"/> null
        /// means it never expires, which only a Search key may do: that key is public by design,
        /// so an expiry mostly schedules an outage, and revoking it is the real control.
        /// </summary>
        public async Task<Created> CreateTeamKeyAsync(ApplicationUser creator, string name, int? expirationDays,
            ApiKeyLevel level, Guid teamId, IReadOnlyCollection<string>? datasets)
        {
            name = name?.Trim() ?? "";
            if (name.Length == 0) throw new ArgumentException("Enter a name for the key.");
            if (name.Length > 100) throw new ArgumentException("The name can be at most 100 characters.");
            if (!Enum.IsDefined(level)) throw new ArgumentException("Unknown access level.");
            if (expirationDays == null && level != ApiKeyLevel.Search)
                throw new ArgumentException("Only a Search key can be set to never expire. Keys that read or change data need an expiry.");
            if (expirationDays is < 1 or > 366) throw new ArgumentException("Expiration must be between 1 and 366 days.");
            RequireTeamAdmin(teamId, creator.Id);

            var names = ValidDatasets(teamId, datasets);
            var jti = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;   // one reading: CreatedAt and ExpiresAt must be exactly the lifetime apart
            var token = Mint(
                [
                    new(ClaimTypes.Name, name),
                    new(JwtRegisteredClaimNames.Jti, jti),
                    .. ApiKeyScope.ClaimsFor(level, teamId, names, teamKey: true),
                ],
                expirationDays ?? NeverExpiresDays);
            var record = new ApiKey
            {
                UserId = null,
                CreatedByUserId = creator.Id,
                Name = name,
                KeyPrefix = token[..Math.Min(24, token.Length)],
                KeySuffix = token[^4..],
                SealedToken = CanBeShownAgain(level) ? ApiKeySealer.Seal(token, JwtKey) : null,
                Jti = jti,
                Level = level.ToString(),
                TeamId = teamId,
                Datasets = names == null ? null : JsonSerializer.Serialize(names),
                CreatedAt = now,
                ExpiresAt = expirationDays is { } days ? now.AddDays(days) : null,
                IsRevoked = false,
            };
            db.ApiKeys.Add(record);
            await db.SaveChangesAsync();
            logger.LogInformation("Team key \"{Name}\" created in {Team} by {User}: {Level}, expires {Expires}",
                record.Name, await TeamNameAsync(teamId), creator.Email, record.Level, record.ExpiresAt?.ToString("yyyy-MM-dd") ?? "never");
            return new Created(token, record);
        }

        public Task<List<ApiKey>> ListAsync(string userId) =>
            db.ApiKeys.Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt).ToListAsync();

        /// <summary>The team's keys, for its Admins. Personal keys scoped to the team are not listed.</summary>
        public Task<List<ApiKey>> ListTeamKeysAsync(Guid teamId) =>
            db.ApiKeys.Where(k => k.TeamId == teamId && k.UserId == null).OrderByDescending(k => k.CreatedAt).ToListAsync();

        /// <summary>
        /// Revokes a team key. Any Admin of the team may, whoever created it: that is the point of
        /// a key the team owns. It stops working at once, not when the revocation cache expires.
        /// </summary>
        public async Task RevokeTeamKeyAsync(Guid teamId, int keyId, string actingUserId)
        {
            RequireTeamAdmin(teamId, actingUserId);
            var row = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.TeamId == teamId && k.UserId == null)
                ?? throw new ArgumentException("That key does not belong to this team.");
            if (row.IsRevoked) return;
            row.IsRevoked = true;
            await db.SaveChangesAsync();
            TokenValidationCache.EvictJti(cache, row.Jti);
            logger.LogInformation("Team key \"{Name}\" in {Team} revoked by {User}",
                row.Name, await TeamNameAsync(teamId), await EmailAsync(actingUserId));
        }

        /// <summary>
        /// Shows a Search or Read key again. A personal key to its owner, a team key to its team's
        /// Admins, and only while it still works. Full keys are never stored, so they cannot be:
        /// losing one means creating a new key and revoking the old.
        /// </summary>
        /// <summary>Which keys are stored, encrypted, so they can be shown again. Search keys sit in
        /// public web pages, and a Read key's reader is its owner or the team's Admins, who could
        /// mint a new one anyway (Anders, 5 Oct 2026; until then Search only). Full keys change and
        /// delete data, and stay shown once.</summary>
        public static bool CanBeShownAgain(ApiKeyLevel level) => level is ApiKeyLevel.Search or ApiKeyLevel.Read;

        public async Task<string> RevealAsync(int keyId, string actingUserId)
        {
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId)
                ?? throw new ArgumentException("That key does not exist.");
            if (key.IsTeamKey) RequireTeamAdmin(key.TeamId!.Value, actingUserId);
            else if (key.UserId != actingUserId) throw new ArgumentException("That key does not exist.");

            if (key.IsRevoked || key.ExpiresAt < DateTime.UtcNow)
                throw new ArgumentException("That key no longer works, so there is nothing to show.");
            if (key.SealedToken == null)
                throw new ArgumentException(key.Level is nameof(ApiKeyLevel.Search) or nameof(ApiKeyLevel.Read)
                    ? "This key was created before it could be shown again. Create a new one if you have lost it."
                    : "Full keys are shown once, because they can change and delete data: create a new one and revoke this.");
            var token = ApiKeySealer.Open(key.SealedToken, JwtKey)
                ?? throw new ArgumentException("This key can no longer be shown: the server's signing key has changed since it was created.");
            logger.LogInformation("{Kind} \"{Name}\" in {Team} shown again to {User}",
                key.IsTeamKey ? "Team key" : "API key", key.Name, await TeamNameAsync(key.TeamId), await EmailAsync(actingUserId));
            return token;
        }

        // Log lines name things, not ids: they are read in the terminal monitor and the log file,
        // where a GUID tells the reader nothing.
        private async Task<string> TeamNameAsync(Guid? teamId) =>
            teamId is { } id && await teams.GetByIdAsync(id) is { } team ? team.Name : "an unknown team";

        private async Task<string> EmailAsync(string userId) =>
            (await users.FindByIdAsync(userId))?.Email ?? "an unknown user";

        private string JwtKey => config["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key is not configured");

        private void RequireTeamAdmin(Guid teamId, string userId)
        {
            if (!TeamRoles.CanAdmin(teams.GetRole(teamId, userId)))
                throw new ArgumentException("Only a team Admin can manage the team's API keys.");
        }

        private static string[]? ValidDatasets(Guid teamId, IReadOnlyCollection<string>? datasets)
        {
            if (datasets is not { Count: > 0 }) return null;
            var existing = IndxServerInternalApi.Manager.GetTeamDataSets(teamId.ToString()).ToHashSet(StringComparer.Ordinal);
            var names = datasets.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var unknown = names.Where(n => !existing.Contains(n)).ToArray();
            if (unknown.Length > 0)
                throw new ArgumentException($"Not a dataset in that team: {string.Join(", ", unknown)}.");
            return names.Length == 0 ? null : names;
        }

        private async Task<string> MintAsync(ApplicationUser user, int expirationDays, string jti,
            ApiKeyLevel level, Guid teamId, IReadOnlyCollection<string>? datasets)
        {
            // No security-stamp claim: a named key survives its owner's password change and is
            // retired by revocation instead (see the JwtBearer OnTokenValidated handler).
            var claims = new List<Claim>
            {
                new(ClaimTypes.Email, user.Email ?? ""),
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Name, user.UserName ?? user.Email ?? ""),
                new(JwtRegisteredClaimNames.Sub, user.Id),
                new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
                new(JwtRegisteredClaimNames.Jti, jti),
            };
            claims.AddRange(ApiKeyScope.ClaimsFor(level, teamId, datasets));
            foreach (var role in await users.GetRolesAsync(user))
                claims.Add(new Claim(ClaimTypes.Role, role));
            return Mint(claims, expirationDays);
        }

        private string Mint(IEnumerable<Claim> claims, int expirationDays)
        {
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: config["Jwt:Issuer"],
                audience: config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddDays(expirationDays),
                signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>"Search only · docs-team · IndxDocs500, IndxDocsChunks" for the key list.</summary>
        public static string DescribeScope(ApiKey key, string? teamName)
        {
            if (key.TeamId == null) return "Full access · all your teams (created before key scopes)";
            var level = Enum.TryParse<ApiKeyLevel>(key.Level, out var l) ? ApiKeyScope.Describe(l) : key.Level;
            var datasets = key.Datasets == null
                ? "all datasets"
                : string.Join(", ", JsonSerializer.Deserialize<string[]>(key.Datasets) ?? []);
            return $"{level} · {teamName ?? "unknown team"} · {datasets}";
        }
    }
}
