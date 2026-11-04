using IndxServer.Engine;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indx.Storage;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>One question an owner marked in Try it, and the record that answers it where they
    /// named one. <see cref="Label"/> is always <see cref="Question"/>: a mark for a search was tried
    /// and dropped, since on the way to a question the list soon stops mattering; it stays in the
    /// row so the replay file keeps its label column.
    /// What the judge said when it was marked is kept beside it, so a later judge or wording can be
    /// measured against the same text.</summary>
    public sealed record SearchOrAskMark(string Text, string Label, long? ExpectedKey, string? ExpectedTitle,
        double? P, string? Offer, string? Judge, DateTime MarkedUtc)
    {
        public const string Question = "question";
    }

    /// <summary>
    /// A dataset's test set for Search or ask: questions marked in Try it (4 Nov 2026). It changes nothing a visitor gets. It is the dataset's own labelled set, in its
    /// own language and words, so a judge, a wording or a threshold can be measured on what this
    /// dataset's visitors type rather than on ours; exported, it is a JevProbe replay set. One JSON
    /// row per dataset in its own table inside indx.db, the pattern of <see cref="SearchOrAskStore"/>;
    /// keyed by (TeamId, DatasetName), cached, writes invalidate.
    /// </summary>
    public class SearchOrAskTestSetStore(ILogger<SearchOrAskTestSetStore> logger)
    {
        private const string Table = "SearchOrAskTestSets";

        /// <summary>Enough for any measurement; a bound so the row stays small.</summary>
        public const int MaxMarks = 2000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // (teamId\0dataset) -> marks, newest first. Invalidate on write; never mutate.
        private readonly ConcurrentDictionary<string, IReadOnlyList<SearchOrAskMark>> _cache = new();

        private static string Key(string teamId, string dataSetName) => $"{teamId}\0{dataSetName}";
        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        // Never open a connection that would create indx.db first: see BoostRuleStore.DbExists.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        /// <summary>The same text, however it was cased or spaced, is one mark.</summary>
        public static string Normalise(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

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
                           MarksJson   TEXT NOT NULL,
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

        /// <summary>The dataset's marks, newest first; empty when it has none.</summary>
        public IReadOnlyList<SearchOrAskMark> Load(string teamId, string dataSetName) =>
            _cache.GetOrAdd(Key(teamId, dataSetName), _ => ReadFromDb(teamId, dataSetName));

        /// <summary>The mark for this text, if it has one.</summary>
        public SearchOrAskMark? Find(string teamId, string dataSetName, string text)
        {
            var key = Normalise(text);
            return Load(teamId, dataSetName).FirstOrDefault(m => Normalise(m.Text) == key);
        }

        /// <summary>Adds the mark, replacing one for the same text, newest first.</summary>
        public void Mark(string teamId, string dataSetName, SearchOrAskMark mark)
        {
            var key = Normalise(mark.Text);
            var marks = Load(teamId, dataSetName).Where(m => Normalise(m.Text) != key).Prepend(mark).Take(MaxMarks).ToList();
            Save(teamId, dataSetName, marks);
        }

        /// <summary>Takes the mark for this text out of the set.</summary>
        public void Unmark(string teamId, string dataSetName, string text)
        {
            var key = Normalise(text);
            Save(teamId, dataSetName, Load(teamId, dataSetName).Where(m => Normalise(m.Text) != key).ToList());
        }

        /// <summary>The set as a JevProbe replay file: tab separated, id, lang, kind, label, query,
        /// note. A question with its record carries <c>from {key}</c>, as the generated sets do. Tabs and line breaks in a text become spaces.</summary>
        public static string ToReplay(IReadOnlyList<SearchOrAskMark> marks)
        {
            static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            var b = new StringBuilder("id\tlang\tkind\tlabel\tquery\tnote\n");
            int n = 0;
            foreach (var m in marks.Reverse())
                b.Append($"m{++n:000}\t\tmarked\tquestion\t{Clean(m.Text)}\t{(m.ExpectedKey is { } k ? $"from {k}" : "")}\n");
            return b.ToString();
        }

        private IReadOnlyList<SearchOrAskMark> ReadFromDb(string teamId, string dataSetName)
        {
            if (!DbExists()) return [];
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT MarksJson FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<SearchOrAskMark>>(json, JsonOptions) ?? [];
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // no such table: nothing marked yet
            {
                return [];
            }
            catch (Exception ex)
            {
                // A row that cannot be read is an empty set: it is a measurement, and nothing a
                // visitor gets depends on it.
                logger.LogError(ex, "Failed to read the Search or ask test set for {Team}/{Dataset}", teamId, dataSetName);
                return [];
            }
        }

        /// <summary>Replaces the dataset's marks. None removes the row.</summary>
        private void Save(string teamId, string dataSetName, IReadOnlyList<SearchOrAskMark> marks)
        {
            if (marks.Count == 0)
            {
                Delete(teamId, dataSetName);
                return;
            }
            new SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
            EnsureTable();
            using var conn = Connection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, MarksJson, UpdatedUtc)
                   VALUES ($t, $d, $j, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET MarksJson = $j, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(marks, JsonOptions));
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _cache.TryRemove(Key(teamId, dataSetName), out _);
        }

        /// <summary>Removes the dataset's test set (on dataset delete).</summary>
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
                logger.LogWarning(ex, "Failed to delete the Search or ask test set for {Team}/{Dataset}", teamId, dataSetName);
            }
            finally
            {
                _cache.TryRemove(Key(teamId, dataSetName), out _);
            }
        }

        /// <summary>Moves a dataset's test set to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName)
        {
            if (Load(fromTeamId, dataSetName) is { Count: > 0 } marks) Save(toTeamId, dataSetName, marks);
            Delete(fromTeamId, dataSetName);
        }

        /// <summary>Re-keys the test set under the dataset's new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName)
        {
            if (Load(teamId, dataSetName) is { Count: > 0 } marks) Save(teamId, newName, marks);
            Delete(teamId, dataSetName);
        }
    }
}
