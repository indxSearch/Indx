using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IndxServer.Services
{
    /// <summary>How a field is filtered on a page: a list of values (as checkboxes or as buttons), a
    /// range or buckets of a number, an on/off switch, or the category menu over level fields.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<FilterKind>))]
    public enum FilterKind
    {
        [JsonStringEnumMemberName("values")] Values,
        [JsonStringEnumMemberName("buttons")] Buttons,
        [JsonStringEnumMemberName("range")] Range,
        [JsonStringEnumMemberName("buckets")] Buckets,
        [JsonStringEnumMemberName("toggle")] Toggle,
        [JsonStringEnumMemberName("hierarchy")] Hierarchy,
    }

    /// <summary>One filter a page shows, in the order it is shown.</summary>
    /// <param name="Field">The field, or for the category menu its top level (or its path field).</param>
    /// <param name="Kind">The panel.</param>
    /// <param name="Label">What the visitor reads.</param>
    /// <param name="ValueCount">Distinct values over the dataset; null when they are not known.</param>
    /// <param name="Levels">The category menu's level fields from the top.</param>
    /// <param name="Separator">The category menu over one path field: what separates its levels.</param>
    /// <param name="Edges">Buckets: the lower edge of each, the last closing the final one.</param>
    public sealed record PlannedFilter(
        string Field,
        FilterKind Kind,
        string Label,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ValueCount = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Levels = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Separator = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<double>? Edges = null);

    /// <summary>What a plan is made from: the dataset's field roles and types, and every field's values
    /// over the whole dataset (an empty search with facets). An empty <see cref="Values"/> means the
    /// values are not known, and nothing is left out for them.</summary>
    public sealed record FilterPlanDataset(
        IReadOnlyList<string> Filterable,
        IReadOnlyList<string> Facetable,
        IReadOnlyDictionary<string, string> FieldTypes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Values);

    /// <summary>The page's or the owner's choices. Every one may be left out.</summary>
    public sealed record FilterPlanOptions
    {
        /// <summary>These fields, in this order; the rules that leave a field out do not apply to them.</summary>
        public IReadOnlyList<string>? Fields { get; init; }
        public IReadOnlyCollection<string>? Exclude { get; init; }
        public IReadOnlyDictionary<string, string>? Labels { get; init; }
        public IReadOnlyDictionary<string, FilterKind>? Kinds { get; init; }
        /// <summary>Level fields of a category menu, from the top: one filter, first, in place of its levels.</summary>
        public IReadOnlyList<string>? Hierarchy { get; init; }
        /// <summary>A text field with more values than this is left out (an id, a title, a director).</summary>
        public int MaxValues { get; init; } = 200;
        /// <summary>A number field with at most this many values is a list (a generation, a rating), not a range.</summary>
        public int ListNumbersUpTo { get; init; } = 20;
    }

    /// <summary>
    /// The filters a dataset offers by itself: the same rules as intrface's <c>planFilters</c>
    /// (packages/indx-intrface/src/core/filterPlan.ts, 26 Nov 2026), so a page that reads the plan from
    /// the server and one that makes it in the browser show the same column. A list for text, a range
    /// for numbers, a list for a number with few values, a switch for true and false; a field with one
    /// value, or text with more than <see cref="FilterPlanOptions.MaxValues"/>, is left out. The tests
    /// (FilterPlannerTests) hold the same cases as intrface's: change both together.
    /// </summary>
    public static partial class FilterPlanner
    {
        private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal) { "true", "false", "null" };
        private static readonly HashSet<string> BooleanTypes = new(StringComparer.Ordinal) { "True", "False", "Boolean", "Bool" };

        [GeneratedRegex("([a-z])([A-Z])")] private static partial Regex LowerUpper();
        [GeneratedRegex("([A-Za-z])([0-9])")] private static partial Regex LetterDigit();
        [GeneratedRegex(@"[\s_.\-]+")] private static partial Regex Separators();

        /// <summary>A readable name from a field's name: base_total "Base total", isLegendary "Is
        /// legendary", type1 "Type 1", productID "Product ID". A dotted name keeps every part.</summary>
        public static string FieldLabel(string field)
        {
            var spaced = LetterDigit().Replace(LowerUpper().Replace(field, "$1 $2"), "$1 $2");
            var words = Separators().Split(spaced)
                .Where(w => w.Length > 0)
                // An abbreviation (ID, ISBN) stays as written; every other word in lower case.
                .Select(w => w.Length > 1 && w == w.ToUpperInvariant() ? w : w.ToLowerInvariant());
            var text = string.Join(' ', words);
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        }

        private static bool IsNumber(string key) =>
            key.Trim().Length > 0 && double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d);

        /// <summary>The filters a dataset offers, in the order they are shown.</summary>
        public static IReadOnlyList<PlannedFilter> Plan(FilterPlanDataset dataset, FilterPlanOptions? options = null)
        {
            options ??= new FilterPlanOptions();
            var usable = new HashSet<string>(dataset.Facetable.Where(dataset.Filterable.Contains), StringComparer.Ordinal);
            var skip = new HashSet<string>((options.Exclude ?? []).Concat(options.Hierarchy ?? []), StringComparer.Ordinal);
            // The values may be unknown (the empty search failed): nothing is left out for them.
            var valuesKnown = dataset.Values.Count > 0;
            string NameOf(string field) => options.Labels?.GetValueOrDefault(field) ?? FieldLabel(field);

            PlannedFilter? PlanOne(string field, bool chosen)
            {
                var keys = (dataset.Values.GetValueOrDefault(field) ?? []).Where(k => k != "null" && k != "").ToList();
                var type = dataset.FieldTypes.GetValueOrDefault(field);
                var numeric = type == "Number" || (type == null && keys.Count > 0 && keys.All(IsNumber));
                var boolean = (type != null && BooleanTypes.Contains(type)) || (keys.Count > 0 && keys.All(BooleanKeys.Contains));
                var kind = options.Kinds != null && options.Kinds.TryGetValue(field, out var k) ? k
                    : boolean ? FilterKind.Toggle
                    : numeric && !(valuesKnown && keys.Count <= options.ListNumbersUpTo) ? FilterKind.Range
                    : FilterKind.Values;
                var entry = new PlannedFilter(field, kind, NameOf(field), valuesKnown ? keys.Count : null);
                if (chosen || !valuesKnown) return entry;
                // Left out unless asked for: a field the dataset never holds, one that holds one value
                // only, and text with too many values to list.
                if (keys.Count == 0) return null;
                if (kind == FilterKind.Toggle) return keys.Contains("true") && keys.Contains("false") ? entry : null;
                if (keys.Count < 2) return null;
                if (kind == FilterKind.Values && !numeric && keys.Count > options.MaxValues) return null;
                return entry;
            }

            var plan = new List<PlannedFilter>();
            if (options.Hierarchy is { Count: > 0 } levels && levels.All(usable.Contains))
            {
                plan.Add(new PlannedFilter(levels[0], FilterKind.Hierarchy,
                    options.Labels?.GetValueOrDefault(levels[0]) ?? "Categories", Levels: levels.ToList()));
            }
            var order = options.Fields ?? dataset.Facetable;
            foreach (var field in order)
            {
                if (!usable.Contains(field) || skip.Contains(field)) continue;
                if (PlanOne(field, chosen: options.Fields != null) is { } entry) plan.Add(entry);
            }
            return plan;
        }
    }
}
