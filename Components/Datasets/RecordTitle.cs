using System.Text.Json;
using Indx.Api;

namespace IndxServer.Components.Datasets
{
    /// <summary>A record's name for a list in the console: the heaviest searchable field's first
    /// value, cut at 80 characters. Shared by the Categories browser's products and the browsing
    /// paths' results, so a record reads the same in both.</summary>
    public static class RecordTitle
    {
        public static string? Of(string? json, IReadOnlyList<FieldProxy> fields)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var inspected = DocumentInspection.Build(doc.RootElement, fields);
                var value = inspected.Indexed.FirstOrDefault(f => f.Values.Count > 0 && f.Field.Embeddable != true)?.Values[0];
                return value == null ? null : value.Length > 80 ? value[..79] + "…" : value;
            }
            catch { return null; }
        }
    }
}
