using IndxServer.Models;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// What the Browse tab marks on a category: boosted by a saved rule, never selected and never
    /// purchased in the statistics window. Pure: takes the tree, the rules and the documents that
    /// were selected or converted with the categories each document is filed under, and answers
    /// per node id. A node is never selected when no document under it, itself and its
    /// subcategories, was selected in the window; the same for purchased. Both marks mean nothing
    /// on a dataset without any such event in the window, so they are only set once there is one.
    /// </summary>
    public sealed class CategoryHealth
    {
        /// <summary>A document's figures in the window.</summary>
        public readonly record struct Activity(long Selects, long Converts);

        /// <summary>A node's figures, its own and its subcategories' together.</summary>
        public readonly record struct Figures(long Selects, long Converts, int SelectedProducts, int PurchasedProducts);

        private readonly Dictionary<string, Figures> _figures = new(StringComparer.Ordinal);
        private readonly HashSet<string> _boosted = new(StringComparer.Ordinal);

        /// <summary>Whether the window held any selection or any purchase at all.</summary>
        public bool AnySelects { get; }
        public bool AnyConverts { get; }
        public int BoostedCount => _boosted.Count;

        /// <param name="tree">The categories.</param>
        /// <param name="rules">The dataset's boost rules; an active one with a value condition on
        /// <paramref name="field"/> marks the node whose id is that value.</param>
        /// <param name="field">The category field.</param>
        /// <param name="today">For the rules' windows.</param>
        /// <param name="activity">Per document that was selected or converted: its figures and the
        /// category paths it is filed under, as the field holds them.</param>
        public CategoryHealth(CategoryHierarchy.Tree tree, IEnumerable<BoostRule> rules, string field, DateOnly today,
            IEnumerable<(Activity Activity, IReadOnlyList<string> Paths)> activity)
        {
            foreach (var rule in rules)
            {
                if (!rule.IsActiveOn(today)) continue;
                foreach (var c in rule.Conditions)
                    if (c.Field == field && !c.IsRange && !string.IsNullOrEmpty(c.Value)) _boosted.Add(c.Value);
            }
            var totals = new Dictionary<string, (long S, long C, int SP, int PP)>(StringComparer.Ordinal);
            foreach (var (a, paths) in activity)
            {
                if (a.Selects > 0) AnySelects = true;
                if (a.Converts > 0) AnyConverts = true;
                var touched = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in paths)
                    foreach (var id in AncestorIds(path, tree.Separator))
                        touched.Add(id);
                foreach (var id in touched)
                {
                    var t = totals.GetValueOrDefault(id);
                    totals[id] = (t.S + a.Selects, t.C + a.Converts, t.SP + (a.Selects > 0 ? 1 : 0), t.PP + (a.Converts > 0 ? 1 : 0));
                }
            }
            foreach (var (id, t) in totals) _figures[id] = new Figures(t.S, t.C, t.SP, t.PP);
        }

        /// <summary>The ids of every node a path passes through, the path's own last.</summary>
        public static IEnumerable<string> AncestorIds(string path, string? separator)
        {
            var segments = CategoryHierarchy.Split(path, separator);
            if (segments.Count > CategoryHierarchy.MaxDepth) segments = segments.Take(CategoryHierarchy.MaxDepth).ToList();
            for (int i = 1; i <= segments.Count; i++)
                yield return separator == null ? segments[0] : string.Join(separator, segments.Take(i));
        }

        public Figures FiguresOf(string id) => _figures.GetValueOrDefault(id);
        public bool IsBoosted(string id) => _boosted.Contains(id);
        public bool IsNeverSelected(string id) => AnySelects && FiguresOf(id).Selects == 0;
        public bool IsNeverPurchased(string id) => AnyConverts && FiguresOf(id).Converts == 0;

        /// <summary>How many nodes of the tree carry each mark.</summary>
        public (int Boosted, int NeverSelected, int NeverPurchased) Count(CategoryHierarchy.Tree tree)
        {
            int b = 0, s = 0, p = 0;
            void Walk(CategoryHierarchy.Node n)
            {
                if (IsBoosted(n.Id)) b++;
                if (IsNeverSelected(n.Id)) s++;
                if (IsNeverPurchased(n.Id)) p++;
                foreach (var c in n.Children) Walk(c);
            }
            foreach (var r in tree.Roots) Walk(r);
            return (b, s, p);
        }
    }
}
