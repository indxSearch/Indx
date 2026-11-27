using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>One row of the console's Filters: a field (or the category menu), what the rules give
    /// it, what the owner chose, and why the rules leave it out.</summary>
    /// <param name="Field">The field, or <see cref="FilterSetup.CategoriesRow"/>.</param>
    /// <param name="FieldType">"String", "Number", "Boolean"..., or null for the category menu.</param>
    /// <param name="DefaultLabel">The rules' name: made from the field's name.</param>
    /// <param name="Label">The owner's name, or null.</param>
    /// <param name="DefaultKind">The rules' panel.</param>
    /// <param name="Kind">The owner's panel, or null.</param>
    /// <param name="Shown">Shown on a page: the owner's choice, else the rules'.</param>
    /// <param name="ShownByDefault">The rules' choice.</param>
    /// <param name="ValueCount">Distinct values over the dataset; null when not known.</param>
    /// <param name="LeftOut">Why the rules leave it out ("one value"), or null.</param>
    /// <param name="Levels">The category menu's level fields from the top.</param>
    /// <param name="Edges">Buckets: the owner's edges.</param>
    /// <param name="IsNew">Not in the owner's saved list: a field that became facetable since.</param>
    public sealed record FilterRow(
        string Field, string? FieldType, string DefaultLabel, string? Label, FilterKind DefaultKind, FilterKind? Kind,
        bool Shown, bool ShownByDefault, int? ValueCount, string? LeftOut,
        IReadOnlyList<string>? Levels, IReadOnlyList<double>? Edges, bool IsNew)
    {
        public string EffectiveLabel => string.IsNullOrWhiteSpace(Label) ? DefaultLabel : Label!;
        public FilterKind EffectiveKind => Kind ?? DefaultKind;
        public bool IsCategories => Field == FilterSetup.CategoriesRow;
    }

    /// <summary>
    /// A dataset's filters (26 Nov 2026): the rules' plan (<see cref="FilterPlanner"/>) with the owner's
    /// choices (<see cref="FilterSetup"/>) laid over it. The route a page reads (GET filter-setup) and the
    /// console's Filters tab both come through here, so they cannot disagree. A field the owner's list
    /// does not name (one that became facetable since) is placed after the named ones, shown if the
    /// rules show it. The category menu is one row, from the Categories setting, when that setting is
    /// a group of level fields; a single path field stays a field (intrface's menu reads level fields).
    /// </summary>
    public static class DatasetFilters
    {
        private const int MaxLabelLength = 100;

        /// <summary>What the rules read: the fields' roles and types, and their values over the whole
        /// dataset (an empty search with facets). Null facets: the values are not known.</summary>
        public static FilterPlanDataset DatasetOf(IReadOnlyList<FieldProxy> fields, IReadOnlyDictionary<string, KeyValuePair<string, int>[]>? facets) => new(
            Filterable: fields.Where(f => f.Filterable == true).Select(f => f.FieldName).ToList(),
            Facetable: fields.Where(f => f.Facetable == true).Select(f => f.FieldName).ToList(),
            FieldTypes: fields.Where(f => f.FieldType != null).ToDictionary(f => f.FieldName, f => f.FieldType!),
            Values: facets?.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Select(v => v.Key).ToList())
                ?? new Dictionary<string, IReadOnlyList<string>>());

        /// <summary>The level fields of the Categories setting, from the top, when it is a group
        /// ("category.department", "category.section", ...) every one of which filters and counts; else null.</summary>
        public static IReadOnlyList<string>? CategoryLevels(DatasetMetadataStore.CategorySetting setting, FilterPlanDataset dataset)
        {
            if (string.IsNullOrEmpty(setting.Field)) return null;
            var prefix = setting.Field + ".";
            var levels = dataset.Facetable.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.IndexOf('.', prefix.Length) < 0).ToList();
            return levels.Count >= 2 && levels.All(dataset.Filterable.Contains) ? levels : null;
        }

        /// <summary>Every row the console shows, in order: the owner's, then the rest as the rules place them.</summary>
        public static IReadOnlyList<FilterRow> Rows(FilterPlanDataset dataset, IReadOnlyList<string>? levels, FilterSetup? setup)
        {
            var defaults = FilterPlanner.Plan(dataset, new FilterPlanOptions { Hierarchy = levels });
            var byDefault = defaults.ToDictionary(p => p.Kind == FilterKind.Hierarchy ? FilterSetup.CategoriesRow : p.Field);
            var valuesKnown = dataset.Values.Count > 0;
            var skip = new HashSet<string>(levels ?? [], StringComparer.Ordinal);
            var candidates = dataset.Facetable.Where(f => dataset.Filterable.Contains(f) && !skip.Contains(f)).ToList();

            FilterRow Row(string field, FilterSetupField? own, bool isNew)
            {
                if (field == FilterSetup.CategoriesRow)
                {
                    return new FilterRow(field, null, "Categories", own?.Label, FilterKind.Hierarchy, null,
                        own?.Shown ?? true, true, null, null, levels, null, isNew);
                }
                var planned = byDefault.GetValueOrDefault(field)
                    ?? FilterPlanner.Plan(dataset, new FilterPlanOptions { Fields = [field] })[0];
                var shownByDefault = byDefault.ContainsKey(field);
                return new FilterRow(field, dataset.FieldTypes.GetValueOrDefault(field), planned.Label, own?.Label,
                    planned.Kind, own?.Kind, own?.Shown ?? shownByDefault, shownByDefault, planned.ValueCount,
                    shownByDefault || !valuesKnown ? null : LeftOutBecause(planned), null, own?.Edges, isNew);
            }

            var order = new List<string>();
            if (levels != null) order.Add(FilterSetup.CategoriesRow);
            order.AddRange(candidates);
            var known = new HashSet<string>(order, StringComparer.Ordinal);
            if (setup == null || setup.Fields.Count == 0) return order.Select(f => Row(f, null, false)).ToList();

            var rows = new List<FilterRow>();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var own in setup.Fields)
            {
                // A field gone from the dataset (or no longer filterable) is skipped, its choices kept.
                if (!known.Contains(own.Field) || !placed.Add(own.Field)) continue;
                rows.Add(Row(own.Field, own, false));
            }
            foreach (var field in order)
                if (placed.Add(field)) rows.Add(Row(field, null, true));
            return rows;
        }

        private static string LeftOutBecause(PlannedFilter planned) => planned switch
        {
            { ValueCount: 0 } => "no values",
            { Kind: FilterKind.Toggle } => "only one of true and false",
            { ValueCount: 1 } => "one value",
            { ValueCount: var n } => $"{n:N0} values: a name or a title?",
        };

        /// <summary>The plan a page reads: the shown rows, named and in order.</summary>
        public static IReadOnlyList<PlannedFilter> Plan(IReadOnlyList<FilterRow> rows) => rows
            .Where(r => r.Shown)
            .Select(r => r.IsCategories
                ? new PlannedFilter(r.Levels![0], FilterKind.Hierarchy, r.EffectiveLabel, Levels: r.Levels)
                : new PlannedFilter(r.Field, r.EffectiveKind, r.EffectiveLabel, r.ValueCount,
                    Edges: r.EffectiveKind == FilterKind.Buckets ? r.Edges : null))
            .ToList();

        /// <summary>Why the owner's choices cannot be saved, or null when they can: a field the
        /// dataset cannot filter by, a field named twice, a panel its type cannot have, buckets without
        /// edges in order, a name too long.</summary>
        public static string? Validate(FilterSetup setup, FilterPlanDataset dataset, IReadOnlyList<string>? levels)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skip = new HashSet<string>(levels ?? [], StringComparer.Ordinal);
            foreach (var f in setup.Fields)
            {
                if (string.IsNullOrWhiteSpace(f.Field)) return "Every row needs a field.";
                if (!seen.Add(f.Field)) return $"'{f.Field}' is named twice.";
                if (f.Label?.Length > MaxLabelLength) return $"The name of '{f.Field}' is longer than {MaxLabelLength} characters.";
                if (f.Field == FilterSetup.CategoriesRow)
                {
                    if (levels == null) return "The dataset has no category menu: choose its levels in Categories first.";
                    if (f.Kind is { } k && k != FilterKind.Hierarchy) return "The category menu cannot have another panel.";
                    continue;
                }
                if (!dataset.Facetable.Contains(f.Field) || !dataset.Filterable.Contains(f.Field))
                    return $"'{f.Field}' is not a field the dataset can filter and count by (Filterable and Facetable).";
                if (skip.Contains(f.Field)) return $"'{f.Field}' is a level of the category menu.";
                var number = dataset.FieldTypes.GetValueOrDefault(f.Field) == "Number";
                switch (f.Kind)
                {
                    case FilterKind.Hierarchy:
                        return $"'{f.Field}' cannot be a category menu: that is the Categories setting.";
                    case FilterKind.Range or FilterKind.Buckets when !number:
                        return $"'{f.Field}' is not a number: it cannot have a range or buckets.";
                    case FilterKind.Buckets when f.Edges is not { Count: >= 2 } || !f.Edges.Zip(f.Edges.Skip(1)).All(p => p.First < p.Second):
                        return $"The buckets of '{f.Field}' need two or more edges, each above the one before.";
                }
            }
            return null;
        }

        /// <summary>The rows of a dataset as it is now: its fields, its values (only from an engine
        /// that is Ready: never a wake, never a wait), its Categories setting and the owner's choices.</summary>
        public static IReadOnlyList<FilterRow> Read(ISearchEngine engine, DatasetMetadataStore.CategorySetting category, FilterSetup? setup, out FilterPlanDataset dataset, out IReadOnlyList<string>? levels)
        {
            var fields = engine.GetFieldConfiguration();
            dataset = DatasetOf(fields, ValuesOf(engine));
            levels = CategoryLevels(category, dataset);
            return Rows(dataset, levels, setup);
        }

        /// <summary>Every facetable field's values over the whole dataset: an empty search with
        /// facets, which the engine keeps after the first. Null when the engine is not Ready or the
        /// read fails: then the rules leave nothing out for want of values.</summary>
        public static IReadOnlyDictionary<string, KeyValuePair<string, int>[]>? ValuesOf(ISearchEngine engine)
        {
            try
            {
                if (engine.Status.SystemState != SystemState.Ready) return null;
                var result = engine.Search(new Query("", 1) { EnableFacets = true, TimeOutLimitMilliseconds = 10_000 });
                return result.Facets;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
