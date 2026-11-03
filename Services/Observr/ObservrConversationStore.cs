using Indx.Storage;
using IndxServer.Engine;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services.Observr
{
    /// <summary>One turn of an Agent conversation as it is shown again: who said it, the text, the
    /// finding it produced when it did (the proposals and their Yes / No render from there), and
    /// where the owner was (a team, or a dataset of it: empty <see cref="DatasetName"/>). An answer
    /// keeps what it looked at (<see cref="Looked"/>, JSON of <see cref="ObservrLooked"/>), so a
    /// follow-up ("and the second one?") can point back at it.</summary>
    public sealed record ObservrTurn(long Id, string Role, string Text, long? FindingId, DateTime CreatedUtc, string TeamId = "", string DatasetName = "",
        long ConversationId = 0, string? Looked = null)
    {
        public const string User = "user";
        public const string Assistant = "assistant";

        /// <summary>What the answer looked at, read back; empty for a question or an answer without tools.</summary>
        public IReadOnlyList<ObservrLooked> LookedAt() =>
            string.IsNullOrEmpty(Looked) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<ObservrLooked>>(Looked, ObservrLooked.Json) ?? [];
    }

    /// <summary>One thing an answer looked at: the tool, the line the panel shows ("Searched for
    /// \"tv\""), and the start of what it found, which the model reads again with the answer.</summary>
    public sealed record ObservrLooked(string Tool, string Done, string Result)
    {
        internal static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    }

    /// <summary>A conversation in the History list: its first question as the title, where it began,
    /// when it was last added to, and how many turns it has.</summary>
    public sealed record ObservrConversation(long Id, string Title, string TeamId, string DatasetName, DateTime LastUtc, int Turns);

    /// <summary>
    /// The Agent's conversations (Notes/Anders/console-shell-plan-2026-10-13.md, stage 5;
    /// Notes/observr-agent-plan.md), in indx.db beside the findings. A user has conversations, not
    /// one endless thread (2 Nov 2026): one is current, until a New conversation or
    /// <see cref="QuietAfter"/> without a turn, and the earlier ones are the History. What the panel
    /// shows of the current one is what the model reads (<see cref="RememberedFrom"/>): the screen
    /// once showed 40 turns while the model read 10. Each turn is marked with where the owner was,
    /// so the model can tell what an earlier question was about; an answer keeps a short note of
    /// what it looked at. Bounded: a user keeps their last <see cref="KeptConversations"/>
    /// conversations. Follows the dataset through delete, rename and transfer as the findings do.
    /// </summary>
    /// <param name="logger">Where a failed read or write is reported.</param>
    /// <param name="dbPath">A file of its own, for a test; the server's indx.db otherwise.</param>
    public class ObservrConversationStore(ILogger<ObservrConversationStore> logger, string? dbPath = null)
    {
        private const string Table = "ObservrConversations";
        private const string Summaries = "ObservrConversationSummaries";
        /// <summary>Conversations kept per user; older ones go, turns and all.</summary>
        public const int KeptConversations = 30;
        /// <summary>A conversation with no turn for this long is no longer current: the next question starts a new one.</summary>
        public static readonly TimeSpan QuietAfter = TimeSpan.FromHours(4);
        /// <summary>What the model reads of a conversation, at most: these many turns, these many
        /// characters (the text and what the answers looked at), counted from the newest.</summary>
        public const int RememberTurns = 60;
        public const int RememberChars = 80_000;

        private string DbPath => dbPath ?? IndxServerInternalApi.SearchDbConnectionString;
        private bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>Whether this store has seen its table with today's columns, so a read need not ask again.</summary>
        private volatile bool _ensured;

        public void EnsureTable()
        {
            if (_ensured || !DbExists()) return;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"CREATE TABLE IF NOT EXISTS {Table} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, TeamId TEXT NOT NULL, DatasetName TEXT NOT NULL,
                    Role TEXT NOT NULL, Text TEXT NOT NULL, FindingId INTEGER NULL, CreatedUtc TEXT NOT NULL,
                    ConversationId INTEGER NOT NULL DEFAULT 0, Looked TEXT NULL);
                    CREATE INDEX IF NOT EXISTS IX_ObservrConversations_Context ON {Table} (UserId, TeamId, DatasetName);";
                cmd.ExecuteNonQuery();
                // A table from before conversations (2 Nov 2026) gets the two columns; its turns
                // become each user's conversation 0, the last one, so nothing said is lost.
                using var columns = conn.CreateCommand();
                columns.CommandText = $"SELECT name FROM pragma_table_info('{Table}')";
                var have = new HashSet<string>();
                using (var r = columns.ExecuteReader()) while (r.Read()) have.Add(r.GetString(0));
                foreach (var (name, type) in new[] { ("ConversationId", "INTEGER NOT NULL DEFAULT 0"), ("Looked", "TEXT NULL") })
                {
                    if (have.Contains(name)) continue;
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE {Table} ADD COLUMN {name} {type}";
                    alter.ExecuteNonQuery();
                }
                using var index = conn.CreateCommand();
                index.CommandText = $@"CREATE INDEX IF NOT EXISTS IX_ObservrConversations_Conversation ON {Table} (UserId, ConversationId);
                    CREATE TABLE IF NOT EXISTS {Summaries} (UserId TEXT NOT NULL, ConversationId INTEGER NOT NULL, UpToId INTEGER NOT NULL,
                    Text TEXT NOT NULL, CreatedUtc TEXT NOT NULL, PRIMARY KEY (UserId, ConversationId));";
                index.ExecuteNonQuery();
                _ensured = true;
            }
            catch (Exception ex) { logger.LogError(ex, "Failed to ensure {Table} table", Table); }
        }

        /// <summary>A user's turns, every conversation, oldest first, at most <paramref name="limit"/>.</summary>
        public List<ObservrTurn> List(string userId, int limit = 1000) =>
            Read($"SELECT * FROM (SELECT * FROM {Table} WHERE UserId = $u ORDER BY Id DESC LIMIT $n) ORDER BY Id",
                cmd => { cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$n", limit); });

        /// <summary>One conversation's turns, oldest first.</summary>
        public List<ObservrTurn> Turns(string userId, long conversationId) =>
            Read($"SELECT * FROM {Table} WHERE UserId = $u AND ConversationId = $c ORDER BY Id",
                cmd => { cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$c", conversationId); });

        /// <summary>The user's current conversation: the newest, unless it has been quiet for
        /// <see cref="QuietAfter"/>; null when the next question starts a new one.</summary>
        public long? Current(string userId, DateTime nowUtc) =>
            Conversations(userId, 1).FirstOrDefault() is { } c && nowUtc - c.LastUtc < QuietAfter ? c.Id : null;

        /// <summary>The number a new conversation of the user takes.</summary>
        public long Next(string userId) => (Conversations(userId, 1).FirstOrDefault()?.Id ?? 0) + 1;

        /// <summary>The user's conversations, the most recent first: the History.</summary>
        public List<ObservrConversation> Conversations(string userId, int limit = KeptConversations)
        {
            var turns = Read($@"SELECT * FROM {Table} WHERE UserId = $u AND ConversationId IN (
                    SELECT ConversationId FROM {Table} WHERE UserId = $u GROUP BY ConversationId ORDER BY MAX(Id) DESC LIMIT $n) ORDER BY Id",
                cmd => { cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$n", limit); });
            return turns.GroupBy(t => t.ConversationId)
                .Select(g =>
                {
                    var first = g.FirstOrDefault(t => t.Role == ObservrTurn.User) ?? g.First();
                    var title = first.Text.Trim().ReplaceLineEndings(" ");
                    return new ObservrConversation(g.Key, title.Length > 80 ? title[..80] + "…" : title, first.TeamId, first.DatasetName,
                        g.Max(t => t.CreatedUtc), g.Count());
                })
                .OrderByDescending(c => c.LastUtc).ToList();
        }

        /// <summary>Where the model starts reading a conversation: the index of the oldest turn it
        /// reads, counted back from the newest within <see cref="RememberTurns"/> and
        /// <see cref="RememberChars"/>, moved on to a question so the reading starts with one. 0 when
        /// it reads all of it. The panel draws its line, or the summary, here.</summary>
        public static int RememberedFrom(IReadOnlyList<ObservrTurn> turns)
        {
            var chars = 0;
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                chars += turns[i].Text.Length + (turns[i].Looked?.Length ?? 0);
                if (turns.Count - i > RememberTurns || chars > RememberChars)
                {
                    var from = i + 1;
                    while (from < turns.Count - 1 && turns[from].Role != ObservrTurn.User) from++;
                    return from;
                }
            }
            return 0;
        }

        /// <summary>The summary of a conversation's earlier part: the newest turn it covers, and the text.</summary>
        public (long UpToId, string Text)? Summary(string userId, long conversationId)
        {
            if (!DbExists()) return null;
            try
            {
                EnsureTable();
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT UpToId, Text FROM {Summaries} WHERE UserId = $u AND ConversationId = $c";
                cmd.Parameters.AddWithValue("$u", userId);
                cmd.Parameters.AddWithValue("$c", conversationId);
                using var r = cmd.ExecuteReader();
                return r.Read() ? (r.GetInt64(0), r.GetString(1)) : null;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { return null; }
        }

        public void SetSummary(string userId, long conversationId, long upToId, string text) =>
            Execute($"INSERT OR REPLACE INTO {Summaries} (UserId, ConversationId, UpToId, Text, CreatedUtc) VALUES ($u, $c, $up, $t, $at)", cmd =>
            {
                cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$c", conversationId);
                cmd.Parameters.AddWithValue("$up", upToId); cmd.Parameters.AddWithValue("$t", text); cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            });

        private List<ObservrTurn> Read(string sql, Action<SqliteCommand> bind)
        {
            if (!DbExists()) return [];
            try
            {
                EnsureTable();
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                bind(cmd);
                using var r = cmd.ExecuteReader();
                var list = new List<ObservrTurn>();
                int Col(string name) => r.GetOrdinal(name);
                while (r.Read())
                    list.Add(new ObservrTurn(r.GetInt64(Col("Id")), r.GetString(Col("Role")), r.GetString(Col("Text")),
                        r.IsDBNull(Col("FindingId")) ? null : r.GetInt64(Col("FindingId")),
                        DateTime.Parse(r.GetString(Col("CreatedUtc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        r.GetString(Col("TeamId")), r.GetString(Col("DatasetName")), r.GetInt64(Col("ConversationId")),
                        r.IsDBNull(Col("Looked")) ? null : r.GetString(Col("Looked"))));
                return list;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { return []; }
        }

        /// <summary>Adds a turn to a conversation (the user's current one when none is named).</summary>
        public long Add(string userId, string teamId, string dataSetName, string role, string text, long? findingId = null,
            long? conversationId = null, IReadOnlyList<ObservrLooked>? looked = null)
        {
            var conversation = conversationId ?? Current(userId, DateTime.UtcNow) ?? Next(userId);
            new SqLiteManager(DbPath, createOrOpenDatabase: true);
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO {Table} (UserId, TeamId, DatasetName, Role, Text, FindingId, CreatedUtc, ConversationId, Looked)
                VALUES ($u, $t, $d, $r, $x, $f, $c, $conv, $l); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$x", text);
            cmd.Parameters.AddWithValue("$f", (object?)findingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$conv", conversation);
            cmd.Parameters.AddWithValue("$l", looked is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(looked, ObservrLooked.Json) : DBNull.Value);
            var id = (long)cmd.ExecuteScalar()!;
            // The bound: the user's conversations past the last KeptConversations go, turns and all.
            using var trim = conn.CreateCommand();
            trim.CommandText = $@"DELETE FROM {Table} WHERE UserId = $u AND ConversationId NOT IN (
                SELECT ConversationId FROM {Table} WHERE UserId = $u GROUP BY ConversationId ORDER BY MAX(Id) DESC LIMIT $n)";
            trim.Parameters.AddWithValue("$u", userId);
            trim.Parameters.AddWithValue("$n", KeptConversations);
            trim.ExecuteNonQuery();
            using var trimSummaries = conn.CreateCommand();
            trimSummaries.CommandText = $"DELETE FROM {Summaries} WHERE UserId = $u AND ConversationId NOT IN (SELECT ConversationId FROM {Table} WHERE UserId = $u)";
            trimSummaries.Parameters.AddWithValue("$u", userId);
            trimSummaries.ExecuteNonQuery();
            return id;
        }

        /// <summary>Every conversation of the user gone (a deleted account; the panel's New conversation keeps them).</summary>
        public void Clear(string userId) =>
            Execute($"DELETE FROM {Table} WHERE UserId = $u; DELETE FROM {Summaries} WHERE UserId = $u", cmd => cmd.Parameters.AddWithValue("$u", userId));

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
