using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Indx.Api;
using IndxServer.Engine;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// A catalogue's answer with <c>answers: best</c>: an agent that searches until it has what the
    /// visitor asked for, then puts its picks on the page and says what it found. The picks are
    /// records it chose by reading them (9 Nov 2026), not a search the page runs: "find queer
    /// movies" put a search for the word on the page, which found one such film and four about
    /// queens, since no field of a film catalogue holds the property and only reading can tell. Measured against
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

        /// <summary>How much of a turn's text is held back before the list is shown, in case it is a
        /// preamble to a search rather than the answer: about a sentence, a few tenths of a second.</summary>
        private const int PreambleChars = 120;

        private static readonly JsonArray AgentTools = (JsonArray)JsonNode.Parse("""
            [
              {"type": "function", "function": {
                "name": "search",
                "description": "Search the dataset with filters, words and an order. Returns how many match and how their words matched, a summary of all of them (ranges, common values, counts) and the first records' facts. Leave text empty to list everything the filters match; words narrow the list to records holding every one of them.",
                "parameters": {"type": "object", "properties": {
                  "filters": {"type": "array", "items": {"type": "object", "properties": {
                    "field": {"type": "string"}, "value": {"type": "string"}, "min": {"type": "number"}, "max": {"type": "number"}},
                    "required": ["field"]}},
                  "filter": {"type": "string", "description": "A filter expression, for what the filters list cannot say, ANDed with it: field = value, !=, <, <=, >, >=, field IN [v1, v2], field BETWEEN a AND b, combined with AND, OR, NOT and parentheses. Text in double quotes, numbers bare, a field name with spaces in backticks. Example: genre IN [\"Drama\", \"Crime\"] AND NOT year < 1990"},
                  "text": {"type": "string"},
                  "broaden": {"type": "boolean", "description": "Only after a search with words found nothing: the records nearest to the words, said to be nearest, not matches."},
                  "sort": {"type": "object", "properties": {"field": {"type": "string"}, "descending": {"type": "boolean"}}, "required": ["field"]}},
                  "required": ["filters"]}}},
              {"type": "function", "function": {
                "name": "read_records",
                "description": "Read records in full, every field, by the keys your searches returned: what a book is about, a film's whole plot. Several keys in one call, at most 10.",
                "parameters": {"type": "object", "properties": {
                  "keys": {"type": "array", "items": {"type": "integer"}}},
                  "required": ["keys"]}}},
              {"type": "function", "function": {
                "name": "read_cold",
                "description": "Read the text kept beside records but not in them, such as a book's table of contents or a review: a record's line in the search results names what it has (cold: toc, review). Several keys in one call, at most 5; name what you want, or get all. Slower than a search: read it when the answer depends on it.",
                "parameters": {"type": "object", "properties": {
                  "keys": {"type": "array", "items": {"type": "integer"}},
                  "names": {"type": "array", "items": {"type": "string"}}},
                  "required": ["keys"]}}},
              {"type": "function", "function": {
                "name": "show_picks",
                "description": "Put your picks on the visitor's page, as its result list: the records that answer them, in the order you recommend them, each by the key in brackets from your searches and with a short reason. Call it once, in the message that answers: write your answer as that message's text and call this beside it. Cite pick n as [n], n being its place in picks. When the visitor asked for everything a search states exactly (every private school in a town), also give that search, so the page can offer all it found.",
                "parameters": {"type": "object", "properties": {
                  "picks": {"type": "array", "items": {"type": "object", "properties": {
                    "key": {"type": "integer"}, "why": {"type": "string"}},
                    "required": ["key", "why"]}},
                  "search": {"type": "object", "properties": {
                    "filters": {"type": "array", "items": {"type": "object", "properties": {
                      "field": {"type": "string"}, "value": {"type": "string"}, "min": {"type": "number"}, "max": {"type": "number"}},
                      "required": ["field"]}},
                    "text": {"type": "string"},
                    "sort": {"type": "object", "properties": {"field": {"type": "string"}, "descending": {"type": "boolean"}}, "required": ["field"]}},
                    "required": ["filters"]}},
                  "required": ["picks"]}}}
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
            var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = AgentPrompt(site, settings.FilterNotes, schema) + SearchOrAskRecipes.AnswerStyle(settings) + AgentLanguage(settings) });
            // The conversation so far, with the list each answer put on the page: a follow-up ("which is
            // the largest?") is about that list, and searching it again starts from those filters.
            foreach (var t in history)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = t.Question });
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = t.Shown is { } shown ? $"{t.Answer}\n\n(What I put on the page: {shown}.)" : t.Answer,
                });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"{question}\n\n{SearchOrAskRecipes.LanguageReminder(settings)}" });
            double cost = 0;
            List<long> shownKeys = [];
            // Every record the agent has read, by key: a pick must be one of them, so a key it made
            // up (or misread) never puts a stranger on the page.
            var read = new Dictionary<long, string>();
            // Every record its searches found, read or not: read_records reads only these.
            var found = new HashSet<long>();
            var titleOf = (JsonNode? doc) => doc != null && fields.TitleField != null ? ReadField(doc, fields.TitleField) ?? "" : "";

            // The agent answers in the message that puts its list on the page (show_results), so
            // the answer needs no turn of its own: one model call fewer, a few seconds of Sonnet's
            // reading the conversation again. Every turn streams, the first included: the list can
            // come without a search before it. Text in a turn that goes on to search is a preamble ("Let me look wider"),
            // and a turn cannot be told from an answer until it ends, so until the list is shown the
            // first PreambleChars are held back: a preamble is one short sentence, and a turn that
            // ends in a search drops what it held. A model that still answers in a turn of its own,
            // after show_results, is streamed from its first word.
            var listShown = false;
            var streamed = new StringBuilder();
            StringBuilder? held = null;
            var breakBefore = false;
            long? firstText = null;
            async Task Emit(string delta)
            {
                firstText ??= Ms();
                if (breakBefore) { delta = "\n\n" + delta; breakBefore = false; }
                streamed.Append(delta);
                await send("text", new { delta });
            }
            async Task Stream(string delta)
            {
                if (held != null)
                {
                    held.Append(delta);
                    if (held.Length < PreambleChars) return;
                    delta = held.ToString();
                    held = null;
                }
                await Emit(delta);
            }

            // Each step as a sentence (step) and as its parts (kind, and a search's or the picks'), so a
            // page can say it in its own language and words (13 Nov 2026: a page had to parse the
            // sentence to translate it).
            await send("status", new { step = "Reading the question", kind = "read" });
            for (var turn = 1; turn <= AgentMaxTurns; turn++)
            {
                held = listShown ? null : new StringBuilder();
                JsonNode message;
                try
                {
                    (message, var c) = await _models.ChatAsync(model, messages, AgentTools, ct, Stream);
                    cost += c ?? 0;
                }
                catch (Exception ex) when (ex is SearchOrAskModelException or HttpRequestException)
                {
                    _logger.LogWarning("Search or ask agent failed for {Team}/{Dataset}: {Error}", teamId, dataSetName, ex.Message);
                    Refused("answer", ex);
                    if (streamed.Length == 0) return null;
                    // Part of the answer is on the page: a second answer after it would read as one.
                    await send("error", new { code = ex is SearchOrAskModelException { Status: 429 } ? "upstreamBusy" : "failed", message = "The answer could not be written." });
                    return new AgentOutcome("failed", shownKeys, firstText, cost);
                }
                messages.Add(message.DeepClone());
                var calls = message["tool_calls"]?.AsArray() ?? [];
                var answer = message["content"]?.GetValue<string>()?.Trim() ?? "";
                // The last turn: no call, or the list with the answer beside it.
                var last = calls.Count == 0
                    || answer.Length > 0 && calls.All(call => call?["function"]?["name"]?.GetValue<string>() == "show_picks");
                if (!last)
                {
                    held = null;
                    // Text streamed in a turn that went on to search again: what comes next is its own paragraph.
                    if (streamed.Length > 0) breakBefore = true;
                }
                if (last && answer.Length == 0 && streamed.Length == 0) return null;

                foreach (var call in calls)
                {
                    var name = call!["function"]?["name"]?.GetValue<string>();
                    var argsJson = call["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                    string result;
                    // A model can send arguments that are not JSON ('7' is an invalid end of a number,
                    // gdagency, 11 Nov 2026); parsed below unguarded, that ended the console's circuit
                    // with the answer left loading. Said to the model instead, which calls again.
                    if (ArgumentsError(argsJson) is { } argumentsError)
                        result = $"Your call to {name} was not run: its arguments are not valid JSON ({argumentsError}). Call it again with valid JSON.";
                    else if (name == "show_picks")
                    {
                        var (picks, dropped) = ParsePicks(argsJson, read);
                        // The search behind the picks, when the visitor asked for all of something: the
                        // page offers what it found beside the picks. Checked as every search is.
                        object? search = null;
                        var searchJson = JsonNode.Parse(argsJson)?["search"]?.ToJsonString();
                        if (searchJson != null)
                        {
                            var (sf, st, ss) = ParseFilters(searchJson, schema);
                            var (sUsed, sKeys) = RunFiltered(engine, teamId, dataSetName, sf, st, ss, CatalogueFound);
                            search = new { filters = sUsed, text = st, sort = ss, found = sKeys.Count, more = sKeys.Count >= CatalogueFound };
                        }
                        // Under an answer already on its way, the step would only flicker.
                        if (streamed.Length == 0) await send("status", new { step = $"Picked {picks.Count}", kind = "pick", count = picks.Count });
                        await send("picks", new { picks, search });
                        shownKeys = picks.Select(p => p.DocumentKey).ToList();
                        await send("sources", picks.Select(p => new AskSource(p.N, p.DocumentKey, p.Title)).ToArray());
                        listShown = true;
                        result = $"The page now lists your {picks.Count} picks, numbered in that order."
                            + (dropped > 0 ? $" {dropped} were left out: their key was not one your searches returned." : "");
                    }
                    else if (name == "read_records")
                        result = ReadRecords(engine, argsJson, found, read, titleOf);
                    else if (name == "read_cold")
                        result = ReadCold(teamId, dataSetName, argsJson, found, read);
                    else
                    {
                        // The arguments go through the same check as the quick summary's filters.
                        var (filters, text, sort) = ParseFilters(argsJson, schema);
                        var args = JsonNode.Parse(argsJson);
                        var expression = args?["filter"]?.ToString();
                        var broaden = args?["broaden"] is JsonValue b && b.TryGetValue<bool>(out var bv) && bv;
                        var (used, expressionError, searched) = RunSearch(engine, teamId, dataSetName, filters, expression, text, sort, CatalogueFound, broaden);
                        var keys = KeysOf(searched);
                        found.UnionWith(keys);
                        await send("status", new
                        {
                            step = $"Searching: {Step(used, text, sort)}",
                            kind = "search",
                            search = new
                            {
                                filters = used, filter = string.IsNullOrWhiteSpace(expression) ? null : expression, text, sort,
                                found = keys.Count, more = keys.Count >= CatalogueFound,
                            },
                        });
                        var docs = keys.Select(k => Parse(engine.GetJsonDataOfKey(k))).ToList();
                        var sb = new StringBuilder();
                        if (expressionError != null)
                            sb.AppendLine($"Not searched: the filter expression makes no filter. {expressionError} Fields must be filter fields, values as they are listed.");
                        sb.AppendLine($"Searched with: {(used.Count > 0 ? string.Join("; ", used.Select(Describe)) : "no filters")}{(string.IsNullOrWhiteSpace(expression) ? "" : $"; filter {expression}")}{(text.Length > 0 ? $"; words \"{text}\"" : "")}{(sort != null ? $"; sorted by {sort.Field} {(sort.Descending ? "descending" : "ascending")}" : "")}.");
                        if (filters.Length < (args?["filters"]?.AsArray().Count ?? 0))
                            sb.AppendLine("Some filters were dropped: their field is not a filter field, or their value does not occur.");
                        sb.AppendLine($"Found: {Count(keys)}.{(text.Length > 0 && searched != null ? " " + HowMatched(searched) : "")}");
                        if (keys.Count > 0)
                        {
                            sb.AppendLine("Summary of all found:");
                            foreach (var line in Summary(docs, schema.Fields)) sb.AppendLine($"- {line}");
                            sb.AppendLine($"The first {Math.Min(AgentRecordsPerSearch, docs.Count)}:");
                            var shownKeysOfSearch = keys.Take(AgentRecordsPerSearch).ToList();
                            // The cold storage each holds that an answer may read, for read_cold.
                            var cold = IndxServerInternalApi.Manager.Cold?.NamesOf(teamId, dataSetName, shownKeysOfSearch, ColdAccess.Answer) ?? [];
                            foreach (var (doc, key) in docs.Zip(keys).Take(AgentRecordsPerSearch))
                            {
                                read[key] = titleOf(doc);
                                var coldNote = cold.TryGetValue(key, out var coldNames) ? $" (cold: {string.Join(", ", coldNames)})" : "";
                                sb.AppendLine($"- {titleOf(doc)} [{key}]: {Facts(doc, schema.Fields, schema.Readable)}{coldNote}");
                            }
                        }
                        result = sb.ToString();
                    }
                    // The language once more after the round's last result: what the agent reads last
                    // before it writes is records, and records in other languages pulled it along.
                    if (call == calls[^1]) result += $"\n\n({LanguageAfterResults(settings, question)})";
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call["id"]?.GetValue<string>() ?? "",
                        ["content"] = result,
                    });
                }
                if (!last) continue;

                if (held is { Length: > 0 }) await Emit(held.ToString());
                Succeeded("answer");
                await send("done", new { model, askId, latencyMs = Ms(), turns = turn });
                return new AgentOutcome("answered", shownKeys, firstText, cost);
            }
            _logger.LogInformation("Search or ask agent ran out of turns for {Team}/{Dataset}", teamId, dataSetName);
            if (streamed.Length == 0) return null;
            Succeeded("answer");
            await send("done", new { model, askId, latencyMs = Ms(), turns = AgentMaxTurns });
            return new AgentOutcome("answered", shownKeys, firstText, cost);
        }

        /// <summary>How the words of a search matched, so the agent knows what it holds: every word
        /// exactly, within one edit or two (the rung v6 counted), or, broadened, the nearest records,
        /// which are not matches. <see cref="Result.IsFallback"/> is set whenever nothing was counted.</summary>
        private static string HowMatched(Result result) => result switch
        {
            { IsFallback: true, Records.Length: > 0 } => "No record holds every word: these are the nearest, not matches.",
            { IsFallback: true } => "No record holds every word.",
            { CountedTolerance: Tolerance.Exact } => "Every word matched exactly.",
            { CountedTolerance: Tolerance.OneEdit } => "No record holds every word exactly: these hold them within one letter's difference.",
            { CountedTolerance: { } } => "No record holds every word exactly: these hold them within two or more letters' difference, so check they mean what was asked.",
            _ => "",
        };

        /// <summary>The most keys a read_records call reads, and the characters of each record's JSON.</summary>
        private const int AgentReadKeys = 10, AgentReadChars = 4000;

        /// <summary>A read_records call: the records' JSON in full, those of the keys the searches found,
        /// at most <see cref="AgentReadKeys"/>, each cut at <see cref="AgentReadChars"/>. A record read
        /// may be picked.</summary>
        private static string ReadRecords(IServerSearchEngine engine, string argsJson, HashSet<long> found,
            Dictionary<long, string> read, Func<JsonNode?, string> titleOf)
        {
            var asked = (JsonNode.Parse(argsJson)?["keys"]?.AsArray() ?? [])
                .Select(n => long.TryParse(n?.ToString(), out var k) ? k : (long?)null).OfType<long>().Distinct().ToList();
            var keys = asked.Where(found.Contains).Take(AgentReadKeys).ToArray();
            var sb = new StringBuilder();
            if (keys.Length > 0)
            {
                foreach (var (key, json) in keys.Zip(engine.GetJsonDataOfKeys(keys)))
                {
                    if (string.IsNullOrEmpty(json)) { sb.AppendLine($"[{key}]: no longer in the dataset."); continue; }
                    read[key] = titleOf(Parse(json));
                    sb.AppendLine($"[{key}]: {(json.Length > AgentReadChars ? json[..AgentReadChars] + " (cut)" : json)}");
                }
            }
            var unknown = asked.Count(k => !found.Contains(k));
            if (unknown > 0) sb.AppendLine($"{unknown} keys were not read: they are not from your searches.");
            if (asked.Count(found.Contains) > AgentReadKeys) sb.AppendLine($"Only the first {AgentReadKeys} were read.");
            return sb.Length > 0 ? sb.ToString() : "No keys to read.";
        }

        /// <summary>The most keys a read_cold call reads, the characters of one item and of the whole.</summary>
        private const int AgentColdKeys = 5, AgentColdItemChars = 6000, AgentColdChars = 20000;

        /// <summary>A read_cold call: the cold storage an answer may read (public and answer, never
        /// private: what the agent reads can reach the visitor) of the keys the searches found, one item
        /// per name, the one without a language first. Cut per item and in all.</summary>
        private static string ReadCold(string teamId, string dataSetName, string argsJson, HashSet<long> found, Dictionary<long, string> read)
        {
            if (IndxServerInternalApi.Manager.Cold is not { } store) return "This dataset has no cold storage.";
            var args = JsonNode.Parse(argsJson);
            var asked = (args?["keys"]?.AsArray() ?? [])
                .Select(n => long.TryParse(n?.ToString(), out var k) ? k : (long?)null).OfType<long>().Distinct().ToList();
            var names = (args?["names"]?.AsArray() ?? []).Select(n => n?.ToString()).OfType<string>().ToList();
            var sb = new StringBuilder();
            var budget = AgentColdChars;
            foreach (var key in asked.Where(found.Contains).Take(AgentColdKeys))
            {
                var items = store.List(new ColdScope(teamId, dataSetName, key), withContent: true, ColdAccess.Answer, names)
                    .GroupBy(i => i.Name).Select(g => g.FirstOrDefault(i => i.Language.Length == 0) ?? g.First()).ToList();
                var title = read.GetValueOrDefault(key, "");
                if (items.Count == 0) { sb.AppendLine($"[{key}] {title}: no cold storage{(names.Count > 0 ? " by those names" : "")}."); continue; }
                foreach (var item in items)
                {
                    var take = Math.Min(Math.Min(item.Content.Length, AgentColdItemChars), Math.Max(0, budget));
                    budget -= take;
                    var about = item.Description.Length > 0 ? $" ({item.Description})" : "";
                    sb.AppendLine($"[{key}] {title}, {item.Name}{about}:");
                    sb.AppendLine(take < item.Content.Length ? item.Content[..take] + " (cut)" : item.Content);
                }
            }
            var unknown = asked.Count(k => !found.Contains(k));
            if (unknown > 0) sb.AppendLine($"{unknown} keys were not read: they are not from your searches.");
            if (asked.Count(found.Contains) > AgentColdKeys) sb.AppendLine($"Only the first {AgentColdKeys} were read.");
            return sb.Length > 0 ? sb.ToString() : "No keys to read.";
        }

        /// <summary>The answer's language in the agent's own instructions. The reminder after the
        /// question was the only one, and the searches came after it: "whats the oldest movie in the
        /// set?" was answered once in Spanish and once in German (20 Oct 2026), the oldest films being
        /// silent European ones whose titles and language fields the agent had just read.</summary>
        private static string AgentLanguage(SearchOrAskSettings settings) =>
            string.IsNullOrWhiteSpace(settings.AnswerLanguage)
                ? "\n\nWrite the answer in the language of the visitor's question. The records you read do not change it: a title, a language field or a description in another language is a fact about a record, not the language to answer in."
                : "";

        /// <summary>The note after a round of search results, naming the question so its language is
        /// the last thing read before the answer.</summary>
        private static string LanguageAfterResults(SearchOrAskSettings settings, string question) =>
            string.IsNullOrWhiteSpace(settings.AnswerLanguage)
                ? $"When you answer, write in the language of the visitor's question: \"{question}\"."
                : $"When you answer, write in {settings.AnswerLanguage.Trim()}.";

        /// <summary>A pick on the page: its place (as the answer cites it), the record, and why.</summary>
        private sealed record AgentPick(int N, long DocumentKey, string Title, string Why);

        /// <summary>The most picks a page lists.</summary>
        private const int MaxPicks = CatalogueShown;

        /// <summary>Why <paramref name="argsJson"/> is not JSON, or null when it is.</summary>
        private static string? ArgumentsError(string argsJson)
        {
            try { JsonNode.Parse(argsJson); return null; }
            catch (JsonException ex) { return ex.Message; }
        }

        /// <summary>The picks of a show_picks call, in its order, each a record the agent read; how many
        /// it named that it had not read (or twice), which are left out.</summary>
        private static (List<AgentPick> Picks, int Dropped) ParsePicks(string argsJson, IReadOnlyDictionary<long, string> read)
        {
            var picks = new List<AgentPick>();
            var dropped = 0;
            JsonArray items;
            try { items = JsonNode.Parse(argsJson)?["picks"]?.AsArray() ?? []; }
            catch (JsonException) { return (picks, 0); }
            foreach (var item in items)
            {
                long.TryParse(item?["key"]?.ToString(), out var key);
                if (!read.TryGetValue(key, out var title) || picks.Any(p => p.DocumentKey == key) || picks.Count >= MaxPicks)
                {
                    dropped++;
                    continue;
                }
                picks.Add(new AgentPick(picks.Count + 1, key, title, (item?["why"]?.ToString() ?? "").Trim()));
            }
            return (picks, dropped);
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

                You can search its records with the search tool, as often as you need. Each record it returns has its
                key in brackets. Search results give each record's facts, not all of it: when the answer depends on what
                a record says (what a book is about, where a film is set), read it with read_records before you pick it.
                A record whose line ends in (cold: ...) has more text beside it, such as a table of contents; read it
                with read_cold when the question is about what that holds. Write no text in a message that searches. When you know the answer, write it in one
                message that also calls show_picks, once, with the records that answer the visitor, best first: the
                page lists your picks as its results, so pick what you would recommend by reading the records, not
                what merely contains their words. Give each a reason of a few words in the visitor's language. Search for what they
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
            // Too many values to list as filters, but in every record found: a question about one
            // is answered by searching its words ("george miller") and reading the records.
            if (schema.Readable.Count > 0)
                sb.AppendLine($"Also in every record found, though not a filter: {string.Join(", ", schema.Readable)}. " +
                              "To find records by one of these, search its words; to answer about one, read it from the records.");
            if (!string.IsNullOrWhiteSpace(notes)) sb.AppendLine().AppendLine(notes.Trim());
            sb.AppendLine("""

                A filter is {"field", "value"} with a listed value (or true/false), or {"field", "min", "max"} for a number.
                For one of several values, an exclusion or an open range, use the filter expression instead.
                Words with no record holding every one of them find nothing; then try other words, or broaden.
                Then answer, as a short answer the visitor can act on: lead with how many you found, in their words for
                what they asked; then what stands out (what most share, the largest and smallest, notable differences),
                naming a few, each cited as [n] by its place in your picks; if many, end with one sentence on how to
                narrow it down. Refer to the picks on their page rather than repeating all of them. Use only what your
                searches returned. Field names are internal: say
                "private" or "pupils", never ErPrivatskole or Elevtall. Markdown is rendered. Do not restate the
                question, and do not close with an offer of more help.
                """);
            return sb.ToString();
        }
    }
}
