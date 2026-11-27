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
                // on its nested fields, or a field that is null in every record. Some roles still
                // apply to it - WordMatching, PatternMatching and Facetable never read Field.Type,
                // and ticking one of the first two is what prepares an empty field for records that do carry a
                // value later. Filterable and Sortable do read the type, so the engine refuses them
                // and they are cleared here rather than carried in to be rejected at save.
                // Only a file that actually asked for one of those two is worth reporting.
                if (existing.FieldType == null)
                {
                    var isContainer = current.Any(f => f.FieldName.StartsWith(imp.FieldName + ".", StringComparison.Ordinal));
                    if (isContainer)
                    {
                        // A container carries no value of its own - the data is on the nested fields
                        // under it - so no role means anything here. Cleared and not reported.
                        ClearCapabilities(existing);
                        continue;
                    }
                    // A field that was null in every record still takes the roles that do not
                    // read Field.Type. That is the console's promise and the engine honours it:
                    // ticking WordMatching or PatternMatching prepares the field, and a record
                    // inserted later that does carry a value is indexed and found. Clearing them here
                    // would lose a legitimate setting on an export-import round trip.
                    existing.WordMatching = imp.WordMatching;
                    if (imp.PatternMatching.HasValue) existing.PatternMatching = imp.PatternMatching;
                    existing.Facetable = imp.Facetable;
                    // These two do read the type, so the engine refuses them. Cleared rather than
                    // carried in to be rejected at save, and reported only when the file asked.
                    existing.Filterable = false;
                    existing.Sortable = false;
                    if (imp.Filterable == true || imp.Sortable == true) skipped.Add(imp.FieldName);
                    continue;
                }

                // A file exported before 28 Oct 2026 has Searchable, which the library reads into
                // WordMatching (FieldProxy).
                existing.WordMatching = imp.WordMatching;
                existing.Filterable = imp.Filterable;
                existing.Facetable = imp.Facetable;
                existing.Sortable = imp.Sortable;
                // A file exported before Oct 2026 has WordIndexing and no PatternMatching: the
                // field keeps the pattern matching it has.
                if (imp.PatternMatching.HasValue) existing.PatternMatching = imp.PatternMatching;
                existing.Embeddable = imp.Embeddable;
                existing.Weight = imp.Weight;
                existing.BM25b = imp.BM25b;
                existing.BM25k1 = imp.BM25k1;
                // A file exported before these existed has none of them: kept as they are, not
                // cleared, since null on a proxy means "leave alone".
                if (imp.IndexSizes != null) existing.IndexSizes = (int[])imp.IndexSizes.Clone();
                if (imp.NumberOfStartCharacters.HasValue) existing.NumberOfStartCharacters = imp.NumberOfStartCharacters;
                if (imp.NumberOfStopCharacters.HasValue) existing.NumberOfStopCharacters = imp.NumberOfStopCharacters;
                matched++;
            }

            return new Result(matched, unmatched, skipped);
        }

        private static void ClearCapabilities(FieldProxy f)
        {
            f.WordMatching = false;
            f.PatternMatching = false;
            f.Filterable = false;
            f.Facetable = false;
            f.Sortable = false;
            f.Embeddable = false;
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
