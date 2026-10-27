using System.Security.Cryptography;
using IndxServer.Engine;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services.Webhooks
{
    /// <summary>A dataset's incoming webhook: who calls it is told its id and secret; the rest says
    /// how its events become documents. <see cref="IdField"/> is the sender's own id (text or a
    /// number); <see cref="VersionField"/>, when named, orders events (an older one is ignored);
    /// <see cref="BrakeShare"/> is the share of the dataset that deletes and destructive updates may
    /// reach within an hour before a batch is held for an admin.</summary>
    public sealed record Webhook(string Id, string TeamId, string DatasetName, string Name, string IdField, string? VersionField,
        double BrakeShare, string Secret, DateTime CreatedUtc, DateTime? LastEventUtc);

    /// <summary>One call as it was handled: how many events it held, what became of them, and why
    /// the refused ones were refused (the first few).</summary>
    public sealed record WebhookDelivery(long Id, string WebhookId, DateTime ReceivedUtc, int Events, int Inserted, int Updated,
        int Deleted, int Ignored, int Refused, int Destructive, string Outcome, string? Detail);

    /// <summary>A batch the brake stopped: kept whole until an admin applies or discards it.</summary>
    public sealed record WebhookHeldBatch(string Id, string WebhookId, DateTime HeldUtc, string Reason, int Events, string Body);

    /// <summary>A call waiting to be applied: stored before the sender is answered, so a restart
    /// loses nothing that was accepted.</summary>
    public sealed record WebhookInboxItem(long Seq, string WebhookId, DateTime ReceivedUtc, string Body);

    /// <summary>
    /// The webhooks' tables, in the search database (indx.db) beside the engine's own, like boost
    /// rules: the server's backups then hold the key registry, whose loss would make every text id
    /// a new document. Webhooks exist only for existing datasets, so the file exists by the time any
    /// of this is written (see BoostRuleStore on why nothing here may create it).
    /// </summary>
    public sealed class WebhookStore(ILogger<WebhookStore> logger)
    {
        public const int DeliveriesKept = 200;

        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            return conn;
        }

        private bool _ensured;
        private readonly Lock _ensureLock = new();

        /// <summary>Creates the tables once the search database exists. Idempotent.</summary>
        public bool Ensure()
        {
            if (_ensured) return true;
            if (!DbExists()) return false;
            lock (_ensureLock)
            {
                if (_ensured) return true;
                try
                {
                    using var conn = Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Webhooks (
    Id           TEXT PRIMARY KEY,
    TeamId       TEXT NOT NULL,
    DatasetName  TEXT NOT NULL,
    Name         TEXT NOT NULL,
    IdField      TEXT NOT NULL,
    VersionField TEXT,
    BrakeShare   REAL NOT NULL,
    Secret       TEXT NOT NULL,
    CreatedUtc   TEXT NOT NULL,
    LastEventUtc TEXT
);
CREATE INDEX IF NOT EXISTS IX_Webhooks_Ds ON Webhooks (TeamId, DatasetName);
CREATE TABLE IF NOT EXISTS WebhookKeys (
    TeamId      TEXT NOT NULL,
    DatasetName TEXT NOT NULL,
    ExternalKey TEXT NOT NULL,
    InternalKey INTEGER NOT NULL,
    PRIMARY KEY (TeamId, DatasetName, ExternalKey)
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WebhookKeys_Internal ON WebhookKeys (TeamId, DatasetName, InternalKey);
CREATE TABLE IF NOT EXISTS WebhookInbox (
    Seq         INTEGER PRIMARY KEY AUTOINCREMENT,
    WebhookId   TEXT NOT NULL,
    ReceivedUtc TEXT NOT NULL,
    Body        TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS WebhookDeliveries (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    WebhookId   TEXT NOT NULL,
    ReceivedUtc TEXT NOT NULL,
    Events      INTEGER NOT NULL,
    Inserted    INTEGER NOT NULL,
    Updated     INTEGER NOT NULL,
    Deleted     INTEGER NOT NULL,
    Ignored     INTEGER NOT NULL,
    Refused     INTEGER NOT NULL,
    Destructive INTEGER NOT NULL,
    Outcome     TEXT NOT NULL,
    Detail      TEXT
);
CREATE INDEX IF NOT EXISTS IX_WebhookDeliveries_Hook ON WebhookDeliveries (WebhookId, Id);
CREATE TABLE IF NOT EXISTS WebhookHeld (
    Id        TEXT PRIMARY KEY,
    WebhookId TEXT NOT NULL,
    HeldUtc   TEXT NOT NULL,
    Reason    TEXT NOT NULL,
    Events    INTEGER NOT NULL,
    Body      TEXT NOT NULL
);";
                    cmd.ExecuteNonQuery();
                    _ensured = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to ensure the webhook tables");
                }
            }
            return _ensured;
        }

        // ── Webhooks ─────────────────────────────────────────────────────────────────────

        public static string NewId() => "wh_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public Webhook Create(string teamId, string datasetName, string name, string idField, string? versionField, double brakeShare)
        {
            if (!Ensure()) throw new InvalidOperationException("The search database does not exist yet.");
            var hook = new Webhook(NewId(), teamId, datasetName, name, idField, versionField, brakeShare, NewSecret(), DateTime.UtcNow, null);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO Webhooks (Id, TeamId, DatasetName, Name, IdField, VersionField, BrakeShare, Secret, CreatedUtc)
                                VALUES ($id, $t, $d, $n, $idf, $vf, $b, $s, $c)";
            cmd.Parameters.AddWithValue("$id", hook.Id);
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", datasetName);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$idf", idField);
            cmd.Parameters.AddWithValue("$vf", (object?)versionField ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$b", brakeShare);
            cmd.Parameters.AddWithValue("$s", hook.Secret);
            cmd.Parameters.AddWithValue("$c", hook.CreatedUtc.ToString("O"));
            cmd.ExecuteNonQuery();
            return hook;
        }

        public Webhook? Get(string id)
        {
            if (!Ensure()) return null;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Webhooks WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadHook(r) : null;
        }

        public List<Webhook> List(string teamId, string datasetName)
        {
            var list = new List<Webhook>();
            if (!Ensure()) return list;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Webhooks WHERE TeamId = $t AND DatasetName = $d ORDER BY CreatedUtc";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", datasetName);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadHook(r));
            return list;
        }

        private static Webhook ReadHook(SqliteDataReader r) => new(
            r.GetString(r.GetOrdinal("Id")), r.GetString(r.GetOrdinal("TeamId")), r.GetString(r.GetOrdinal("DatasetName")),
            r.GetString(r.GetOrdinal("Name")), r.GetString(r.GetOrdinal("IdField")),
            r.IsDBNull(r.GetOrdinal("VersionField")) ? null : r.GetString(r.GetOrdinal("VersionField")),
            r.GetDouble(r.GetOrdinal("BrakeShare")), r.GetString(r.GetOrdinal("Secret")),
            DateTime.Parse(r.GetString(r.GetOrdinal("CreatedUtc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
            r.IsDBNull(r.GetOrdinal("LastEventUtc")) ? null
                : DateTime.Parse(r.GetString(r.GetOrdinal("LastEventUtc")), null, System.Globalization.DateTimeStyles.RoundtripKind));

        /// <summary>A new secret; the old one stops working at once.</summary>
        public string? RotateSecret(string id)
        {
            if (!Ensure()) return null;
            var secret = NewSecret();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Webhooks SET Secret = $s WHERE Id = $id";
            cmd.Parameters.AddWithValue("$s", secret);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() == 1 ? secret : null;
        }

        /// <summary>Removes the webhook, what waits for it and its log. The key registry is the
        /// dataset's and stays: another webhook, or this one made again, keeps the same documents.</summary>
        public bool Delete(string id)
        {
            if (!Ensure()) return false;
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            int n = 0;
            foreach (var sql in new[] { "DELETE FROM WebhookInbox WHERE WebhookId = $id", "DELETE FROM WebhookDeliveries WHERE WebhookId = $id",
                                        "DELETE FROM WebhookHeld WHERE WebhookId = $id", "DELETE FROM Webhooks WHERE Id = $id" })
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", id);
                n = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n == 1;
        }

        /// <summary>A dataset deleted: its webhooks and its key registry go with it.</summary>
        public void DeleteDataset(string teamId, string datasetName)
        {
            if (!Ensure()) return;
            foreach (var hook in List(teamId, datasetName)) Delete(hook.Id);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WebhookKeys WHERE TeamId = $t AND DatasetName = $d";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", datasetName);
            cmd.ExecuteNonQuery();
        }

        /// <summary>A dataset renamed: its webhooks and key registry follow it, addresses unchanged.</summary>
        public void Rename(string teamId, string datasetName, string newName) =>
            Move("UPDATE {0} SET DatasetName = $n WHERE TeamId = $t AND DatasetName = $d", teamId, datasetName, newName);

        /// <summary>A dataset moved to another team: its webhooks and key registry go with it.</summary>
        public void Transfer(string fromTeamId, string toTeamId, string datasetName) =>
            Move("UPDATE {0} SET TeamId = $n WHERE TeamId = $t AND DatasetName = $d", fromTeamId, datasetName, toTeamId);

        private void Move(string sql, string teamId, string datasetName, string to)
        {
            if (!Ensure()) return;
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var table in new[] { "Webhooks", "WebhookKeys" })
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = string.Format(sql, table);
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", datasetName);
                cmd.Parameters.AddWithValue("$n", to);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // ── The key registry ─────────────────────────────────────────────────────────────

        /// <summary>The numeric keys the registry holds for these external ids.</summary>
        public Dictionary<string, long> Keys(string teamId, string datasetName, IEnumerable<string> externalIds)
        {
            var found = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!Ensure()) return found;
            using var conn = Open();
            foreach (var chunk in externalIds.Distinct().Chunk(500))
            {
                using var cmd = conn.CreateCommand();
                var names = chunk.Select((_, i) => "$k" + i).ToArray();
                cmd.CommandText = $"SELECT ExternalKey, InternalKey FROM WebhookKeys WHERE TeamId = $t AND DatasetName = $d AND ExternalKey IN ({string.Join(',', names)})";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", datasetName);
                for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read()) found[r.GetString(0)] = r.GetInt64(1);
            }
            return found;
        }

        /// <summary>Whether the registry gives this numeric key to an id already.</summary>
        public bool KeyTaken(string teamId, string datasetName, long internalKey)
        {
            if (!Ensure()) return false;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM WebhookKeys WHERE TeamId = $t AND DatasetName = $d AND InternalKey = $k";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", datasetName);
            cmd.Parameters.AddWithValue("$k", internalKey);
            return cmd.ExecuteScalar() != null;
        }

        public void SaveKeys(string teamId, string datasetName, IReadOnlyDictionary<string, long> keys)
        {
            if (keys.Count == 0 || !Ensure()) return;
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (external, internalKey) in keys)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO WebhookKeys (TeamId, DatasetName, ExternalKey, InternalKey) VALUES ($t, $d, $e, $k)";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", datasetName);
                cmd.Parameters.AddWithValue("$e", external);
                cmd.Parameters.AddWithValue("$k", internalKey);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // ── The inbox ────────────────────────────────────────────────────────────────────

        public void Enqueue(string webhookId, string body)
        {
            if (!Ensure()) throw new InvalidOperationException("The search database does not exist yet.");
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO WebhookInbox (WebhookId, ReceivedUtc, Body) VALUES ($w, $r, $b); " +
                              "UPDATE Webhooks SET LastEventUtc = $r WHERE Id = $w;";
            cmd.Parameters.AddWithValue("$w", webhookId);
            cmd.Parameters.AddWithValue("$r", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$b", body);
            cmd.ExecuteNonQuery();
        }

        /// <summary>The oldest waiting calls, in the order they came.</summary>
        public List<WebhookInboxItem> Waiting(int max)
        {
            var list = new List<WebhookInboxItem>();
            if (!Ensure()) return list;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Seq, WebhookId, ReceivedUtc, Body FROM WebhookInbox ORDER BY Seq LIMIT $n";
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetString(3)));
            return list;
        }

        public int WaitingCount(string webhookId)
        {
            if (!Ensure()) return 0;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM WebhookInbox WHERE WebhookId = $w";
            cmd.Parameters.AddWithValue("$w", webhookId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void Done(long seq)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WebhookInbox WHERE Seq = $s";
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.ExecuteNonQuery();
        }

        // ── The log ──────────────────────────────────────────────────────────────────────

        public void Log(WebhookDelivery d)
        {
            if (!Ensure()) return;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO WebhookDeliveries (WebhookId, ReceivedUtc, Events, Inserted, Updated, Deleted, Ignored, Refused, Destructive, Outcome, Detail)
                                VALUES ($w, $r, $e, $i, $u, $del, $ig, $rf, $ds, $o, $dt);
                                DELETE FROM WebhookDeliveries WHERE WebhookId = $w AND Id NOT IN
                                  (SELECT Id FROM WebhookDeliveries WHERE WebhookId = $w ORDER BY Id DESC LIMIT $keep);";
            cmd.Parameters.AddWithValue("$w", d.WebhookId);
            cmd.Parameters.AddWithValue("$r", d.ReceivedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$e", d.Events);
            cmd.Parameters.AddWithValue("$i", d.Inserted);
            cmd.Parameters.AddWithValue("$u", d.Updated);
            cmd.Parameters.AddWithValue("$del", d.Deleted);
            cmd.Parameters.AddWithValue("$ig", d.Ignored);
            cmd.Parameters.AddWithValue("$rf", d.Refused);
            cmd.Parameters.AddWithValue("$ds", d.Destructive);
            cmd.Parameters.AddWithValue("$o", d.Outcome);
            cmd.Parameters.AddWithValue("$dt", (object?)d.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$keep", DeliveriesKept);
            cmd.ExecuteNonQuery();
        }

        public List<WebhookDelivery> Deliveries(string webhookId, int limit = 50)
        {
            var list = new List<WebhookDelivery>();
            if (!Ensure()) return list;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, WebhookId, ReceivedUtc, Events, Inserted, Updated, Deleted, Ignored, Refused, Destructive, Outcome, Detail " +
                              "FROM WebhookDeliveries WHERE WebhookId = $w ORDER BY Id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$w", webhookId);
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, DeliveriesKept));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9),
                    r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11)));
            return list;
        }

        /// <summary>Deletes and destructive updates applied for the dataset since a time: what the
        /// brake counts against, across all its webhooks.</summary>
        public int DestructiveSince(string teamId, string datasetName, DateTime sinceUtc)
        {
            if (!Ensure()) return 0;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(SUM(d.Destructive), 0) FROM WebhookDeliveries d JOIN Webhooks w ON w.Id = d.WebhookId
                                WHERE w.TeamId = $t AND w.DatasetName = $d AND d.ReceivedUtc >= $since AND d.Outcome = 'applied'";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", datasetName);
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // ── Held batches ─────────────────────────────────────────────────────────────────

        public WebhookHeldBatch Hold(string webhookId, string reason, int events, string body)
        {
            var held = new WebhookHeldBatch("hb_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                webhookId, DateTime.UtcNow, reason, events, body);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO WebhookHeld (Id, WebhookId, HeldUtc, Reason, Events, Body) VALUES ($id, $w, $h, $r, $e, $b)";
            cmd.Parameters.AddWithValue("$id", held.Id);
            cmd.Parameters.AddWithValue("$w", webhookId);
            cmd.Parameters.AddWithValue("$h", held.HeldUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$r", reason);
            cmd.Parameters.AddWithValue("$e", events);
            cmd.Parameters.AddWithValue("$b", body);
            cmd.ExecuteNonQuery();
            return held;
        }

        public List<WebhookHeldBatch> Held(string webhookId)
        {
            var list = new List<WebhookHeldBatch>();
            if (!Ensure()) return list;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, WebhookId, HeldUtc, Reason, Events, Body FROM WebhookHeld WHERE WebhookId = $w ORDER BY HeldUtc";
            cmd.Parameters.AddWithValue("$w", webhookId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new(r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    r.GetString(3), r.GetInt32(4), r.GetString(5)));
            return list;
        }

        /// <summary>Takes a held batch out of the hold: to apply it (it goes to the front of the
        /// queue, past the brake) or to discard it. Null when there is no such batch.</summary>
        public WebhookHeldBatch? Release(string webhookId, string heldId)
        {
            var held = Held(webhookId).FirstOrDefault(h => h.Id == heldId);
            if (held == null) return null;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WebhookHeld WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", heldId);
            cmd.ExecuteNonQuery();
            return held;
        }
    }
}
