using IndxServer.Engine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indx.Storage;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// Server-owned persistence for a dataset's field profiles (<see cref="DatasetFieldProfiles"/>):
    /// its own profiles and which profile each field has. One JSON row per dataset in its own
    /// table (<c>DatasetFieldProfiles</c>) inside indx.db, the pattern of
    /// <see cref="QueryParameterStore"/>, because <c>ISearchEngine</c> has no concept of a profile:
    /// the values a profile stands for live on the engine's fields. Keyed by (TeamId,
    /// DatasetName); read by the console only, cached, writes invalidate.
    /// </summary>
    public class FieldProfileStore(ILogger<FieldProfileStore> logger)
    {
        private const string Table = "DatasetFieldProfiles";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        // (teamId\0dataset) -> profiles, or null for none. Invalidate on write; never mutate.
        private readonly StoreCache<string, DatasetFieldProfiles?> _cache = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";
        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        // Never open a connection that would create indx.db first: see BoostRuleStore.DbExists.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Creates the table if the search DB already exists. Idempotent; call once at startup.</summary>
        public void EnsureTable()
        {
            if (!DbExists()) return; // fresh instance: created on first Save instead.
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $@"CREATE TABLE IF NOT EXISTS {Table} (
                           TeamId       TEXT NOT NULL,
                           DatasetName  TEXT NOT NULL,
                           ProfilesJson TEXT NOT NULL,
                           UpdatedUtc   TEXT NOT NULL,
                           PRIMARY KEY (TeamId, DatasetName)
                       );";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure {Table} table", Table);
            }
        }

        /// <summary>The dataset's profiles and assignments, or null when it has none. Cached after
        /// first read. The instance is shared: callers copy before changing it.</summary>
        public DatasetFieldProfiles? Load(string teamId, string dataSetName) =>
            _cache.Get(Key(teamId, dataSetName), () => ReadFromDb(teamId, dataSetName));

        private DatasetFieldProfiles? ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return null;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT ProfilesJson FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(json)) return null;
                var stored = JsonSerializer.Deserialize<DatasetFieldProfiles>(json, JsonOptions);
                if (stored != null)
                    stored.Assignments = new Dictionary<string, string>(stored.Assignments, StringComparer.Ordinal);
                return stored;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // no such table: nothing saved yet
            {
                return null;
            }
            catch (Exception ex)
            {
                // A row that cannot be read is treated as none: every field shows as Custom, and the
                // fields' own values, which are what searches use, are untouched.
                StoreRead.Failed(ex);
                logger.LogError(ex, "Failed to read field profiles for {Team}/{Dataset}", teamId, dataSetName);
                return null;
            }
        }

        /// <summary>Replaces the dataset's profiles, assignments and default. None of any removes the row.
        /// A built-in as shipped is never stored: it is code. One this dataset edited is, under the
        /// built-in's id, and takes its place here (FieldProfiles.All).</summary>
        public void Save(string teamId, string dataSetName, DatasetFieldProfiles profiles)
        {
            var stored = new DatasetFieldProfiles
            {
                Profiles = profiles.Profiles.Where(p => !p.BuiltIn).ToList(),
                Assignments = new Dictionary<string, string>(profiles.Assignments, StringComparer.Ordinal),
                // The default chosen (6 Nov 2026). Left out of the copy that day, so a default set in
                // the Profile manager was back to Name once the cache was read again.
                DefaultProfileId = profiles.DefaultProfileId,
            };
            if (stored.Profiles.Count == 0 && stored.Assignments.Count == 0 && stored.DefaultProfileId == null)
            {
                Delete(teamId, dataSetName);
                return;
            }
            new SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, ProfilesJson, UpdatedUtc)
                   VALUES ($t, $d, $j, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET ProfilesJson = $j, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(stored, JsonOptions));
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _cache.Invalidate(Key(teamId, dataSetName));
        }

        public void Delete(string teamId, string dataSetName)
        {
            if (!DbExists()) { _cache.Invalidate(Key(teamId, dataSetName)); return; }
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { } // no such table: nothing to delete
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete field profiles for {Team}/{Dataset}", teamId, dataSetName);
            }
            finally
            {
                _cache.Invalidate(Key(teamId, dataSetName));
            }
        }

        /// <summary>Moves a dataset's profiles to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            if (Load(fromTeamId, dataSetName) is { } p) Save(toTeamId, dataSetName, p);
            Delete(fromTeamId, dataSetName);
        }

        /// <summary>Re-keys the profiles under the dataset's new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName)
        {
            if (Load(teamId, dataSetName) is { } p) Save(teamId, newName, p);
            Delete(teamId, dataSetName);
        }
    }
}
