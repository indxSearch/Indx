using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Indx.Api;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// A catalogue's answer with <c>answers: best</c>: an agent that searches until it has what the
    /// visitor asked for, then puts that list on the page and says what it found. Measured against
    /// the quick summary on the school register (JevProbe --agent, 11 Oct 2026): it retries when a
    /// search finds nothing, compares, and notices what a single search cannot (a kulturskole is not an
    /// ordinary school), at about 10 s and 7 cents a question. It starts knowing the filter fields and
    /// their values, so it spends no turn on describing them; its two tools are checked with the same
    /// code as the quick summary's filters, so it cannot search with a field or value the dataset lacks.
    /// Each tool call is shown to the visitor as a step (event <c>status</c>) while it works.
    /// </summary>
    public sealed partial class SearchOrAskService
    {
        private const int AgentMaxTurns = 6;
        private const int AgentRecordsPerSearch = 30;

        private static readonly JsonArray AgentTools = (JsonArray)JsonNode.Parse("""
            [
              {"type": "function", "function": {
                "name": "search",
                "description": "Search the dataset with filters, words and an order. Returns how many match, a summary of all of them (ranges, common values, counts) and the first records' facts. Leave text empty to list everything the filters match; words narrow the list to records containing them.",
                "parameters": {"type": "object", "properties": {
                  "filters": {"type": "array", "items": {"type": "object", "properties": {
                    "field": {"type": "string"}, "value": {"type": "string"}, "min": {"type": "number"}, "max": {"type": "number"}},
                    "required": ["field"]}},
                  "text": {"type": "string"},
                  "sort": {"type": "object", "properties": {"field": {"type": "string"}, "descending": {"type": "boolean"}}, "required": ["field"]}},
                  "required": ["filters"]}}},
              {"type": "function", "function": {
                "name": "show_results",
                "description": "Put a list on the visitor's page, below your answer: the search that best answers them, with its order. Call it once, with the filters, text and sort of the list you are going to talk about, before you answer.",
                "parameters": {"type": "object", "properties": {
                  "filters": {"type": "array", "items": {"type": "object", "properties": {
                    "field": {"type": "string"}, "value": {"type": "string"}, "min": {"type": "number"}, "max": {"type": "number"}},
                    "required": ["field"]}},
                  "text": {"type": "string"},
                  "sort": {"type": "object", "properties": {"field": {"type": "string"}, "descending": {"type": "boolean"}}, "required": ["field"]}},
                  "required": ["filters"]}}}
            ]
            """)!;

        private sealed record AgentOutcome(string Outcome, List<long> Keys, long? FirstTextMs, double? Cost);

        /// <summary>Answers on a catalogue as an agent. Null when it could not (a model failure, out of
        /// turns before an answer): the quick summary then answers instead.</summary>
        private async Task<AgentOutcome?> AgentAnswerAsync(string teamId, string dataSetName, IServerSearchEngine engine,
            SearchOrAskSettings settings, SearchOrAskFields fields, string question, IReadOnlyList<AskTurn> history, string model,
            string? askId, Func<string, object, Task> send, long started, CancellationToken ct)
        {
            long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var schema = FilterFields(teamId, dataSetName, engine);
            if (schema.Count == 0) return null;
            var site = SiteDescription(teamId, dataSetName, settings);
            var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = AgentPrompt(site, settings.FilterNotes, schema) });
            // The conversation so far, with the list each answer put on the page: a follow-up ("which is
            // the largest?") is about that list, and searching it again starts from those filters.
            foreach (var t in history)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = t.Question });
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = t.Shown is { } shown ? $"{t.Answer}\n\n(The list I put on the page: {shown}.)" : t.Answer,
                });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"{question}\n\n{SearchOrAskRecipes.AnswerLanguageReminder}" });
            double cost = 0;
            List<long> shownKeys = [];
            var titleOf = (JsonNode? doc) => doc != null && fields.TitleField != null ? ReadField(doc, fields.TitleField) ?? "" : "";

            await send("status", new { step = "Reading the question" });
            for (var turn = 1; turn <= AgentMaxTurns; turn++)
            {
                JsonNode message;
                try
                {
                    (message, var c) = await _models.ChatAsync(model, messages, AgentTools, ct);
                    cost += c ?? 0;
                }
                catch (Exception ex) when (ex is SearchOrAskModelException or HttpRequestException)
                {
                    _logger.LogWarning("Search or ask agent failed for {Team}/{Dataset}: {Error}", teamId, dataSetName, ex.Message);
                    return null;
                }
                messages.Add(message.DeepClone());
                var calls = message["tool_calls"]?.AsArray();
                if (calls == null || calls.Count == 0)
                {
                    var answer = message["content"]?.GetValue<string>()?.Trim() ?? "";
                    if (answer.Length == 0) return null;
                    var firstText = Ms();
                    // The answer as a few deltas, so a page renders it as it does a streamed one.
                    foreach (var part in answer.Split("\n\n"))
                        await send("text", new { delta = part + "\n\n" });
                    await send("done", new { model, askId, latencyMs = Ms(), turns = turn });
                    return new AgentOutcome("answered", shownKeys, firstText, cost);
                }

                foreach (var call in calls)
                {
                    var name = call!["function"]?["name"]?.GetValue<string>();
                    var argsJson = call["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                    // The arguments go through the same check as the quick summary's filters.
                    var (filters, text, sort) = ParseFilters(argsJson, schema);
                    var (used, keys) = RunFiltered(engine, teamId, dataSetName, filters, text, sort, CatalogueFound);
                    string result;
                    if (name == "show_results")
                    {
                        await send("status", new { step = $"Showing {Count(keys)} on the page" });
                        await send("filters", new { filters = used, text, sort, found = keys.Count, more = keys.Count >= CatalogueFound });
                        shownKeys = keys.Take(CatalogueShown).ToList();
                        var shown = shownKeys.Select((k, i) => new AskSource(i + 1, k, titleOf(Parse(engine.GetJsonDataOfKey(k))))).ToArray();
                        await send("sources", shown);
                        result = $"The page now shows these {Count(keys)}, in that order.";
                    }
                    else
                    {
                        await send("status", new { step = $"Searching: {Step(used, text, sort)}" });
                        var docs = keys.Select(k => Parse(engine.GetJsonDataOfKey(k))).ToList();
                        var sb = new StringBuilder();
                        sb.AppendLine($"Searched with: {(used.Count > 0 ? string.Join("; ", used.Select(Describe)) : "no filters")}{(text.Length > 0 ? $"; words \"{text}\"" : "")}{(sort != null ? $"; sorted by {sort.Field} {(sort.Descending ? "descending" : "ascending")}" : "")}.");
                        if (filters.Length < (JsonNode.Parse(argsJson)?["filters"]?.AsArray().Count ?? 0))
                            sb.AppendLine("Some filters were dropped: their field is not a filter field, or their value does not occur.");
                        sb.AppendLine($"Found: {Count(keys)}.");
                        if (keys.Count > 0)
                        {
                            sb.AppendLine("Summary of all found:");
                            foreach (var line in Summary(docs, schema.Fields)) sb.AppendLine($"- {line}");
                            sb.AppendLine($"The first {Math.Min(AgentRecordsPerSearch, docs.Count)}:");
                            foreach (var doc in docs.Take(AgentRecordsPerSearch))
                                sb.AppendLine($"- {titleOf(doc)}: {Facts(doc, schema.Fields)}");
                        }
                        result = sb.ToString();
                    }
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call["id"]?.GetValue<string>() ?? "",
                        ["content"] = result,
                    });
                }
            }
            _logger.LogInformation("Search or ask agent ran out of turns for {Team}/{Dataset}", teamId, dataSetName);
            return null;
        }

        private static string Count(List<long> keys) => keys.Count >= CatalogueFound ? $"{CatalogueFound} or more" : keys.Count.ToString();

        /// <summary>A search as a step the visitor reads: the values, not the field names.</summary>
        private static string Step(List<FilterSuggestion> used, string text, SortSuggestion? sort)
        {
            var parts = used.Select(f => f.Value is "true" ? f.Field.Replace("Er", "") : f.Value ?? $"{f.Field} {f.Min}–{f.Max}").ToList();
            if (text.Length > 0) parts.Add($"\"{text}\"");
            if (sort != null) parts.Add($"by {sort.Field}");
            return parts.Count == 0 ? "everything" : string.Join(", ", parts);
        }

        private static string AgentPrompt(string site, string? notes, FilterSchema schema)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"""
                You answer a visitor who typed into the search field of a website. About the site: {site}

                You can search its records with the search tool, as often as you need, and you put the list that
                answers the visitor on their page with show_results, once, before you answer. Search for what they
                described with filters; when a search finds nothing or too little, try what is nearest (a wider
                place, a looser condition) and say what you did. When the results mix kinds the visitor would want
                apart (an arts school among ordinary schools), notice it, and filter it in or out as they meant.
                """);
            sb.AppendLine("The fields a filter may use, with the values they hold:");
            foreach (var f in schema.Fields)
            {
                sb.Append($"- {f.Name} ({f.Type})");
                if (f.Values != null) sb.Append($": {string.Join(" | ", f.Values)}");
                else if (f.Min != null) sb.Append($": {f.Min} to {f.Max}");
                sb.AppendLine();
            }
            if (schema.Sortable.Count > 0) sb.AppendLine($"Sortable: {string.Join(", ", schema.Sortable)}.");
            if (!string.IsNullOrWhiteSpace(notes)) sb.AppendLine().AppendLine(notes.Trim());
            sb.AppendLine("""

                A filter is {"field", "value"} with a listed value (or true/false), or {"field", "min", "max"} for a number.
                Then answer, as a short answer the visitor can act on: lead with how many you found, in their words for
                what they asked; then what stands out (what most share, the largest and smallest, notable differences),
                naming a few; if many, end with one sentence on how to narrow it down. Refer to the list on their page
                rather than repeating all of it. Use only what your searches returned. Field names are internal: say
                "private" or "pupils", never ErPrivatskole or Elevtall. Markdown is rendered. Do not restate the
                question, and do not close with an offer of more help.
                """);
            return sb.ToString();
        }
    }
}
