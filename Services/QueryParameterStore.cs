using IndxServer.Engine;
using IndxServer.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indx.Storage;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// Server-owned persistence for a dataset's query parameters (<see cref="DatasetQueryParameters"/>):
    /// the coverage values a search takes when its request leaves them out. One JSON row per dataset
    /// in its own table (<c>DatasetQueryParameters</c>) inside indx.db, the same pattern as
    /// <see cref="BoostRuleStore"/>, because <c>ISearchEngine</c> has no concept of a default for a
    /// query. Keyed by (TeamId, DatasetName); read on every search, so cached; writes invalidate.
    /// </summary>
    public class QueryParameterStore(ILogger<QueryParameterStore> logger)
    {
        private const string Table = "DatasetQueryParameters";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // (teamId\0dataset) -> parameters, or null for "none set". Invalidate on write; never mutate.
        private readonly ConcurrentDictionary<string, DatasetQueryParameters?> _cache = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";
        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        // Never open a connection that would create indx.db first: see BoostRuleStore.DbExists.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Where a save is reported as a change, so the Statistics tab can say why the
        /// truncation rate moved. Set by StatisticsService at startup; null with statistics off.</summary>
        public IDatasetChangeSink? ChangeSink { get; set; }

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
                           TeamId         TEXT NOT NULL,
                           DatasetName    TEXT NOT NULL,
                           ParametersJson TEXT NOT NULL,
                           UpdatedUtc     TEXT NOT NULL,
                           PRIMARY KEY (TeamId, DatasetName)
                       );";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure {Table} table", Table);
            }
        }

        /// <summary>The dataset's query parameters, or null when it sets none. Cached after first read.
        /// The instance is shared: callers must not modify it.</summary>
        public DatasetQueryParameters? Load(string teamId, string dataSetName) =>
            _cache.GetOrAdd(Key(teamId, dataSetName), _ => ReadFromDb(teamId, dataSetName));

        private DatasetQueryParameters? ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return null;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT ParametersJson FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<DatasetQueryParameters>(json, JsonOptions);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // no such table: nothing saved yet
            {
                return null;
            }
            catch (Exception ex)
            {
                // A row that cannot be read is treated as none set: searches fall back to the engine
                // defaults rather than failing.
                logger.LogError(ex, "Failed to read query parameters for {Team}/{Dataset}", teamId, dataSetName);
                return null;
            }
        }

        /// <summary>Replaces the dataset's query parameters. Parameters that set nothing remove the row.
        /// Validation (<see cref="QueryParameterResolution.Validate"/>) is the caller's.</summary>
        public void Save(string teamId, string dataSetName, DatasetQueryParameters parameters)
        {
            var before = Load(teamId, dataSetName);
            var stored = Normalize(parameters);
            if (stored == null)
            {
                Delete(teamId, dataSetName);
            }
            else
            {
                new SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
                EnsureTable();
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $@"INSERT INTO {Table} (TeamId, DatasetName, ParametersJson, UpdatedUtc)
                       VALUES ($t, $d, $j, $u)
                       ON CONFLICT(TeamId, DatasetName) DO UPDATE SET ParametersJson = $j, UpdatedUtc = $u;";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(stored, JsonOptions));
                cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
                _cache.TryRemove(Key(teamId, dataSetName), out _);
            }

            // The names of the values that changed, never the values: the Changes list says what
            // moved, and the console says to what.
            var changed = ChangedNames(before, stored);
            if (changed.Count > 0)
                ChangeSink?.Changed(teamId, dataSetName, DatasetChangeKind.QueryParameters, new { changed });
        }

        /// <summary>Removes the dataset's query parameters (on dataset delete) and invalidates the cache.</summary>
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
                logger.LogWarning(ex, "Failed to delete query parameters for {Team}/{Dataset}", teamId, dataSetName);
            }
            finally
            {
                _cache.TryRemove(Key(teamId, dataSetName), out _);
            }
        }

        /// <summary>Moves a dataset's query parameters to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            if (Load(fromTeamId, dataSetName) is { } p) Save(toTeamId, dataSetName, p);
            Delete(fromTeamId, dataSetName);
        }

        /// <summary>Re-keys the query parameters under the dataset's new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName)
        {
            if (Load(teamId, dataSetName) is { } p) Save(teamId, newName, p);
            Delete(teamId, dataSetName);
        }

        /// <summary>A copy holding only what is set, or null when nothing is.</summary>
        private static DatasetQueryParameters? Normalize(DatasetQueryParameters p)
        {
            if (QueryParameterResolution.SetNames(p).Count == 0) return null;
            var json = JsonSerializer.Serialize(p, JsonOptions);
            var copy = JsonSerializer.Deserialize<DatasetQueryParameters>(json, JsonOptions)!;
            if (copy.CoverageSetup != null && JsonSerializer.Serialize(copy.CoverageSetup, JsonOptions) == "{}")
                copy.CoverageSetup = null;
            return copy;
        }

        private static List<string> ChangedNames(DatasetQueryParameters? before, DatasetQueryParameters? after)
        {
            var a = QueryParameterResolution.Effective(before);
            var b = QueryParameterResolution.Effective(after);
            var setBefore = QueryParameterResolution.SetNames(before);
            var setAfter = QueryParameterResolution.SetNames(after);
            var names = new List<string>();
            foreach (var name in setBefore.Union(setAfter))
                if (Value(a, name) != Value(b, name) || setBefore.Contains(name) != setAfter.Contains(name))
                    names.Add(name);
            return names;
        }

        private static string? Value(DatasetQueryParameters p, string name)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(p, JsonOptions));
            var el = doc.RootElement;
            foreach (var part in name.Split('.'))
                if (!el.TryGetProperty(part, out el)) return null;
            return el.GetRawText();
        }
    }
}
