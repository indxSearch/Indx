using IndxServer.Engine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indx.Storage;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>The owner's choices for a dataset's filters (the console's Filters, 26 Nov 2026): the
    /// fields in their order, each with a name, a panel and whether it is shown. A field the list does
    /// not name is placed by <see cref="DatasetFilters"/> after the named ones, as the rules decide.</summary>
    public sealed class FilterSetup
    {
        /// <summary>The row of the category menu, from the dataset's Categories setting.</summary>
        public const string CategoriesRow = "@categories";

        public List<FilterSetupField> Fields { get; set; } = new();
    }

    /// <summary>One field's choices. Null is the rules' choice.</summary>
    public sealed class FilterSetupField
    {
        public string Field { get; set; } = "";
        public string? Label { get; set; }
        public bool Shown { get; set; } = true;
        public FilterKind? Kind { get; set; }
        /// <summary>Buckets: the lower edge of each, the last closing the final one.</summary>
        public List<double>? Edges { get; set; }
    }

    /// <summary>
    /// Server-owned persistence for <see cref="FilterSetup"/>: one JSON row per dataset in its own
    /// table (<c>DatasetFilterSetup</c>) inside indx.db, the pattern of <see cref="QueryParameterStore"/>,
    /// since <c>ISearchEngine</c> has no concept of a page's filters. Keyed by (TeamId, DatasetName);
    /// cached, writes invalidate. Follows the dataset on delete, rename and transfer.
    /// </summary>
    public class FilterSetupStore(ILogger<FilterSetupStore> logger)
    {
        private const string Table = "DatasetFilterSetup";

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly StoreCache<string, FilterSetup?> _cache = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";
        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        // Never open a connection that would create indx.db first: see BoostRuleStore.DbExists.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Where a save is reported as a change (the Changes list). Set at startup; null with statistics off.</summary>
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
                           TeamId      TEXT NOT NULL,
                           DatasetName TEXT NOT NULL,
                           SetupJson   TEXT NOT NULL,
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

        /// <summary>The owner's choices, or null when the dataset has none. The instance is shared:
        /// callers must not modify it.</summary>
        public FilterSetup? Load(string teamId, string dataSetName) =>
            _cache.Get(Key(teamId, dataSetName), () => ReadFromDb(teamId, dataSetName));

        private FilterSetup? ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return null;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT SetupJson FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<FilterSetup>(json, JsonOptions);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // no such table: nothing saved yet
            {
                return null;
            }
            catch (Exception ex)
            {
                // A row that cannot be read is treated as none: the dataset gets the rules' filters.
                StoreRead.Failed(ex);
                logger.LogError(ex, "Failed to read the filter setup of {Team}/{Dataset}", teamId, dataSetName);
                return null;
            }
        }

        /// <summary>Replaces the owner's choices; null or an empty list removes them, so the rules
        /// decide again. Validation is <see cref="DatasetFilters.Validate"/>, the caller's.</summary>
        public void Save(string teamId, string dataSetName, FilterSetup? setup)
        {
            if (setup == null || setup.Fields.Count == 0)
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
                    $@"INSERT INTO {Table} (TeamId, DatasetName, SetupJson, UpdatedUtc)
                       VALUES ($t, $d, $j, $u)
                       ON CONFLICT(TeamId, DatasetName) DO UPDATE SET SetupJson = $j, UpdatedUtc = $u;";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(setup, JsonOptions));
                cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
                _cache.Invalidate(Key(teamId, dataSetName));
            }
            ChangeSink?.Changed(teamId, dataSetName, DatasetChangeKind.Filters, new { fields = setup?.Fields.Count ?? 0, shown = setup?.Fields.Count(f => f.Shown) ?? 0 });
        }

        /// <summary>Removes the owner's choices (on dataset delete, or a reset) and invalidates the cache.</summary>
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
                logger.LogWarning(ex, "Failed to delete the filter setup of {Team}/{Dataset}", teamId, dataSetName);
            }
            finally
            {
                _cache.Invalidate(Key(teamId, dataSetName));
            }
        }

        /// <summary>Moves the row to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName) => Move(fromTeamId, dataSetName, toTeamId, dataSetName);

        /// <summary>Re-keys the row under the dataset's new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName) => Move(teamId, dataSetName, teamId, newName);

        private void Move(string fromTeam, string fromName, string toTeam, string toName)
        {
            try
            {
                if (DbExists())
                {
                    using var conn = Connection();
                    DatasetRowMove.Move(conn, Table, fromTeam, fromName, toTeam, toName);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to move the filter setup of {Team}/{Dataset} to {NewTeam}/{NewDataset}", fromTeam, fromName, toTeam, toName);
            }
            finally
            {
                _cache.Invalidate(Key(fromTeam, fromName));
                _cache.Invalidate(Key(toTeam, toName));
            }
        }
    }
}
