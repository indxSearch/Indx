using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Indx.Api;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// What "Automatic" gives the judge to read of a result, from the field roles and a sample of the
    /// records (13 Nov 2026). Until then the section was empty and the snippet the two searched fields
    /// after the title, which on a shop were two category fields and no description: the judge told a
    /// list holding what the visitor meant from one sharing its words at 0.66 (area under the curve)
    /// there, and at 0.95 with these picks, as well as the best fields chosen by hand; 0.93 against
    /// 0.98 on films, 0.91 against 0.93 on schools (the rule this ports,
    /// measured on our test sets).
    /// <list type="bullet">
    /// <item><b>Title</b>: the highest-weighted searched text field, as before.</item>
    /// <item><b>Snippet</b>: prose, values of 60 characters and 8 words or more on average; the
    /// searched first by weight, then the longest. At most two; none when the records hold no prose.</item>
    /// <item><b>Section</b>: the short facts visitors name besides the name: text with a role and
    /// short values, a number only as a year, filled in a tenth of the records, not codes, not one
    /// value nearly everywhere, nothing a field already chosen says (the same values, or the category
    /// above a class). Ranked by the roles the owner gave (searched by weight, facetable, filterable),
    /// a facet of few values most records have (what a record is) above the rest. At most six.</item>
    /// </list>
    /// </summary>
    public static class SearchOrAskAutomatic
    {
        /// <summary>The records read to choose: spread over the dataset in the order they were stored.</summary>
        public const int SampleSize = 400;

        private const int MaxSection = 6, MaxSnippet = 2, ShortChars = 40, ProseChars = 60, ProseWords = 8, KindDistinct = 50;

        /// <summary>What Automatic resolves to.</summary>
        public sealed record Picks(string? Title, IReadOnlyList<string> Section, IReadOnlyList<string> Snippet);

        // A literal "None" or "null" is how many exports write a missing value.
        private static readonly HashSet<string> Empty = ["", "None", "none", "null", "NULL", "NaN", "N/A", "n/a"];
        private static readonly Regex Token = new(@"^[\w.:/#-]+$", RegexOptions.Compiled);

        /// <summary>What the sample holds in a field.</summary>
        private sealed class Profile
        {
            public required List<List<JsonValue>> PerRecord { get; init; }
            public double Fill, Length, Words, Codes, Commonest, Paths;
            public int Distinct;
            public bool YearLike;
        }

        public static Picks Choose(IReadOnlyList<Field> fields, IReadOnlyList<JsonNode> sample)
        {
            static bool Searched(Field f) => f.WordMatching || f.PatternMatching != null;
            var profiles = fields.Where(f => f.Type is JsonValueKind.String or JsonValueKind.Number)
                .ToDictionary(f => f.Name, f => Read(f.Name, sample), StringComparer.Ordinal);
            var text = fields.Where(f => f.Type == JsonValueKind.String && profiles.ContainsKey(f.Name)).ToList();
            var title = text.Where(Searched).OrderByDescending(f => f.Weight).FirstOrDefault()?.Name;

            var snippet = text
                .Where(f => f.Name != title && profiles[f.Name] is { Length: >= ProseChars, Words: >= ProseWords, Fill: >= 0.1, Paths: < 0.5 })
                .OrderBy(f => Searched(f) ? 0 : 1).ThenByDescending(f => Searched(f) ? f.Weight : 0).ThenByDescending(f => profiles[f.Name].Length)
                .Take(MaxSnippet).Select(f => f.Name).ToList();

            (double Score, double Fill) Rank(Field f)
            {
                var p = profiles[f.Name];
                double score = (Searched(f) ? 2 * f.Weight : 0) + (f.Facetable ? 1.5 : 0) + (f.Filterable ? 1 : 0);
                // A facet of few values most records have (a genre, a type, a language): what a record is.
                if (f.Facetable && f.Type == JsonValueKind.String && p.Distinct <= KindDistinct && p.Fill >= 0.5) score += 2;
                return (score * Math.Min(1, p.Fill * 2), p.Fill);
            }
            var candidates = fields.Where(f => profiles.TryGetValue(f.Name, out var p) && f.Name != title && !snippet.Contains(f.Name)
                    && (Searched(f) || f.Filterable || f.Facetable)
                    && p.Fill >= 0.1 && p.Length >= 2 && p.Length <= ShortChars && p.Codes <= 0.5 && p.Commonest <= 0.97 && p.Paths < 0.5
                    // A number reads on its own only as a year: "245" says nothing without its field's name.
                    && (f.Type != JsonValueKind.Number || p.YearLike))
                .OrderByDescending(f => Rank(f).Score).ThenByDescending(f => Rank(f).Fill).ToList();
            var section = new List<string>();
            foreach (var f in candidates)
            {
                if (section.Count == MaxSection) break;
                var p = profiles[f.Name];
                var chosen = section.Concat(title != null && profiles.ContainsKey(title) ? [title] : []);
                if (chosen.Any(g => SameValues(profiles[g], p) || Implied(profiles[g], p))) continue;
                section.Add(f.Name);
            }
            return new Picks(title, section, snippet);
        }

        private static Profile Read(string path, IReadOnlyList<JsonNode> sample)
        {
            var per = sample.Select(r => Leaves(r, path)).ToList();
            var values = per.SelectMany(v => v).Select(v => v.ToString()).ToList();
            int n = Math.Max(1, values.Count);
            var counts = values.GroupBy(v => v, StringComparer.Ordinal).Select(g => g.Count()).ToList();
            return new Profile
            {
                PerRecord = per,
                Fill = (double)per.Count(v => v.Count > 0) / Math.Max(1, sample.Count),
                Length = values.Sum(v => v.Length) / (double)n,
                Words = values.Sum(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) / (double)n,
                // One token with a digit or a separator, six characters or more: an ISBN, a slug, a code.
                Codes = values.Count(v => v.Length >= 6 && !v.Contains(' ') && Token.IsMatch(v)
                    && (v.Any(char.IsDigit) || v.Contains('-') || v.Contains('/') || v.Contains('_'))) / (double)n,
                // The share of the commonest value: a field where nearly every record says the same says nothing.
                Commonest = counts.Count == 0 ? 0 : counts.Max() / (double)n,
                // Paths and breadcrumbs ("Furniture / Beds / Twin Beds") are long but not prose.
                Paths = values.Count(v => v.Count(c => c is '/' or '>' or '|') >= 2) / (double)n,
                Distinct = counts.Count,
                YearLike = values.Count > 0 && per.SelectMany(v => v).All(v =>
                    v.TryGetValue<double>(out var d) && Math.Floor(d) == d && d is >= 1000 and <= 2100),
            };
        }

        /// <summary>The values at a field's path: a dotted name walks into objects, an array along the
        /// way gives one value per element, and an array at the end each of its items.</summary>
        private static List<JsonValue> Leaves(JsonNode record, string path)
        {
            List<JsonNode> at = [record];
            foreach (var part in path.Split('.'))
                at = at.SelectMany(n => n switch
                {
                    JsonObject o when o[part] is { } v => [v],
                    JsonArray a => a.OfType<JsonObject>().Select(o => o[part]).OfType<JsonNode>(),
                    _ => Enumerable.Empty<JsonNode>(),
                }).ToList();
            return at.SelectMany(n => n is JsonArray a ? a.OfType<JsonNode>() : [n]).OfType<JsonValue>()
                .Where(v => v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number && !Empty.Contains(v.ToString().Trim()))
                .ToList();
        }

        private static string[] Key(List<JsonValue> values) => values.Select(v => v.ToString()).ToArray();

        /// <summary>Whether two fields hold the same values in nearly every record where both have one.</summary>
        private static bool SameValues(Profile a, Profile b)
        {
            var both = a.PerRecord.Zip(b.PerRecord).Where(x => x.First.Count > 0 && x.Second.Count > 0).ToList();
            if (both.Count < 10) return false;
            return both.Count(x => Key(x.First).Select(s => s.ToLowerInvariant()).SequenceEqual(Key(x.Second).Select(s => s.ToLowerInvariant())))
                >= 0.9 * both.Count;
        }

        /// <summary>Whether field b says nothing a has not: a groups the records, and nearly every
        /// group has one value of b (a class and the category above it).</summary>
        private static bool Implied(Profile a, Profile b)
        {
            // Only over the records b covers: a category on the products alone says nothing about
            // the content type of the rest.
            if (a.Fill < b.Fill * 0.9) return false;
            var pairs = a.PerRecord.Zip(b.PerRecord).Where(x => x.First.Count > 0 && x.Second.Count > 0)
                .Select(x => (A: string.Join('\u0001', Key(x.First)), B: string.Join('\u0001', Key(x.Second)))).ToList();
            if (pairs.Count < 10) return false;
            var groups = pairs.GroupBy(p => p.A, StringComparer.Ordinal).ToList();
            // A near-unique field (a title) decides every other field trivially.
            if (groups.Count > pairs.Count * 0.6) return false;
            // Only groups of several records say anything, and only a field that groups most of the
            // records: a few authors with several books each do not make the author decide the type.
            var repeated = groups.Where(g => g.Count() >= 2).ToList();
            int covered = repeated.Sum(g => g.Count());
            if (repeated.Count < 10 || covered < pairs.Count / 2.0) return false;
            int agreeing = repeated.Sum(g => g.GroupBy(p => p.B, StringComparer.Ordinal).Max(x => x.Count()));
            return agreeing >= 0.99 * covered;
        }
    }
}
