using Indx.Api;
using IndxServer.Engine;
using Indx.Storage;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndxServer.Services
{
    /// <summary>One letter read as another (<see cref="DatasetTextRules.Folds"/>): "å" as "a".</summary>
    public sealed record TextFold(string From, string To);

    /// <summary>
    /// How a dataset reads text before it is matched, over what Indx does by itself (17 Nov 2026):
    /// strings replaced in every searched field, and letters read as other letters. It is the
    /// engine's <see cref="TextNormalizer"/>, so it is built into the index: a change rebuilds the
    /// dataset from its store. A field's own rules (<see cref="FieldProxy.TextRules"/>, in its
    /// profile) run before these.
    /// </summary>
    public sealed class DatasetTextRules
    {
        /// <summary>Strings replaced, in their order, before Indx's own; an empty To removes the
        /// string. With KeepOriginal the text is indexed both ways.</summary>
        public List<TextRule> Replacements { get; set; } = [];

        /// <summary>Letters read as other letters, over Indx's own table: one for the same letter
        /// takes the place of Indx's.</summary>
        public List<TextFold> Folds { get; set; } = [];

        /// <summary>Whether both letters of a fold are kept (<see cref="CharReplacer.OneWayMode"/>),
        /// the default: "gra" finds grå and "grå" still means grå. False: they are one letter. It
        /// is the whole table's, Indx's own folds included.</summary>
        public bool OneWay { get; set; } = true;

        /// <summary>Letters of Indx's own fold table this dataset does not fold: "å" here keeps å
        /// apart from a, where Indx reads it as a by itself.</summary>
        public List<string> RemovedFolds { get; set; } = [];

        /// <summary>Strings of Indx's own replacements this dataset does not replace, by what they
        /// look for.</summary>
        public List<string> RemovedReplacements { get; set; } = [];

        /// <summary>The shortest word (<see cref="TokenizerSetup.MinimumWordSize"/>, 18 Nov 2026):
        /// a shorter one is not indexed or looked up as a word, and is found as part of a word
        /// only. Null is Indx's own, <see cref="TokenizerSetup.Default"/>'s 2. 1 makes single
        /// letters and digits words ("a 4"); 3 leaves two-letter words to the pattern matcher.</summary>
        public int? MinimumWordSize { get; set; }

        /// <summary>Nothing over what Indx does by itself.</summary>
        [JsonIgnore]
        public bool IsDefault => !ChangesText && MinimumWordSize is null;

        /// <summary>Whether the rules change how text is read (the normalizer), not only the
        /// shortest word.</summary>
        [JsonIgnore]
        public bool ChangesText => Replacements.Count > 0 || Folds.Count > 0 || !OneWay || RemovedFolds.Count > 0 || RemovedReplacements.Count > 0;

        public DatasetTextRules Copy() => new()
        {
            Replacements = [.. Replacements], Folds = [.. Folds], OneWay = OneWay,
            RemovedFolds = [.. RemovedFolds], RemovedReplacements = [.. RemovedReplacements],
            MinimumWordSize = MinimumWordSize,
        };

        public bool SameAs(DatasetTextRules? other) =>
            other != null && OneWay == other.OneWay && MinimumWordSize == other.MinimumWordSize
            && Replacements.SequenceEqual(other.Replacements) && Folds.SequenceEqual(other.Folds)
            && RemovedFolds.Order(StringComparer.Ordinal).SequenceEqual(other.RemovedFolds.Order(StringComparer.Ordinal))
            && RemovedReplacements.Order(StringComparer.Ordinal).SequenceEqual(other.RemovedReplacements.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Server-owned persistence for a dataset's text rules (<see cref="DatasetTextRules"/>): one JSON
    /// row per dataset in its own table inside indx.db, as <see cref="QueryParameterStore"/>. Read
    /// where a dataset's engine is built (<c>IndxServerInternalApi.ConfigurationFor</c>), not on a
    /// search, so not cached.
    /// </summary>
    public class DatasetTextRuleStore(ILogger<DatasetTextRuleStore> logger)
    {
        private const string Table = "DatasetTextRules";

        public const int MaxReplacements = 200, MaxFolds = 500, MaxLength = 64;

        /// <summary>The shortest word a dataset can choose, 1 to 5: past that a word of the
        /// records' language is left out of the word lookup.</summary>
        public const int LowestMinimumWordSize = 1, HighestMinimumWordSize = 5;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private static string DbPath => IndxServerInternalApi.SearchDbConnectionString;
        // Never open a connection that would create indx.db first: see BoostRuleStore.DbExists.
        private static bool DbExists() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath);
        private static SqliteConnection Connection() => new($"Data Source={DbPath}");

        private void EnsureTable(SqliteConnection conn)
        {
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

        /// <summary>The dataset's text rules, or null when it has none of its own. A row that cannot
        /// be read is none: the dataset is then built as Indx reads text by itself.</summary>
        public DatasetTextRules? Load(string teamId, string dataSetName)
        {
            if (!DbExists()) return null;
            try
            {
                using var conn = Connection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT RulesJson FROM {Table} WHERE TeamId = $t AND DatasetName = $d";
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSetName);
                var json = cmd.ExecuteScalar() as string;
                var rules = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<DatasetTextRules>(json, JsonOptions);
                return rules is { IsDefault: false } ? rules : null;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // no such table: nothing saved yet
            {
                return null;
            }
            catch (Exception ex)
            {
                StoreRead.Failed(ex);
                logger.LogError(ex, "Failed to read the text rules of {Team}/{Dataset}", teamId, dataSetName);
                return null;
            }
        }

        /// <summary>Replaces the dataset's text rules; rules that say nothing remove the row.
        /// Validation (<see cref="Objection"/>) is the caller's.</summary>
        public void Save(string teamId, string dataSetName, DatasetTextRules? rules)
        {
            if (rules == null || rules.IsDefault) { Delete(teamId, dataSetName); return; }
            new SqLiteManager(DbPath, createOrOpenDatabase: true); // ensure base schema exists
            using var conn = Connection();
            conn.Open();
            EnsureTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $@"INSERT INTO {Table} (TeamId, DatasetName, RulesJson, UpdatedUtc)
                   VALUES ($t, $d, $j, $u)
                   ON CONFLICT(TeamId, DatasetName) DO UPDATE SET RulesJson = $j, UpdatedUtc = $u;";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSetName);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(rules, JsonOptions));
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        /// <summary>Removes the dataset's text rules (on dataset delete, or back to none).</summary>
        public void Delete(string teamId, string dataSetName)
        {
            if (!DbExists()) return;
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
        }

        /// <summary>Moves a dataset's text rules to a new owning team (on dataset transfer).</summary>
        public void Transfer(string fromTeamId, string toTeamId, string dataSetName) => Move(fromTeamId, dataSetName, toTeamId, dataSetName);

        /// <summary>Re-keys the dataset's row under its new name (same team).</summary>
        public void Rename(string teamId, string dataSetName, string newName) => Move(teamId, dataSetName, teamId, newName);

        private void Move(string fromTeam, string fromName, string toTeam, string toName)
        {
            try
            {
                if (!DbExists()) return;
                using var conn = Connection();
                DatasetRowMove.Move(conn, Table, fromTeam, fromName, toTeam, toName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to move the text rules of {Team}/{Dataset} to {NewTeam}/{NewDataset}", fromTeam, fromName, toTeam, toName);
            }
        }

        /// <summary>Why the rules cannot be taken, null when they can.</summary>
        public static string? Objection(DatasetTextRules? rules)
        {
            if (rules == null) return "Text rules are required.";
            if (rules.Replacements.Count > MaxReplacements) return $"At most {MaxReplacements} replacements.";
            if (rules.Folds.Count > MaxFolds) return $"At most {MaxFolds} folds.";
            foreach (var r in rules.Replacements)
            {
                if (r == null || string.IsNullOrEmpty(r.From)) return "A replacement needs a from.";
                if (r.To == null) return $"The replacement for '{r.From}' needs a to; an empty one removes the text.";
                if (r.From.Length > MaxLength || r.To.Length > MaxLength) return $"A replacement's from and to are at most {MaxLength} characters.";
            }
            var seen = new HashSet<char>();
            foreach (var f in rules.Folds)
            {
                if (f == null || f.From is not { Length: 1 } || f.To is not { Length: 1 }) return "A fold is one letter read as one other letter.";
                if (f.From == f.To) return $"The fold of '{f.From}' reads it as itself.";
                if (!seen.Add(f.From[0])) return $"The letter '{f.From}' is folded twice.";
            }
            if (rules.RemovedFolds.Any(l => l is not { Length: 1 })) return "A removed fold is named by its one letter.";
            if (rules.RemovedReplacements.Any(string.IsNullOrEmpty)) return "A removed replacement is named by what it looks for.";
            if (rules.MinimumWordSize is { } size && (size < LowestMinimumWordSize || size > HighestMinimumWordSize))
                return $"The shortest word is {LowestMinimumWordSize} to {HighestMinimumWordSize} characters.";
            return null;
        }

        /// <summary>The engine's tokenizer for a shortest word: Indx's own in everything else.</summary>
        public static TokenizerSetup Tokenizer(int minimumWordSize)
        {
            var shipped = TokenizerSetup.Default;
            return new TokenizerSetup(shipped.Delimiters, minimumWordSize)
            {
                MaxSizeOfWordToken = shipped.MaxSizeOfWordToken,
                AllowedWordTerminators = shipped.AllowedWordTerminators,
            };
        }

        /// <summary>The configuration a dataset's engine is built with, given its rules: the
        /// normalizer when they change text, the tokenizer when they set the shortest word.</summary>
        public static ConfigurationParameters Apply(ConfigurationParameters configuration, DatasetTextRules? rules)
        {
            if (rules == null) return configuration;
            return configuration.With(
                textNormalizer: rules.ChangesText ? Build(rules) : null,
                tokenizerSetup: rules.MinimumWordSize is { } size ? Tokenizer(size) : null);
        }

        /// <summary>
        /// The engine's normalizer for these rules: the dataset's replacements before Indx's own of
        /// their kind (more than one character, then one, the shape the fast tables need, as a
        /// field's own rules are placed), and its folds over Indx's table, the dataset's taking the
        /// place of Indx's for the same letter.
        /// </summary>
        public static TextNormalizer Build(DatasetTextRules rules)
        {
            var shipped = TextNormalizer.Default;
            var from = shipped.StringReplacer?.ToReplace ?? [];
            var to = shipped.StringReplacer?.ReplaceBy ?? [];
            int multi = 0;
            while (multi < from.Length && from[multi].Length > 1) multi++;
            bool shaped = true;
            for (int i = multi; i < from.Length; i++)
                if (from[i].Length != 1) { shaped = false; break; }

            var gone = rules.RemovedReplacements.ToHashSet(StringComparer.Ordinal);
            var pairs = new List<(string, string)>(rules.Replacements.Count + from.Length);
            void Shipped(int start, int end)
            {
                for (int i = start; i < end; i++)
                    if (!gone.Contains(from[i])) pairs.Add((from[i], to[i] ?? ""));
            }
            if (shaped)
            {
                foreach (var r in rules.Replacements) if (r.From.Length > 1) pairs.Add((r.From, r.To));
                Shipped(0, multi);
                foreach (var r in rules.Replacements) if (r.From.Length <= 1) pairs.Add((r.From, r.To));
                Shipped(multi, from.Length);
            }
            else
            {
                foreach (var r in rules.Replacements) pairs.Add((r.From, r.To));
                Shipped(0, from.Length);
            }

            var folded = shipped.CharReplacer?.ToBeReplaced ?? [];
            var foldedTo = shipped.CharReplacer?.ReplaceBy ?? [];
            var own = rules.Folds.ToDictionary(f => f.From[0], f => f.To[0]);
            // A letter switched off in either case is switched off in both (17 Nov 2026). In a
            // case-insensitive field, every field unless set otherwise, text is lower-cased before
            // it is folded, so a capital's fold alone does nothing: "Å" switched off in place of "å"
            // left å read as a. In a case-sensitive field both folds apply, and both go.
            var removed = new HashSet<char>();
            foreach (var letter in rules.RemovedFolds.Where(l => l.Length == 1).Select(l => l[0]))
            {
                removed.Add(letter);
                removed.Add(char.ToLowerInvariant(letter));
                removed.Add(char.ToUpperInvariant(letter));
            }
            var folds = new List<(char, char)>(folded.Length + own.Count);
            for (int i = 0; i < folded.Length; i++)
                if (!own.ContainsKey(folded[i]) && !removed.Contains(folded[i])) folds.Add((folded[i], foldedTo[i]));
            foreach (var (letter, readAs) in own) folds.Add((letter, readAs));

            var kept = rules.Replacements.Where(r => r.KeepOriginal).Select(r => r.From).Distinct().ToArray();
            return new TextNormalizer(pairs.ToArray(), folds.ToArray(), rules.OneWay, kept.Length > 0 ? kept : null);
        }
    }
}
