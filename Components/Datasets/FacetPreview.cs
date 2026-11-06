namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// What the Search preview shows of a result's facets: display only, one row per facetable
    /// field, the most frequent values as chips. A facet can hold tens of thousands of values
    /// (every actor of a film dataset), so each row keeps the top few and says how many it left
    /// out. The engine returns each histogram sorted by count, highest first, so "the top few" is
    /// the head of the array. A value of a filterable field can be picked as a filter (Nov 2026,
    /// <see cref="FacetFilter"/>); a full filter panel with ranges belongs to indx-react.
    /// </summary>
    public static class FacetPreview
    {
        public const int DefaultValuesPerField = 8;

        /// <param name="Field">The facetable field.</param>
        /// <param name="Values">Its most frequent values with their counts.</param>
        /// <param name="MoreValues">How many further values were left out of <paramref name="Values"/>.</param>
        public sealed record Row(string Field, IReadOnlyList<KeyValuePair<string, int>> Values, int MoreValues);

        /// <param name="facets">As returned on <c>Result.Facets</c>; null when no field is facetable.</param>
        /// <param name="fieldOrder">The order to list fields in (the field configuration's). Fields
        /// it does not name follow, alphabetically.</param>
        /// <param name="valuesPerField">How many values to keep per field; the rest are counted in
        /// <see cref="Row.MoreValues"/>.</param>
        /// <param name="selected">Values picked as filters, by field. A picked value is always
        /// shown, after the most frequent ones when it is not among them and with 0 when the
        /// result does not hold it, so it can be unpicked; a field with a picked value is listed
        /// even when the result carries none of its values.</param>
        public static IReadOnlyList<Row> Build(
            IReadOnlyDictionary<string, KeyValuePair<string, int>[]>? facets,
            IEnumerable<string>? fieldOrder = null,
            int valuesPerField = DefaultValuesPerField,
            IReadOnlyDictionary<string, List<string>>? selected = null)
        {
            var all = new Dictionary<string, KeyValuePair<string, int>[]>();
            foreach (var (field, values) in facets ?? new Dictionary<string, KeyValuePair<string, int>[]>())
                all[field] = values ?? [];
            foreach (var (field, picked) in selected ?? new Dictionary<string, List<string>>())
                if (picked.Count > 0 && !all.ContainsKey(field)) all[field] = [];
            if (all.Count == 0) return [];
            var rank = (fieldOrder ?? []).Select((name, i) => (name, i)).GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.First().i);
            return all
                .Where(f => f.Value.Length > 0 || Picked(selected, f.Key).Count > 0) // a field nothing in the result carries says nothing
                .OrderBy(f => rank.TryGetValue(f.Key, out var i) ? i : int.MaxValue)
                .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(f => RowOf(f.Key, f.Value, valuesPerField, Picked(selected, f.Key)))
                .ToList();
        }

        private static List<string> Picked(IReadOnlyDictionary<string, List<string>>? selected, string field) =>
            selected != null && selected.TryGetValue(field, out var picked) ? picked : [];

        private static Row RowOf(string field, KeyValuePair<string, int>[] values, int valuesPerField, List<string> picked)
        {
            var shown = values.Take(valuesPerField).ToList();
            foreach (var value in picked)
            {
                if (shown.Any(v => v.Key == value)) continue;
                var counted = values.FirstOrDefault(v => v.Key == value);
                shown.Add(new(value, counted.Key == null ? 0 : counted.Value));
            }
            int inHistogram = shown.Count(v => values.Any(h => h.Key == v.Key));
            return new Row(field, shown, Math.Max(0, values.Length - inHistogram));
        }
    }
}
