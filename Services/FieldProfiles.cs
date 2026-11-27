using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// A searched field's search settings under a name many fields share (Nov 2026): its
    /// <see cref="Field.Tolerance"/>, <see cref="Field.RerankWeight"/>, the
    /// and the <see cref="PatternMatchingSetup"/> it takes when <see cref="Field.PatternMatching"/>
    /// is on. A console convenience over the engine's own values: choosing a profile for a field
    /// writes them onto it, editing a profile writes them onto every field that has it, and the
    /// engine stays where the values live. A profile also has the fields' own text rules
    /// (<see cref="FieldProxy.TextRules"/>, 17 Nov 2026): strings replaced before the dataset's,
    /// built into the index, so a change rebuilds it. <see cref="Field.CaseSensitive"/> is not
    /// here yet.
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
        /// <summary>Field.TypingPrefixFrom (6 Nov 2026): from how many letters the word being typed
        /// meets the longer words it starts in the field, later than the search says, never earlier
        /// (the larger of the two wins), 0 never. Null, the default on every profile, follows the
        /// search.</summary>
        public int? TypingPrefixFrom { get; init; }
        /// <summary>The fields' own string rules, in their order, each run before the engine's of
        /// its kind (<see cref="FieldProxy.TextRules"/>): From becomes To, an empty To removes it,
        /// and a rule with KeepOriginal indexes the text both ways. Empty, the default: the
        /// engine's rules alone.</summary>
        public TextRule[] TextRules { get; init; } = [];

        /// <summary>Equal by value, the index sizes and the text rules element by element (a
        /// record compares an array by reference).</summary>
        public bool Equals(FieldProfile? other) =>
            other is not null && Id == other.Id && Name == other.Name && About == other.About && BuiltIn == other.BuiltIn
            && Tolerance == other.Tolerance && RerankWeight == other.RerankWeight
            && IndexSizes.AsSpan().SequenceEqual(other.IndexSizes)
            && NumberOfStartCharacters == other.NumberOfStartCharacters
            && NumberOfStopCharacters == other.NumberOfStopCharacters
            && TypingPrefixFrom == other.TypingPrefixFrom
            && TextRules.AsSpan().SequenceEqual(other.TextRules);

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
        /// <summary>The profile a field gets when it becomes searched (6 Nov 2026); null is
        /// <see cref="FieldProfiles.DefaultBuiltIn"/>.</summary>
        public string? DefaultProfileId { get; set; }

        public DatasetFieldProfiles Copy() => new()
        {
            Profiles = [.. Profiles],
            Assignments = new Dictionary<string, string>(Assignments, StringComparer.Ordinal),
            DefaultProfileId = DefaultProfileId,
        };

        public bool SameAs(DatasetFieldProfiles? other) =>
            other != null && Profiles.SequenceEqual(other.Profiles) && DefaultProfileId == other.DefaultProfileId
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

        /// <summary>A field's own TypingPrefixFrom, null when it follows the search (unset, or the
        /// negative a working copy holds after a profile set it back).</summary>
        public static int? StartsOf(FieldProxy f) => f.TypingPrefixFrom is int n && n >= 0 ? n : null;

        /// <summary>The default when a dataset names none: Name, whose values are the library's
        /// own defaults, so a field given it searches as it did without a profile.</summary>
        public const string DefaultBuiltIn = "name";

        /// <summary>The profile a field gets when it becomes searched: the dataset's choice, else Name.</summary>
        public static FieldProfile DefaultOf(DatasetFieldProfiles? own)
        {
            var all = All(own);
            return all.FirstOrDefault(p => p.Id == own?.DefaultProfileId) ?? all.First(p => p.Id == DefaultBuiltIn);
        }

        /// <summary>
        /// Gives a searched field with no profile chosen the dataset's default: its values written
        /// onto the field and the choice kept (6 Nov 2026). For a field that becomes searched, and
        /// the fields an upload's analysis found; a field already Custom is left alone, as nothing
        /// changes a profile by itself. Returns whether it did.
        /// </summary>
        public static bool GiveDefault(FieldProxy f, DatasetFieldProfiles own)
        {
            if (!IsSearched(f) || own.Assignments.ContainsKey(f.FieldName)) return false;
            var profile = DefaultOf(own);
            own.Assignments[f.FieldName] = profile.Id;
            Apply(f, profile);
            return true;
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
            if (StartsOf(f) != p.TypingPrefixFrom) return false;
            if (!(f.TextRules ?? []).AsSpan().SequenceEqual(p.TextRules)) return false;
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
            // Null follows the search; FieldProxy takes a negative number for that (null leaves it).
            f.TypingPrefixFrom = p.TypingPrefixFrom ?? -1;
            // An empty list is the engine's rules again; a proxy's null would leave the field's.
            f.TextRules = [.. p.TextRules];
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
            TypingPrefixFrom = StartsOf(f),
            TextRules = f.TextRules is { } rules ? [.. rules] : [],
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
            // The default goes with it: to the replacement, else back to Name.
            if (own.DefaultProfileId == id) own.DefaultProfileId = replacement?.Id;
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

        /// <summary>
        /// The profiles a PUT fields/profiles asks for, merged onto the dataset's (6 Nov 2026): the
        /// whole own list when given, the assignments named, the default. Checked whole before
        /// anything is returned; the reason when refused. <paramref name="searched"/> and
        /// <paramref name="exists"/> answer for a field name.
        /// </summary>
        public static (DatasetFieldProfiles? Merged, string? Refused) Merge(DatasetFieldProfiles current,
            List<FieldProfile>? profiles, Dictionary<string, string?>? assignments, string? defaultProfileId,
            Func<string, bool> exists, Func<string, bool> searched)
        {
            var merged = current.Copy();
            if (profiles != null)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in profiles)
                {
                    if (string.IsNullOrWhiteSpace(p.Id) || !System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[a-z0-9][a-z0-9-]{0,63}$"))
                        return (null, $"Profile id '{p.Id}' is not a valid id: lowercase letters, digits and dashes, at most 64.");
                    if (!ids.Add(p.Id)) return (null, $"Profile id '{p.Id}' appears twice.");
                    if (string.IsNullOrWhiteSpace(p.Name)) return (null, $"Profile '{p.Id}' has no name.");
                    if (!Enum.IsDefined(p.Tolerance)) return (null, $"Profile '{p.Id}': tolerance must be Exact, OneEdit or TwoEdits.");
                    if (float.IsNaN(p.RerankWeight) || p.RerankWeight < 0) return (null, $"Profile '{p.Id}': rerankWeight cannot be negative.");
                    if (p.IndexSizes is not { Length: > 0 } || p.IndexSizes.Any(n => n is < 1 or > 8))
                        return (null, $"Profile '{p.Id}': indexSizes must be one or more lengths from 1 to 8.");
                    if (p.TypingPrefixFrom is < 0 or > 64)
                        return (null, $"Profile '{p.Id}': typingPrefixFrom is from 0 (never) to 64, or null to follow the search.");
                    if (p.NumberOfStartCharacters is < 0 or > 8 || p.NumberOfStopCharacters is < 0 or > 8)
                        return (null, $"Profile '{p.Id}': numberOfStartCharacters and numberOfStopCharacters are from 0 to 8.");
                    if (TextRulesObjection(p.TextRules) is { } objection) return (null, $"Profile '{p.Id}': {objection}");
                }
                // The fields of a profile that goes keep their values, as their own.
                foreach (var gone in merged.Assignments.Where(a => !IsBuiltInId(a.Value) && !ids.Contains(a.Value)).Select(a => a.Key).ToList())
                    merged.Assignments.Remove(gone);
                merged.Profiles = profiles.Select(p => p with { BuiltIn = false, Name = p.Name.Trim(), About = p.About?.Trim() ?? "", TextRules = p.TextRules ?? [] }).ToList();
            }
            if (assignments != null)
                foreach (var (field, id) in assignments)
                {
                    if (!exists(field)) return (null, $"Field '{field}' does not exist in this dataset.");
                    if (id == null) { merged.Assignments.Remove(field); continue; }
                    if (!All(merged).Any(p => p.Id == id)) return (null, $"Field '{field}': there is no profile '{id}'.");
                    if (!searched(field))
                        return (null, $"Field '{field}' is not searched: turn on wordMatching or patternMatching in fields/configuration first.");
                    merged.Assignments[field] = id;
                }
            if (defaultProfileId != null)
            {
                if (!All(merged).Any(p => p.Id == defaultProfileId)) return (null, $"There is no profile '{defaultProfileId}' to be the default.");
                merged.DefaultProfileId = defaultProfileId == DefaultBuiltIn ? null : defaultProfileId;
            }
            else if (merged.DefaultProfileId != null && !All(merged).Any(p => p.Id == merged.DefaultProfileId))
                merged.DefaultProfileId = null;
            return (merged, null);
        }

        /// <summary>The most text rules a profile holds, and the longest a rule's From or To.</summary>
        public const int MaxTextRules = 50, MaxTextRuleLength = 64;

        /// <summary>Why a profile's text rules cannot be taken, null when they can: each needs a
        /// From, a To that may be empty, both within <see cref="MaxTextRuleLength"/>.</summary>
        public static string? TextRulesObjection(TextRule[]? rules)
        {
            if (rules == null) return null;
            if (rules.Length > MaxTextRules) return $"at most {MaxTextRules} textRules.";
            foreach (var r in rules)
            {
                if (r == null || string.IsNullOrEmpty(r.From)) return "a text rule needs a from.";
                if (r.To == null) return $"the text rule for '{r.From}' needs a to; an empty one removes the text.";
                if (r.From.Length > MaxTextRuleLength || r.To.Length > MaxTextRuleLength)
                    return $"a text rule's from and to are at most {MaxTextRuleLength} characters.";
            }
            return null;
        }

        /// <summary>An id for a new profile from its name, unique among the profiles.</summary>
        public static string NewId(string name, IEnumerable<FieldProfile> existing)
        {
            var taken = existing.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            // The letters an id may have (Merge): a name's other letters become dashes.
            var stem = new string(name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (stem.Length > 48) stem = stem[..48].Trim('-');
            if (stem.Length == 0) stem = "profile";
            var id = stem;
            for (int n = 2; taken.Contains(id); n++) id = $"{stem}-{n}";
            return id;
        }

        /// <summary>
        /// Whether a profile change rebuilds the index for the fields it is on: the text rules, and
        /// the pattern setup for a field with PatternMatching. Tolerance and the rerank weight are
        /// read at every search.
        /// </summary>
        public static bool Rebuilds(FieldProfile before, FieldProfile after, IEnumerable<FieldProxy> fields)
        {
            var list = fields.ToList();
            if (list.Count == 0) return false;
            bool pattern = !before.IndexSizes.AsSpan().SequenceEqual(after.IndexSizes)
                           || before.NumberOfStartCharacters != after.NumberOfStartCharacters
                           || before.NumberOfStopCharacters != after.NumberOfStopCharacters;
            return !before.TextRules.AsSpan().SequenceEqual(after.TextRules)
                   || pattern && list.Any(f => f.PatternMatching == true);
        }
    }
}
