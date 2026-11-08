using System.Collections.Concurrent;
using System.Text.Json;
using IndxServer.Engine;
using Indx.Storage;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// Server-owned persistence for a dataset's last data check (<see cref="DataCheck"/>): the report
    /// as JSON, how many records have changed since, and whether the records were replaced. One row
    /// per dataset in its own table (<c>DatasetDataChecks</c>) in indx.db, the pattern of
    /// <see cref="FieldProfileStore"/>. The counting rides on what is already reported: every
    /// document route passes <see cref="StatisticsService.RecordDocuments"/>, which tells this store
    /// whether statistics are on or not, and a replace comes through the registry's change events.
    /// A write to a dataset with no report costs a dictionary lookup and nothing else.
    /// </summary>
    public class DataCheckStore(ILogger<DataCheckStore> logger) : IDatasetChangeSink
    {
        private const string Table = "DatasetDataChecks";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // (teamId\0dataset) of the datasets with a report: the only ones a document change is counted for.
        private readonly ConcurrentDictionary<string, byte> _checked = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";
        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        // Never open a connection that would create indx.db first: see BoostRuleStore.DbExists.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Creates the table if the search DB already exists, and learns which datasets have
        /// a report. Idempotent; call once at startup.</summary>
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
                           TeamId        TEXT NOT NULL,
                           DatasetName   TEXT NOT NULL,
                           ReportJson    TEXT NOT NULL,
                           ChangedSince  INTEGER NOT NULL DEFAULT 0,
                           Replaced      TEXT NULL,
                           PRIMARY KEY (TeamId, DatasetName)
                       );";
                cmd.ExecuteNonQuery();
                cmd.CommandText = $"SELECT TeamId, DatasetName FROM {Table}";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) _checked[Key(reader.GetString(0), reader.GetString(1))] = 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure {Table} table", Table);
            }
        }

        /// <summary>The dataset's last report and what has happened since, or null when it was never checked.</summary>
        public DataCheckStanding? Load(string teamId, string dataSetName)
        {
            if (!DbExists() || !_checked.ContainsKey(Key(teamId, dataSetName))) return null;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT ReportJson, ChangedSince, Replaced FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;
                var report = JsonSerializer.Deserialize<DataCheckReport>(reader.GetString(0), JsonOptions);
                return report == null ? null : new DataCheckStanding(report, reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
            catch (Exception ex)
            {
                // A row that cannot be read is treated as none: the page offers a new check.
                logger.LogError(ex, "Failed to read the data check for {Team}/{Dataset}", teamId, dataSetName);
                return null;
            }
        }

        /// <summary>Keeps a new report in place of the last, with nothing changed since.</summary>
        public void Save(string teamId, string dataSetName, DataCheckReport report)
        {
            new SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, ReportJson, ChangedSince, Replaced)
                   VALUES ($t, $d, $j, 0, NULL)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET ReportJson = $j, ChangedSince = 0, Replaced = NULL;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(report, JsonOptions));
            cmd.ExecuteNonQuery();
            _checked[Key(teamId, dataSetName)] = 0;
        }

        /// <summary>Counts records inserted, updated or deleted since the check. Nothing to do for a
        /// dataset that was never checked, which is most writes.</summary>
        public void RecordChanges(string teamId, string dataSetName, long records)
        {
            if (records <= 0 || !_checked.ContainsKey(Key(teamId, dataSetName))) return;
            Execute($"UPDATE {Table} SET ChangedSince = ChangedSince + $n WHERE TeamId = $t AND DatasetName = $d",
                teamId, dataSetName, ("$n", records));
        }

        /// <summary>Marks the report as made on records that are no longer there.</summary>
        public void MarkReplaced(string teamId, string dataSetName)
        {
            if (!_checked.ContainsKey(Key(teamId, dataSetName))) return;
            Execute($"UPDATE {Table} SET Replaced = $r WHERE TeamId = $t AND DatasetName = $d",
                teamId, dataSetName, ("$r", DateTime.UtcNow.ToString("o")));
        }

        /// <summary>A replace makes the report out of date; the other changes either do not touch the
        /// records or reach this store another way (documents, delete, rename, transfer).</summary>
        void IDatasetChangeSink.Changed(string teamId, string dataSet, string kind, object? summary)
        {
            if (kind == DatasetChangeKind.Replace) MarkReplaced(teamId, dataSet);
        }

        public void Delete(string teamId, string dataSetName)
        {
            _checked.TryRemove(Key(teamId, dataSetName), out _);
            if (DbExists()) Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DatasetName = $d", teamId, dataSetName);
        }

        /// <summary>Moves the report to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName) => Move(fromTeamId, dataSetName, toTeamId, dataSetName);

        /// <summary>Re-keys the report under the dataset's new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName) => Move(teamId, dataSetName, teamId, newName);

        private void Move(string fromTeam, string fromName, string toTeam, string toName)
        {
            if (!_checked.TryRemove(Key(fromTeam, fromName), out _)) return;
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DatasetName = $d", toTeam, toName);
            Execute($"UPDATE {Table} SET TeamId = $nt, DatasetName = $nd WHERE TeamId = $t AND DatasetName = $d",
                fromTeam, fromName, ("$nt", toTeam), ("$nd", toName));
            _checked[Key(toTeam, toName)] = 0;
        }

        private void Execute(string sql, string teamId, string dataSetName, params (string Name, object Value)[] more)
        {
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                foreach (var (name, value) in more) cmd.Parameters.AddWithValue(name, value);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { } // no such table: nothing stored
            catch (Exception ex)
            {
                // Never fails the change that called it: the change has happened, the count is a hint.
                logger.LogWarning(ex, "Failed to update the data check for {Team}/{Dataset}", teamId, dataSetName);
            }
        }
    }
}
