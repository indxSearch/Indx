using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Indx.Api;
using Indx.Http;
using IndxServer.Engine;

namespace IndxServer.Services.Observr
{
    /// <summary>Where a conversation is: the team, and the dataset when one is open.</summary>
    public sealed record ObservrChatContext(string TeamId, string TeamName, string? DataSetName, string? Tab)
    {
        public bool HasDataset => !string.IsNullOrEmpty(DataSetName);
        /// <summary>The store key: a team's conversation keeps an empty dataset name.</summary>
        public string DatasetKey => DataSetName ?? "";
    }

    /// <summary>A tool as it goes to Observr: its name, what it does, its input as a JSON schema.</summary>
    public sealed record ObservrChatTool(string Name, string Description, JsonElement InputSchema);

    /// <summary>
    /// What the Agent can see (Notes/Anders/console-shell-plan-2026-10-13.md, stage 5): the tools
    /// are defined here, on the instance, so what Observr reads is enumerable, and each reads one
    /// thing the console already shows. Nothing here writes; a change goes through a proposal and
    /// a Yes. Searches run here never reach the statistics, and nothing wakes a sleeping dataset:
    /// a tool on one says it is asleep.
    /// </summary>
    internal sealed class ObservrTools(IDatasetEngines engines, StatisticsService statistics, DatasetMetadataStore metadata,
        ObservrFindingStore findings, QueryParameterStore queryParameters, SearchOrAsk.SearchOrAskStore searchOrAsk, ObservrClient client,
        SearchOrAsk.SearchOrAskService searchOrAskService)
    {
        private const int SearchMax = 20;
        private const int Top = 15;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

        private static JsonElement Schema(object o) => JsonSerializer.SerializeToElement(o);

        /// <summary>The tools of a context: a dataset's, or a team's.</summary>
        public List<ObservrChatTool> Definitions(ObservrChatContext ctx)
        {
            var tools = new List<ObservrChatTool>
            {
                new("findings", "What Observr found and proposed earlier here, newest first, with each one's status (open, accepted, declined).",
                    Schema(new { type = "object", properties = new { limit = new { type = "integer", description = "How many, up to 30." } } })),
            };
            if (!ctx.HasDataset)
            {
                tools.Add(new("datasets", "The team's datasets: each one's state (ready, hibernated, created), how many records it holds, and whether Observr watches it.",
                    Schema(new { type = "object", properties = new { } })));
                return tools;
            }
            tools.AddRange(
            [
                new("search", "Search the dataset as a visitor would, and see how many records match and the first ones by name. Use it to check what a query finds, or what a word the visitor used finds.",
                    Schema(new { type = "object", properties = new { text = new { type = "string", description = "The search text." }, take = new { type = "integer", description = "Names to return, up to 20." } }, required = new[] { "text" } })),
                new("search_or_ask", "Type a text into the dataset's search box as a visitor would, with Search or ask: what the list searches (the Ask phrases taken off a question), the first results by name, and the judge's reading of it (a question, a description, how answerable from these results) with the offer the visitor would get. To try Ask phrases before proposing them, give them here: they apply to this try only, nothing is saved. Each call is one judge call against the dataset's daily budget, and none reaches the statistics.",
                    Schema(new
                    {
                        type = "object",
                        properties = new
                        {
                            text = new { type = "string", description = "What the visitor types." },
                            try_lead_ins = new { type = "array", items = new { type = "string" }, description = "Lead-ins to try on top of the list in effect, for this try only." },
                            try_frames = new { type = "array", items = new { type = "string" }, description = "What visitors call a record (\"the movie\"), to try on top of the list in effect, for this try only." },
                        },
                        required = new[] { "text" },
                    })),
                new("statistics", "The dataset's search statistics for a period: totals, the top queries, the queries that found nothing, the ones with the lowest click-through, the fuzzy finds, the most chosen records, and what changed. Each list is capped, so a query missing from them was not necessarily never searched: look it up with the query tool.",
                    Schema(new { type = "object", properties = new { days = new { type = "integer", description = "The period in days ending today: 7, 30 or 90." } } })),
                new("query", "How often one exact query text was searched in a period, how often it found nothing, how often a result was chosen, and which records were chosen. The answer to \"how often did visitors search for X\", including a query searched only once.",
                    Schema(new { type = "object", properties = new { text = new { type = "string", description = "The query text, as the visitor typed it." }, days = new { type = "integer", description = "The period in days ending today: 7, 30 or 90." } }, required = new[] { "text" } })),
                new("fields", "The fields and their roles (searchable, filterable, facetable, sortable, weight), with what a sample of the records holds in each: fill, distinct values, the commonest values, a number's range.",
                    Schema(new { type = "object", properties = new { } })),
                new("synonyms", "The dataset's synonym list.", Schema(new { type = "object", properties = new { } })),
                new("settings", "The dataset's settings: its description, its query parameters (coverage depth, truncation) and its Search or ask settings.",
                    Schema(new { type = "object", properties = new { } })),
                new("zero_hit_evidence", "For a query that found nothing: what the same search finds with pattern matching on, and what each of its words finds alone. The evidence for a synonym.",
                    Schema(new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } })),
            ]);
            return tools;
        }

        /// <summary>What the instance tells Observr before the conversation: where the owner is, and
        /// the facts that every answer needs. Short; the tools hold the rest.</summary>
        public string Describe(ObservrChatContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Team: {ctx.TeamName}.");
            if (!ctx.HasDataset)
            {
                sb.AppendLine("No dataset is open: the owner is on the team's pages.");
                sb.AppendLine($"The team's datasets: {string.Join(", ", engines.GetTeamDataSets(ctx.TeamId))}.");
                return sb.ToString();
            }
            var ds = ctx.DataSetName!;
            sb.AppendLine($"Dataset: {ds}." + (ctx.Tab != null ? $" The owner is on its {ctx.Tab} page." : ""));
            var site = metadata.Load(ctx.TeamId, ds);
            if (!string.IsNullOrWhiteSpace(site)) sb.AppendLine($"About the site, in the owner's words: {site.Trim()}");
            var engine = engines.FindSearchEngine(ds, ctx.TeamId);
            if (engine != null && engine.Status.SystemState == SystemState.Ready)
            {
                sb.AppendLine($"The dataset is loaded: {engine.Status.DocumentCount:N0} records.");
                sb.AppendLine($"Fields: {string.Join(", ", engine.GetFieldList().Select(f => f.Name + (f.Searchable ? " (searched)" : "")))}.");
            }
            else
            {
                var ka = engines.GetKeepAliveInfo(ds, ctx.TeamId);
                sb.AppendLine(ka.RecordCount > 0
                    ? $"The dataset is hibernated: {ka.RecordCount:N0} records on disk, not loaded. Searching and the field sample need it awake; the statistics and the settings do not."
                    : "The dataset has no records yet.");
            }
            sb.AppendLine($"Observr watches this dataset: {(findings.IsEnabled(ctx.TeamId, ds) ? "yes" : "no")}.");
            return sb.ToString();
        }

        /// <summary>The field names a proposal may name, for the propose tool's check.</summary>
        public List<string> FieldNames(ObservrChatContext ctx)
        {
            if (!ctx.HasDataset) return [];
            var engine = engines.FindSearchEngine(ctx.DataSetName!, ctx.TeamId);
            return engine == null ? [] : engine.GetFieldList().Select(f => f.Name).ToList();
        }

        /// <summary>Runs one tool and returns what the model reads. Never throws: a tool that cannot
        /// run says why, so the model can say so too.</summary>
        // Numbers the model reads are written as English writes them, whatever the server's culture
        // (a Norwegian machine would otherwise say "0,90").
        static string N(double v, string format = "0.00") => v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

        public string Run(string name, Dictionary<string, JsonElement> input, ObservrChatContext ctx)
        {
            try
            {
                return name switch
                {
                    "findings" => Findings(ctx, Int(input, "limit", 10, 1, 30)),
                    "datasets" => Datasets(ctx),
                    "search" => Search(ctx, Text(input, "text"), Int(input, "take", 10, 1, SearchMax)),
                    "search_or_ask" => TrySearchOrAsk(ctx, Text(input, "text"), Strings(input, "try_lead_ins"), Strings(input, "try_frames")),
                    "statistics" => Statistics(ctx, Int(input, "days", 7, 1, 90)),
                    "query" => QueryStatistics(ctx, Text(input, "text"), Int(input, "days", 7, 1, 90)),
                    "fields" => Fields(ctx),
                    "synonyms" => Synonyms(ctx),
                    "settings" => Settings(ctx),
                    "zero_hit_evidence" => ZeroHit(ctx, Text(input, "query")),
                    _ => $"There is no tool called {name}.",
                };
            }
            catch (Exception ex)
            {
                return $"The tool {name} failed: {ex.Message}";
            }
        }

        private static string Text(Dictionary<string, JsonElement> input, string key) =>
            input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
        private static int Int(Dictionary<string, JsonElement> input, string key, int fallback, int min, int max) =>
            input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? Math.Clamp(n, min, max) : fallback;
        /// <summary>A list of short texts, at most ten.</summary>
        private static List<string> Strings(Dictionary<string, JsonElement> input, string key) =>
            input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() ?? "" : "").Where(x => x.Length > 0).Take(10).ToList()
                : [];

        private string Findings(ObservrChatContext ctx, int limit)
        {
            var list = ctx.HasDataset ? findings.ListForDataset(ctx.TeamId, ctx.DataSetName!, limit) : findings.List([ctx.TeamId], limit);
            if (list.Count == 0) return "Nothing found here yet.";
            return string.Join("\n", list.Select(f =>
                $"- {f.CreatedUtc:yyyy-MM-dd} {f.Title}{(ctx.HasDataset ? "" : $" ({f.DataSetName})")}: {f.Answer} [{f.Status}]" +
                (f.Proposals.Count > 0 ? $" Proposed: {string.Join("; ", f.Proposals.Select(p => p.Label + " " + string.Join(" ", p.Describe())))}." : "")));
        }

        private string Datasets(ObservrChatContext ctx)
        {
            var names = engines.GetTeamDataSets(ctx.TeamId);
            if (names.Count == 0) return "The team has no datasets.";
            var sb = new StringBuilder();
            foreach (var name in names)
            {
                var ka = engines.GetKeepAliveInfo(name, ctx.TeamId);
                var engine = engines.FindSearchEngine(name, ctx.TeamId);
                var state = engine != null && engine.Status.SystemState == SystemState.Ready ? $"ready, {engine.Status.DocumentCount:N0} records"
                    : ka.RecordCount > 0 ? $"hibernated, {ka.RecordCount:N0} records on disk" : "created, no records";
                sb.AppendLine($"- {name}: {state}; Observr {(findings.IsEnabled(ctx.TeamId, name) ? "on" : "off")}");
            }
            return sb.ToString();
        }

        private string Search(ObservrChatContext ctx, string text, int take)
        {
            if (text.Length == 0) return "The search text is empty.";
            var ds = ctx.DataSetName!;
            var engine = engines.FindSearchEngine(ds, ctx.TeamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready) return "The dataset is not loaded, so it cannot be searched now.";
            var label = StatisticsLabels.For(engine, statistics.Store?.LabelField(ctx.TeamId, ds));
            var result = IndxServerInternalApi.Manager.Search(new QueryProxy { Text = text, MaxNumberOfRecordsToReturn = take, EnableCoverage = true }, ds, ctx.TeamId);
            var records = result.Records ?? [];
            // The list a visitor sees: the truncation point is where the search stopped trusting
            // the matches, and what sits beyond it is what pattern matching found.
            var sb = new StringBuilder();
            if (records.Length == 0) sb.AppendLine($"\"{text}\" finds nothing.");
            else
            {
                var trusted = result.TruncationIndex > 0 && result.TruncationIndex < records.Length ? result.TruncationIndex : records.Length;
                sb.AppendLine($"\"{text}\": {records.Length} records returned (the first {take} asked for), {trusted} of them matches the search trusts; by name:");
                for (var i = 0; i < records.Length; i++)
                    sb.AppendLine($"- {label(records[i].DocumentKey) ?? records[i].DocumentKey.ToString()}{(i >= trusted ? " (beyond the truncation)" : "")}");
            }
            return sb.ToString();
        }

        /// <summary>A text typed into the dataset's search box with Search or ask, as the console's
        /// tester runs it: the trial settings (the stored ones, with any phrases to try on top), the
        /// refocus, the judge. Nothing is saved and nothing reaches the statistics.</summary>
        private string TrySearchOrAsk(ObservrChatContext ctx, string text, List<string> tryLeadIns, List<string> tryFrames)
        {
            if (text.Length == 0) return "The text is empty.";
            var ds = ctx.DataSetName!;
            var engine = engines.FindSearchEngine(ds, ctx.TeamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready) return "The dataset is not loaded, so it cannot be searched now.";
            if (!searchOrAskService.Available) return "Search or ask cannot run on this instance: it has no model key.";
            var stored = searchOrAsk.Load(ctx.TeamId, ds);
            var trial = JsonSerializer.Deserialize<SearchOrAsk.SearchOrAskSettings>(JsonSerializer.Serialize(stored ?? new SearchOrAsk.SearchOrAskSettings(), Json), Json)!;
            if (tryLeadIns.Count > 0 || tryFrames.Count > 0)
            {
                var basis = trial.AskPhrases ?? SearchOrAsk.SearchOrAskPhrases.Default;
                static List<string> With(List<string> have, List<string> add) =>
                    [.. have, .. add.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0 && !have.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct()];
                trial.AskPhrases = new SearchOrAsk.SearchOrAskPhrases
                {
                    LeadIns = With(basis.LeadIns, tryLeadIns), Frames = With(basis.Frames, tryFrames),
                    Links = [.. basis.Links], Articles = [.. basis.Articles],
                };
                if (trial.AskPhrases.Validate() is { } bad) return bad;
            }
            var (searched, taken) = trial.RefocusWhileTyping
                ? SearchOrAsk.SearchOrAskPhrases.Strip(text, trial.AskPhrases ?? SearchOrAsk.SearchOrAskPhrases.Default)
                : (text, (IReadOnlyList<string>)[]);

            System.Text.Json.Nodes.JsonObject? state = null;
            var outcome = searchOrAskService.IntentAsync(ctx.TeamId, ds, engine, text, "observr", CancellationToken.None,
                trial, s => state = s).GetAwaiter().GetResult();

            var sb = new StringBuilder();
            sb.AppendLine(taken.Count > 0
                ? $"\"{text}\": the list searches \"{searched}\" (Ask phrases taken off: {string.Join(" + ", taken.Select(t => $"\"{t}\""))})."
                : $"\"{text}\": the list searches it as typed{(trial.RefocusWhileTyping ? " (no Ask phrase matched)" : " (refocus while typing is off)")}.");
            if (state?["search"] is System.Text.Json.Nodes.JsonObject search)
            {
                var results = search["results"] as System.Text.Json.Nodes.JsonArray ?? [];
                sb.AppendLine($"It finds {search["hits"]} records{(search["closeMatch"]?.GetValue<bool>() == false ? ", none a close match" : "")}; the first {results.Count}, as the judge reads them:");
                foreach (var r in results)
                    sb.AppendLine($"- {r?["title"]}{(string.IsNullOrWhiteSpace(r?["section"]?.ToString()) ? "" : $" ({r?["section"]})")}");
            }
            if (outcome.Result is { } j)
            {
                var offer = j.Offer switch { "strong" => "the full offer to answer", "invite" => "the quieter \"Ask about this\"", _ => "no offer: the results and nothing more" };
                sb.AppendLine($"The judge: question {N(j.P)}{(j.Criteria is { } c ? $", description {N(c)}" : "")}{(j.Answerable is { } a ? $", answerable {N(a)}" : "")}{(j.Reason != null ? $" ({j.Reason})" : "")}. The visitor would get {offer}.");
            }
            else sb.AppendLine($"The judge did not answer: {outcome.Status}.");
            return sb.ToString();
        }

        private string Statistics(ObservrChatContext ctx, int days)
        {
            var store = statistics.Store;
            if (store == null || !statistics.Enabled) return "This instance keeps no statistics.";
            var ds = ctx.DataSetName!;
            var to = store.Today(ctx.TeamId, ds);
            var from = to - (days - 1);
            var engine = engines.FindSearchEngine(ds, ctx.TeamId);
            var label = engine != null && engine.Status.SystemState == SystemState.Ready ? StatisticsLabels.For(engine, store.LabelField(ctx.TeamId, ds)) : (Func<long, string?>)(_ => null);
            string Q(QueryStat q)
            {
                var chosen = q.MostChosenLabel ?? (q.MostChosenDocument is { } k ? label(k) : null);
                return $"- \"{q.QueryText}\": {q.Searches} searches, {q.ZeroHits} found nothing, {q.ClickedSearches} clicked" +
                    (q.Selects > 0 ? $", average click position {N((double)q.PositionSum / q.Selects, "0.0")}" : "") +
                    (chosen != null ? $", most chosen: {chosen}" : "");
            }
            var o = store.Overview(ctx.TeamId, ds, from, to);
            var sb = new StringBuilder();
            sb.AppendLine($"Last {days} days: {o.Searches:N0} searches, {o.ZeroHits:N0} found nothing, {o.ClickedSearches:N0} ended in a click, {o.Uncovered:N0} were fuzzy finds.");
            sb.AppendLine("The lists below are capped; a query that is not in them may still have been searched. The query tool looks one up.");
            void Section(string title, List<QueryStat> list)
            {
                if (list.Count == 0) return;
                sb.AppendLine(title);
                foreach (var q in list) sb.AppendLine(Q(q));
            }
            Section("Top queries:", store.TopQueries(ctx.TeamId, ds, from, to, Top));
            Section("Found nothing:", store.TopQueries(ctx.TeamId, ds, from, to, Top, zeroHitsOnly: true));
            Section("Lowest click-through:", store.TopQueries(ctx.TeamId, ds, from, to, 10, order: QueryOrder.LowestClickThrough));
            Section("Fuzzy finds (typos the search still matched):", store.TopQueries(ctx.TeamId, ds, from, to, 10, uncoveredOnly: true));
            var docs = store.TopDocuments(ctx.TeamId, ds, from, to, 10);
            if (docs.Count > 0)
            {
                sb.AppendLine("Most chosen records:");
                foreach (var d in docs) sb.AppendLine($"- {d.Label ?? label(d.DocumentKey) ?? d.DocumentKey.ToString()}: chosen {d.Selects} times, {d.Converts} conversions");
            }
            var changes = store.Changes(ctx.TeamId, ds, from, to);
            if (changes.Count > 0)
            {
                sb.AppendLine("Changes in the period:");
                foreach (var c in changes.Take(20))
                    sb.AppendLine($"- {DateTimeOffset.FromUnixTimeMilliseconds(c.Timestamp):yyyy-MM-dd} {ChangeDescriptions.Describe(c.Kind, c.Summary)}");
            }
            return sb.ToString();
        }

        /// <summary>One query's counts, however rarely it was searched: the top lists are capped,
        /// and "how often did X occur" must not be answered from their absence.</summary>
        private string QueryStatistics(ObservrChatContext ctx, string text, int days)
        {
            if (text.Length == 0) return "The query text is empty.";
            var store = statistics.Store;
            if (store == null || !statistics.Enabled) return "This instance keeps no statistics.";
            var ds = ctx.DataSetName!;
            var to = store.Today(ctx.TeamId, ds);
            var from = to - (days - 1);
            var q = store.OneQuery(ctx.TeamId, ds, text, from, to);
            if (q == null) return $"\"{text}\" was not searched in the last {days} days (compared without regard to case; the search statistics count visitors' searches, not the console's preview).";
            var sb = new StringBuilder();
            sb.Append($"\"{text}\" in the last {days} days: {q.Value.Searches} searches, {q.Value.ZeroHits} found nothing, {q.Value.ClickedSearches} ended in a click, {q.Value.Uncovered} were fuzzy finds");
            if (q.Value.Selects > 0) sb.Append($", average click position {N((double)q.Value.PositionSum / q.Value.Selects, "0.0")}");
            sb.AppendLine(".");
            var engine = engines.FindSearchEngine(ds, ctx.TeamId);
            var label = engine != null && engine.Status.SystemState == SystemState.Ready ? StatisticsLabels.For(engine, store.LabelField(ctx.TeamId, ds)) : (Func<long, string?>)(_ => null);
            var docs = store.QueryDocuments(ctx.TeamId, ds, text, from, to, 10);
            if (docs.Count > 0)
            {
                sb.AppendLine("Records chosen from its results:");
                foreach (var d in docs)
                    sb.AppendLine($"- {label(d.DocumentKey) ?? d.DocumentKey.ToString()}: chosen {d.Selects} times" + (d.Selects > 0 ? $", average position {N((double)d.PositionSum / d.Selects, "0.0")}" : ""));
            }
            return sb.ToString();
        }

        private string Fields(ObservrChatContext ctx)
        {
            var ds = ctx.DataSetName!;
            var engine = engines.FindSearchEngine(ds, ctx.TeamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready) return "The dataset is not loaded; the fields and their sample need it awake.";
            var ask = client.BuildFieldReviewAsk(ctx.TeamId, ds, engine);
            var sb = new StringBuilder();
            sb.AppendLine($"Key field: {ask.KeyField}. {ask.Records:N0} records, {ask.Sampled} sampled.");
            foreach (var f in ask.FieldEvidence)
            {
                var roles = new List<string>();
                if (f.Searchable) roles.Add("searchable");
                if (f.Filterable) roles.Add("filterable");
                if (f.Facetable) roles.Add("facetable");
                if (f.Sortable) roles.Add("sortable");
                sb.Append($"- {f.Name} ({f.Type}{(f.IsArray ? " array" : "")}): {(roles.Count > 0 ? string.Join(", ", roles) : "no role")}, weight {f.Weight}; filled in {f.Filled} of {ask.Sampled} sampled, {f.Distinct} distinct values over {f.ValuesOver:N0} records");
                if (f.TopValues.Count > 0) sb.Append($"; commonest: {string.Join(" | ", f.TopValues)}");
                if (f.Min != null) sb.Append($"; {f.Min} to {f.Max}");
                if (f.AvgLength > 0) sb.Append($"; about {f.AvgLength} characters");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string Synonyms(ObservrChatContext ctx)
        {
            var list = engines.GetSynonyms(ctx.DataSetName!, ctx.TeamId);
            if (list == null || list.Entries.Count == 0) return "No synonyms on this dataset.";
            return string.Join("\n", list.Entries.Select(e =>
                e.Direction == SynonymDirection.OneWay ? $"- {e.Source} → {string.Join(", ", e.Terms)}" : $"- {string.Join(" = ", e.Terms)}"));
        }

        private string Settings(ObservrChatContext ctx)
        {
            var ds = ctx.DataSetName!;
            var sb = new StringBuilder();
            var site = metadata.Load(ctx.TeamId, ds);
            sb.AppendLine($"Description: {(string.IsNullOrWhiteSpace(site) ? "(none)" : site.Trim())}");
            var ka = engines.GetKeepAliveInfo(ds, ctx.TeamId);
            sb.AppendLine($"Keep-alive: {(ka.KeepAliveTimeHrs == int.MaxValue ? "always on" : ka.KeepAliveTimeHrs == 0 ? "client managed" : ka.KeepAliveTimeHrs + " hours")}.");
            var qp = queryParameters.Load(ctx.TeamId, ds);
            sb.AppendLine($"Query parameters: {(qp == null ? "the defaults" : JsonSerializer.Serialize(qp, Json))}");
            var soa = searchOrAsk.Load(ctx.TeamId, ds);
            sb.AppendLine($"Search or ask: {(soa == null ? "not set up" : JsonSerializer.Serialize(soa, Json))}");
            // Ask phrases in effect: a dataset with no list of its own runs the defaults, which the
            // settings above show as null.
            var phrases = soa?.AskPhrases ?? SearchOrAsk.SearchOrAskPhrases.Default;
            sb.AppendLine($"Ask phrases{(soa?.AskPhrases == null ? " (the defaults)" : "")}: {JsonSerializer.Serialize(phrases, Json)}; refocus while typing {(soa?.RefocusWhileTyping ?? true ? "on" : "off")}, invite at once {(soa?.InviteOnAskPhrase ?? true ? "on" : "off")}.");
            return sb.ToString();
        }

        private string ZeroHit(ObservrChatContext ctx, string query)
        {
            if (query.Length == 0) return "The query is empty.";
            var ds = ctx.DataSetName!;
            var engine = engines.FindSearchEngine(ds, ctx.TeamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready) return "The dataset is not loaded, so it cannot be searched now.";
            var ask = client.BuildZeroHitAsk(ctx.TeamId, ds, engine, query, 0);
            var sb = new StringBuilder();
            sb.AppendLine(ask.NearMatches.Count == 0 ? "With pattern matching on, still nothing." : $"With pattern matching on: {string.Join("; ", ask.NearMatches.Select(n => n.Name))}.");
            foreach (var w in ask.Words)
                sb.AppendLine($"The word \"{w.Word}\" alone finds {(w.Matches.Count == 0 ? "nothing" : string.Join("; ", w.Matches.Select(m => m.Name)))}.");
            return sb.ToString();
        }
    }
}
