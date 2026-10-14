using System.Text.Json;
using Indx.Storage;
using IndxServer.Engine;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services.Observr
{
    /// <summary>One thing Observr found, with the proposal it carries and what the owners made of
    /// it. Lives in the Observr menu until decided; the history keeps it after.</summary>
    public sealed record ObservrFinding(long Id, string TeamId, string DataSetName, string Kind, string Query, string Outcome,
        string Answer, List<ObservrProposal> Proposals, string Status, int AskId, DateTime CreatedUtc, DateTime? DecidedUtc)
    {
        public const string Open = "open";
        public const string Accepted = "accepted";
        public const string Declined = "declined";

        /// <summary>The dataset tab a finding points into: a digest item's kind names it
        /// (<c>digest/fields</c>), a field review goes to the fields, the rest to the statistics.</summary>
        public string Tab => Kind == ObservrClient.FieldReviewKind ? "fields"
            : Kind == ObservrClient.SynonymsKind ? "synonyms"
            : Kind == ObservrClient.SetupKind ? "search-or-ask"
            : Kind.StartsWith(ObservrClient.DigestKind + "/") ? Kind[(ObservrClient.DigestKind.Length + 1)..] : "statistics";

        /// <summary>What the menu shows it as: the query in quotes, a review by name, a digest item or
        /// a change by its title.</summary>
        public string Title => Kind switch
        {
            ObservrClient.FieldReviewKind => "Field review",
            ObservrClient.SynonymsKind => "Synonym suggestions",
            ObservrClient.SetupKind => "Search or ask setup",
            ObservrClient.ChangeKind => $"Did this help: {Query}",
            ObservrClient.LowClickKind => $"Why isn't this working: “{Query}”",
            ObservrClient.ZeroHitKind => $"Found nothing: “{Query}”",
            _ when Kind.StartsWith(ObservrClient.DigestKind) => Query,
            _ => $"“{Query}”",
        };
    }

    /// <summary>What Observr does on one dataset, chosen by the team's editors on the team's Observr
    /// page: whether it is on at all, how often it reports (<c>daily</c>, <c>weekly</c> on Mondays,
    /// <c>off</c>), and whether its buttons show in the dataset's tabs.</summary>
    public sealed record ObservrDatasetSettings(bool Enabled, string Report, bool Buttons)
    {
        public const string Daily = "daily";
        public const string Weekly = "weekly";
        public const string Off = "off";
        public static readonly ObservrDatasetSettings Default = new(false, Daily, true);

        /// <summary>Whether the daily job reports this dataset today.</summary>
        public bool ReportsOn(DayOfWeek day) => Enabled && (Report == Daily || Report == Weekly && day == DayOfWeek.Monday);
    }

    /// <summary>
    /// Observr's findings, a server-owned table in indx.db like the Search or ask settings: one row
    /// per answer Observr gave, with its proposals as JSON, its status, and no record of who decided
    /// (as change events record no one). Beside it, which datasets have Observr at all: it is opt-in
    /// per dataset (an instance admin's switch, Admin, Observr), since a setup holds datasets nobody
    /// should be asked about, test copies and the like. Both follow the dataset through delete,
    /// rename and transfer.
    /// </summary>
    public class ObservrFindingStore(ILogger<ObservrFindingStore> logger)
    {
        private const string Table = "ObservrFindings";
        private const string Datasets = "ObservrDatasets";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        private const string Columns = "Id, TeamId, DatasetName, Kind, Query, Outcome, Answer, ProposalsJson, Status, AskId, CreatedUtc, DecidedUtc";

        public void EnsureTable()
        {
            if (!DbExists()) return;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"CREATE TABLE IF NOT EXISTS {Table} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, TeamId TEXT NOT NULL, DatasetName TEXT NOT NULL, Kind TEXT NOT NULL,
                    Query TEXT NOT NULL, Outcome TEXT NOT NULL, Answer TEXT NOT NULL, ProposalsJson TEXT NOT NULL,
                    Status TEXT NOT NULL, AskId INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, DecidedUtc TEXT NULL);
                    CREATE INDEX IF NOT EXISTS IX_ObservrFindings_Team ON {Table} (TeamId, Status);
                    CREATE TABLE IF NOT EXISTS {Datasets} (TeamId TEXT NOT NULL, DatasetName TEXT NOT NULL, Enabled INTEGER NOT NULL,
                    Report TEXT NOT NULL DEFAULT 'daily', Buttons INTEGER NOT NULL DEFAULT 1, PRIMARY KEY (TeamId, DatasetName));";
                cmd.ExecuteNonQuery();
                // The two columns that came after the table (12 Oct 2026): added to a table that lacks them.
                foreach (var add in new[] { $"ALTER TABLE {Datasets} ADD COLUMN Report TEXT NOT NULL DEFAULT 'daily'", $"ALTER TABLE {Datasets} ADD COLUMN Buttons INTEGER NOT NULL DEFAULT 1" })
                {
                    try { using var alter = conn.CreateCommand(); alter.CommandText = add; alter.ExecuteNonQuery(); }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Failed to ensure {Table} table", Table); }
        }

        /// <summary>Whether Observr's buttons show in this dataset's tabs: on, with buttons on.</summary>
        public bool IsEnabled(string teamId, string dataSetName)
        {
            var s = Settings(teamId, dataSetName);
            return s.Enabled && s.Buttons;
        }

        public ObservrDatasetSettings Settings(string teamId, string dataSetName) =>
            SettingsForTeam(teamId).GetValueOrDefault(dataSetName, ObservrDatasetSettings.Default);

        /// <summary>Every dataset of the team that has settings, by name; a dataset absent here has
        /// <see cref="ObservrDatasetSettings.Default"/>.</summary>
        public Dictionary<string, ObservrDatasetSettings> SettingsForTeam(string teamId)
        {
            var result = new Dictionary<string, ObservrDatasetSettings>();
            if (!DbExists()) return result;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT DatasetName, Enabled, Report, Buttons FROM {Datasets} WHERE TeamId = $t";
                cmd.Parameters.AddWithValue("$t", teamId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result[r.GetString(0)] = new ObservrDatasetSettings(r.GetInt32(1) != 0, r.GetString(2), r.GetInt32(3) != 0);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
            return result;
        }

        public void SaveSettings(string teamId, string dataSetName, ObservrDatasetSettings s)
        {
            new SqLiteManager(DbPath, createOrOpenDatabase: true);
            EnsureTable();
            var report = s.Report is ObservrDatasetSettings.Weekly or ObservrDatasetSettings.Off ? s.Report : ObservrDatasetSettings.Daily;
            Execute($"INSERT INTO {Datasets} (TeamId, DatasetName, Enabled, Report, Buttons) VALUES ($t, $d, $e, $r, $b) ON CONFLICT(TeamId, DatasetName) DO UPDATE SET Enabled = $e, Report = $r, Buttons = $b",
                c => { c.Parameters.AddWithValue("$t", teamId); c.Parameters.AddWithValue("$d", dataSetName); c.Parameters.AddWithValue("$e", s.Enabled ? 1 : 0); c.Parameters.AddWithValue("$r", report); c.Parameters.AddWithValue("$b", s.Buttons ? 1 : 0); });
        }

        /// <summary>Switches Observr on or off for a dataset, keeping its other settings.</summary>
        public void SetEnabled(string teamId, string dataSetName, bool enabled) =>
            SaveSettings(teamId, dataSetName, Settings(teamId, dataSetName) with { Enabled = enabled });

        /// <summary>A finding was added, decided, deleted or moved to another team: what shows an open
        /// count (the header's Agent button) counts again. Raised on the writer's thread, after the
        /// write; a listener marshals to its own.</summary>
        public event Action? Changed;

        /// <summary>Keeps an answer. Open when there is something to decide, or when nobody has read it
        /// yet (<paramref name="unread"/>: a digest item), closed as answered otherwise (an answer shown
        /// inline, with no proposal).</summary>
        public long Add(string teamId, string dataSetName, string kind, string query, ObservrAnswer answer, bool unread = false)
        {
            new SqLiteManager(DbPath, createOrOpenDatabase: true);
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO {Table} (TeamId, DatasetName, Kind, Query, Outcome, Answer, ProposalsJson, Status, AskId, CreatedUtc)
                VALUES ($t, $d, $k, $q, $o, $a, $p, $s, $ask, $c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$o", answer.Outcome);
            cmd.Parameters.AddWithValue("$a", answer.Answer);
            cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(answer.Proposals, JsonOptions));
            // A finding with nothing to decide that was read where it was asked is kept, but not open.
            cmd.Parameters.AddWithValue("$s", answer.Proposals.Count > 0 || unread ? ObservrFinding.Open : ObservrFinding.Accepted);
            cmd.Parameters.AddWithValue("$ask", answer.AskId);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            var id = (long)cmd.ExecuteScalar()!;
            Changed?.Invoke();
            return id;
        }

        public void Decide(long id, string status)
        {
            if (!DbExists()) return;
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {Table} SET Status = $s, DecidedUtc = $u WHERE Id = $id";
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            Changed?.Invoke();
        }

        public ObservrFinding? Get(long id) => Query($"WHERE Id = $id", cmd => cmd.Parameters.AddWithValue("$id", id), 1).FirstOrDefault();

        /// <summary>The open findings of these teams, newest first.</summary>
        public List<ObservrFinding> ListOpen(IReadOnlyCollection<string> teamIds, int limit = 20) =>
            teamIds.Count == 0 ? [] : Query($"WHERE Status = 'open' AND TeamId IN ({Placeholders(teamIds.Count)})", cmd => AddTeams(cmd, teamIds), limit);

        public int CountOpen(IReadOnlyCollection<string> teamIds)
        {
            if (teamIds.Count == 0 || !DbExists()) return 0;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {Table} WHERE Status = 'open' AND TeamId IN ({Placeholders(teamIds.Count)})";
                AddTeams(cmd, teamIds);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { return 0; }
        }

        /// <summary>A dataset's latest findings, whatever their status: what Observr has said about it.</summary>
        public List<ObservrFinding> ListForDataset(string teamId, string dataSetName, int limit = 20) =>
            Query("WHERE TeamId = $t AND DatasetName = $d", cmd => { cmd.Parameters.AddWithValue("$t", teamId); cmd.Parameters.AddWithValue("$d", dataSetName); }, limit);

        /// <summary>Every finding of these teams, newest first: the history.</summary>
        public List<ObservrFinding> List(IReadOnlyCollection<string> teamIds, int limit = 200) =>
            teamIds.Count == 0 ? [] : Query($"WHERE TeamId IN ({Placeholders(teamIds.Count)})", cmd => AddTeams(cmd, teamIds), limit);

        private static string Placeholders(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"$team{i}"));
        private static void AddTeams(SqliteCommand cmd, IReadOnlyCollection<string> teamIds)
        {
            int i = 0;
            foreach (var t in teamIds) cmd.Parameters.AddWithValue($"$team{i++}", t);
        }

        private List<ObservrFinding> Query(string where, Action<SqliteCommand> bind, int limit)
        {
            var list = new List<ObservrFinding>();
            if (!DbExists()) return list;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT {Columns} FROM {Table} {where} ORDER BY Id DESC LIMIT {limit}";
                bind(cmd);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new ObservrFinding(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                        r.GetString(6), JsonSerializer.Deserialize<List<ObservrProposal>>(r.GetString(7), JsonOptions) ?? [],
                        r.GetString(8), r.GetInt32(9), DateTime.Parse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
            catch (Exception ex) { logger.LogError(ex, "Failed to read Observr findings"); }
            return list;
        }

        private void Execute(string sql, Action<SqliteCommand> bind)
        {
            if (!DbExists()) return;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                bind(cmd);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
            catch (Exception ex) { logger.LogWarning(ex, "Observr findings: {Sql}", sql); }
        }

        public void Delete(string teamId, string dataSetName)
        {
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DatasetName = $d; DELETE FROM {Datasets} WHERE TeamId = $t AND DatasetName = $d",
                c => { c.Parameters.AddWithValue("$t", teamId); c.Parameters.AddWithValue("$d", dataSetName); });
            Changed?.Invoke();
        }

        public void Rename(string teamId, string dataSetName, string newName) =>
            Execute($"UPDATE {Table} SET DatasetName = $n WHERE TeamId = $t AND DatasetName = $d; UPDATE OR REPLACE {Datasets} SET DatasetName = $n WHERE TeamId = $t AND DatasetName = $d",
                c => { c.Parameters.AddWithValue("$n", newName); c.Parameters.AddWithValue("$t", teamId); c.Parameters.AddWithValue("$d", dataSetName); });

        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            Execute($"UPDATE {Table} SET TeamId = $to WHERE TeamId = $from AND DatasetName = $d; UPDATE OR REPLACE {Datasets} SET TeamId = $to WHERE TeamId = $from AND DatasetName = $d",
                c => { c.Parameters.AddWithValue("$to", toTeamId); c.Parameters.AddWithValue("$from", fromTeamId); c.Parameters.AddWithValue("$d", dataSetName); });
            Changed?.Invoke();
        }
    }
}
