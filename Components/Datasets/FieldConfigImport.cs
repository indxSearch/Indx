using Indx.Api;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// Merges an imported field configuration onto a dataset's working copy, by field name.
    /// Kept apart from the toolbar so the rules can be tested without a file upload.
    /// </summary>
    internal static class FieldConfigImport
    {
        /// <param name="Matched">Fields the import applied to.</param>
        /// <param name="Unmatched">Imported fields this dataset does not have.</param>
        /// <param name="SkippedNoValue">Fields the file configures but that carry no value in
        /// this dataset, so they have no type and cannot be configured. Named so the user can be
        /// told which part of their configuration did not take effect.</param>
        internal sealed record Result(int Matched, int Unmatched, IReadOnlyList<string> SkippedNoValue);

        internal static Result Merge(FieldProxy[] current, FieldProxy[] imported)
        {
            var byName = current.ToDictionary(f => f.FieldName, StringComparer.Ordinal);
            int matched = 0, unmatched = 0;
            var skipped = new List<string>();

            foreach (var imp in imported)
            {
                if (!byName.TryGetValue(imp.FieldName, out var existing)) { unmatched++; continue; }

                // No type means Analyze never read a value: either a container, whose data lives
                // on its nested fields, or a field that is null in every record. Neither can be
                // configured, and the table shows no controls for them, so a flag copied here
                // would be invisible, impossible to clear, and refused by the library at load.
                // Any flag already sitting there from an earlier import is cleared for the same
                // reason. Only a field the file actually configures is worth reporting.
                if (existing.FieldType == null)
                {
                    ClearCapabilities(existing);
                    var isContainer = current.Any(f => f.FieldName.StartsWith(imp.FieldName + ".", StringComparison.Ordinal));
                    if (!isContainer && !FieldConfigTable.IsUnused(imp)) skipped.Add(imp.FieldName);
                    continue;
                }

                existing.Searchable = imp.Searchable;
                existing.Filterable = imp.Filterable;
                existing.Facetable = imp.Facetable;
                existing.Sortable = imp.Sortable;
                existing.WordIndexing = imp.WordIndexing;
                existing.Embeddable = imp.Embeddable;
                existing.PreloadFilters = imp.PreloadFilters;
                existing.HighResolution = imp.HighResolution;
                existing.Weight = imp.Weight;
                existing.BM25b = imp.BM25b;
                existing.BM25k1 = imp.BM25k1;
                matched++;
            }

            return new Result(matched, unmatched, skipped);
        }

        private static void ClearCapabilities(FieldProxy f)
        {
            f.Searchable = false;
            f.Filterable = false;
            f.Facetable = false;
            f.Sortable = false;
            f.WordIndexing = false;
            f.Embeddable = false;
            f.PreloadFilters = false;
            f.HighResolution = false;
        }

        /// <summary>The message shown beside the import button, or null when there is nothing to
        /// say. The skipped-field sentence is added to whichever match message applies.</summary>
        internal static string? Describe(Result r, int importedCount)
        {
            var parts = new List<string>(2);
            if (r.Matched == 0 && r.SkippedNoValue.Count == 0)
                parts.Add("No matching fields found. This config may be for a different dataset.");
            else if (r.Unmatched > importedCount / 3)
                parts.Add($"{r.Matched} field(s) applied, {r.Unmatched} not found in this dataset and were skipped.");

            if (r.SkippedNoValue.Count > 0)
            {
                const int shown = 5;
                var names = string.Join(", ", r.SkippedNoValue.Take(shown));
                if (r.SkippedNoValue.Count > shown) names += $" and {r.SkippedNoValue.Count - shown} more";
                parts.Add(r.SkippedNoValue.Count == 1
                    ? $"{names} has no value in this dataset, so its configuration was skipped."
                    : $"{r.SkippedNoValue.Count} fields have no value in this dataset, so their configuration was skipped: {names}.");
            }
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }
}
