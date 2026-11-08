using System.Text.Json;
using IndxServer.Components.Datasets;

namespace IndxServer.Services
{
    /// <summary>
    /// The data check (Nov 2026): what a dataset's records lack, found by reading every one of them,
    /// which no search can answer. A record missing a searched field cannot be found by it; a searched
    /// value longer than the index takes is cut. The report is a table, field by field, not one count
    /// of records missing any searched field: on tmdb23k that count was 20,650 of 23,439, nearly all
    /// of them films with no collection or tagline, which is no fault, hiding the 20 without a
    /// description, which is. It runs when the owner asks for it
    /// (<see cref="DataCheckService"/>), never after an Index(): on 2M records it keeps a core busy
    /// for a while. The report is kept (<see cref="DataCheckStore"/>) and judged against what has
    /// changed since (<see cref="DataCheckStanding"/>), not against its age.
    /// </summary>
    public static class DataCheck
    {
        /// <summary>The most keys a list keeps: as many as the Records section lists from a search.</summary>
        public const int KeyCap = 1000;
        /// <summary>The most values a field's count tells apart. Past it the count says "or more":
        /// a field of unique values on 2M records would otherwise hold millions of hashes.</summary>
        public const int DistinctCap = 100_000;
        /// <summary>A report goes out of date once this share of the records it read has changed.</summary>
        public const double OutdatedShare = 0.05;

        /// <summary>A field as the check reads it: its name, and whether a search reads it.</summary>
        public sealed record CheckedField(string Name, bool Searched);

        /// <summary>What a report was made against: the searched fields and the text ceiling. When
        /// either changes, the findings about missing and cut text no longer hold.</summary>
        public static string Fingerprint(IEnumerable<CheckedField> fields, int textCeiling) =>
            string.Join(",", fields.Where(f => f.Searched).Select(f => f.Name).Order(StringComparer.Ordinal)) + "|" + textCeiling;

        /// <summary>The ceiling a server engine indexes to: every dataset runs the default configuration.</summary>
        public static int TextCeiling => Indx.Api.ConfigurationParameters.Default.Indexer.MaxIndexTextLength;

        /// <summary>Whether any searched value of the record is longer than the index takes, and was cut.
        /// The ceiling holds per value, an array's per element, as the index applies it.</summary>
        public static bool IsCut(JsonElement record, IEnumerable<CheckedField> fields, int textCeiling) =>
            fields.Any(f => f.Searched && DocumentInspection.Values(record, f.Name).Any(v => v.Length > textCeiling));

        /// <summary>
        /// Reads the records once and reports on them. <paramref name="read"/> is told how many have
        /// been read every thousand records and at the end. A record that is not JSON is counted and
        /// otherwise passed over.
        /// </summary>
        public static DataCheckReport Run(IEnumerable<(long Key, string Json)> records, IReadOnlyList<CheckedField> fields,
            int textCeiling, Action<long>? read = null, CancellationToken ct = default)
        {
            var tallies = fields.Select(f => new Tally(f)).ToArray();
            var cut = new KeyList();
            long count = 0;
            foreach (var (key, json) in records)
            {
                ct.ThrowIfCancellationRequested();
                count++;
                if (count % 1000 == 0) read?.Invoke(count);
                JsonDocument doc;
                try { doc = JsonDocument.Parse(json); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var isCut = false;
                    foreach (var t in tallies)
                    {
                        var values = DocumentInspection.Values(root, t.Field.Name);
                        if (values.Count == 0 || values.All(v => v.Length == 0))
                        {
                            t.Without++;
                            t.MissingKeys.Add(key);
                            continue;
                        }
                        foreach (var v in values)
                        {
                            if (v.Length == 0) continue;
                            if (t.Field.Searched && v.Length > textCeiling) isCut = true;
                            if (t.Hashes.Count < DistinctCap) t.Hashes.Add(Hash(v));
                            else if (!t.Hashes.Contains(Hash(v))) t.Capped = true;
                        }
                    }
                    if (isCut) { cut.Count++; cut.Add(key); }
                }
            }
            read?.Invoke(count);
            return new DataCheckReport(
                DateTime.UtcNow, count, Fingerprint(fields, textCeiling),
                tallies.Select(t => new DataCheckField(t.Field.Name, t.Field.Searched, t.Without, t.Hashes.Count, t.Capped, t.MissingKeys.Keys())).ToList(),
                cut.Count, cut.Keys());
        }

        /// <summary>FNV-1a over the value's characters: 64 bits, so 100,000 values are told apart
        /// without holding their text.</summary>
        private static ulong Hash(string value)
        {
            var h = 14695981039346656037UL;
            foreach (var c in value) { h ^= c; h *= 1099511628211UL; }
            return h;
        }

        private sealed class Tally(CheckedField field)
        {
            public CheckedField Field { get; } = field;
            public long Without;
            public bool Capped;
            public readonly HashSet<ulong> Hashes = new();
            public readonly KeyList MissingKeys = new();
        }

        /// <summary>The smallest <see cref="KeyCap"/> keys seen, in order: the store is read in no
        /// particular order, and sorting it by key is most of the wait on a large dataset.</summary>
        private sealed class KeyList
        {
            private readonly SortedSet<long> _keys = new();
            public long Count;
            public void Add(long key)
            {
                if (_keys.Count == KeyCap && key >= _keys.Max) return;
                _keys.Add(key);
                if (_keys.Count > KeyCap) _keys.Remove(_keys.Max);
            }
            public long[] Keys() => _keys.ToArray();
        }
    }

    /// <summary>A field in a report: how many records lack it, how many values it has, and the
    /// first <see cref="DataCheck.KeyCap"/> records without it.</summary>
    public sealed record DataCheckField(string Name, bool Searched, long Without, long Distinct, bool DistinctCapped, long[] MissingKeys);

    /// <summary>What one check found. <see cref="Cut"/> counts every record with a cut value; its
    /// key list keeps the first <see cref="DataCheck.KeyCap"/>.</summary>
    public sealed record DataCheckReport(
        DateTime CheckedUtc, long Records, string Fingerprint, IReadOnlyList<DataCheckField> Fields,
        long Cut, long[] CutKeys)
    {
        public DataCheckField? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);
    }

    public enum DataCheckFreshness
    {
        /// <summary>No record has changed since the check, however long ago it was.</summary>
        Current,
        /// <summary>Some records have changed: the findings hold for the rest, the totals have drifted.</summary>
        Changed,
        /// <summary>The records were replaced, the searched fields changed, or too many records changed.</summary>
        Outdated,
    }

    /// <summary>
    /// A stored report and what has happened to the dataset since it was made. The age alone never
    /// makes a report wrong: a check from weeks ago on records nobody has touched is exact.
    /// </summary>
    public sealed record DataCheckStanding(DataCheckReport Report, long ChangedSince, string? Replaced)
    {
        /// <summary>How the report stands against the dataset's searched fields now.</summary>
        public DataCheckFreshness FreshnessFor(string fingerprint) =>
            OutdatedBecause(fingerprint) != null ? DataCheckFreshness.Outdated
            : ChangedSince > 0 ? DataCheckFreshness.Changed
            : DataCheckFreshness.Current;

        /// <summary>Why the report no longer holds, as the end of a sentence; null while it holds.</summary>
        public string? OutdatedBecause(string fingerprint) =>
            Replaced != null ? "the records were replaced since"
            : Report.Fingerprint != fingerprint ? "the searched fields have changed since"
            : Report.Records > 0 && ChangedSince >= Report.Records * DataCheck.OutdatedShare ? "more than 5% of the records have changed since"
            : null;
    }
}
