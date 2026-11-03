using System.Text.Json;
using Indx.Api;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// One document laid out against the saved field configuration, for the Search preview's
    /// document panel: what was indexed for search, what can filter, facet or sort, and what is
    /// only stored. Every value, arrays in full, because the point is to see why a hit is there
    /// (watching a customer read four truncated fields per hit made that plain, Oct 2026).
    /// </summary>
    public sealed record DocumentInspection(
        IReadOnlyList<InspectedField> Indexed,
        IReadOnlyList<InspectedField> Roles,
        IReadOnlyList<InspectedField> Stored)
    {
        /// <summary>Characters of text indexed for search on this document, all searchable fields
        /// together; what the engine's text ceiling applies to.</summary>
        public int IndexedLength => Indexed.Sum(f => f.Values.Sum(v => v.Length));

        /// <summary>
        /// Lays <paramref name="doc"/> out by <paramref name="fields"/>. Container fields (the
        /// parents of dotted names) hold no value of their own and are left out; a field the
        /// document lacks is kept with no values, so an empty searchable field is visible as such.
        /// </summary>
        public static DocumentInspection Build(JsonElement doc, IReadOnlyList<FieldProxy> fields)
        {
            var containers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in fields)
                for (var dot = f.FieldName.LastIndexOf('.'); dot > 0; dot = f.FieldName.LastIndexOf('.', dot - 1))
                    containers.Add(f.FieldName[..dot]);

            var indexed = new List<InspectedField>();
            var roles = new List<InspectedField>();
            var stored = new List<InspectedField>();
            foreach (var f in fields)
            {
                if (containers.Contains(f.FieldName)) continue;
                var field = new InspectedField(f, Values(doc, f.FieldName));
                if (f.Searchable == true) indexed.Add(field);
                else if (field.RoleNames.Count > 0) roles.Add(field);
                else stored.Add(field);
            }
            // Heaviest first: the fields that decide ranking most are the ones read first.
            indexed.Sort((a, b) => (b.Field.Weight ?? 1f).CompareTo(a.Field.Weight ?? 1f));
            return new DocumentInspection(indexed, roles, stored);
        }

        /// <summary>
        /// Every scalar value at a parser field name: dot-separated, no array indices. An array on
        /// the way applies the rest of the path to each of its elements, as the parser does when it
        /// names the field, so <c>authors.name</c> yields every author's name.
        /// </summary>
        public static IReadOnlyList<string> Values(JsonElement doc, string path)
        {
            var found = new List<string>();
            Collect(doc, path.Split('.'), 0, found);
            return found;
        }

        private static void Collect(JsonElement element, string[] segments, int index, List<string> found)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Collect(item, segments, index, found);
                return;
            }
            if (index == segments.Length)
            {
                if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) return;
                var text = element.ToString();
                if (text.Length > 0) found.Add(text);
                return;
            }
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segments[index], out var child))
                Collect(child, segments, index + 1, found);
        }
    }

    public sealed record InspectedField(FieldProxy Field, IReadOnlyList<string> Values)
    {
        public string Name => Field.FieldName;

        /// <summary>The roles other than searchable, in the field table's order.</summary>
        public IReadOnlyList<string> RoleNames { get; } = new[]
        {
            Field.Filterable == true ? "filterable" : null,
            Field.Facetable == true ? "facetable" : null,
            Field.Sortable == true ? "sortable" : null,
            Field.WordIndexing == true ? "word indexing" : null,
            Field.Embeddable == true ? "embeddable" : null,
        }.OfType<string>().ToArray();
    }
}
