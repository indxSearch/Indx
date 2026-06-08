namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Result of resolving a team-scoped request: the owning team and the requesting user's
    /// role on it. <see cref="TeamId"/> (as a string) is the owner key used by the storage layer.
    /// </summary>
    public sealed record TeamContext(Guid TeamId, string TeamName, string Role)
    {
        /// <summary>The owner key the storage layer expects (where it used to receive a userId).</summary>
        public string OwnerKey => TeamId.ToString();
    }

    /// <summary>
    /// Resolves <c>{teamName}</c> + the JWT user id into a <see cref="TeamContext"/>, checking
    /// membership server-side on every request — so removing a user from a team takes effect
    /// immediately, without waiting for their JWT to expire. Returns null when the team is
    /// unknown or the user is not a member (caller maps that to 403).
    /// </summary>
    public class TeamContextResolver(TeamService teams)
    {
        public async Task<TeamContext?> ResolveAsync(string teamName, string userId)
        {
            if (string.IsNullOrWhiteSpace(teamName) || string.IsNullOrWhiteSpace(userId))
                return null;

            var team = await teams.GetByNameAsync(teamName);
            if (team == null)
                return null;

            var role = await teams.GetRoleAsync(team.Id, userId);
            if (role == null)
                return null; // not a member

            return new TeamContext(team.Id, team.Name, role);
        }

        /// <summary>Synchronous resolve for the MVC request path (SQLite is a sync provider).</summary>
        public TeamContext? Resolve(string teamName, string userId)
        {
            if (string.IsNullOrWhiteSpace(teamName) || string.IsNullOrWhiteSpace(userId))
                return null;

            var team = teams.GetByName(teamName);
            if (team == null)
                return null;

            var role = teams.GetRole(team.Id, userId);
            if (role == null)
                return null;

            return new TeamContext(team.Id, team.Name, role);
        }
    }
#pragma warning restore 1591
}
