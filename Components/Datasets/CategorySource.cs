using System.Text.Json;
using Indx.Api;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// Where the Categories tab's categories come from: a hierarchy group, fields that share a dotted
    /// prefix and each hold one level ("category.department", "category.section", "category.type"),
    /// or one field whose values are paths ("Apparel > Shoes > Running shoes"). A flat field, one
    /// whose values have no separator and no siblings in a group, is not a hierarchy and is not
    /// offered. The sources are found from the field configuration and one facet read, so the
    /// picker names only what will draw a tree.
    /// </summary>
    public sealed record CategorySource(string Key, string Label, IReadOnlyList<string> Fields, bool IsGroup)
    {
        /// <summary>The separator the tree is built with: a group's levels are joined with the
        /// Google one; a path field uses its own, detected or chosen.</summary>
        public const string GroupSeparator = " > ";

        /// <summary>The sources a dataset offers: its hierarchy groups first, then its path fields,
        /// in the field configuration's order.</summary>
        public static IReadOnlyList<CategorySource> Find(IReadOnlyList<FieldProxy> fields, IReadOnlyDictionary<string, KeyValuePair<string, int>[]>? facets)
        {
            var facetable = fields.Where(f => f.Facetable == true && f.FieldType != "Number").ToList();
            var sources = new List<CategorySource>();
            var grouped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in facetable.Where(f => f.FieldName.Contains('.')).GroupBy(f => f.FieldName[..f.FieldName.LastIndexOf('.')]))
            {
                var members = group.ToList();
                if (members.Count < 2) continue;
                var names = members.Select(f => f.FieldName).ToList();
                foreach (var n in names) grouped.Add(n);
                sources.Add(new CategorySource(group.Key, $"{group.Key}: {string.Join(" › ", names.Select(Leaf))}", names, IsGroup: true));
            }
            foreach (var f in facetable)
            {
                if (grouped.Contains(f.FieldName)) continue;
                if (facets == null || !facets.TryGetValue(f.FieldName, out var values) || values.Length == 0) continue;
                if (CategoryHierarchy.DetectSeparator(values.Select(v => v.Key)) == null) continue;
                sources.Add(new CategorySource(f.FieldName, f.FieldName, [f.FieldName], IsGroup: false));
            }
            return sources;
        }

        private static string Leaf(string field) => field[(field.LastIndexOf('.') + 1)..];

        /// <summary>A group's paths with their counts, from the documents themselves: each document's
        /// value per level, joined, empty levels dropped so a document filed at the department alone
        /// is a path of one. A facet cannot give this, since it counts one field at a time.</summary>
        public static IReadOnlyList<KeyValuePair<string, int>> GroupHistogram(ISearchEngine engine, IReadOnlyList<string> fields)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var doc in engine.GetAllDocuments())
            {
                if (doc.Deleted) continue;
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(doc.JsonData); }
                catch { continue; }
                using (parsed)
                {
                    var levels = new List<string>();
                    foreach (var field in fields)
                    {
                        var value = DocumentInspection.Values(parsed.RootElement, field).FirstOrDefault(v => v.Trim().Length > 0);
                        if (value == null) break;   // a missing level ends the path: no "department > > type"
                        levels.Add(value.Trim());
                    }
                    var path = levels.Count == 0 ? "" : string.Join(GroupSeparator, levels);
                    counts[path] = counts.GetValueOrDefault(path) + 1;
                }
            }
            return counts.OrderByDescending(c => c.Value).Select(c => new KeyValuePair<string, int>(c.Key, c.Value)).ToList();
        }
    }
}
