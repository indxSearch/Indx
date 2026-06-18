using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace IndxCloudApi.Services
{
    /// <summary>
    /// Cloud-owned per-dataset metadata (currently an owner-authored description used by the MCP
    /// <c>describe_dataset</c> tool to give agents domain context). Stored in its own table in
    /// indx.db — same pattern as <see cref="BoostRuleStore"/>, so the lib (SqLiteManager) is untouched.
    /// Keyed by (TeamId, DatasetName); cached in memory; writes invalidate the cache.
    /// </summary>
    public class DatasetMetadataStore(ILogger<DatasetMetadataStore> logger)
    {
        private const string Table = "DatasetMetadata";

        private readonly ConcurrentDictionary<string, string> _cache = new();

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
                           UpdatedUtc  TEXT NOT NULL,
                           PRIMARY KEY (TeamId, DatasetName)
                       );";
                cmd.ExecuteNonQuery();
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
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, Description, UpdatedUtc)
                   VALUES ($t, $d, $x, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET Description = $x, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$x", description ?? "");
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _cache.TryRemove(Key(teamId, dataSetName), out _);
        }

        /// <summary>Removes a dataset's description (on dataset delete) and invalidates the cache.</summary>
        public void Delete(string teamId, string dataSetName)
        {
            if (!DbExists()) { _cache.TryRemove(Key(teamId, dataSetName), out _); return; }
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
            }
        }
    }
}
