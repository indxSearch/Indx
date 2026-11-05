using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// A searched field's search settings under a name many fields share (Nov 2026): its
    /// <see cref="Field.Tolerance"/>, <see cref="Field.RerankWeight"/>, the
    /// and the <see cref="PatternMatchingSetup"/> it takes when <see cref="Field.PatternMatching"/>
    /// is on. A console convenience over the engine's own values: choosing a profile for a field
    /// writes them onto it, editing a profile writes them onto every field that has it, and the
    /// engine stays where the values live. <see cref="Field.CaseSensitive"/> and a field's own
    /// normalizer are not here: the library takes both only before the documents are loaded (the
    /// store holds the values as the field reads them and does not re-encode them), so neither can
    /// change on a live dataset, not even through the shadow build.
    /// </summary>
    public sealed record FieldProfile
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string About { get; init; } = "";
        public bool BuiltIn { get; init; }
        public Tolerance Tolerance { get; init; } = Tolerance.OneEdit;
        public float RerankWeight { get; init; } = 1f;
        public int[] IndexSizes { get; init; } = [3];
        public int NumberOfStartCharacters { get; init; } = 2;
        public int NumberOfStopCharacters { get; init; }

        /// <summary>Equal by value, the index sizes element by element (a record compares an
        /// array by reference).</summary>
        public bool Equals(FieldProfile? other) =>
            other is not null && Id == other.Id && Name == other.Name && About == other.About && BuiltIn == other.BuiltIn
            && Tolerance == other.Tolerance && RerankWeight == other.RerankWeight
            && IndexSizes.AsSpan().SequenceEqual(other.IndexSizes)
            && NumberOfStartCharacters == other.NumberOfStartCharacters
            && NumberOfStopCharacters == other.NumberOfStopCharacters;

        public override int GetHashCode() => HashCode.Combine(Id, Name, Tolerance, RerankWeight);
    }

    /// <summary>
    /// A dataset's own profiles and which profile each field has, as one JSON row
    /// (<see cref="FieldProfileStore"/>). The built-ins are not stored: they are
    /// <see cref="FieldProfiles.BuiltIn"/>.
    /// </summary>
    public sealed class DatasetFieldProfiles
    {
        public List<FieldProfile> Profiles { get; set; } = [];
        /// <summary>Field name to profile id.</summary>
        public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.Ordinal);

        public DatasetFieldProfiles Copy() => new()
        {
            Profiles = [.. Profiles],
            Assignments = new Dictionary<string, string>(Assignments, StringComparer.Ordinal),
        };

        public bool SameAs(DatasetFieldProfiles? other) =>
            other != null && Profiles.SequenceEqual(other.Profiles)
            && Assignments.Count == other.Assignments.Count
            && Assignments.All(a => other.Assignments.TryGetValue(a.Key, out var id) && id == a.Value);
    }

    /// <summary>The built-in profiles, and how a profile and a field's values meet.</summary>
    public static class FieldProfiles
    {
        /// <summary>
        /// Five profiles every dataset has. Their pattern setups are the old engine-wide
        /// IndexerSetups, now per field: Default [3] with two start characters (Name, Category,
        /// Text), Dense [2, 3] (Title), and SingleWord's stop character for codes. Text is exact,
        /// as the library advises for body text: a misspelling in a long description confirms the
        /// wrong record.
        /// </summary>
        public static readonly IReadOnlyList<FieldProfile> BuiltIn =
        [
            new() { Id = "title", Name = "Title", About = "Titles and product names: the most forgiving.", BuiltIn = true,
                    Tolerance = Tolerance.TwoEdits, IndexSizes = [2, 3] },
            new() { Id = "name", Name = "Name", About = "People and brands.", BuiltIn = true },
            new() { Id = "category", Name = "Category", About = "Categories, tags and other short labels.", BuiltIn = true,
                    RerankWeight = 0.8f },
            new() { Id = "code", Name = "Code", About = "ISBNs, SKUs and article numbers: exact, a whole code first.", BuiltIn = true,
                    Tolerance = Tolerance.Exact, IndexSizes = [2, 3], NumberOfStopCharacters = 1 },
            new() { Id = "text", Name = "Text", About = "Descriptions and other long text: its words, exactly.", BuiltIn = true,
                    Tolerance = Tolerance.Exact, RerankWeight = 0.5f },
        ];

        /// <summary>The built-ins, then the dataset's own. A dataset's own profile with a built-in's
        /// id is that built-in as this dataset edited it, and takes its place.</summary>
        public static IReadOnlyList<FieldProfile> All(DatasetFieldProfiles? own)
        {
            var mine = own?.Profiles ?? [];
            return [.. BuiltIn.Select(b => mine.FirstOrDefault(p => p.Id == b.Id) ?? b),
                    .. mine.Where(p => !BuiltIn.Any(b => b.Id == p.Id))];
        }

        /// <summary>Whether the id is one of the five built-ins, edited here or not.</summary>
        public static bool IsBuiltInId(string id) => BuiltIn.Any(b => b.Id == id);

        public static bool IsSearched(FieldProxy f) => f.WordMatching == true || f.PatternMatching == true;

        /// <summary>
        /// Whether the field's values are the profile's. A field reads its own when the proxy
        /// leaves one out, so an absent value counts as the library's default; the pattern setup
        /// counts only on a field with PatternMatching on, the only one that reads it.
        /// </summary>
        public static bool Matches(FieldProxy f, FieldProfile p)
        {
            if ((f.Tolerance ?? Tolerance.OneEdit) != p.Tolerance) return false;
            if (Math.Abs((f.RerankWeight ?? 1f) - p.RerankWeight) > 0.0001f) return false;
            if (f.PatternMatching == true)
            {
                if (!(f.IndexSizes ?? [3]).AsSpan().SequenceEqual(p.IndexSizes)) return false;
                if ((f.NumberOfStartCharacters ?? 2) != p.NumberOfStartCharacters) return false;
                if ((f.NumberOfStopCharacters ?? 0) != p.NumberOfStopCharacters) return false;
            }
            return true;
        }

        /// <summary>
        /// The profile a field has: the one chosen for it, while its values are still that
        /// profile's. Null is Custom: no profile was chosen yet (every field before profiles), or
        /// its values were changed since through the API or by Observr. A field is not given a
        /// profile because its values happen to equal one; nothing changes until someone chooses.
        /// </summary>
        public static FieldProfile? ProfileOf(FieldProxy f, DatasetFieldProfiles? own)
        {
            if (own?.Assignments.TryGetValue(f.FieldName, out var id) != true) return null;
            return All(own).FirstOrDefault(p => p.Id == id) is { } given && Matches(f, given) ? given : null;
        }

        /// <summary>Writes the profile's values onto the field; the pattern setup only when the
        /// field has PatternMatching on, since it applies to nothing otherwise.</summary>
        public static void Apply(FieldProxy f, FieldProfile p)
        {
            f.Tolerance = p.Tolerance;
            f.RerankWeight = p.RerankWeight;
            if (f.PatternMatching == true)
            {
                f.IndexSizes = (int[])p.IndexSizes.Clone();
                f.NumberOfStartCharacters = p.NumberOfStartCharacters;
                f.NumberOfStopCharacters = p.NumberOfStopCharacters;
            }
        }

        /// <summary>A field's own values as a profile, for editing a Custom field in the same
        /// form; not stored until it is saved as a profile.</summary>
        public static FieldProfile FromField(FieldProxy f) => new()
        {
            Id = "",
            Name = "",
            Tolerance = f.Tolerance ?? Tolerance.OneEdit,
            RerankWeight = f.RerankWeight ?? 1f,
            IndexSizes = f.IndexSizes is { Length: > 0 } sizes ? (int[])sizes.Clone() : [3],
            NumberOfStartCharacters = f.NumberOfStartCharacters ?? 2,
            NumberOfStopCharacters = f.NumberOfStopCharacters ?? 0,
        };

        /// <summary>
        /// What Save in the profile editor does to the working copy. On a Custom field
        /// (<paramref name="customField"/>): its own values, or with a name a new profile it then
        /// has. A new profile (no id): added, given to no field yet. An existing one: every
        /// searched field that has it takes the new values, and a built-in edited here is kept as
        /// this dataset's own copy under the built-in's id. A new one made from a field's Profile
        /// menu (<paramref name="newFor"/>) is that field's at once.
        /// </summary>
        public static void SaveEdit(DatasetFieldProfiles own, IReadOnlyList<FieldProxy> fields, FieldProxy? customField, FieldProfile edited,
                                    FieldProxy? newFor = null)
        {
            if (customField != null)
            {
                if (string.IsNullOrWhiteSpace(edited.Name))
                {
                    Apply(customField, edited);
                    own.Assignments.Remove(customField.FieldName);
                    return;
                }
                var made = edited with { Id = NewId(edited.Name, All(own)), BuiltIn = false };
                own.Profiles.Add(made);
                own.Assignments[customField.FieldName] = made.Id;
                Apply(customField, made);
                return;
            }
            if (edited.Id.Length == 0)
            {
                var created = edited with { Id = NewId(edited.Name, All(own)), BuiltIn = false };
                own.Profiles.Add(created);
                if (newFor != null && IsSearched(newFor))
                {
                    own.Assignments[newFor.FieldName] = created.Id;
                    Apply(newFor, created);
                }
                return;
            }
            // Read before the profile changes: a field has the profile whose values it has.
            var having = fields.Where(f => IsSearched(f) && ProfileOf(f, own)?.Id == edited.Id).ToList();
            var stored = edited with { BuiltIn = false };
            own.Profiles.RemoveAll(p => p.Id == edited.Id);
            own.Profiles.Add(stored);
            foreach (var f in having)
            {
                own.Assignments[f.FieldName] = edited.Id;
                Apply(f, stored);
            }
        }

        /// <summary>The searched fields that have the profile (<see cref="ProfileOf"/>).</summary>
        public static IReadOnlyList<FieldProxy> FieldsWith(string id, IEnumerable<FieldProxy> fields, DatasetFieldProfiles? own) =>
            fields.Where(f => IsSearched(f) && ProfileOf(f, own)?.Id == id).ToList();

        /// <summary>The searched fields whose values are their own: no profile chosen, or changed since.</summary>
        public static IReadOnlyList<FieldProxy> CustomFields(IEnumerable<FieldProxy> fields, DatasetFieldProfiles? own) =>
            fields.Where(f => IsSearched(f) && ProfileOf(f, own) == null).ToList();

        /// <summary>
        /// Deletes one of the dataset's own profiles (a built-in cannot be deleted) and gives its
        /// fields <paramref name="replacementId"/>: its values written onto them and the choice kept,
        /// so a profile in use goes in one step rather than field by field (Nov 2026). With no
        /// replacement, or one that is not there, the fields keep their values and become Custom.
        /// </summary>
        public static void DeleteReplacing(DatasetFieldProfiles own, IEnumerable<FieldProxy> fields, string id, string? replacementId)
        {
            if (IsBuiltInId(id)) return;
            var replacement = replacementId == id ? null : All(own).FirstOrDefault(p => p.Id == replacementId);
            foreach (var f in FieldsWith(id, fields, own))
            {
                if (replacement == null) continue;
                Apply(f, replacement);
                own.Assignments[f.FieldName] = replacement.Id;
            }
            own.Profiles.RemoveAll(p => p.Id == id);
            foreach (var key in own.Assignments.Where(a => a.Value == id).Select(a => a.Key).ToList())
                own.Assignments.Remove(key);
        }

        /// <summary>A built-in this dataset edited, back to the shipped values, on its fields too.</summary>
        public static void ResetBuiltIn(DatasetFieldProfiles own, IEnumerable<FieldProxy> fields, string id)
        {
            if (BuiltIn.FirstOrDefault(b => b.Id == id) is not { } shipped) return;
            var having = FieldsWith(id, fields, own);
            own.Profiles.RemoveAll(p => p.Id == id);
            foreach (var f in having) Apply(f, shipped);
        }

        /// <summary>Whether this dataset changed the built-in with this id.</summary>
        public static bool IsEditedBuiltIn(string id, DatasetFieldProfiles? own) =>
            IsBuiltInId(id) && own?.Profiles.Any(p => p.Id == id) == true;

        /// <summary>The profile a deleted one's fields most likely want: the same typos and weight,
        /// else the same typos, else Name, the library's own defaults.</summary>
        public static string SuggestedReplacement(FieldProfile deleted, DatasetFieldProfiles? own)
        {
            var others = All(own).Where(p => p.Id != deleted.Id).ToList();
            return (others.FirstOrDefault(p => p.Tolerance == deleted.Tolerance && p.RerankWeight == deleted.RerankWeight)
                    ?? others.FirstOrDefault(p => p.Tolerance == deleted.Tolerance)
                    ?? others.First(p => p.Id == "name")).Id;
        }

        /// <summary>An id for a new profile from its name, unique among the profiles.</summary>
        public static string NewId(string name, IEnumerable<FieldProfile> existing)
        {
            var taken = existing.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var stem = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (stem.Length == 0) stem = "profile";
            var id = stem;
            for (int n = 2; taken.Contains(id); n++) id = $"{stem}-{n}";
            return id;
        }

        /// <summary>
        /// Whether a profile change rebuilds the index for the fields it is on: the pattern setup,
        /// for a field with PatternMatching. Tolerance and the rerank weight are read at every search.
        /// </summary>
        public static bool Rebuilds(FieldProfile before, FieldProfile after, IEnumerable<FieldProxy> fields)
        {
            var list = fields.ToList();
            if (list.Count == 0) return false;
            bool pattern = !before.IndexSizes.AsSpan().SequenceEqual(after.IndexSizes)
                           || before.NumberOfStartCharacters != after.NumberOfStartCharacters
                           || before.NumberOfStopCharacters != after.NumberOfStopCharacters;
            return pattern && list.Any(f => f.PatternMatching == true);
        }
    }
}
