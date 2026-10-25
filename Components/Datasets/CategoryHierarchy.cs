namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// A field's facet values read as a category hierarchy, for the Categories tab's TreeCanvas. The
    /// values are paths in the way a Google product feed writes product_type and
    /// google_product_category: "Apparel &amp; Accessories > Shoes > Running shoes", one path per
    /// value, the separator detected from the values when the dataset has not said which. A node's
    /// count is the documents whose path passes through it, so a parent holds the sum of its
    /// children plus the documents filed at the parent itself; a document in several categories
    /// (an array field) counts in each, which is what "products in this category" means.
    /// Built from a facet histogram and nothing else, so it never touches an engine, and bounded
    /// on every side: too many distinct values and it declines rather than draws, a path deeper
    /// than <see cref="MaxDepth"/> is cut there, a name longer than <see cref="MaxLabelLength"/>
    /// is clipped, empty segments are dropped, and a blank value is "No category".
    /// </summary>
    public static class CategoryHierarchy
    {
        /// <summary>Distinct values past which the tree is not built: hundreds of categories are the
        /// expectation, thousands a sign the field is not a category.</summary>
        public const int MaxValues = 2000;
        public const int MaxDepth = 8;
        public const int MaxLabelLength = 60;
        public const string NoCategory = "No category";
        /// <summary>The share of values that must carry the separator for the field to be a hierarchy.</summary>
        public const double SeparatorShare = 0.6;

        /// <summary>The separators tried, in the order of preference when two are equally common.
        /// Google's " > " comes first and is tried before the bare ">" that it contains.</summary>
        public static readonly IReadOnlyList<string> Separators = [" > ", " / ", "/", "|", ">", " - "];

        /// <param name="Id">The path as the field holds it, which is also the value to filter on.</param>
        /// <param name="Label">The last segment, clipped.</param>
        /// <param name="Count">Documents whose path passes through this node.</param>
        /// <param name="Own">Documents filed at this node itself, not under a child.</param>
        public sealed record Node(string Id, string Label, int Count, int Own, IReadOnlyList<Node> Children);

        /// <param name="Roots">The top level, by count descending then name.</param>
        /// <param name="Separator">The separator used; null when the values have none, in which case the tree is flat.</param>
        /// <param name="DistinctValues">How many distinct values the field had.</param>
        /// <param name="Nodes">How many nodes the tree holds.</param>
        /// <param name="Depth">The deepest level, 1 for a flat list.</param>
        /// <param name="Cut">How many values were cut at <see cref="MaxDepth"/>.</param>
        public sealed record Tree(IReadOnlyList<Node> Roots, string? Separator, int DistinctValues, int Nodes, int Depth, int Cut);

        /// <summary>The separator the values use: the one found in most of them, by the preference
        /// order on a tie; null when fewer than <see cref="SeparatorShare"/> of the values contain
        /// any. A path field has it in nearly every value; a flat field with compound values
        /// ("Gold/black", "Sofas|Sectionals", a quarter to half of furniture's) is not a hierarchy.</summary>
        public static string? DetectSeparator(IEnumerable<string> values)
        {
            var list = values as IReadOnlyCollection<string> ?? values.ToList();
            if (list.Count == 0) return null;
            string? best = null;
            int bestHits = 0;
            foreach (var sep in Separators)
            {
                int hits = list.Count(v => v.Contains(sep, StringComparison.Ordinal));
                if (hits > bestHits) { best = sep; bestHits = hits; }
            }
            return bestHits >= list.Count * SeparatorShare ? best : null;
        }

        /// <summary>Builds the tree, or null when the field has more than <see cref="MaxValues"/>
        /// distinct values. A null <paramref name="separator"/> detects one; an empty one means
        /// the values are flat.</summary>
        public static Tree? Build(IReadOnlyList<KeyValuePair<string, int>> values, string? separator = null)
        {
            if (values.Count > MaxValues) return null;
            separator ??= DetectSeparator(values.Select(v => v.Key));
            if (separator == "") separator = null;

            var roots = new Dictionary<string, Draft>(StringComparer.Ordinal);
            int cut = 0, depth = 0;
            foreach (var (raw, count) in values)
            {
                if (count <= 0) continue;
                var segments = Split(raw, separator);
                if (segments.Count > MaxDepth) { segments = segments.Take(MaxDepth).ToList(); cut++; }
                depth = Math.Max(depth, segments.Count);
                var level = roots;
                Draft? node = null;
                var path = new List<string>();
                foreach (var segment in segments)
                {
                    path.Add(segment);
                    var id = separator == null ? segment : string.Join(separator, path);
                    if (!level.TryGetValue(segment, out node))
                        level[segment] = node = new Draft(id, Clip(segment));
                    node.Count += count;
                    level = node.Children;
                }
                if (node != null) node.Own += count;
            }
            int nodes = 0;
            var built = Finish(roots.Values, ref nodes);
            return new Tree(built, separator, values.Count, nodes, depth, cut);
        }

        private sealed class Draft(string id, string label)
        {
            public readonly string Id = id;
            public readonly string Label = label;
            public int Count, Own;
            public readonly Dictionary<string, Draft> Children = new(StringComparer.Ordinal);
        }

        private static IReadOnlyList<Node> Finish(IEnumerable<Draft> drafts, ref int nodes)
        {
            var list = new List<Node>();
            foreach (var d in drafts.OrderByDescending(d => d.Count).ThenBy(d => d.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                nodes++;
                list.Add(new Node(d.Id, d.Label, d.Count, d.Own, Finish(d.Children.Values, ref nodes)));
            }
            return list;
        }

        /// <summary>The path's segments, trimmed, empties dropped; a blank value is the one segment
        /// <see cref="NoCategory"/>.</summary>
        public static List<string> Split(string value, string? separator)
        {
            var parts = separator == null ? [value] : value.Split(separator, StringSplitOptions.None);
            var segments = parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (segments.Count == 0) segments.Add(NoCategory);
            return segments;
        }

        private static string Clip(string label) =>
            label.Length <= MaxLabelLength ? label : label[..(MaxLabelLength - 1)].TrimEnd() + "…";
    }
}
