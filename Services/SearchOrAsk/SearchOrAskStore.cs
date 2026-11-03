using IndxServer.Engine;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indx.Storage;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// Server-owned persistence for a dataset's Search or ask settings: one JSON row per dataset in
    /// its own table (<c>DatasetSearchOrAsk</c>) inside indx.db, the same pattern as
    /// <see cref="QueryParameterStore"/>, because <c>ISearchEngine</c> has no concept of it. Keyed by
    /// (TeamId, DatasetName); read on every intent call, so cached; writes invalidate.
    /// </summary>
    public class SearchOrAskStore(ILogger<SearchOrAskStore> logger)
    {
        private const string Table = "DatasetSearchOrAsk";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // (teamId\0dataset) -> settings, or null for "none saved". Invalidate on write; never mutate.
        private readonly ConcurrentDictionary<string, SearchOrAskSettings?> _cache = new();

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
                           SettingsJson TEXT NOT NULL,
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

        /// <summary>The dataset's settings, or null when none are saved (the feature is off). Cached
        /// after first read. The instance is shared: callers must not modify it.</summary>
        public SearchOrAskSettings? Load(string teamId, string dataSetName) =>
            _cache.GetOrAdd(Key(teamId, dataSetName), _ => ReadFromDb(teamId, dataSetName));

        private SearchOrAskSettings? ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return null;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT SettingsJson FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SearchOrAskSettings>(json, JsonOptions);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // no such table: nothing saved yet
            {
                return null;
            }
            catch (Exception ex)
            {
                // A row that cannot be read is treated as none saved: the feature is off, which is
                // the safe reading of a setting that sends visitors' text to a third party.
                logger.LogError(ex, "Failed to read Search or ask settings for {Team}/{Dataset}", teamId, dataSetName);
                return null;
            }
        }

        /// <summary>Replaces the dataset's settings. Validation (<see cref="SearchOrAskSettings.Validate"/>)
        /// is the caller's.</summary>
        public void Save(string teamId, string dataSetName, SearchOrAskSettings settings)
        {
            new SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, SettingsJson, UpdatedUtc)
                   VALUES ($t, $d, $j, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET SettingsJson = $j, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(settings, JsonOptions));
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _cache.TryRemove(Key(teamId, dataSetName), out _);
        }

        /// <summary>Removes the dataset's settings (on dataset delete, or DELETE) and invalidates the cache.</summary>
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
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { } // no such table: nothing to delete
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete Search or ask settings for {Team}/{Dataset}", teamId, dataSetName);
            }
            finally
            {
                _cache.TryRemove(Key(teamId, dataSetName), out _);
            }
        }

        /// <summary>Moves a dataset's settings to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            if (Load(fromTeamId, dataSetName) is { } s) Save(toTeamId, dataSetName, s);
            Delete(fromTeamId, dataSetName);
        }

        /// <summary>Re-keys the settings under the dataset's new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName)
        {
            if (Load(teamId, dataSetName) is { } s) Save(teamId, newName, s);
            Delete(teamId, dataSetName);
        }
    }
}
