using System.Globalization;
using System.Text.Json;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// The Search preview's filter, from the facet values picked (Nov 2026): within a field any of
    /// its picked values, across fields all of them, written as a filter expression
    /// (<c>`genres` IN ["Action", "Drama"] AND `release_year` IN [2018]</c>). A value is written as
    /// its field's type reads it: a number field's as a number, a boolean field's as true or false,
    /// anything else as text. A value that cannot be read so is left out rather than refused, since
    /// it came from the field's own facet.
    /// </summary>
    public static class FacetFilter
    {
        /// <param name="selection">The picked values by field, in the order picked.</param>
        /// <param name="fieldType">A field's type as the field configuration names it ("Number",
        /// "Boolean", "String"), or null when unknown.</param>
        /// <param name="except">A field to leave out: its own counts are taken without its own
        /// filter, so its other values can still be picked.</param>
        /// <returns>The expression, or null when nothing is picked.</returns>
        public static string? Expression(IReadOnlyDictionary<string, List<string>> selection,
            Func<string, string?> fieldType, string? except = null)
        {
            var parts = new List<string>();
            foreach (var (field, values) in selection)
            {
                if (field == except || values.Count == 0) continue;
                var type = fieldType(field);
                var literals = values.Select(v => Literal(v, type)).Where(l => l != null).ToList();
                if (literals.Count == 0) continue;
                parts.Add($"{Name(field)} IN [{string.Join(", ", literals)}]");
            }
            return parts.Count == 0 ? null : string.Join(" AND ", parts);
        }

        /// <summary>A field name in backticks, a backtick in it doubled.</summary>
        public static string Name(string field) => "`" + field.Replace("`", "``") + "`";

        /// <summary>A facet value as its field's type reads it; null when it cannot be.</summary>
        public static string? Literal(string value, string? type) => type switch
        {
            "Number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                ? n.ToString("R", CultureInfo.InvariantCulture) : null,
            "Boolean" => bool.TryParse(value, out var b) ? (b ? "true" : "false") : null,
            // JSON's escapes are the expression's (FilterExpressionParser).
            _ => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        };
    }
}
