using IndxServer.Services.SearchOrAsk;
using Microsoft.Data.Sqlite;

namespace IndxServer.Services
{
    /// <summary>
    /// The judge's word on whether two question texts from different visits ask for the same thing
    /// (<see cref="SearchOrAskRecipes.SameNeed"/>, 2 Nov 2026), and which texts have been compared.
    /// Texts are <see cref="SearchOrAskRephrasing.Normalise"/>d, as the needs key them; a pair is
    /// stored once, its two texts in ordinal order, low probabilities too, so it is never asked twice.
    /// </summary>
    public sealed partial class StatisticsStore
    {
        private static void EnsureSameNeedSchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS AskNeedLinks (
    TeamId    TEXT    NOT NULL,
    DataSet   TEXT    NOT NULL,
    A         TEXT    NOT NULL,
    B         TEXT    NOT NULL,
    P         REAL    NOT NULL,
    Timestamp INTEGER NOT NULL,
    PRIMARY KEY (TeamId, DataSet, A, B)
);
CREATE TABLE IF NOT EXISTS AskNeedDenied (
    TeamId    TEXT    NOT NULL,
    DataSet   TEXT    NOT NULL,
    A         TEXT    NOT NULL,
    B         TEXT    NOT NULL,
    DeniedBy  TEXT    NOT NULL,
    Timestamp INTEGER NOT NULL,
    PRIMARY KEY (TeamId, DataSet, A, B)
);
CREATE TABLE IF NOT EXISTS AskNeedTexts (
    TeamId    TEXT    NOT NULL,
    DataSet   TEXT    NOT NULL,
    Text      TEXT    NOT NULL,
    Timestamp INTEGER NOT NULL,
    PRIMARY KEY (TeamId, DataSet, Text)
);";
            cmd.ExecuteNonQuery();
        }

        /// <summary>The question texts a need can be made of, within retention: counted asks that are
        /// no follow-up, normalised, most recently asked first, at most <paramref name="max"/>.</summary>
        public List<string> AskTexts(string teamId, string dataSet, int max)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT Question, MAX(Timestamp) FROM AskEvents
                WHERE TeamId = $t AND DataSet = $d AND Counted = 1 AND FollowUp = 0
                GROUP BY Question ORDER BY MAX(Timestamp) DESC";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            var texts = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read() && texts.Count < max)
            {
                var text = SearchOrAskRephrasing.Normalise(r.GetString(0));
                if (text.Length > 0 && seen.Add(text)) texts.Add(text);
            }
            return texts;
        }

        /// <summary>The texts already compared with the others.</summary>
        public HashSet<string> SameNeedCompared(string teamId, string dataSet)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Text FROM AskNeedTexts WHERE TeamId = $t AND DataSet = $d";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            var set = new HashSet<string>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }

        public void MarkSameNeedCompared(string teamId, string dataSet, string text)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO AskNeedTexts (TeamId, DataSet, Text, Timestamp) VALUES ($t, $d, $x, $ts)";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$x", text);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        private static (string A, string B) PairKey(string x, string y) => string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);

        /// <summary>Whether the judge has given its word on the pair, either way round.</summary>
        public bool SameNeedJudged(string teamId, string dataSet, string x, string y)
        {
            var (a, b) = PairKey(x, y);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM AskNeedLinks WHERE TeamId = $t AND DataSet = $d AND A = $a AND B = $b";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$a", a);
            cmd.Parameters.AddWithValue("$b", b);
            return cmd.ExecuteScalar() != null;
        }

        public void SaveSameNeed(string teamId, string dataSet, string x, string y, double p)
        {
            var (a, b) = PairKey(x, y);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO AskNeedLinks (TeamId, DataSet, A, B, P, Timestamp) VALUES ($t, $d, $a, $b, $p, $ts)";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$a", a);
            cmd.Parameters.AddWithValue("$b", b);
            cmd.Parameters.AddWithValue("$p", p);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        /// <summary>The pairs the judge said ask for the same thing, with its probability, less the
        /// ones an editor said are not.</summary>
        public List<(string A, string B, double P)> SameNeedLinks(string teamId, string dataSet)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT l.A, l.B, l.P FROM AskNeedLinks l
                WHERE l.TeamId = $t AND l.DataSet = $d AND l.P >= $threshold
                  AND NOT EXISTS (SELECT 1 FROM AskNeedDenied x WHERE x.TeamId = l.TeamId AND x.DataSet = l.DataSet AND x.A = l.A AND x.B = l.B)";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$threshold", SearchOrAskRephrasing.Threshold);
            var links = new List<(string, string, double)>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) links.Add((r.GetString(0), r.GetString(1), r.GetDouble(2)));
            return links;
        }

        /// <summary>
        /// An editor's "not the same" on two wordings of a need (Nov 2026): the pair is no longer a
        /// link, whether the judge made it across visits or within one. One pair, never a wording or a
        /// need: the other links stand, so the need splits only where that pair held it together. Who
        /// said so is kept, for the measurement the denials feed (JevProbe's same-need pairs).
        /// </summary>
        public void DenySameNeed(string teamId, string dataSet, string x, string y, string deniedBy)
        {
            var (a, b) = PairKey(SearchOrAskRephrasing.Normalise(x), SearchOrAskRephrasing.Normalise(y));
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO AskNeedDenied (TeamId, DataSet, A, B, DeniedBy, Timestamp) VALUES ($t, $d, $a, $b, $by, $ts)";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            cmd.Parameters.AddWithValue("$a", a);
            cmd.Parameters.AddWithValue("$b", b);
            cmd.Parameters.AddWithValue("$by", deniedBy);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        /// <summary>The pairs an editor said are not the same, in ordinal order.</summary>
        public HashSet<(string A, string B)> DeniedSameNeeds(string teamId, string dataSet)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT A, B FROM AskNeedDenied WHERE TeamId = $t AND DataSet = $d";
            cmd.Parameters.AddWithValue("$t", teamId);
            cmd.Parameters.AddWithValue("$d", dataSet);
            var set = new HashSet<(string, string)>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add((r.GetString(0), r.GetString(1)));
            return set;
        }

        private static bool IsDenied(HashSet<(string A, string B)> denied, string x, string y) => denied.Contains(PairKey(x, y));
    }
}
