using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Indx.Api;
using Indx.Http;
using IndxServer.Engine;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// A catalogue's answer to a description ("private videregående skoler i trondheim"): the
    /// description becomes filters, the search runs with them, and the answer model summarises
    /// what was found: how many, what they share, what stands out, a few by name with citations,
    /// and one way to narrow it. The filters go to the page first (event <c>filters</c>), so its
    /// result list shows the same set the answer is about. Notes/search-or-ask-server.md.
    /// </summary>
    public sealed partial class SearchOrAskService
    {
        /// <summary>The most records the summary counts and reads facts from.</summary>
        private const int CatalogueFound = 200;
        /// <summary>The records the model sees one by one, and may cite.</summary>
        private const int CatalogueShown = 25;

        private sealed record CatalogueOutcome(string Outcome, List<long> Keys, long? FirstTextMs, double? Cost);

        /// <summary>Answers a description with a summary of what it finds, or returns null when the
        /// text asks for no filter (a question about one item): the ordinary answer then follows.</summary>
        private async Task<CatalogueOutcome?> CatalogueAnswerAsync(string teamId, string dataSetName, IServerSearchEngine engine,
            SearchOrAskSettings settings, SearchOrAskFields fields, string question, string model, string? askId,
            Func<string, object, Task> send, long started, CancellationToken ct)
        {
            long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var filterFields = FilterFields(teamId, dataSetName, engine);
            if (filterFields.Count == 0) return null;
            var (suggested, left, filterCost) = await SuggestFiltersAsync(teamId, dataSetName, settings, filterFields, question, ct);
            if (suggested.Length == 0) return null;

            // The filters as the engine builds them; one it refuses is left out, never widened to all.
            Filter? combined = null;
            var used = new List<FilterSuggestion>();
            foreach (var f in suggested)
            {
                if (FilterConditionBuilder.Build(engine, f.Field, f.Value, f.Min, f.Max) is not { } built) continue;
                combined = combined == null ? built : combined & built;
                used.Add(f);
            }
            if (combined == null) return null;

            var result = IndxServerInternalApi.Manager.Search(new QueryProxy
            {
                Text = left,
                Filter = new FilterProxy(combined.SerializedKey),
                MaxNumberOfRecordsToReturn = CatalogueFound,
                EnableFacets = true,
                EnableCoverage = left.Length > 0,
            }, dataSetName, teamId);
            var keys = (result.Records ?? []).Select(r => r.DocumentKey).ToList();
            await send("filters", new { filters = used, text = left, found = keys.Count, more = keys.Count >= CatalogueFound });
            if (keys.Count == 0)
            {
                await send("error", new { code = "nothingFound", message = "Nothing matches all of that. Try fewer conditions." });
                return new CatalogueOutcome("nothingFound", keys, null, filterCost);
            }

            // Facts from the fields a visitor can filter by: what a summary of a set is made of.
            var docs = keys.Select(k => (Key: k, Doc: Parse(engine.GetJsonDataOfKey(k)))).ToList();
            var shown = docs.Take(CatalogueShown).Select((d, i) => (N: i + 1, d.Key, d.Doc)).ToList();
            var titleOf = (JsonNode? doc) => doc != null && fields.TitleField != null ? ReadField(doc, fields.TitleField) ?? "" : "";
            await send("sources", shown.Select(s => new AskSource(s.N, s.Key, titleOf(s.Doc))).ToArray());

            var user = new StringBuilder();
            user.AppendLine($"The visitor typed: {question}");
            user.AppendLine($"Searched with: {string.Join("; ", used.Select(Describe))}{(left.Length > 0 ? $"; and the words \"{left}\"" : "")}");
            user.AppendLine(keys.Count >= CatalogueFound
                ? $"Found: {CatalogueFound} or more; the summary below covers the first {CatalogueFound}."
                : $"Found: {keys.Count}.");
            user.AppendLine();
            user.AppendLine("Summary of everything found:");
            foreach (var line in Summary(docs.Select(d => d.Doc).ToList(), filterFields)) user.AppendLine($"- {line}");
            user.AppendLine();
            user.AppendLine(keys.Count > CatalogueShown ? $"The first {CatalogueShown}, numbered:" : "Each one, numbered:");
            foreach (var s in shown)
                user.AppendLine($"[{s.N}] {titleOf(s.Doc)}: {Facts(s.Doc, filterFields)}");
            user.AppendLine().Append(SearchOrAskRecipes.AnswerLanguageReminder);

            var usage = new AnswerUsage();
            long? firstText = null;
            await foreach (var delta in _models.AnswerAsync(model,
                               SearchOrAskRecipes.CatalogueAnswerSystemPrompt(SiteDescription(teamId, dataSetName, settings)),
                               user.ToString(), [], usage, ct))
            {
                firstText ??= Ms();
                await send("text", new { delta });
            }
            await send("done", new { model, askId, latencyMs = Ms() });
            return new CatalogueOutcome("answered", shown.Select(s => s.Key).ToList(), firstText,
                usage.Cost == null && filterCost == null ? null : (usage.Cost ?? 0) + (filterCost ?? 0));
        }

        private static JsonNode? Parse(string? json)
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json); }
            catch (JsonException) { return null; }
        }

        private static string Describe(FilterSuggestion f) =>
            f.Value != null ? $"{f.Field} = {f.Value}"
            : $"{f.Field} {(f.Min != null ? $"from {Num(f.Min.Value)}" : "")}{(f.Min != null && f.Max != null ? " " : "")}{(f.Max != null ? $"to {Num(f.Max.Value)}" : "")}";

        private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>One record's facts on the filter fields: a yes/no only when yes, absent ones left out.</summary>
        private static string Facts(JsonNode? doc, List<FilterField> fields)
        {
            if (doc == null) return "";
            var parts = new List<string>();
            foreach (var f in fields)
            {
                var v = FieldAt(doc, f.Name);
                if (v is not JsonValue value) continue;
                if (f.Type == "true/false")
                {
                    if (value.GetValueKind() == JsonValueKind.True) parts.Add(f.Name);
                }
                else parts.Add($"{f.Name} {value}");
            }
            return string.Join("; ", parts);
        }

        /// <summary>The set as a whole: each number's range and median, each text field's most common
        /// values, each yes/no's count, over every record found.</summary>
        private static List<string> Summary(List<JsonNode?> docs, List<FilterField> fields)
        {
            var lines = new List<string>();
            foreach (var f in fields)
            {
                var values = docs.Select(d => d == null ? null : FieldAt(d, f.Name)).OfType<JsonValue>().ToList();
                if (values.Count == 0) continue;
                switch (f.Type)
                {
                    case "number":
                        var n = values.Select(v => v.TryGetValue<double>(out var d) ? d : (double?)null).Where(d => d != null).Select(d => d!.Value).Order().ToList();
                        if (n.Count > 0) lines.Add($"{f.Name}: {Num(n[0])} to {Num(n[^1])}, median {Num(n[n.Count / 2])} ({n.Count} with a value)");
                        break;
                    case "true/false":
                        var yes = values.Count(v => v.GetValueKind() == JsonValueKind.True);
                        if (yes > 0 && yes < docs.Count) lines.Add($"{f.Name}: {yes} of {docs.Count}");
                        break;
                    default:
                        var top = values.GroupBy(v => v.ToString()).OrderByDescending(g => g.Count()).Take(5).ToList();
                        if (top.Count > 1 || top.Count == 1 && top[0].Count() < docs.Count)
                            lines.Add($"{f.Name}: {string.Join(", ", top.Select(g => $"{g.Key} {g.Count()}"))}");
                        break;
                }
            }
            return lines;
        }
    }
}
