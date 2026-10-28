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
        /// is a path of one. A facet cannot give this, since it counts one field at a time. With a
        /// <paramref name="scope"/>, only the documents in it count.</summary>
        public static IReadOnlyList<KeyValuePair<string, int>> GroupHistogram(ISearchEngine engine, IReadOnlyList<string> fields, IReadOnlySet<long>? scope = null)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, path) in GroupPaths(engine, fields, scope))
                counts[path] = counts.GetValueOrDefault(path) + 1;
            return counts.OrderByDescending(c => c.Value).Select(c => new KeyValuePair<string, int>(c.Key, c.Value)).ToList();
        }

        /// <summary>The first documents of a group that are filed nowhere, the ones its "No category"
        /// counts: a blank, null or missing first level. Read the way the count is made, from the
        /// documents, since no value filter finds a level that is not there.</summary>
        public static IReadOnlyList<long> Uncategorised(ISearchEngine engine, IReadOnlyList<string> fields, IReadOnlySet<long>? scope, int take) =>
            GroupPaths(engine, fields, scope).Where(d => d.Path.Length == 0).Select(d => d.Key).Take(take).ToList();

        // The documents are read by key: the scope's, or every key, from an empty search. A key
        // deleted since it was read has no JSON and is passed over.
        private static IEnumerable<(long Key, string Path)> GroupPaths(ISearchEngine engine, IReadOnlyList<string> fields, IReadOnlySet<long>? scope)
        {
            foreach (var key in (IEnumerable<long>?)scope ?? AllKeys(engine))
            {
                var json = engine.GetJsonDataOfKey(key);
                if (string.IsNullOrEmpty(json)) continue;
                string path;
                try
                {
                    using var parsed = JsonDocument.Parse(json);
                    path = PathOf(parsed.RootElement, fields);
                }
                catch { continue; }
                yield return (key, path);
            }
        }

        /// <summary>The keys of every live document: an empty search with no filter. The tab is
        /// only reached with a facetable field, which an empty search needs.</summary>
        private static IReadOnlyList<long> AllKeys(ISearchEngine engine) =>
            engine.Search(new Query(string.Empty, Math.Max(1, engine.Status.DocumentCount)) { EnableFacets = true })
                .Records.Select(r => r.DocumentKey).ToList();

        /// <summary>A document's path in a group: its value per level, joined; "" when the first
        /// level is blank or missing.</summary>
        public static string PathOf(JsonElement doc, IReadOnlyList<string> fields)
        {
            var levels = new List<string>();
            foreach (var field in fields)
            {
                var value = DocumentInspection.Values(doc, field).FirstOrDefault(v => v.Trim().Length > 0);
                if (value == null) break;   // a missing level ends the path: no "department > > type"
                levels.Add(value.Trim());
            }
            return levels.Count == 0 ? "" : string.Join(GroupSeparator, levels);
        }

        /// <summary>A part of the dataset the tab can be limited to: the documents with one value in
        /// one field, "contentType: publication". A dataset that mixes kinds of records has a tree
        /// that means something for one kind only, and its "No category" then counts that kind.</summary>
        public sealed record Scope(string Field, string Value, int Count)
        {
            public string Key => $"{Field}\u001f{Value}";
            public string Label => $"{Field}: {Value}";
        }

        /// <summary>The values in a field with at most this many, offered as scopes; past it the
        /// field is a name or a code, not a kind of record.</summary>
        public const int MaxScopeValues = 30;

        /// <summary>The scopes a source can be limited to: each value of a field that is Filterable
        /// and Facetable, not a Number, not one of the source's own and with between two and
        /// <see cref="MaxScopeValues"/> values, by field in the configuration's order then count.</summary>
        public static IReadOnlyList<Scope> Scopes(IReadOnlyList<FieldProxy> fields, IReadOnlyDictionary<string, KeyValuePair<string, int>[]>? facets, CategorySource source)
        {
            var scopes = new List<Scope>();
            if (facets == null) return scopes;
            foreach (var f in fields)
            {
                if (f.Filterable != true || f.Facetable != true || f.FieldType == "Number" || source.Fields.Contains(f.FieldName)) continue;
                if (!facets.TryGetValue(f.FieldName, out var values)) continue;
                var kept = values.Where(v => v.Value > 0 && v.Key.Trim().Length > 0 && v.Key != CategoryHierarchy.NullValue).ToList();
                if (kept.Count < 2 || kept.Count > MaxScopeValues) continue;
                scopes.AddRange(kept.OrderByDescending(v => v.Value).Select(v => new Scope(f.FieldName, v.Key, v.Value)));
            }
            return scopes;
        }

        /// <summary>The keys of the documents a filter holds: an empty search under it, asking for
        /// as many records as the dataset has. The engine takes an empty search only with facets on.</summary>
        public static IReadOnlySet<long> KeysIn(ISearchEngine engine, Filter filter, int timeoutMs)
        {
            int all = Math.Max(1, engine.Status.DocumentCount);
            var result = engine.Search(new Query(string.Empty, all) { Filter = filter, EnableFacets = true, TimeOutLimitMilliseconds = timeoutMs });
            return result.Records.Select(r => r.DocumentKey).ToHashSet();
        }
    }
}
