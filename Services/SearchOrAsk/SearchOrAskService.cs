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
    /// failed (with the provider's status). <c>cutWhileAsking</c> is the dataset's setting for the
    /// list under a strong offer (coverage hits only), carried with every answer like the thresholds.
    /// </summary>
    /// <summary><c>Criteria</c>, for a catalogue: the probability the visitor describes what to list,
    /// which the page turns into filters (POST filters) when it reaches the strong threshold.</summary>
    public sealed record IntentResult(double P, double? Answerable, string Offer, SearchOrAskThresholds Thresholds,
        bool Cached, long Ms, string? Reason = null, double? Criteria = null, bool CutWhileAsking = true,
        double CutShareOfTop = 0.5, double CutTopFloor = 0.2, bool StrictCoverageWhileAsking = true, string FiltersWhileTyping = "off",
        string? SearchText = null, bool RefocusWhileTyping = true, bool InviteOnAskPhrase = true);

    /// <summary>One source handed to the answer model, as the visitor's client sees it.</summary>
    public sealed record AskSource(int N, long DocumentKey, string Title);

    /// <summary>The instance's experimental switch for Search or ask (admin settings).</summary>
    public interface ISearchOrAskSwitch
    {
        bool Shown { get; }

        /// <summary>The model key set in the console, if any.</summary>
        string? ModelKey { get; }

        /// <summary>The console's provider settings, the key among them.</summary>
        SearchOrAskProviderSettings Providers => new(ApiKey: ModelKey);
    }

    /// <summary>The switch and the console's provider settings, as the instance settings file holds them.</summary>
    internal sealed class InstanceSearchOrAskSwitch(InstanceSettingsService settings) : ISearchOrAskSwitch
    {
        public bool Shown => settings.Load().SearchOrAskShown;
        public string? ModelKey => settings.Load().SearchOrAskApiKey;
        public SearchOrAskProviderSettings Providers
        {
            get
            {
                var s = settings.Load();
                return new SearchOrAskProviderSettings(s.SearchOrAskProvider, s.SearchOrAskApiKey, s.SearchOrAskBaseUrl, s.SearchOrAskEuRouting,
                    s.SearchOrAskJudgeProvider, s.SearchOrAskJudgeApiKey, s.SearchOrAskAnswerModel, s.SearchOrAskFastAnswerModel, s.SearchOrAskFilterModel);
            }
        }
    }

    /// <summary>An earlier question and its answer in the same conversation, for a follow-up.</summary>
    public sealed record AskTurn(string Question, string Answer, AskShown? Shown = null);

    /// <summary>On a catalogue, the list a turn put on the page: its filters, words and order. A
    /// follow-up ("which is the largest?") is about that list.</summary>
    public sealed record AskShown(FilterSuggestion[]? Filters, string? Text, SortSuggestion? Sort)
    {
        public override string ToString() =>
            string.Join("; ", (Filters ?? []).Select(f => f.Value != null ? $"{f.Field} = {f.Value}"
                : $"{f.Field} {f.Min?.ToString() ?? ""}..{f.Max?.ToString() ?? ""}"))
            + (string.IsNullOrWhiteSpace(Text) ? "" : $"; words \"{Text}\"")
            + (Sort == null ? "" : $"; sorted by {Sort.Field} {(Sort.Descending ? "descending" : "ascending")}");
    }

    /// <summary>Where a judgement or an ask came from, for the statistics, as a search names it:
    /// the page's search behind it (<c>?queryId=</c>), its session, its surface, an optional
    /// subject, and whether it counts. None (null) records nothing: the console's tester, which
    /// would only ever be uncounted.</summary>
    public sealed record SearchOrAskOrigin(string? QueryId, string? Session, string? Source, string? Subject, bool Counted);

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
        private readonly StatisticsService? _statistics;
        private readonly ISearchOrAskSwitch? _instance;
        private (bool Shown, long At) _shown;

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
            SearchOrAskOptions options, ILogger<SearchOrAskService> logger, StatisticsService? statistics = null,
            ISearchOrAskSwitch? instance = null, SearchOrAskSetupProvider? setup = null)
        {
            _instance = instance;
            _setup = setup ?? new SearchOrAskSetupProvider(options, instance);
            _statistics = statistics;
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

        private readonly SearchOrAskSetupProvider _setup;

        /// <summary>Where each call goes and with which key (Notes/search-or-ask-server.md): the
        /// configuration's values win over the console's. The clients read the same object.</summary>
        public SearchOrAskSetup Setup => _setup.Current;

        /// <summary>Whether this instance can run Search or ask: the answer provider has a key and so
        /// has the judge's door, from the configuration (which wins) or from the console. The judge
        /// is the feature; an answer without it is retrieval and a model, which is nobody's product
        /// (decided 14 Oct 2026), so a missing judge key makes the whole thing unavailable.</summary>
        public bool Available => Setup.AnswersAvailable && Setup.JudgeAvailable;

        /// <summary>Whether the judge can be asked: Jev's door has a key.</summary>
        public bool JudgeAvailable => Setup.JudgeAvailable;

        /// <summary>One line per door, for the admin's "Check the keys": what the judge and the
        /// answer model say to a trivial call, in the provider's own words when they refuse. Only
        /// a failure's reason tells a spent key budget from a moderation flag from a wrong key
        /// (19 Oct 2026: a 403 with nothing else to go on).</summary>
        public async Task<(string Judge, string Answers)> CheckKeysAsync(CancellationToken ct)
        {
            var s = Setup;
            async Task<string> Try(string what, bool available, Func<Task<string>> call)
            {
                if (!available) return $"{what}: no key.";
                try { return $"{what}: OK, {await call()}."; }
                catch (SearchOrAskModelException ex) { return $"{what}: {ex.Message}"; }
                catch (OperationCanceledException) { return $"{what}: no answer in time."; }
                catch (Exception ex) { return $"{what}: unreachable ({ex.Message})."; }
            }
            var judge = Try($"The judge ({s.JudgeModel})", s.JudgeAvailable, async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var state = State("A site that explains itself.", "how do I reset my password", new SearchSummary(0, false, []));
                var questions = SearchOrAskRecipes.Questions(SearchOrAskRecipes.Documentation)
                    .Where(q => q.Key == SearchOrAskRecipes.Asking).ToDictionary(q => q.Key, q => q.Value);
                var answer = await _models.JudgeAsync(state, questions, timeout.Token);
                return $"it answered{(answer.Cost is { } c ? $" for ${c:0.0000}" : "")}";
            });
            var answers = Try($"The answers ({s.AnswerModel})", s.AnswersAvailable, async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var (content, cost) = await _models.CompleteAsync(s.FastAnswerModel, "Reply with the one word OK.", "Ready?", timeout.Token);
                return $"\"{content.Trim()}\"{(cost is { } c ? $" for ${c:0.0000}" : "")}";
            });
            return (await judge, await answers);
        }

        /// <summary>Where the key in use comes from, for the console: "configuration", "console" or null.</summary>
        public string? ModelKeySource =>
            !string.IsNullOrWhiteSpace(_options.ApiKey) ? "configuration" : ModelKey != null ? "console" : null;

        /// <summary>The answer provider's key in use.</summary>
        public string? ModelKey => Setup.AnswerKey;

        /// <summary>Whether the instance shows Search or ask at all: the experimental switch in the
        /// admin settings. Read from settings.json at most every five seconds, since the judge asks on
        /// every typing pause. Without an instance settings service (some tests), shown.</summary>
        public bool Shown
        {
            get
            {
                if (_instance == null) return true;
                var now = Environment.TickCount64;
                var cached = _shown;
                if (cached.At != 0 && now - cached.At < 5000) return cached.Shown;
                var shown = _instance.Shown;
                _shown = (shown, now);
                return shown;
            }
        }

        /// <summary>Whether a dataset has Search or ask: the instance shows it, and a team admin
        /// switched it on in the dataset's Options tab. Without an instance switch (component tests,
        /// which have no settings database either), shown, as <see cref="Shown"/> is.</summary>
        public bool ShownFor(string teamId, string dataSetName) =>
            _instance == null || (Shown && (_store.Load(teamId, dataSetName)?.Shown ?? false));

        /// <summary>Forgets the cached switch, so a change in the admin settings applies at once.</summary>
        public void InstanceSettingsChanged() { _shown = default; _setup.Changed(); }

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

        private StatisticsWriter? Writer => _statistics is { Enabled: true } s ? s.Writer : null;

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
            SearchOrAskSettings? trial = null, Action<JsonObject>? onState = null, SearchOrAskOrigin? origin = null)
        {
            if (!JudgeAvailable) return new IntentOutcome(IntentStatus.Unavailable);
            var settings = trial ?? Settings(teamId, dataSetName);
            if (trial == null && !settings.Enabled) return new IntentOutcome(IntentStatus.Disabled);

            var started = Stopwatch.GetTimestamp();
            long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // The judge reads the results of what the question is about (Ask phrases), not of its
            // words: "help me find the movie about the green ogre" is judged on Shrek, not on films
            // called Movie. The request it reads stays the text as typed.
            var (searched, taken) = settings.RefocusWhileTyping
                ? SearchOrAskPhrases.Strip(text, settings.AskPhrases ?? SearchOrAskPhrases.Default)
                : (text, (IReadOnlyList<string>)[]);
            string? searchText = searched == text ? null : searched;
            // For the statistics: the phrases taken, and what the rest found, so Observr can see which
            // questions no phrase catches and which phrase left nothing to find.
            string? askPhrase = taken.Count > 0 ? string.Join(' ', taken) : null;
            int? focusedHits = null;
            IntentResult Result(double p, double? answerable, bool cached, string? reason = null, double? cost = null, double? criteria = null)
            {
                var result = new IntentResult(p, answerable, settings.Thresholds.OfferFor(p, criteria), settings.Thresholds, cached, Ms(), reason, criteria,
                    settings.CutWhileAsking, settings.CutShareOfTop, settings.CutTopFloor, settings.StrictCoverageWhileAsking, settings.FiltersWhileTyping, searchText,
                    settings.RefocusWhileTyping, settings.InviteOnAskPhrase);
                // A judgement is recorded once the judge or its cache answered for it, a failing
                // judge included (its reason says so); an identifier never reaches here.
                if (reason != "identifier" && origin != null && Writer is { } w)
                    w.RecordAskIntent(new AskIntentEventRow(teamId, dataSetName, Blank(origin.QueryId), p, answerable,
                        result.Offer, cached, reason, cost, Blank(origin.Subject), Blank(origin.Session), origin.Source,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), origin.Counted, askPhrase, focusedHits));
                return result;
            }

            // On the text as typed: lowercasing first would hide the camel case the rule looks for.
            if (IsIdentifier(text))
                return new IntentOutcome(IntentStatus.Ok, Result(0, null, false, "identifier"));

            var site = SiteDescription(teamId, dataSetName, settings);
            var fields = Fields(settings, engine.GetFieldList());
            var cacheKey = string.Join('\0', teamId, dataSetName, settings.Kind, site, fields.TitleField,
                string.Join(',', fields.SectionFields), string.Join(',', fields.SnippetFields), Normalise(text));
            if (_cache.TryGetValue(cacheKey, out var hit))
            {
                // Only the console's tester asks for the state; a cached answer used to come back
                // without one, and "What the judge was sent" was empty for a text tried before.
                // The search only when someone reads it: the tester's state, or a refocus to count.
                if (onState != null || askPhrase != null)
                {
                    var found = Search(engine, dataSetName, teamId, searched, fields);
                    if (askPhrase != null) focusedHits = found.Hits;
                    onState?.Invoke(State(site, text, found));
                }
                return new IntentOutcome(IntentStatus.Ok, Result(hit[SearchOrAskRecipes.Asking] ?? 0, hit[SearchOrAskRecipes.Answerable], true,
                    criteria: hit[SearchOrAskRecipes.Criteria]));
            }

            using (var lease = _intentLimiter.AttemptAcquire(clientAddress))
                if (!lease.IsAcquired)
                    return new IntentOutcome(IntentStatus.RateLimited, RetryAfterSeconds: RetryAfter(lease));
            if (!TryTakeBudget(teamId, dataSetName, settings.DailyCallBudget))
                return new IntentOutcome(IntentStatus.BudgetSpent, RetryAfterSeconds: SecondsToUtcMidnight());

            var searchedFor = Search(engine, dataSetName, teamId, searched, fields);
            if (askPhrase != null) focusedHits = searchedFor.Hits;
            var state = State(site, text, searchedFor);
            onState?.Invoke(state);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.JudgeTimeoutMs);
            try
            {
                var answer = await _models.JudgeAsync(state, SearchOrAskRecipes.Questions(settings.Kind), timeout.Token);
                if (_cache.Count > MaxCached) _cache.Clear();
                _cache[cacheKey] = answer;
                return new IntentOutcome(IntentStatus.Ok, Result(answer[SearchOrAskRecipes.Asking] ?? 0, answer[SearchOrAskRecipes.Answerable], false,
                    cost: answer.Cost, criteria: answer[SearchOrAskRecipes.Criteria]));
            }
            // A judge that is slow, out of credit or down means no offer, never a failed search:
            // p = 0 with the reason, and the search the visitor already has is untouched.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new IntentOutcome(IntentStatus.Ok, Result(0, null, false, "timeout"));
            }
            catch (SearchOrAskModelException ex)
            {
                _logger.LogWarning("Search or ask judge failed for {Team}/{Dataset}: {Message}", teamId, dataSetName, ex.Message);
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

        /// <summary>What the search finds for a text, as the judge reads it, for the console's
        /// tester: the hits by title, section and snippet. No model call, so it costs nothing.</summary>
        public SearchSummary Results(string teamId, string dataSetName, IServerSearchEngine engine, string text, SearchOrAskSettings settings) =>
            Search(engine, dataSetName, teamId, SearchTextFor(text, settings), Fields(settings, engine.GetFieldList()));

        /// <summary>What the list searches for a typed text: what the question is about, its Ask
        /// phrases taken off, while <see cref="SearchOrAskSettings.RefocusWhileTyping"/> is on; else
        /// the text as typed. The page and the judge search the same.</summary>
        public static string SearchTextFor(string text, SearchOrAskSettings settings) =>
            settings.RefocusWhileTyping ? SearchOrAskPhrases.Strip(text, settings.AskPhrases ?? SearchOrAskPhrases.Default).Text : text;

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
                string.Join(separator, names.Select(n => FieldAt(doc, n) switch
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

        /// <summary>A record's field by the engine's name for it, which for a nested field is a dotted
        /// path ("Kommune.Navn").</summary>
        private static JsonNode? FieldAt(JsonNode doc, string path)
        {
            JsonNode? n = doc;
            foreach (var part in path.Split('.')) n = n is JsonObject o ? o[part] : null;
            return n;
        }

        /// <summary>A record's field as text, for tests and the console: null when it is absent.</summary>
        public static string? ReadField(JsonNode doc, string path) => FieldAt(doc, path)?.ToString();

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
                // The first call since the server started reads the day so far from the statistics,
                // so a restart does not start the day over and let it run past its budget.
                var current = _budget.GetOrAdd(key, _ => (today, CallsRecorded(teamId, dataSetName, today)));
                var calls = current.Day == today ? current.Calls : 0;
                if (calls >= budget) { RecordBudget(teamId, dataSetName, today, budget, refused: true); return false; }
                if (_budget.TryUpdate(key, (today, calls + 1), current)) { RecordBudget(teamId, dataSetName, today, budget, refused: false); return true; }
            }
        }

        private static long BudgetDay(DateOnly day) => day.DayNumber - new DateOnly(1970, 1, 1).DayNumber;

        private int CallsRecorded(string teamId, string dataSetName, DateOnly day)
        {
            try { return _statistics is { Enabled: true, Store: { } s } ? (int)s.BudgetCalls(teamId, dataSetName, BudgetDay(day)) : 0; }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read today's Search or ask calls"); return 0; }
        }

        /// <summary>Every call against the budget, taken or turned away, for the statistics.</summary>
        private void RecordBudget(string teamId, string dataSetName, DateOnly day, int budget, bool refused) =>
            Writer?.RecordBudget(new AskBudgetRow(teamId, dataSetName, BudgetDay(day), budget, refused,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        /// <summary>The cost of a call with no statistics row of its own, on today's budget day.</summary>
        private void RecordCost(string teamId, string dataSetName, int budget, double cost) =>
            Writer?.RecordBudget(new AskBudgetRow(teamId, dataSetName, BudgetDay(DateOnly.FromDateTime(DateTime.UtcNow)), budget,
                Refused: false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Call: false, Cost: cost));

        /// <summary>Calls made today against a dataset's budget, for the console: the count in
        /// memory, or before the first call since a restart, the statistics' record of the day.</summary>
        public int CallsToday(string teamId, string dataSetName)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (_budget.TryGetValue($"{teamId}\0{dataSetName}", out var b)) return b.Day == today ? b.Calls : 0;
            return CallsRecorded(teamId, dataSetName, today);
        }

        private static int SecondsToUtcMidnight() =>
            (int)Math.Ceiling((DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds);

        private static int RetryAfter(RateLimitLease lease) =>
            lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : 60;

        // ── Ask ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Whether an ask may proceed: the same gates as intent, against the ask limit.
        /// Ask counts against the daily budget too: it is the expensive call.</summary>
        public IntentOutcome CanAsk(string teamId, string dataSetName, string clientAddress, SearchOrAskSettings? trial = null)
        {
            if (!Available) return new IntentOutcome(IntentStatus.Unavailable);
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
            Func<string, object, Task> send, CancellationToken ct, SearchOrAskSettings? trial = null,
            string? askId = null, SearchOrAskOrigin? origin = null, IReadOnlyList<AskTurn>? history = null)
        {
            history = Conversation(history);
            var started = Stopwatch.GetTimestamp();
            long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var settings = trial ?? Settings(teamId, dataSetName);
            var fields = Fields(settings, engine.GetFieldList());
            var model = Setup.AnswerModelFor(settings.Answers);
            var outcome = "stopped";
            long? firstText = null;
            var usage = new AnswerUsage();
            List<long> merged = [];
            Task<JudgeAnswer?>? answerability = null;
            // A catalogue's answer puts its list on the page; whether that list could answer the
            // question is its score, asked after the answer is out, for the statistics only.
            // ("the oldest movie" is sorted by year, so a good list holds it; a low score is an
            // answer that missed.) Until Oct 2026 a catalogue's answers had no score at all.
            Task<JudgeAnswer?>? ListAnswerability(List<long> keys) =>
                origin != null && Writer != null && keys.Count > 0
                    ? AnswerabilityAsync(SiteDescription(teamId, dataSetName, settings), question,
                        new SearchSummary(keys.Count, true, keys.Take(ResultsForJudge)
                            .Select(key => Describe(engine, key, fields, SnippetChars, forAnswer: false)).ToList()), settings.Kind)
                    : null;
            try
            {
                // A catalogue's description is answered with what it finds; a question about one item
                // (no filter asked for) goes on to the ordinary answer below.
                // With answers: best, a catalogue's question goes to the agent, which searches until it has
                // the answer and puts the list on the page; it falls back to the quick summary below.
                if (settings.Kind == SearchOrAskRecipes.Catalogue
                    && settings.Answers == SearchOrAskSettings.BestAnswers
                    && await AgentAnswerAsync(teamId, dataSetName, engine, settings, fields, question, history, model, askId, send, started, ct)
                        is { } agent)
                {
                    outcome = agent.Outcome;
                    merged = agent.Keys;
                    firstText = agent.FirstTextMs;
                    usage.Cost = agent.Cost;
                    answerability = ListAnswerability(merged);
                    return;
                }
                if (settings.Kind == SearchOrAskRecipes.Catalogue)
                {
                    CatalogueOutcome? catalogue;
                    try
                    {
                        catalogue = await CatalogueAnswerAsync(teamId, dataSetName, engine, settings, fields, question, history, model, askId, send, started, ct);
                    }
                    catch (Exception ex) when (ex is SearchOrAskModelException or HttpRequestException)
                    {
                        outcome = "failed";
                        _logger.LogWarning("Search or ask catalogue answer failed for {Team}/{Dataset}: {Error}", teamId, dataSetName, ex.Message);
                        await send("error", new { code = ex is SearchOrAskModelException { Status: 429 } ? "upstreamBusy" : "failed", message = "The answer could not be written." });
                        return;
                    }
                    if (catalogue != null)
                    {
                        outcome = catalogue.Outcome;
                        merged = catalogue.Keys;
                        firstText = catalogue.FirstTextMs;
                        usage.Cost = catalogue.Cost;
                        answerability = ListAnswerability(merged);
                        return;
                    }
                }

                (var typedSearch, merged) = AnswerSources(engine, dataSetName, teamId, question, fields, history);
                if (merged.Count == 0)
                {
                    outcome = "nothingFound";
                    await send("error", new { code = "nothingFound", message = "The search found nothing to answer from." });
                    return;
                }

                // Whether these sources could answer the question, asked of the judge beside the
                // answer (one more call, about $0.00004): a low score is a question the content
                // does not cover, which is what the statistics list first. Only when recorded.
                // On the sources the answer is written from, in their order, not on the typed
                // search: "is there a limit on how many documents I can index for free" found
                // Licensing through its keywords only, and judged on the typed search scored 0.13
                // for a question the answer then answered in full.
                if (origin != null && Writer != null)
                    answerability = AnswerabilityAsync(SiteDescription(teamId, dataSetName, settings), question,
                        typedSearch with
                        {
                            Top = merged.Take(ResultsForJudge).Select(key => Describe(engine, key, fields, SnippetChars, forAnswer: false)).ToList(),
                        }, settings.Kind);

                // The sources in full (up to a ceiling), not the judge's snippets: the answer is written from them.
                var sources = merged.Select((key, i) => (N: i + 1, Doc: Describe(engine, key, fields, SourceChars, forAnswer: true))).ToList();
                await send("sources", sources.Select(s => new AskSource(s.N, s.Doc.Item1, s.Doc.Item2)).ToArray());

                // A catalogue's records are made of fields, not prose: each source also carries its facts
                // on the filter fields (pupils, staff, grades, type), or a question about one item could
                // only be answered from whatever the snippet fields happen to be.
                var facts = settings.Kind == SearchOrAskRecipes.Catalogue ? FilterFields(teamId, dataSetName, engine).Fields : null;
                var user = new StringBuilder();
                foreach (var (n, doc) in sources)
                {
                    user.Append($"[{n}] {doc.Item2}");
                    if (doc.Item3.Length > 0) user.Append($" ({doc.Item3})");
                    user.Append('\n').Append(doc.Item4);
                    if (facts is { Count: > 0 } && Facts(Parse(engine.GetJsonDataOfKey(doc.Item1)), facts) is { Length: > 0 } f)
                        user.Append(doc.Item4.Length > 0 ? "\n" : "").Append("Facts: ").Append(f);
                    user.Append("\n\n");
                }
                user.Append("Question: ").Append(question);
                // Said again last, next to the question: from the system prompt alone, a site description
                // naming its readers' languages still won 1 time in 10 (11 Oct 2026); with this, 0 in 10.
                user.Append("\n\n").Append(SearchOrAskRecipes.LanguageReminder(settings));

                try
                {
                    await foreach (var delta in _models.AnswerAsync(model, SearchOrAskRecipes.AnswerSystemPrompt(SiteDescription(teamId, dataSetName, settings), settings),
                                       user.ToString(), history, usage, ct))
                    {
                        firstText ??= Ms();
                        await send("text", new { delta });
                    }
                    outcome = "answered";
                    await send("done", new { model, askId, latencyMs = Ms() });
                }
                catch (SearchOrAskModelException ex)
                {
                    outcome = "failed";
                    _logger.LogWarning("Search or ask answer failed for {Team}/{Dataset}: {Message}", teamId, dataSetName, ex.Message);
                    await send("error", new { code = ex.Status == 429 ? "upstreamBusy" : "failed", message = "The answer could not be written." });
                }
                catch (HttpRequestException ex)
                {
                    outcome = "failed";
                    _logger.LogWarning(ex, "Search or ask answer model unreachable for {Team}/{Dataset}", teamId, dataSetName);
                    await send("error", new { code = "failed", message = "The answer could not be written." });
                }
            }
            finally
            {
                // Recorded however it ended, a visitor who went away included ("stopped").
                if (origin != null && Writer is { } w)
                {
                    var judged = answerability == null ? null : await answerability;
                    w.RecordAsk(new AskEventRow(askId ?? Guid.NewGuid().ToString("N"), teamId, dataSetName, Blank(origin.QueryId),
                        question, model, string.Join(',', merged), outcome, firstText, Ms(),
                        Sum(usage.Cost, judged?.Cost), judged?[SearchOrAskRecipes.Answerable],
                        Blank(origin.Subject), Blank(origin.Session), origin.Source,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), origin.Counted));
                }
            }
        }

        public const int MaxHistoryTurns = 3;
        public const int MaxHistoryAnswer = 4000;

        /// <summary>The conversation as the models get it: the last three turns, blank ones dropped,
        /// each answer cut to 4,000 characters and stripped of its [n] marks, which named the
        /// sources of that turn and would point at the wrong records in this one.</summary>
        public static IReadOnlyList<AskTurn> Conversation(IReadOnlyList<AskTurn>? history) =>
            (history ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Question) && !string.IsNullOrWhiteSpace(t.Answer))
                .TakeLast(MaxHistoryTurns)
                .Select(t => new AskTurn(Clip(t.Question.Trim(), MaxQuestion),
                    Clip(Regex.Replace(t.Answer, @"\s*\[\d+\]", "").Trim(), MaxHistoryAnswer), t.Shown))
                .ToList();

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

        /// <summary>The search behind a follow-up: the words of the question before it and of this one,
        /// within what a search may be sent.</summary>
        public static string ContextSearch(string previous, string question)
        {
            var text = $"{KeywordsOf(previous) ?? previous} {KeywordsOf(question) ?? question}".Trim();
            return text.Length <= MaxText ? text : text[..MaxText];
        }

        private static double? Sum(double? a, double? b) => a == null && b == null ? null : (a ?? 0) + (b ?? 0);

        /// <summary>The records an answer is written from: the question as typed, its keywords and,
        /// in a conversation, the previous question's words with this one's, interleaved so each
        /// contributes its best. The typed search comes back too, for its hit count.</summary>
        private static (SearchSummary Typed, List<long> Keys) AnswerSources(IServerSearchEngine engine, string dataSetName, string teamId,
            string question, SearchOrAskFields fields, IReadOnlyList<AskTurn> history)
        {
            var typedSearch = Search(engine, dataSetName, teamId, question, fields, SourcesForAnswer, truncate: false);
            var keywords = KeywordsOf(question) is { } k ? Search(engine, dataSetName, teamId, k, fields, SourcesForAnswer, truncate: false).Top : [];
            // A follow-up ("and over HTTP?") names too little to search on alone: the previous
            // question's words and this one's together find what the conversation is about.
            var context = history.Count > 0
                ? Search(engine, dataSetName, teamId, ContextSearch(history[^1].Question, question), fields, SourcesForAnswer, truncate: false).Top
                : [];
            List<long> merged = [];
            var lists = new[] { context, keywords, typedSearch.Top };
            for (int i = 0; merged.Count < SourcesForAnswer && lists.Any(l => i < l.Count); i++)
                foreach (var list in lists)
                    if (merged.Count < SourcesForAnswer && i < list.Count && !merged.Contains(list[i].Key)) merged.Add(list[i].Key);
            return (typedSearch, merged);
        }

        /// <summary>The judge's answerable question alone, on the question and what its search
        /// found. Never throws: no score is the outcome of a judge that fails, within its timeout.</summary>
        private async Task<JudgeAnswer?> AnswerabilityAsync(string site, string question, SearchSummary search, string kind)
        {
            using var timeout = new CancellationTokenSource(_options.JudgeTimeoutMs);
            try
            {
                var questions = SearchOrAskRecipes.Questions(kind)
                    .Where(q => q.Key == SearchOrAskRecipes.Answerable).ToDictionary(q => q.Key, q => q.Value);
                return await _models.JudgeAsync(State(site, question, search), questions, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SearchOrAskModelException or HttpRequestException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _intentLimiter.Dispose();
            _askLimiter.Dispose();
        }
    }
}
