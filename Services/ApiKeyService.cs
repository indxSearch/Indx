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
    public sealed class ApiKeyService(ApplicationDbContext db, UserManager<ApplicationUser> users, TeamService teams, IConfiguration config)
    {
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

            string[]? names = null;
            if (datasets is { Count: > 0 })
            {
                var existing = IndxServerInternalApi.Manager.GetTeamDataSets(teamId.ToString()).ToHashSet(StringComparer.Ordinal);
                names = datasets.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                var unknown = names.Where(n => !existing.Contains(n)).ToArray();
                if (unknown.Length > 0)
                    throw new ArgumentException($"Not a dataset in that team: {string.Join(", ", unknown)}.");
                if (names.Length == 0) names = null;
            }

            var jti = Guid.NewGuid().ToString();
            var token = await MintAsync(user, expirationDays, jti, level, teamId, names);
            var record = new ApiKey
            {
                UserId = user.Id,
                Name = name,
                KeyPrefix = token[..Math.Min(24, token.Length)],
                Jti = jti,
                Level = level.ToString(),
                TeamId = teamId,
                Datasets = names == null ? null : JsonSerializer.Serialize(names),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(expirationDays),
                IsRevoked = false,
            };
            db.ApiKeys.Add(record);
            await db.SaveChangesAsync();
            return new Created(token, record);
        }

        public Task<List<ApiKey>> ListAsync(string userId) =>
            db.ApiKeys.Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt).ToListAsync();

        private async Task<string> MintAsync(ApplicationUser user, int expirationDays, string jti,
            ApiKeyLevel level, Guid teamId, IReadOnlyCollection<string>? datasets)
        {
            var jwtKey = config["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key is not configured");
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)), SecurityAlgorithms.HmacSha256);

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
