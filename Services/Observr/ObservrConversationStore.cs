using Indx.Storage;
using IndxServer.Engine;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services.Observr
{
    /// <summary>One turn of an Agent conversation as it is shown again: who said it, the text, the
    /// finding it produced when it did (the proposals and their Yes / No render from there), and
    /// where the owner was (a team, or a dataset of it: empty <see cref="DatasetName"/>).</summary>
    public sealed record ObservrTurn(long Id, string Role, string Text, long? FindingId, DateTime CreatedUtc, string TeamId = "", string DatasetName = "")
    {
        public const string User = "user";
        public const string Assistant = "assistant";
    }

    /// <summary>
    /// The Agent's conversations (Notes/Anders/console-shell-plan-2026-10-13.md, stage 5), in indx.db
    /// beside the findings: one thread per user, the text turns only, each turn marked with where
    /// the owner was (a team, or a dataset of it) so the model can tell what an earlier question
    /// was about. One thread, not one per context (15 Oct 2026): a person has one agent and expects
    /// it to remember what was said a minute ago on another page. The tool calls a question took
    /// are not kept; they were the model's working, not the conversation. Bounded: a user keeps
    /// their last turns, so a chat that goes on for months does not grow the file. Follows the
    /// dataset through delete, rename and transfer as the findings do.
    /// </summary>
    /// <param name="dbPath">A file of its own, for a test; the server's indx.db otherwise.</param>
    public class ObservrConversationStore(ILogger<ObservrConversationStore> logger, string? dbPath = null)
    {
        private const string Table = "ObservrConversations";
        /// <summary>Turns kept per user.</summary>
        public const int Kept = 40;

        private string DbPath => dbPath ?? IndxServerInternalApi.SearchDbConnectionString;
        private bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private SqliteConnection Connection() => new($"Data Source={DbPath}");

        public void EnsureTable()
        {
            if (!DbExists()) return;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"CREATE TABLE IF NOT EXISTS {Table} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, TeamId TEXT NOT NULL, DatasetName TEXT NOT NULL,
                    Role TEXT NOT NULL, Text TEXT NOT NULL, FindingId INTEGER NULL, CreatedUtc TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_ObservrConversations_Context ON {Table} (UserId, TeamId, DatasetName);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { logger.LogError(ex, "Failed to ensure {Table} table", Table); }
        }

        /// <summary>The conversation of one user, oldest first.</summary>
        public List<ObservrTurn> List(string userId, int limit = Kept)
        {
            if (!DbExists()) return [];
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT Id, Role, Text, FindingId, CreatedUtc, TeamId, DatasetName FROM (
                    SELECT * FROM {Table} WHERE UserId = $u ORDER BY Id DESC LIMIT $n) ORDER BY Id";
                cmd.Parameters.AddWithValue("$u", userId);
                cmd.Parameters.AddWithValue("$n", limit);
                using var r = cmd.ExecuteReader();
                var list = new List<ObservrTurn>();
                while (r.Read())
                    list.Add(new ObservrTurn(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                        DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetString(5), r.GetString(6)));
                return list;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { return []; }
        }

        public long Add(string userId, string teamId, string dataSetName, string role, string text, long? findingId = null)
        {
            new SqLiteManager(DbPath, createOrOpenDatabase: true);
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO {Table} (UserId, TeamId, DatasetName, Role, Text, FindingId, CreatedUtc)
                VALUES ($u, $t, $d, $r, $x, $f, $c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$x", text);
            cmd.Parameters.AddWithValue("$f", (object?)findingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            var id = (long)cmd.ExecuteScalar()!;
            // The bound: everything older than the user's last Kept turns goes.
            using var trim = conn.CreateCommand();
            trim.CommandText = $@"DELETE FROM {Table} WHERE UserId = $u AND Id NOT IN (
                SELECT Id FROM {Table} WHERE UserId = $u ORDER BY Id DESC LIMIT $n)";
            trim.Parameters.AddWithValue("$u", userId);
            trim.Parameters.AddWithValue("$n", Kept);
            trim.ExecuteNonQuery();
            return id;
        }

        /// <summary>Starts over: the owner's "Clear".</summary>
        public void Clear(string userId) =>
            Execute($"DELETE FROM {Table} WHERE UserId = $u", cmd => cmd.Parameters.AddWithValue("$u", userId));

        public void Delete(string teamId, string dataSetName) =>
            Execute($"DELETE FROM {Table} WHERE TeamId = $t AND DatasetName = $d",
                cmd => { cmd.Parameters.AddWithValue("$t", teamId); cmd.Parameters.AddWithValue("$d", dataSetName); });

        public void Rename(string teamId, string dataSetName, string newName) =>
            Execute($"UPDATE {Table} SET DatasetName = $n WHERE TeamId = $t AND DatasetName = $d",
                cmd => { cmd.Parameters.AddWithValue("$n", newName); cmd.Parameters.AddWithValue("$t", teamId); cmd.Parameters.AddWithValue("$d", dataSetName); });

        public void Transfer(string fromTeamId, string toTeamId, string dataSetName) =>
            Execute($"UPDATE {Table} SET TeamId = $to WHERE TeamId = $from AND DatasetName = $d",
                cmd => { cmd.Parameters.AddWithValue("$to", toTeamId); cmd.Parameters.AddWithValue("$from", fromTeamId); cmd.Parameters.AddWithValue("$d", dataSetName); });

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
        }
    }
}
