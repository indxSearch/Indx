using IndxCloudApi.Data;
using Indx.Storage;
using Microsoft.EntityFrameworkCore;

namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    /// <summary>
    /// One-time, idempotent migration from per-user dataset ownership to team ownership.
    /// Runs at startup before the search engines are warmed up. For every existing user it
    /// ensures a personal team exists, then rewrites the owner key of that user's datasets in
    /// indx.db from their user id to their team id (a single-row-per-table UPDATE via the
    /// existing <see cref="SqLiteManager.TransferOwnership"/>).
    ///
    /// Idempotency comes from the data shape, not a flag: after migration a dataset's owner key
    /// is a team id, which never matches a known user id, so a second run finds nothing to move.
    /// Users that already have a team (e.g. created via registration) are left as-is.
    /// </summary>
    public class DataMigrationService(
        ApplicationDbContext db,
        TeamService teams,
        ILogger<DataMigrationService> logger)
    {
        public async Task MigrateAsync(string searchConnectionString)
        {
            // 1. Ensure every user has a team and map userId -> owning team id.
            var users = await db.Users.ToListAsync();
            var userToTeam = new Dictionary<string, string>();
            foreach (var user in users)
            {
                var myTeams = await teams.GetTeamsForUserAsync(user.Id);
                var team = myTeams.Count > 0
                    ? myTeams[0].Team
                    : await teams.CreatePersonalTeamAsync(user, null);
                userToTeam[user.Id] = team.Id.ToString();
            }

            // 2. Move legacy datasets (owner key == a user id) onto that user's team.
            var sqlite = new SqLiteManager(searchConnectionString);
            if (!sqlite.DatabaseExists())
                return;

            foreach (var ownerKey in sqlite.GetUsers())
            {
                // Only legacy user-owned datasets have an owner key that maps to a user; team ids
                // (already-migrated) won't be in the map, so they're skipped.
                if (!userToTeam.TryGetValue(ownerKey, out var teamId) || ownerKey == teamId)
                    continue;

                foreach (var dataSetName in sqlite.GetUserDataSets(ownerKey))
                {
                    try
                    {
                        sqlite.TransferOwnership(dataSetName, ownerKey, teamId);
                        logger.LogInformation(
                            "Migrated dataset '{DataSet}' from user {User} to team {Team}",
                            dataSetName, ownerKey, teamId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to migrate dataset '{DataSet}' (user {User} -> team {Team})",
                            dataSetName, ownerKey, teamId);
                    }
                }
            }
        }
    }
#pragma warning restore 1591
}
