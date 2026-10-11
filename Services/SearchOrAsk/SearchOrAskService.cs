using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Indx.Api;
using Indx.Http;
using IndxServer.Engine;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>What an intent call came to. <see cref="Status"/> is Ok unless the call was refused
    /// before the judge was asked; the controller maps the refusals to problems.</summary>
    public sealed record IntentOutcome(IntentStatus Status, IntentResult? Result = null, int RetryAfterSeconds = 0);

    public enum IntentStatus { Ok, Unavailable, Disabled, RateLimited, BudgetSpent }

    /// <summary>
    /// The intent response. <c>p</c> is the judge's probability that the visitor is asking for
    /// something an answer would serve; <c>offer</c> is that probability read on its own against
    /// the thresholds. A client that keeps what it has shown applies the thresholds itself (enter
    /// at one value, leave below the lower), which is why they travel with every answer.
    /// <c>reason</c> says why the judge was not asked or did not answer: identifier, timeout,
    /// failed (with the provider's status).
    /// </summary>
    public sealed record IntentResult(double P, double? Answerable, string Offer, SearchOrAskThresholds Thresholds,
        bool Cached, long Ms, string? Reason = null);

    /// <summary>One source handed to the answer model, as the visitor's client sees it.</summary>
    public sealed record AskSource(int N, long DocumentKey, string Title);

    /// <summary>
    /// Search or ask on the request path: the intent judgement on a typing pause, and the answer
    /// when the visitor asks. Notes/search-or-ask-server.md has the design; Notes/search-or-ask.md
    /// the measurements every default here comes from. Nothing in here decides whether a dataset
    /// should have the feature: that is Observr's, outside the server.
    /// </summary>
    public sealed partial class SearchOrAskService : IDisposable
    {
        private readonly SearchOrAskStore _store;
        private readonly DatasetMetadataStore _metadata;
        private readonly ISearchOrAskModels _models;
        private readonly SearchOrAskOptions _options;
        private readonly ILogger<SearchOrAskService> _logger;

        private readonly PartitionedRateLimiter<string> _intentLimiter;
        private readonly PartitionedRateLimiter<string> _askLimiter;

        // The same text over the same dataset and settings gets the same judgement: backspacing and
        // retyping cost nothing. Bounded crudely; a soft cache is all typing needs.
        private readonly ConcurrentDictionary<string, JudgeAnswer> _cache = new();
        private const int MaxCached = 5000;

        // (teamId\0dataset) -> (UTC day, judge calls made that day).
        private readonly ConcurrentDictionary<string, (DateOnly Day, int Calls)> _budget = new();

        public const int MaxText = 300;
        public const int MaxQuestion = 1000;
        private const int ResultsForJudge = 5;
        private const int SnippetChars = 240;
        private const int SourcesForAnswer = 8;
        private const int SourceChars = 3000;

        public SearchOrAskService(SearchOrAskStore store, DatasetMetadataStore metadata, ISearchOrAskModels models,
            SearchOrAskOptions options, ILogger<SearchOrAskService> logger)
        {
            _store = store;
            _metadata = metadata;
            _models = models;
            _options = options;
            _logger = logger;
            _intentLimiter = PerAddress(options.IntentPerMinute);
            _askLimiter = PerAddress(options.AskPerMinute);
        }

        private static PartitionedRateLimiter<string> PerAddress(int perMinute) =>
            PartitionedRateLimiter.Create<string, string>(address => RateLimitPartition.GetFixedWindowLimiter(address,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, perMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

        public bool Available => _options.Available;

        /// <summary>The dataset's saved settings, or the defaults (off) when none are saved.</summary>
        public SearchOrAskSettings Settings(string teamId, string dataSetName) =>
            _store.Load(teamId, dataSetName) ?? new SearchOrAskSettings();

        /// <summary>What the judge reads about the site: the setting, else the dataset's
        /// description, else a plain sentence naming the dataset.</summary>
        public string SiteDescription(string teamId, string dataSetName, SearchOrAskSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.SiteDescription)) return settings.SiteDescription.Trim();
            var description = _metadata.Load(teamId, dataSetName);
            return string.IsNullOrWhiteSpace(description) ? $"A search over the records of the dataset \"{dataSetName}\"." : description.Trim();
        }

        /// <summary>The fields the judge sees, from the settings where set and otherwise from the
        /// field configuration: the highest-weighted searchable field is the title, the next two
        /// searchable fields the snippet. Section is only what the owner names.</summary>
        public static SearchOrAskFields Fields(SearchOrAskSettings settings, IReadOnlyList<Field>? fields)
        {
            var searchable = (fields ?? []).Where(f => f.Searchable).OrderByDescending(f => f.Weight).Select(f => f.Name).ToList();
            var title = settings.TitleField ?? searchable.FirstOrDefault();
            var snippet = settings.SnippetFields is { Count: > 0 } s ? s : searchable.Where(n => n != title).Take(2).ToList();
            return new SearchOrAskFields(title, settings.SectionFields ?? [], snippet);
        }

        // ── Intent ──────────────────────────────────────────────────────────────────────────

        // Digits and separators (ISBN, SKU), a dotted member, camel or Pascal case, a code with a
        // digit. The same rule as the docs prototype and JevProbe's replay: these are lookups, and
        // the judge is not asked.
        [GeneratedRegex(@"^[\d\s\-_.:/]+$|^\w+\.\w+|^[A-Za-z]*[a-z][A-Z]\w*$|^[A-Za-z]+[-_]?\d[\w-]*$")]
        private static partial Regex IdentifierShape();

        public static bool IsIdentifier(string text)
        {
            var t = text.Trim();
            return t.Length > 0 && !t.Contains(' ') && IdentifierShape().IsMatch(t);
        }

        private static string Normalise(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>Judges one pause. <c>trial</c> is the console's tester: settings not yet saved,
        /// judged as if on, still limited and counted against the budget, since it costs the same.
        /// <c>onState</c> receives the state the judge is sent, for the tester to show.</summary>
        public async Task<IntentOutcome> IntentAsync(string teamId, string dataSetName, IServerSearchEngine engine,
            string text, string clientAddress, CancellationToken ct,
            SearchOrAskSettings? trial = null, Action<JsonObject>? onState = null)
        {
            if (!_options.Available) return new IntentOutcome(IntentStatus.Unavailable);
            var settings = trial ?? Settings(teamId, dataSetName);
            if (trial == null && !settings.Enabled) return new IntentOutcome(IntentStatus.Disabled);

            var started = Stopwatch.GetTimestamp();
            long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            IntentResult Result(double p, double? answerable, bool cached, string? reason = null) =>
                new(p, answerable, settings.Thresholds.OfferFor(p), settings.Thresholds, cached, Ms(), reason);

            // On the text as typed: lowercasing first would hide the camel case the rule looks for.
            if (IsIdentifier(text))
                return new IntentOutcome(IntentStatus.Ok, Result(0, null, false, "identifier"));

            var site = SiteDescription(teamId, dataSetName, settings);
            var fields = Fields(settings, engine.GetFieldList());
            var cacheKey = string.Join('\0', teamId, dataSetName, settings.Kind, site, fields.TitleField,
                string.Join(',', fields.SectionFields), string.Join(',', fields.SnippetFields), Normalise(text));
            if (_cache.TryGetValue(cacheKey, out var hit))
                return new IntentOutcome(IntentStatus.Ok, Result(hit[SearchOrAskRecipes.Asking] ?? 0, hit[SearchOrAskRecipes.Answerable], true));

            using (var lease = _intentLimiter.AttemptAcquire(clientAddress))
                if (!lease.IsAcquired)
                    return new IntentOutcome(IntentStatus.RateLimited, RetryAfterSeconds: RetryAfter(lease));
            if (!TryTakeBudget(teamId, dataSetName, settings.DailyCallBudget))
                return new IntentOutcome(IntentStatus.BudgetSpent, RetryAfterSeconds: SecondsToUtcMidnight());

            var state = State(site, text, Search(engine, dataSetName, teamId, text, fields));
            onState?.Invoke(state);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.JudgeTimeoutMs);
            try
            {
                var answer = await _models.JudgeAsync(state, SearchOrAskRecipes.Questions(settings.Kind), timeout.Token);
                if (_cache.Count > MaxCached) _cache.Clear();
                _cache[cacheKey] = answer;
                return new IntentOutcome(IntentStatus.Ok, Result(answer[SearchOrAskRecipes.Asking] ?? 0, answer[SearchOrAskRecipes.Answerable], false));
            }
            // A judge that is slow, out of credit or down means no offer, never a failed search:
            // p = 0 with the reason, and the search the visitor already has is untouched.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new IntentOutcome(IntentStatus.Ok, Result(0, null, false, "timeout"));
            }
            catch (SearchOrAskModelException ex)
            {
                _logger.LogWarning("Search or ask judge failed for {Team}/{Dataset}: {Status}", teamId, dataSetName, ex.Status);
                return new IntentOutcome(IntentStatus.Ok, Result(0, null, false, $"failed:{ex.Status}"));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Search or ask judge unreachable for {Team}/{Dataset}", teamId, dataSetName);
                return new IntentOutcome(IntentStatus.Ok, Result(0, null, false, "failed:unreachable"));
            }
        }

        /// <summary>What the search found, reduced to what the judge reads.</summary>
        public sealed record SearchSummary(int Hits, bool CloseMatch, IReadOnlyList<(long Key, string Title, string Section, string Snippet)> Top);

        /// <summary>Runs the search the visitor's client already ran, here, so the judge sees the
        /// same results without the client sending results it could forge. Through the manager
        /// directly: not a visitor's search, so it never reaches the statistics.</summary>
        /// <remarks>An ask passes <paramref name="truncate"/> false: a whole question has many words,
        /// coverage cuts the list to the records that hold most of them, and the record that answers
        /// can be ranked below that cut ("is there a limit on how many documents I can index for free"
        /// kept one record on the docs, and not Licensing). The answer model reads the sources and
        /// decides what is relevant, so it gets the best ranked eight either way.</remarks>
        private static SearchSummary Search(IServerSearchEngine engine, string dataSetName, string teamId, string text,
            SearchOrAskFields fields, int top = ResultsForJudge, bool truncate = true)
        {
            var result = IndxServerInternalApi.Manager.Search(new QueryProxy
            {
                Text = text,
                MaxNumberOfRecordsToReturn = 10,
                EnableCoverage = true,
                CoverageSetup = truncate ? null : new CoverageSetupProxy { Truncate = false },
            }, dataSetName, teamId);
            var records = result.Records ?? [];
            var hits = records.Take(top).Select(r => Describe(engine, r.DocumentKey, fields, SnippetChars, forAnswer: false)).ToList();
            return new SearchSummary(records.Length, result.TruncationIndex >= 0, hits);
        }

        /// <summary>A record as the models read it: key, title, sections and text. For the judge the
        /// text is one line of all snippet fields, cut at <paramref name="chars"/>. For an answer each
        /// field keeps its line breaks and gets <paramref name="chars"/> of its own, separated by a
        /// blank line: a page's code (its own field on the docs) must arrive whole and formatted, not
        /// flattened, and not crowded out by the prose before it.</summary>
        private static (long, string, string, string) Describe(IServerSearchEngine engine, long key, SearchOrAskFields fields, int chars, bool forAnswer)
        {
            JsonNode? doc = null;
            try { doc = JsonNode.Parse(engine.GetJsonDataOfKey(key) ?? ""); }
            catch (System.Text.Json.JsonException) { }
            string Read(IEnumerable<string> names, string separator) => doc == null ? "" :
                string.Join(separator, names.Select(n => doc[n] switch
                {
                    JsonArray a => string.Join(", ", a.Select(x => x?.ToString())),
                    JsonNode v => v.ToString(),
                    null => "",
                }).Where(s => s.Length > 0));
            string Cut(string s) => s.Length > chars ? s[..chars] + "…" : s;
            var snippet = forAnswer
                ? string.Join("\n\n", fields.SnippetFields.Select(f => Cut(Read([f], " ").Trim())).Where(s => s.Length > 0))
                : Cut(Read(fields.SnippetFields, " ").ReplaceLineEndings(" "));
            return (key, fields.TitleField == null ? "" : Read([fields.TitleField], ""), Read(fields.SectionFields, "; "), snippet);
        }

        /// <summary>The state the judge reads: the shape every recipe was measured with.</summary>
        public static JsonObject State(string site, string text, SearchSummary search) => new()
        {
            ["site"] = site,
            ["request"] = text,
            ["search"] = new JsonObject
            {
                ["hits"] = search.Hits,
                ["closeMatch"] = search.CloseMatch,
                ["results"] = new JsonArray(search.Top.Select(h => (JsonNode)new JsonObject
                {
                    ["title"] = h.Title,
                    ["section"] = h.Section,
                    ["snippet"] = h.Snippet,
                }).ToArray()),
            },
        };

        private bool TryTakeBudget(string teamId, string dataSetName, int budget)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var key = $"{teamId}\0{dataSetName}";
            while (true)
            {
                var current = _budget.GetOrAdd(key, (today, 0));
                var calls = current.Day == today ? current.Calls : 0;
                if (calls >= budget) return false;
                if (_budget.TryUpdate(key, (today, calls + 1), current)) return true;
            }
        }

        /// <summary>Judge calls made today for a dataset, for the console.</summary>
        public int CallsToday(string teamId, string dataSetName) =>
            _budget.TryGetValue($"{teamId}\0{dataSetName}", out var b) && b.Day == DateOnly.FromDateTime(DateTime.UtcNow) ? b.Calls : 0;

        private static int SecondsToUtcMidnight() =>
            (int)Math.Ceiling((DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds);

        private static int RetryAfter(RateLimitLease lease) =>
            lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : 60;

        // ── Ask ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Whether an ask may proceed: the same gates as intent, against the ask limit.
        /// Ask counts against the daily budget too: it is the expensive call.</summary>
        public IntentOutcome CanAsk(string teamId, string dataSetName, string clientAddress, SearchOrAskSettings? trial = null)
        {
            if (!_options.Available) return new IntentOutcome(IntentStatus.Unavailable);
            var settings = trial ?? Settings(teamId, dataSetName);
            if (trial == null && !settings.Enabled) return new IntentOutcome(IntentStatus.Disabled);
            using (var lease = _askLimiter.AttemptAcquire(clientAddress))
                if (!lease.IsAcquired)
                    return new IntentOutcome(IntentStatus.RateLimited, RetryAfterSeconds: RetryAfter(lease));
            if (!TryTakeBudget(teamId, dataSetName, settings.DailyCallBudget))
                return new IntentOutcome(IntentStatus.BudgetSpent, RetryAfterSeconds: SecondsToUtcMidnight());
            return new IntentOutcome(IntentStatus.Ok);
        }

        private static readonly HashSet<string> QuestionWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "how", "what", "whats", "why", "where", "which", "who", "when", "is", "are", "does", "do", "did", "can",
            "could", "should", "would", "will", "the", "a", "an", "about", "of", "to", "i", "me", "my", "it", "there",
            // Filler that a whole question carries and a lookup does not: "how many documents" ranked
            // pages about indexing above the one about the document limit.
            "many", "much", "on", "in", "for", "with", "and", "or", "be", "this", "that", "any", "some",
            "hvordan", "hva", "hvorfor", "hvor", "hvilken", "hvilke", "hvilket", "hvem", "når", "kan", "skal", "må",
            "bør", "er", "går", "det", "jeg", "den", "en", "et", "om", "på", "til", "seg", "man",
            "mange", "mye", "noen", "med", "og", "eller", "av", "min", "mitt", "mine",
        };

        /// <summary>The question without its question words, which match titles and push the record
        /// asked about out of the top results: "what is ronin about" finds What About Bob?, "ronin"
        /// finds Ronin (measured on tmdb23k). Null when nothing is left or nothing was removed.</summary>
        public static string? KeywordsOf(string question)
        {
            var words = Regex.Split(question.ToLowerInvariant().Replace("'s", "").Replace("’s", ""), @"[^\p{L}\p{N}]+")
                .Where(w => w.Length > 0).ToList();
            var kept = words.Where(w => !QuestionWords.Contains(w)).ToList();
            return kept.Count == 0 || kept.Count == words.Count ? null : string.Join(' ', kept);
        }

        /// <summary>
        /// Answers a question from the dataset's records, as events through <paramref name="send"/>:
        /// <c>sources</c> (the records handed to the model, numbered), <c>text</c> deltas, then
        /// <c>done</c>, or <c>error</c>. Two searches, the question as typed and its keywords,
        /// interleaved so each contributes its best; then the model, held to the sources.
        /// </summary>
        public async Task AskAsync(string teamId, string dataSetName, IServerSearchEngine engine, string question,
            Func<string, object, Task> send, CancellationToken ct, SearchOrAskSettings? trial = null)
        {
            var started = Stopwatch.GetTimestamp();
            var settings = trial ?? Settings(teamId, dataSetName);
            var fields = Fields(settings, engine.GetFieldList());

            var typed = Search(engine, dataSetName, teamId, question, fields, SourcesForAnswer, truncate: false).Top;
            var keywords = KeywordsOf(question) is { } k ? Search(engine, dataSetName, teamId, k, fields, SourcesForAnswer, truncate: false).Top : [];
            var merged = new List<long>();
            for (int i = 0; merged.Count < SourcesForAnswer && (i < typed.Count || i < keywords.Count); i++)
            {
                if (i < keywords.Count && !merged.Contains(keywords[i].Key)) merged.Add(keywords[i].Key);
                if (merged.Count < SourcesForAnswer && i < typed.Count && !merged.Contains(typed[i].Key)) merged.Add(typed[i].Key);
            }
            if (merged.Count == 0)
            {
                await send("error", new { code = "nothingFound", message = "The search found nothing to answer from." });
                return;
            }

            // The sources in full (up to a ceiling), not the judge's snippets: the answer is written from them.
            var sources = merged.Select((key, i) => (N: i + 1, Doc: Describe(engine, key, fields, SourceChars, forAnswer: true))).ToList();
            await send("sources", sources.Select(s => new AskSource(s.N, s.Doc.Item1, s.Doc.Item2)).ToArray());

            var user = new StringBuilder();
            foreach (var (n, doc) in sources)
            {
                user.Append($"[{n}] {doc.Item2}");
                if (doc.Item3.Length > 0) user.Append($" ({doc.Item3})");
                user.Append('\n').Append(doc.Item4).Append("\n\n");
            }
            user.Append("Question: ").Append(question);
            // Said again last, next to the question: from the system prompt alone, a site description
            // naming its readers' languages still won 1 time in 10 (11 Oct 2026); with this, 0 in 10.
            user.Append("\n\n").Append(SearchOrAskRecipes.AnswerLanguageReminder);

            var model = _options.AnswerModelFor(settings.Answers);
            try
            {
                await foreach (var delta in _models.AnswerAsync(model, SearchOrAskRecipes.AnswerSystemPrompt(SiteDescription(teamId, dataSetName, settings)),
                                   user.ToString(), ct))
                    await send("text", new { delta });
                await send("done", new { model, latencyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
            catch (SearchOrAskModelException ex)
            {
                _logger.LogWarning("Search or ask answer failed for {Team}/{Dataset}: {Status}", teamId, dataSetName, ex.Status);
                await send("error", new { code = ex.Status == 429 ? "upstreamBusy" : "failed", message = "The answer could not be written." });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Search or ask answer model unreachable for {Team}/{Dataset}", teamId, dataSetName);
                await send("error", new { code = "failed", message = "The answer could not be written." });
            }
        }

        public void Dispose()
        {
            _intentLimiter.Dispose();
            _askLimiter.Dispose();
        }
    }
}
