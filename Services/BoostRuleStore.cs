using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indx.Api;
using Indx.Storage;
using IndxCloudApi.Models;
using Microsoft.Data.Sqlite;

namespace IndxCloudApi.Services
{
    /// <summary>
    /// Cloud-owned persistence + apply for per-dataset boost rules. Stores the rule list as JSON
    /// in its own table (<c>DatasetBoostRules</c>) inside the search database (indx.db) — the same
    /// file the engine uses, but a table this layer owns, so the lib (SqLiteManager) is untouched.
    /// Keyed by (TeamId, DatasetName), mirroring the engine's DataSet table. Reads are cached in
    /// memory; writes invalidate the cache so the next search reloads.
    /// </summary>
    public class BoostRuleStore(ILogger<BoostRuleStore> logger)
    {
        private const string Table = "DatasetBoostRules";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // (teamId\0dataset) -> rules. Invalidate on write; never mutate in place.
        private readonly ConcurrentDictionary<string, IReadOnlyList<BoostRule>> _cache = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";

        private static string DbPath => IndxCloudInternalApi.SearchDbConnectionString;

        // The search DB is created lazily by the engine (SqLiteManager) on the first dataset
        // operation. We must NEVER open a connection that would create the file first, or the
        // engine's one-time CreateDatabase() is skipped (DatabaseExists() then true) and its
        // base tables (User, DataSet, …) never get built. So every read/ensure path no-ops
        // until the file exists; Save() routes base-schema creation through the lib.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);

        private static SqliteConnection Connection() =>
            // Mirror SqLiteManager.CreateConnection so we open indx.db identically.
            new($"Data Source={DbPath}");

        /// <summary>Creates the boost-rule table if the search DB already exists. Idempotent; call once at startup.</summary>
        public void EnsureTable()
        {
            if (!DbExists()) return; // fresh instance: table is created on first Save instead.
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $@"CREATE TABLE IF NOT EXISTS {Table} (
                           TeamId      TEXT NOT NULL,
                           DatasetName TEXT NOT NULL,
                           RulesJson   TEXT NOT NULL,
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

        /// <summary>The dataset's boost rules (empty if none). Cached after first read.</summary>
        public IReadOnlyList<BoostRule> Load(string teamId, string dataSetName)
        {
            return _cache.GetOrAdd(Key(teamId, dataSetName), _ => ReadFromDb(teamId, dataSetName));
        }

        private IReadOnlyList<BoostRule> ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return []; // no search DB yet → no rules.
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT RulesJson FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(json)) return [];
                return JsonSerializer.Deserialize<List<BoostRule>>(json, JsonOptions) ?? (IReadOnlyList<BoostRule>)[];
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read boost rules for {Team}/{Dataset}", teamId, dataSetName);
                return [];
            }
        }

        /// <summary>Replaces the whole rule list for a dataset and invalidates the cache.</summary>
        public void Save(string teamId, string dataSetName, IReadOnlyList<BoostRule> rules)
        {
            var json = JsonSerializer.Serialize(rules, JsonOptions);

            // Ensure the search DB exists WITH its base schema before we touch it — going through
            // the lib so the engine's tables (User, DataSet, …) are created, never a schema-less
            // file. In practice a dataset already exists when rules are saved, so this is a no-op.
            new SqLiteManager(DbPath, createOrOpenDatabase: true);
            EnsureTable();

            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, RulesJson, UpdatedUtc)
                   VALUES ($t, $d, $j, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET RulesJson = $j, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _cache.TryRemove(Key(teamId, dataSetName), out _);
        }

        /// <summary>Removes all boost rules for a dataset (e.g. on dataset delete) and invalidates the cache.</summary>
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
                logger.LogWarning(ex, "Failed to delete boost rules for {Team}/{Dataset}", teamId, dataSetName);
            }
            finally
            {
                _cache.TryRemove(Key(teamId, dataSetName), out _);
            }
        }

        /// <summary>Moves a dataset's rules to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            var rules = Load(fromTeamId, dataSetName);
            if (rules.Count > 0) Save(toTeamId, dataSetName, rules);
            Delete(fromTeamId, dataSetName);
        }

        /// <summary>
        /// Materializes the dataset's currently-active rules (enabled and within their schedule on
        /// <paramref name="today"/>) into engine <see cref="Boost"/> objects. A condition that can't
        /// be built (e.g. its field is no longer filterable) drops the whole rule (fail-safe).
        /// </summary>
        public List<Boost> BuildActiveBoosts(ISearchEngine engine, string teamId, string dataSetName, DateOnly today)
        {
            var result = new List<Boost>();
            foreach (var rule in Load(teamId, dataSetName))
            {
                if (!rule.IsActiveOn(today) || rule.Conditions.Count == 0) continue;

                Filter? combined = null;
                var buildable = true;
                foreach (var cond in rule.Conditions)
                {
                    var f = BuildConditionFilter(engine, cond);
                    if (f == null) { buildable = false; break; }
                    combined = combined == null
                        ? f
                        : (rule.Join == BoostJoin.And ? combined & f : combined | f);
                }
                if (buildable && combined != null)
                    result.Add(engine.CreateBoost(combined, rule.Strength));
            }
            return result;
        }

        private static Filter? BuildConditionFilter(ISearchEngine engine, BoostCondition cond)
        {
            if (cond.IsRange)
                return engine.CreateRangeFilter(cond.Field, cond.Min ?? double.MinValue, cond.Max ?? double.MaxValue);
            if (!string.IsNullOrEmpty(cond.Value))
                return engine.CreateValueFilter(cond.Field, cond.Value, isCaseSensitive: false);
            return null;
        }
    }
}
