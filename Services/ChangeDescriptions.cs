using System.Text.Json;

namespace IndxServer.Services
{
    /// <summary>
    /// One recorded change in words, for the Statistics tab's chart markers and its Changes list
    /// ("Synonyms: 34 → 44 entries"). One place, so the marker's tooltip and the list read the
    /// same. Works from the stored summary alone: a summary written by an older build, or missing
    /// a property, still gets a sentence rather than an exception.
    /// </summary>
    public static class ChangeDescriptions
    {
        /// <summary>The kind's name on its own, for the Changes section's filter.</summary>
        public static string KindLabel(string kind) => kind switch
        {
            DatasetChangeKind.Synonyms => "Synonyms",
            DatasetChangeKind.BoostRules => "Boost rules",
            DatasetChangeKind.QueryParameters => "Query parameters",
            DatasetChangeKind.Fields => "Fields",
            DatasetChangeKind.Reindex => "Reindex",
            DatasetChangeKind.Replace => "Replace",
            DatasetChangeKind.Documents => "Documents",
            DatasetChangeKind.Hibernate => "Hibernation",
            DatasetChangeKind.Wake => "Wake",
            DatasetChangeKind.Rename => "Rename",
            DatasetChangeKind.Transfer => "Transfer",
            DatasetChangeKind.Delete => "Delete",
            _ => kind,
        };

        public static string Describe(string kind, string? summaryJson)
        {
            JsonElement s = default;
            bool has = false;
            if (!string.IsNullOrEmpty(summaryJson))
            {
                try { s = JsonDocument.Parse(summaryJson).RootElement.Clone(); has = s.ValueKind == JsonValueKind.Object; }
                catch (JsonException) { }
            }

            long? N(string name) => has && s.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
            string? S(string name) => has && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string[] A(string name) => has && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            static string Count(long n) => n.ToString("N0");

            switch (kind)
            {
                case DatasetChangeKind.Synonyms:
                {
                    var before = N("entriesBefore") ?? 0;
                    var after = N("entries") ?? 0;
                    if (after == 0) return "Synonyms removed";
                    if (before == 0) return $"Synonyms added: {Count(after)} entries";
                    return before == after
                        ? $"Synonyms edited: {Count(after)} entries"
                        : $"Synonyms: {Count(before)} → {Count(after)} entries";
                }
                case DatasetChangeKind.BoostRules:
                {
                    var before = N("rulesBefore") ?? 0;
                    var after = N("rules") ?? 0;
                    var enabled = N("enabled") ?? after;
                    var rules = before == after ? $"{Count(after)} rules" : $"{Count(before)} → {Count(after)} rules";
                    return $"Boost rules: {rules}, {Count(enabled)} enabled";
                }
                case DatasetChangeKind.QueryParameters:
                {
                    // Stored as "coverageDepth", "coverageSetup.truncate": the last part reads.
                    var changed = A("changed").Select(n => n[(n.LastIndexOf('.') + 1)..]).ToArray();
                    if (changed.Length == 0) return "Query parameters changed";
                    var shown = string.Join(", ", changed.Take(3));
                    return changed.Length > 3 ? $"Query parameters changed: {shown} and {changed.Length - 3} more" : $"Query parameters changed: {shown}";
                }
                case DatasetChangeKind.Fields:
                {
                    var fields = A("fields");
                    if (fields.Length == 0) return "Fields changed";
                    var shown = string.Join(", ", fields.Take(3));
                    return fields.Length > 3 ? $"Fields changed: {shown} and {fields.Length - 3} more" : $"Fields changed: {shown}";
                }
                case DatasetChangeKind.Reindex:
                    return "Reindexed";
                case DatasetChangeKind.Replace:
                {
                    var before = N("documentsBefore");
                    var after = N("documents");
                    var text = before != null && after != null
                        ? $"Replaced: {Count(before.Value)} → {Count(after.Value)} documents"
                        : "Replaced";
                    var added = A("fieldsAdded").Length;
                    var removed = A("fieldsRemoved").Length;
                    if (added > 0) text += $", {added} {(added == 1 ? "field" : "fields")} added";
                    if (removed > 0) text += $", {removed} {(removed == 1 ? "field" : "fields")} removed";
                    return text;
                }
                case DatasetChangeKind.Documents:
                {
                    var op = S("operation");
                    long inserted = N("inserted") ?? 0, updated = N("updated") ?? 0, deleted = N("deleted") ?? 0;
                    return op switch
                    {
                        "deleteByFilter" => $"{Count(deleted)} documents deleted by filter",
                        "updateByFilter" => $"{Count(updated)} documents updated by filter",
                        _ when inserted > 0 => $"{Count(inserted)} documents inserted in one request",
                        _ when deleted > 0 => $"{Count(deleted)} documents deleted in one request",
                        _ => $"{Count(updated)} documents updated in one request",
                    };
                }
                case DatasetChangeKind.Hibernate:
                    return S("reason") == "idle" ? "Hibernated after its keep-alive time" : "Hibernated";
                case DatasetChangeKind.Wake:
                    return S("reason") == "onDemand" ? "Woken by a search" : "Woken";
                case DatasetChangeKind.Rename:
                    return S("from") is { } from ? $"Renamed from {from}" : "Renamed";
                case DatasetChangeKind.Transfer:
                    return "Moved here from another team";
                case DatasetChangeKind.Delete:
                    return "Dataset deleted";
                default:
                    return kind;
            }
        }
    }
}
