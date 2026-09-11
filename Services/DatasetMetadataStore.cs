using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace IndxCloudApi.Services
{
    /// <summary>
    /// Cloud-owned per-dataset metadata. Holds an owner-authored <c>Description</c> (used by the MCP
    /// <c>describe_dataset</c> tool to give agents domain context) and the declared <c>KeyField</c>
    /// (the JSON field whose value is the document's primary key — see <c>indx_dataset_primary_key.md</c>).
    /// The lib does not persist the key-field name anywhere, so the cloud owns it and re-applies it
    /// before each external Load. Stored in its own table in indx.db — same pattern as
    /// <see cref="BoostRuleStore"/>, so the lib (SqLiteManager) is untouched. Keyed by
    /// (TeamId, DatasetName); cached in memory; writes invalidate the cache.
    /// </summary>
    public class DatasetMetadataStore(ILogger<DatasetMetadataStore> logger)
    {
        private const string Table = "DatasetMetadata";

        private readonly ConcurrentDictionary<string, string> _cache = new();
        private readonly ConcurrentDictionary<string, string> _keyFieldCache = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";
        private static string DbPath => IndxCloudApi.Models.IndxCloudInternalApi.SearchDbConnectionString;
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Creates the metadata table if the search DB already exists. Idempotent; call once at startup.</summary>
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
                           TeamId      TEXT NOT NULL,
                           DatasetName TEXT NOT NULL,
                           Description TEXT NOT NULL,
                           KeyField    TEXT NOT NULL DEFAULT '',
                           UpdatedUtc  TEXT NOT NULL,
                           PRIMARY KEY (TeamId, DatasetName)
                       );";
                cmd.ExecuteNonQuery();

                // Migrate older DBs that predate the KeyField column. SQLite has no "ADD COLUMN IF
                // NOT EXISTS", so just try and ignore the duplicate-column error.
                try
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE {Table} ADD COLUMN KeyField TEXT NOT NULL DEFAULT '';";
                    alter.ExecuteNonQuery();
                }
                catch (SqliteException) { /* column already exists */ }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure {Table} table", Table);
            }
        }

        /// <summary>The dataset's description, or empty string if none. Cached after first read.</summary>
        public string Load(string teamId, string dataSetName)
        {
            return _cache.GetOrAdd(Key(teamId, dataSetName), _ => ReadFromDb(teamId, dataSetName));
        }

        private string ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return "";
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT Description FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                return cmd.ExecuteScalar() as string ?? "";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read metadata for {Team}/{Dataset}", teamId, dataSetName);
                return "";
            }
        }

        /// <summary>Sets the dataset description and invalidates the cache.</summary>
        public void Save(string teamId, string dataSetName, string description)
        {
            new Indx.Storage.SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            // KeyField defaults to '' only when the row is first created; the ON CONFLICT branch
            // updates Description alone so a previously declared key field is preserved.
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, Description, KeyField, UpdatedUtc)
                   VALUES ($t, $d, $x, '', $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET Description = $x, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$x", description ?? "");
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _cache.TryRemove(Key(teamId, dataSetName), out _);
        }

        /// <summary>
        /// The dataset's declared key-field name, or empty string if none is declared (the engine then
        /// falls back to its default "id"/auto-increment behaviour). Cached after first read.
        /// </summary>
        public string LoadKeyField(string teamId, string dataSetName)
        {
            return _keyFieldCache.GetOrAdd(Key(teamId, dataSetName), _ => ReadKeyFieldFromDb(teamId, dataSetName));
        }

        private string ReadKeyFieldFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return "";
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT KeyField FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                return cmd.ExecuteScalar() as string ?? "";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read key field for {Team}/{Dataset}", teamId, dataSetName);
                return "";
            }
        }

        /// <summary>
        /// Declares the dataset's key field (empty string = none / auto-generated) and invalidates the
        /// cache. Validation (field exists, numeric) is the caller's responsibility.
        /// </summary>
        public void SaveKeyField(string teamId, string dataSetName, string keyField)
        {
            new Indx.Storage.SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, Description, KeyField, UpdatedUtc)
                   VALUES ($t, $d, '', $k, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET KeyField = $k, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$k", keyField ?? "");
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _keyFieldCache.TryRemove(Key(teamId, dataSetName), out _);
        }

        /// <summary>Removes a dataset's description (on dataset delete) and invalidates the cache.</summary>
        public void Delete(string teamId, string dataSetName)
        {
            if (!DbExists()) { _cache.TryRemove(Key(teamId, dataSetName), out _); _keyFieldCache.TryRemove(Key(teamId, dataSetName), out _); return; }
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete metadata for {Team}/{Dataset}", teamId, dataSetName);
            }
            finally
            {
                _cache.TryRemove(Key(teamId, dataSetName), out _);
                _keyFieldCache.TryRemove(Key(teamId, dataSetName), out _);
            }
        }

        /// <summary>Moves a dataset's metadata (description + key field) to a new owning team.</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            var description = Load(fromTeamId, dataSetName);
            var keyField = LoadKeyField(fromTeamId, dataSetName);
            if (!string.IsNullOrEmpty(description)) Save(toTeamId, dataSetName, description);
            if (!string.IsNullOrEmpty(keyField)) SaveKeyField(toTeamId, dataSetName, keyField);
            Delete(fromTeamId, dataSetName);
        }

        /// <summary>Re-keys the description and key field under the dataset's new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName)
        {
            var description = Load(teamId, dataSetName);
            var keyField = LoadKeyField(teamId, dataSetName);
            if (!string.IsNullOrEmpty(description)) Save(teamId, newName, description);
            if (!string.IsNullOrEmpty(keyField)) SaveKeyField(teamId, newName, keyField);
            Delete(teamId, dataSetName);
        }
    }
}
