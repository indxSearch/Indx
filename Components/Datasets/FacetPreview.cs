namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// What the Search preview shows of a result's facets: display only, one row per facetable
    /// field, the most frequent values as chips. A facet can hold tens of thousands of values
    /// (every actor of a film dataset), so each row keeps the top few and says how many it left
    /// out. The engine returns each histogram sorted by count, highest first, so "the top few" is
    /// the head of the array. Real filtering belongs to indx-react; this is a glance.
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
        public static IReadOnlyList<Row> Build(
            IReadOnlyDictionary<string, KeyValuePair<string, int>[]>? facets,
            IEnumerable<string>? fieldOrder = null,
            int valuesPerField = DefaultValuesPerField)
        {
            if (facets == null || facets.Count == 0) return [];
            var rank = (fieldOrder ?? []).Select((name, i) => (name, i)).GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.First().i);
            return facets
                .Where(f => f.Value is { Length: > 0 })           // a field nothing in the result carries says nothing
                .OrderBy(f => rank.TryGetValue(f.Key, out var i) ? i : int.MaxValue)
                .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(f => new Row(f.Key, f.Value.Take(valuesPerField).ToArray(), Math.Max(0, f.Value.Length - valuesPerField)))
                .ToList();
        }
    }
}
