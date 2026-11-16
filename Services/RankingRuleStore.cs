using IndxServer.Engine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indx.Api;
using Indx.Storage;
using IndxServer.Models;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// The store of one kind of ranking rule as the console sees it, whichever the kind: the
    /// rules as <see cref="IRankingRule"/>, saved, serialized and read back the store's own way.
    /// </summary>
    public interface IRankingRuleStore
    {
        /// <summary>"boost" or "bury": the word the console puts in its sentences.</summary>
        string Word { get; }
        IReadOnlyList<IRankingRule> LoadRules(string teamId, string dataSetName);
        void SaveRules(string teamId, string dataSetName, IReadOnlyList<IRankingRule> rules);
        string Serialize(IEnumerable<IRankingRule> rules, bool indented = false);
        List<IRankingRule> Deserialize(string json);
        IRankingRule NewRule();
    }

    /// <summary>
    /// The per-dataset ranking rules the server owns, one table a kind (25 Oct 2026, from the
    /// boost rule store of Sep 2026, so that a bury rule is kept the way a boost rule is): JSON per
    /// (team, dataset) in <c>indx.db</c>, cached, invalidated on write, never mutated in place.
    /// <see cref="BoostRuleStore"/> and <see cref="BuryRuleStore"/> give each its table, its
    /// change kind and the engine object its rules become.
    /// </summary>
    public abstract class RankingRuleStore<TRule>(string table, string changeKind, string word, ILogger logger) : IRankingRuleStore
        where TRule : class, IRankingRule, new()
    {
        protected static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // (teamId\0dataset) -> rules. Invalidate on write; never mutate in place.
        private readonly StoreCache<string, IReadOnlyList<TRule>> _cache = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";

        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;

        // The search DB is created lazily by the engine (SqLiteManager) on the first dataset
        // operation. We must NEVER open a connection that would create the file first, or the
        // engine's one-time CreateDatabase() is skipped (DatabaseExists() then true) and its
        // base tables (User, DataSet, …) never get built. So every read/ensure path no-ops
        // until the file exists; Save() routes base-schema creation through the lib.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);

        private static SqliteConnection Connection() =>
            // Mirror SqLiteManager.CreateConnection so we open indx.db identically.
            new($"Data Source={DbPath}");

        public string Word => word;

        /// <summary>Keeps <paramref name="rules"/> as the dataset's, as if read from the database: for a
        /// test that renders without one.</summary>
        internal void SeedForTest(string teamId, string dataSetName, IReadOnlyList<TRule> rules) =>
            _cache.SeedForTest(Key(teamId, dataSetName), rules);

        /// <summary>Test seam: called with the dataset's name when its rules have been read from the
        /// database and before they are kept, so a test can hold a read there while a save runs.</summary>
        internal Action<string>? ReadFromDbForTest { get; set; }

        public void EnsureTable()
        {
            if (!DbExists()) return; // fresh instance: table is created on first Save instead.
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $@"CREATE TABLE IF NOT EXISTS {table} (
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
                logger.LogError(ex, "Failed to ensure {Table} table", table);
            }
        }

        public IReadOnlyList<TRule> Load(string teamId, string dataSetName)
        {
            return _cache.Get(Key(teamId, dataSetName), () =>
            {
                var rules = ReadFromDb(teamId, dataSetName);
                ReadFromDbForTest?.Invoke(dataSetName);
                return rules;
            });
        }

        private IReadOnlyList<TRule> ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return []; // no search DB yet → no rules.
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT RulesJson FROM {table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(json)) return [];
                return JsonSerializer.Deserialize<List<TRule>>(json, JsonOptions) ?? (IReadOnlyList<TRule>)[];
            }
            catch (Exception ex)
            {
                StoreRead.Failed(ex);
                logger.LogError(ex, "Failed to read {Word} rules for {Team}/{Dataset}", word, teamId, dataSetName);
                return [];
            }
        }

        public IDatasetChangeSink? ChangeSink { get; set; }

        public void Save(string teamId, string dataSetName, IReadOnlyList<TRule> rules)
        {
            var before = ChangeSink == null ? null : Load(teamId, dataSetName);
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
                $@"INSERT INTO {table} (TeamId, DatasetName, RulesJson, UpdatedUtc)
                   VALUES ($t, $d, $j, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET RulesJson = $j, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _cache.Invalidate(Key(teamId, dataSetName));

            // Counts and the schedule as saved, so a chart can mark when a scheduled rule starts
            // and ends without an event of its own. Names and conditions stay out: counts only.
            ChangeSink?.Changed(teamId, dataSetName, changeKind, new
            {
                rulesBefore = before?.Count ?? 0,
                rules = rules.Count,
                enabledBefore = before?.Count(r => r.Enabled) ?? 0,
                enabled = rules.Count(r => r.Enabled),
                scheduled = rules.Where(r => r.ActiveFrom != null || r.ActiveUntil != null)
                    .Select(r => new { from = r.ActiveFrom, until = r.ActiveUntil }).ToArray(),
            });
        }

        public void Delete(string teamId, string dataSetName)
        {
            if (!DbExists()) { _cache.Invalidate(Key(teamId, dataSetName)); return; }
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete {Word} rules for {Team}/{Dataset}", word, teamId, dataSetName);
            }
            finally
            {
                _cache.Invalidate(Key(teamId, dataSetName));
            }
        }

        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            var rules = Load(fromTeamId, dataSetName);
            if (rules.Count > 0) Save(toTeamId, dataSetName, rules);
            Delete(fromTeamId, dataSetName);
        }

        public void Rename(string teamId, string dataSetName, string newName)
        {
            var rules = Load(teamId, dataSetName);
            if (rules.Count > 0) Save(teamId, newName, rules);
            Delete(teamId, dataSetName);
        }

        /// <summary>The filter a rule's conditions make, joined as the rule says; null when a
        /// condition cannot be built on this engine (a field that is not filterable now).</summary>
        protected static Filter? CombinedFilter(ISearchEngine engine, TRule rule)
        {
            Filter? combined = null;
            foreach (var cond in rule.Conditions)
            {
                var f = FilterConditionBuilder.Build(engine, cond.Field, cond.Value, cond.Min, cond.Max);
                if (f == null) return null;
                combined = combined == null
                    ? f
                    : (rule.Join == BoostJoin.And ? combined & f : combined | f);
            }
            return combined;
        }

        // ── The console's view ────────────────────────────────────────────────

        IReadOnlyList<IRankingRule> IRankingRuleStore.LoadRules(string teamId, string dataSetName) => Load(teamId, dataSetName);

        void IRankingRuleStore.SaveRules(string teamId, string dataSetName, IReadOnlyList<IRankingRule> rules) =>
            Save(teamId, dataSetName, rules.Cast<TRule>().ToList());

        string IRankingRuleStore.Serialize(IEnumerable<IRankingRule> rules, bool indented) =>
            JsonSerializer.Serialize(rules.Cast<TRule>().ToList(), indented ? new JsonSerializerOptions(JsonOptions) { WriteIndented = true } : JsonOptions);

        List<IRankingRule> IRankingRuleStore.Deserialize(string json) =>
            (JsonSerializer.Deserialize<List<TRule>>(json, JsonOptions) ?? []).Cast<IRankingRule>().ToList();

        IRankingRule IRankingRuleStore.NewRule() => new TRule();
    }

    /// <summary>The bury rules, in <c>DatasetBuryRules</c>: a boost rule's twin, taking documents down.</summary>
    public class BuryRuleStore(ILogger<BuryRuleStore> logger)
        : RankingRuleStore<BuryRule>("DatasetBuryRules", DatasetChangeKind.BuryRules, "bury", logger)
    {
        public List<Bury> BuildActiveBuries(ISearchEngine engine, string teamId, string dataSetName, DateOnly today)
        {
            var result = new List<Bury>();
            foreach (var rule in Load(teamId, dataSetName))
            {
                if (!rule.IsActiveOn(today) || rule.Conditions.Count == 0) continue;
                // A strength the PUT refuses can still be in a row written before it did.
                if (CombinedFilter(engine, rule) is { } combined && engine.CreateBury(combined, rule.Strength, out _) is { } bury)
                    result.Add(bury);
            }
            return result;
        }
    }
}
