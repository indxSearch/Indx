using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Indx.Api;
using Indx.Http;
using IndxServer.Engine;
using IndxServer.Models;

namespace IndxServer.Services.Observr
{
    /// <summary>A record named for Observr: its key and the name the owner sees for it.</summary>
    public sealed record ObservrNamed(long Key, string Name);
    public sealed record ObservrWordMatches(string Word, List<ObservrNamed> Matches);

    /// <summary>A search that found nothing, with the evidence Observr judges it by. Gathered here,
    /// since only the instance has the index: the same search with strictness off, and what each
    /// word finds alone. The format is the contract with IndxObservr.</summary>
    public sealed record ObservrZeroHitAsk(string Kind, string Team, string Dataset, string Site, List<string> Fields,
        string Query, long Searches, List<ObservrNamed> NearMatches, List<ObservrWordMatches> Words);

    /// <summary>One field as Observr reviews it: its roles today and what the records hold in it.
    /// <see cref="Filled"/> is of the sample; <see cref="Distinct"/> and <see cref="TopValues"/>
    /// (each with its count) are over <see cref="ValuesOver"/> records: every record for a
    /// facetable field, whose facets count the whole dataset, the sample for the rest. Values are
    /// the top few only, so a review sends little of the data.</summary>
    public sealed record ObservrFieldEvidence(string Name, string Type, bool IsArray, bool Searchable, bool Filterable, bool Facetable,
        bool Sortable, float Weight, int Filled, int Distinct, List<string> TopValues, double? Min, double? Max, int AvgLength, long ValuesOver);

    /// <summary>The dataset's setup for review: every field with its evidence, the key field, and
    /// what visitors searched for lately, which says what the fields are expected to answer.</summary>
    public sealed record ObservrFieldReviewAsk(string Kind, string Team, string Dataset, string Site, string KeyField, long Records,
        int Sampled, List<ObservrFieldEvidence> FieldEvidence, List<string> TopQueries);

    /// <summary>What Search or ask runs with today, for the setup help: the settings, the fields the
    /// judge sees once the automatic choice is applied, and the query parameters.</summary>
    public sealed record ObservrSearchOrAskCurrent(bool Shown, bool Enabled, string Kind, string? SiteDescription, string EffectiveSite,
        string? TitleField, List<string>? SectionFields, List<string>? SnippetFields, string? EffectiveTitle, List<string> EffectiveSection,
        List<string> EffectiveSnippet, string? FilterNotes, string Answers, string? AnswerInstructions, string? AnswerLanguage, double Strong, double Invite,
        int? CoverageDepth, bool? Truncate, SearchOrAsk.SearchOrAskPhrases AskPhrases, bool AskPhrasesAreDefault,
        bool RefocusWhileTyping, bool InviteOnAskPhrase);

    /// <summary>"Help me set this up" on the Search or ask tab: the dataset as the field review sees it,
    /// a few record names, the queries, and the settings as they stand. No record bodies.</summary>
    public sealed record ObservrSearchOrAskSetupAsk(string Kind, string Team, string Dataset, string Site, List<string> Fields, long Records,
        int Sampled, List<ObservrFieldEvidence> FieldEvidence, List<string> Names, List<ObservrSummaryQuery> TopQueries,
        List<string> ZeroHitQueries, ObservrSearchOrAskCurrent Current);

    /// <summary>A change to one field's roles: null leaves that role as it is.</summary>
    public sealed record ObservrFieldChange(string Name, bool? Searchable, bool? Filterable, bool? Facetable, bool? Sortable, float? Weight)
    {
        /// <summary>The change in words: "Elevtall: sortable", "Navn: weight 2, no longer facetable".</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                Role(Searchable, "searchable"); Role(Filterable, "filterable"); Role(Facetable, "facetable"); Role(Sortable, "sortable");
                if (Weight != null) parts.Add($"weight {Weight.Value.ToString("0.##", CultureInfo.InvariantCulture)}");
                return parts.Count == 0 ? "no change" : string.Join(", ", parts);
                void Role(bool? v, string role) { if (v == true) parts.Add(role); else if (v == false) parts.Add($"no longer {role}"); }
            }
        }
    }

    /// <summary>A record with how often visitors chose it for a query, and where in the list it stood.</summary>
    public sealed record ObservrChosen(long Key, string Name, long Selects, double? AvgPosition);

    /// <summary>A query visitors search for often and rarely click on, with what the search returns
    /// for it now and what the few who clicked chose. The format is the contract with IndxObservr.</summary>
    public sealed record ObservrLowClickAsk(string Kind, string Team, string Dataset, string Site, List<string> Fields, string Query,
        long Searches, long ClickedSearches, long Selects, double? AvgPosition, long Uncovered, List<ObservrNamed> Results, List<ObservrChosen> Chosen);

    public sealed record ObservrZeroHitBrief(string Query, long Searches, List<ObservrNamed> NearMatches);
    public sealed record ObservrFuzzyBrief(string Query, long Searches, string? Chosen);
    /// <summary>What a synonym list could be built from: the queries that found nothing with their near
    /// matches, the fuzzy finds with what was chosen, and the entries the list already has.</summary>
    public sealed record ObservrSynonymsAsk(string Kind, string Team, string Dataset, string Site, List<string> Fields,
        List<string[]> Existing, List<ObservrZeroHitBrief> ZeroHits, List<ObservrFuzzyBrief> Fuzzy);

    public sealed record ObservrDay(DateOnly Date, long Searches, long ZeroHits, long ClickedSearches);
    public sealed record ObservrChangeBrief(DateTime AtUtc, string Kind, string Summary);
    /// <summary>A change the owners made, with the days around it: what the figures did before and after.</summary>
    public sealed record ObservrChangeAsk(string Kind, string Team, string Dataset, string Site, ObservrChangeBrief Change,
        List<ObservrDay> Before, List<ObservrDay> After, List<ObservrSummaryQuery> TopBefore, List<ObservrSummaryQuery> TopAfter);

    /// <summary>A change to the dataset's Search or ask settings: null leaves that setting as it is.
    /// Never the switches (Shown, Enabled, the budget): switching on stays the owner's. Ask phrases only
    /// grow: <see cref="AddLeadIns"/> and <see cref="AddFrames"/> join the list in effect, so an owner's
    /// own edits are never undone and a yes means just the phrases named.</summary>
    public sealed record ObservrSearchOrAskChange(string? Kind, string? SiteDescription, string? TitleField, List<string>? SectionFields,
        List<string>? SnippetFields, string? FilterNotes, string? Answers, string? AnswerInstructions = null, string? AnswerLanguage = null,
        List<string>? AddLeadIns = null, List<string>? AddFrames = null)
    {
        [JsonIgnore]
        public IEnumerable<string> Parts
        {
            get
            {
                if (Kind != null) yield return $"kind {Kind}";
                if (TitleField != null) yield return $"title {TitleField}";
                if (SectionFields != null) yield return $"section {(SectionFields.Count == 0 ? "none" : string.Join(", ", SectionFields))}";
                if (SnippetFields != null) yield return $"snippet {(SnippetFields.Count == 0 ? "none" : string.Join(", ", SnippetFields))}";
                if (Answers != null) yield return $"answers {Answers}";
                if (AnswerLanguage != null) yield return $"answers in {AnswerLanguage}";
                if (SiteDescription != null) yield return "about the site";
                if (FilterNotes != null) yield return "notes about the fields";
                if (AnswerInstructions != null) yield return "answer instructions";
                if (AddLeadIns is { Count: > 0 }) yield return $"Ask phrases: start with {string.Join(", ", AddLeadIns.Select(p => $"\"{p}\""))}";
                if (AddFrames is { Count: > 0 }) yield return $"Ask phrases: ask for {string.Join(", ", AddFrames.Select(p => $"\"{p}\""))}";
            }
        }
    }

    /// <summary>A change to the dataset's query parameters: null leaves that one as it is.</summary>
    public sealed record ObservrQueryParametersChange(int? CoverageDepth, bool? Truncate)
    {
        [JsonIgnore]
        public IEnumerable<string> Parts
        {
            get
            {
                if (CoverageDepth != null) yield return $"coverage depth {CoverageDepth}";
                if (Truncate != null) yield return Truncate.Value ? "truncation on" : "truncation off";
            }
        }
    }

    /// <summary>A proposal Observr made, which this instance can carry out after a yes: a synonym
    /// (<see cref="Terms"/>), a field change (<see cref="Field"/>), Search or ask settings
    /// (<see cref="SearchOrAsk"/>) or query parameters (<see cref="QueryParameters"/>).</summary>
    public sealed record ObservrProposal(string Kind, string[] Terms, string Direction, string Reason, ObservrFieldChange? Field = null,
        ObservrSearchOrAskChange? SearchOrAsk = null, ObservrQueryParametersChange? QueryParameters = null)
    {
        /// <summary>What this server can carry out, sent to Observr as <see cref="AcceptsHeader"/> on
        /// every request: the kinds, and as <c>kind.part</c> each part added to a kind after it first
        /// shipped. Observr is deployed ahead of every installed server and narrows what it proposes
        /// to this list. A part added here must be applied in the same change, or Observr sends it
        /// and a yes leaves it out.</summary>
        public static readonly string[] Accepted = [Synonym, FieldKind, SearchOrAskKind, QueryParametersKind, "searchOrAsk.askPhrases"];
        public const string AcceptsHeader = "X-Indx-Observr-Accepts";

        /// <summary>Whether a yes would change something. Reading drops JSON this server does not
        /// know, so a kind from a newer Observr, or a proposal whose only content is a newer part,
        /// arrives with nothing in it; kept, it would show as an empty suggestion and be marked
        /// accepted with nothing done.</summary>
        [JsonIgnore]
        public bool CanApply => Kind switch
        {
            Synonym => Terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2,
            FieldKind => Field != null,
            SearchOrAskKind => SearchOrAsk?.Parts.Any() == true,
            QueryParametersKind => QueryParameters?.Parts.Any() == true,
            _ => false,
        };

        /// <summary>The proposal as a sentence an owner reads at a glance: the words before the
        /// names, the names (fields or terms, drawn as chips), the words after. "Make [Pokedex
        /// number] sortable"; "Treat [bike] [bicycle] as the same word"; "Change Search or ask:
        /// [kind catalogue] [title Navn]".</summary>
        [JsonIgnore]
        public (string Before, IReadOnlyList<string> Names, string After) Sentence
        {
            get
            {
                switch (Kind)
                {
                    case Synonym:
                        return (Terms.Length == 1 ? "Add the synonym" : "Treat as the same word:", Terms, "");
                    case FieldKind when Field != null:
                    {
                        var c = Field;
                        var on = new List<string>(); var off = new List<string>();
                        Role(c.Searchable, "searchable"); Role(c.Filterable, "filterable"); Role(c.Facetable, "facetable"); Role(c.Sortable, "sortable");
                        var tail = new List<string>();
                        if (on.Count > 0) tail.Add(Join(on));
                        if (off.Count > 0) tail.Add("no longer " + Join(off));
                        var weight = c.Weight is { } w ? $"weight {w.ToString("0.##", CultureInfo.InvariantCulture)}" : null;
                        if (tail.Count == 0 && weight != null) return ("Give", [c.Name], weight);
                        if (weight != null) tail.Add("with " + weight);
                        return ("Make", [c.Name], tail.Count == 0 ? "as it is" : string.Join(", ", tail));
                        void Role(bool? v, string role) { if (v == true) on.Add(role); else if (v == false) off.Add(role); }
                        static string Join(List<string> roles) => roles.Count == 1 ? roles[0] : string.Join(", ", roles[..^1]) + " and " + roles[^1];
                    }
                    case SearchOrAskKind when SearchOrAsk != null:
                        return ("Change Search or ask:", SearchOrAsk.Parts.ToList(), "");
                    case QueryParametersKind when QueryParameters != null:
                        return ("Change the query parameters:", QueryParameters.Parts.ToList(), "");
                    default:
                        return (Label, [], "");
                }
            }
        }

        public const string Synonym = "synonym";
        public const string FieldKind = "field";
        public const string SearchOrAskKind = "searchOrAsk";
        public const string QueryParametersKind = "queryParameters";

        /// <summary>What the proposal is, as a label, for a panel that is not a synonym's or a field's.</summary>
        [JsonIgnore]
        public string Label => Kind switch
        {
            Synonym => "Synonym",
            FieldKind => "Field",
            SearchOrAskKind => "Search or ask",
            QueryParametersKind => "Query parameters",
            _ => Kind,
        };

        /// <summary>The proposal as chips, for a kind without a panel of its own.</summary>
        public IEnumerable<string> Describe() => Kind switch
        {
            Synonym => Terms,
            FieldKind when Field != null => [Field.Name + ": " + Field.Summary],
            SearchOrAskKind when SearchOrAsk != null => SearchOrAsk.Parts,
            QueryParametersKind when QueryParameters != null => QueryParameters.Parts,
            _ => [],
        };
    }

    /// <summary>A finding's proposals were carried out: the dataset, and the kinds of change.</summary>
    public sealed record ObservrApplied(string TeamId, string DataSetName, IReadOnlySet<string> Kinds);

    public sealed record ObservrAnswer(int AskId, string Outcome, string Answer, List<ObservrProposal> Proposals, string Model);

    /// <summary>One thing worth the owners' attention in a digest: a title, the text, which tab it
    /// is about (statistics, fields, synonyms, search-or-ask) and any proposal.</summary>
    public sealed record ObservrDigestItem(string Title, string Text, string Where, List<ObservrProposal> Proposals);
    public sealed record ObservrDigest(int AskId, string Outcome, List<ObservrDigestItem> Items, string Model);

    /// <summary>What came of a digest: a status as an ask's, and the findings it became.</summary>
    public sealed record ObservrDigestOutcome(ObservrStatus Status, string? Reason = null, List<long>? FindingIds = null)
    {
        public int Count => FindingIds?.Count ?? 0;
    }

    public enum ObservrStatus { Ok, NoToken, UnknownToken, NoSubscription, Unavailable, Failed }

    public static class ObservrText
    {
        /// <summary>A field change as chips: a role switched on (true), one switched off, a weight
        /// with the value it has today when known.</summary>
        public static List<(string Text, bool On)> Parts(ObservrFieldChange c, float? currentWeight)
        {
            var parts = new List<(string, bool)>();
            Role(c.Searchable, "searchable"); Role(c.Filterable, "filterable"); Role(c.Facetable, "facetable"); Role(c.Sortable, "sortable");
            if (c.Weight is { } w)
                parts.Add((currentWeight is { } n ? $"weight {n:0.##} → {w:0.##}" : $"weight {w:0.##}", true));
            return parts;
            void Role(bool? v, string role) { if (v == true) parts.Add((role, true)); else if (v == false) parts.Add(($"{role} off", false)); }
        }

        /// <summary>A refusal as the console says it; <paramref name="nothingYet"/> is the dataset-not-ready
        /// sentence's tail ("to look at", "to review").</summary>
        public static string Refusal(ObservrOutcome o, string nothingYet) => o.Status switch
        {
            ObservrStatus.NoToken => "This instance has no licence token, so it cannot reach Observr. Add one under Admin, License.",
            ObservrStatus.UnknownToken => "Observr does not recognise this instance's licence token. Check it under Admin, License.",
            ObservrStatus.NoSubscription => o.Reason == "expired"
                ? "This licence's Observr access has ended, so Observr is not included."
                : "Observr is part of the Pro and Enterprise subscriptions. This instance's licence does not include it.",
            ObservrStatus.Unavailable => "Observr cannot be reached right now. Try again in a moment.",
            _ => o.Reason == "datasetNotReady" ? $"The dataset is not ready to search, so there is nothing {nothingYet} yet."
                : o.Reason == "noStatistics" ? "This instance keeps no statistics, so there is nothing to summarise."
                : o.Reason == "quota" ? "This licence has used today's Observr allowance. It is back tomorrow."
                : o.Reason == "tooLong" ? $"The question is too long for Observr: up to {ObservrChat.MaxQuestion:N0} characters."
                : "Observr could not answer this time.",
        };
    }

    /// <summary>What came of asking: an answer, or why there is none, in a word the console turns
    /// into a sentence (<see cref="Reason"/> is Observr's own word where it gave one).</summary>
    public sealed record ObservrOutcome(ObservrStatus Status, ObservrAnswer? Answer = null, string? Reason = null, long FindingId = 0);

    /// <summary>The wire of one chat turn, as IndxObservr's ChatResponse.</summary>
    public sealed record ObservrChatToolUse(string Id, string Name, Dictionary<string, JsonElement> Input, string? Result = null);
    public sealed record ObservrChatReply(int AskId, string Text, List<ObservrChatToolUse> ToolUses, List<ObservrProposal> Proposals, string Model, string? Problem);

    /// <summary>What a proposed synonym would do for the query, measured here before anyone says
    /// yes: how many records the search then finds, and the first few by name.</summary>
    public sealed record ObservrSynonymTest(int Found, List<string> Names);

    /// <summary>
    /// The instance's side of Observr: gathers what a question is about, sends it with the licence
    /// token, and carries out a proposal after the owner's yes through the same paths the console
    /// uses, so the change is recorded as any other. Observr never writes to an instance.
    /// </summary>
    internal sealed class ObservrClient(IHttpClientFactory http, ObservrOptions options, InstanceSettingsService settings,
        IConfiguration configuration, IDatasetEngines engines, DatasetMetadataStore metadata, StatisticsService statistics,
        ObservrFindingStore findings, ObservrSummaryBuilder summaries, SearchOrAsk.SearchOrAskStore searchOrAsk,
        SearchOrAsk.SearchOrAskService searchOrAskService, QueryParameterStore queryParameters, ILogger<ObservrClient> logger)
    {
        public const string HttpClientName = "Observr";
        public const string ZeroHitKind = "zeroHitQuery";
        public const string FieldReviewKind = "fieldReview";
        public const string DigestKind = "digest";
        public const string LowClickKind = "lowClickThrough";
        public const string SynonymsKind = "synonyms";
        public const string ChangeKind = "changeEffect";
        public const string SetupKind = "searchOrAskSetup";
        private const int SetupNames = 12;
        private const int ChangeDays = 7;
        private static readonly string[] DigestTabs = ["statistics", "fields", "synonyms", "search-or-ask"];
        private const int NearMatches = 10;
        private const int PerWord = 3;
        private const int MaxWords = 8;
        /// <summary>The records a field review reads: enough for fill rates and value counts, few
        /// enough to read in a moment. Spread over the whole dataset, never the first few hundred:
        /// gyldendal.no's file begins with its teaching materials, and a sample of those had
        /// Observr set up Search or ask for a school catalogue on a dataset that is mostly fiction
        /// (19 Oct 2026).</summary>
        private const int ReviewSample = 300;
        /// <summary>The most records an empty search is asked to list for the spread.</summary>
        private const int SampleWalk = 2_000_000;
        private const int ReviewTopValues = 8;
        private const int ReviewTopQueries = 20;
        private const int ReviewQueryDays = 30;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>Whether this instance can ask at all: it has a licence token.</summary>
        public bool HasToken => Token() != null;

        /// <summary>The last time Observr could not be reached or refused to serve (when, and the
        /// reason), until a call goes through again; and the last time one did. The admin page
        /// reads both, so an outage is visible somewhere other than the log.</summary>
        public ObservrPortalFailure? LastFailure { get; private set; }
        public DateTime? LastOkUtc { get; private set; }

        /// <summary>Asks Observr whether it is up (GET /health), without a token or an ask: the admin
        /// page's Try again. Records the outcome as a call would.</summary>
        public async Task<bool> PingAsync(CancellationToken ct)
        {
            var client = http.CreateClient(HttpClientName);
            try
            {
                using var response = await client.GetAsync($"{options.Url.TrimEnd('/')}/health", ct);
                if (response.IsSuccessStatusCode) { LastOkUtc = DateTime.UtcNow; LastFailure = null; return true; }
                LastFailure = new ObservrPortalFailure(DateTime.UtcNow, $"http{(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                LastFailure = new ObservrPortalFailure(DateTime.UtcNow, "unreachable");
            }
            return false;
        }

        /// <summary>One model turn of the Agent chat (POST /api/chat): the conversation as the
        /// instance keeps it goes up, the model's message comes back. The loop is ObservrChat's.</summary>
        public async Task<(ObservrStatus Status, ObservrChatReply? Reply, string? Reason)> ChatTurnAsync(object payload, string teamId, string dataSetName, CancellationToken ct)
        {
            if (Token() is not { } token) return (ObservrStatus.NoToken, null, null);
            var (status, body, reason) = await PostAsync(token, "/api/chat", payload, teamId, dataSetName, ct);
            if (status != HttpStatusCode.OK) return (Status(status), null, reason);
            var reply = JsonSerializer.Deserialize<ObservrChatReply>(body, Json);
            return reply == null ? (ObservrStatus.Failed, null, "badAnswer") : (ObservrStatus.Ok, reply, null);
        }

        private string? Token()
        {
            if (!string.IsNullOrWhiteSpace(options.Token)) return options.Token.Trim();
            var token = settings.Load().LicenseToken;
            if (string.IsNullOrWhiteSpace(token)) token = configuration["Indx:LicenseToken"];
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        /// <summary>Asks Observr about a query that found nothing.</summary>
        public async Task<ObservrOutcome> AskZeroHitAsync(string teamId, string dataSetName, string query, long searches, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrOutcome(ObservrStatus.NoToken);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var ask = BuildZeroHitAsk(teamId, dataSetName, engine, query, searches);
            return await SendAsync(token, teamId, dataSetName, ZeroHitKind, query.Trim(), ask, ct);
        }

        /// <summary>Asks Observr to review the dataset's field setup ("Review my setup" on the
        /// Fields tab): what it sends is the roles and a summary of a sample, never the records.</summary>
        public async Task<ObservrOutcome> AskFieldReviewAsync(string teamId, string dataSetName, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrOutcome(ObservrStatus.NoToken);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var ask = BuildFieldReviewAsk(teamId, dataSetName, engine);
            return await SendAsync(token, teamId, dataSetName, FieldReviewKind, "", ask, ct);
        }

        /// <summary>"Why isn't this working?" on a query searched often and rarely clicked.</summary>
        public async Task<ObservrOutcome> AskLowClickAsync(string teamId, string dataSetName, QueryStat q, long fromDay, long toDay, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrOutcome(ObservrStatus.NoToken);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready || statistics.Store is not { } store)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var label = StatisticsLabels.For(engine, store.LabelField(teamId, dataSetName));
            var results = (IndxServerInternalApi.Manager.Search(new QueryProxy { Text = q.QueryText, MaxNumberOfRecordsToReturn = 10, EnableCoverage = true }, dataSetName, teamId)
                .Records ?? []).Select(r => new ObservrNamed(r.DocumentKey, label(r.DocumentKey) ?? r.DocumentKey.ToString())).ToList();
            var chosen = store.QueryDocuments(teamId, dataSetName, q.QueryText, fromDay, toDay, 10)
                .Select(d => new ObservrChosen(d.DocumentKey, d.Label ?? label(d.DocumentKey) ?? d.DocumentKey.ToString(), d.Selects,
                    d.Selects > 0 ? Math.Round((double)d.PositionSum / d.Selects, 1) : null)).ToList();
            var ask = new ObservrLowClickAsk(LowClickKind, teamId, dataSetName, Site(teamId, dataSetName), SearchableFields(engine), q.QueryText,
                q.Searches, q.ClickedSearches, q.Selects, q.Selects > 0 ? Math.Round((double)q.PositionSum / q.Selects, 1) : null, q.Uncovered, results, chosen);
            return await SendAsync(token, teamId, dataSetName, LowClickKind, q.QueryText, ask, ct);
        }

        /// <summary>"Suggest synonyms" on the Synonyms tab: from the period's zero hits and fuzzy finds.</summary>
        public async Task<ObservrOutcome> AskSynonymsAsync(string teamId, string dataSetName, int days, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrOutcome(ObservrStatus.NoToken);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready || statistics.Store is not { } store)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var today = store.Today(teamId, dataSetName);
            var label = StatisticsLabels.For(engine, store.LabelField(teamId, dataSetName));
            List<ObservrNamed> Near(string text) => (IndxServerInternalApi.Manager.Search(new QueryProxy
            {
                Text = text, MaxNumberOfRecordsToReturn = 3, EnableCoverage = true,
                CoverageSetup = new CoverageSetupProxy { Truncate = false, IncludePatternMatches = true },
            }, dataSetName, teamId).Records ?? []).Select(r => new ObservrNamed(r.DocumentKey, label(r.DocumentKey) ?? r.DocumentKey.ToString())).ToList();
            var zero = store.TopQueries(teamId, dataSetName, today - days + 1, today, 20, zeroHitsOnly: true)
                .Select(q => new ObservrZeroHitBrief(q.QueryText, q.ZeroHits, Near(q.QueryText))).ToList();
            var fuzzy = store.TopQueries(teamId, dataSetName, today - days + 1, today, 20, uncoveredOnly: true)
                .Select(q => new ObservrFuzzyBrief(q.QueryText, q.Searches, q.MostChosenLabel ?? (q.MostChosenDocument is { } k ? label(k) : null))).ToList();
            var existing = (engines.GetSynonyms(dataSetName, teamId)?.Entries ?? []).Select(e => e.Terms).ToList();
            var ask = new ObservrSynonymsAsk(SynonymsKind, teamId, dataSetName, Site(teamId, dataSetName), SearchableFields(engine), existing, zero, fuzzy);
            return await SendAsync(token, teamId, dataSetName, SynonymsKind, "", ask, ct);
        }

        /// <summary>"Did this help?" on a change the owners made: the week before against the week after.</summary>
        public async Task<ObservrOutcome> AskChangeAsync(string teamId, string dataSetName, ChangeStat change, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrOutcome(ObservrStatus.NoToken);
            if (statistics.Store is not { } store) return new ObservrOutcome(ObservrStatus.Failed, Reason: "noStatistics");
            var tz = store.ZoneOf(teamId, dataSetName);
            var day = StatisticsDays.DayOf(tz, change.Timestamp);
            DateOnly Date(long d) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(StatisticsDays.Start(tz, d)), tz).DateTime);
            // Every day of the range, a quiet one as zeros: the series itself lists only days with traffic.
            List<ObservrDay> Days(long from, long to)
            {
                var byDay = store.TimeSeries(teamId, dataSetName, from, to).ToDictionary(d => d.Day);
                return Enumerable.Range(0, (int)(to - from + 1)).Select(i => from + i)
                    .Select(d => byDay.TryGetValue(d, out var x) ? new ObservrDay(Date(d), x.Searches, x.ZeroHits, x.ClickedSearches) : new ObservrDay(Date(d), 0, 0, 0)).ToList();
            }
            List<ObservrSummaryQuery> Top(long from, long to) => store.TopQueries(teamId, dataSetName, from, to, 10)
                .Select(q => new ObservrSummaryQuery(q.QueryText, q.Searches, q.ZeroHits, q.ClickedSearches,
                    q.Selects > 0 ? Math.Round((double)q.PositionSum / q.Selects, 1) : null, q.MostChosenLabel)).ToList();
            var text = ChangeDescriptions.Describe(change.Kind, change.Summary);
            var ask = new ObservrChangeAsk(ChangeKind, teamId, dataSetName, Site(teamId, dataSetName),
                new ObservrChangeBrief(DateTimeOffset.FromUnixTimeMilliseconds(change.Timestamp).UtcDateTime, change.Kind, text),
                Days(day - ChangeDays, day - 1), Days(day, day + ChangeDays - 1), Top(day - ChangeDays, day - 1), Top(day, day + ChangeDays - 1));
            return await SendAsync(token, teamId, dataSetName, ChangeKind, text, ask, ct);
        }

        /// <summary>"Help me set this up" on the Search or ask tab: what the field review sends, plus a
        /// few record names, the month's queries, and the settings as they stand. Observr proposes the
        /// configuration (kind, what the judge sees, the site sentence, notes, the answer model, field
        /// roles, coverage), never the switches, and leaves thresholds to their measured defaults.</summary>
        public async Task<ObservrOutcome> AskSearchOrAskSetupAsync(string teamId, string dataSetName, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrOutcome(ObservrStatus.NoToken);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var review = BuildFieldReviewAsk(teamId, dataSetName, engine);
            var label = StatisticsLabels.For(engine, statistics.Store?.LabelField(teamId, dataSetName));
            // Names from across the dataset, like the sample: the first dozen records say what the
            // file begins with, not what a record is.
            var names = Spread(teamId, dataSetName, SetupNames).Keys.Select(label).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            var today = statistics.Store?.Today(teamId, dataSetName) ?? 0;
            var top = statistics.Store?.TopQueries(teamId, dataSetName, today - 29, today, 30)
                .Select(q => new ObservrSummaryQuery(q.QueryText, q.Searches, q.ZeroHits, q.ClickedSearches,
                    q.Selects > 0 ? Math.Round((double)q.PositionSum / q.Selects, 1) : null, q.MostChosenLabel)).ToList() ?? [];
            var zero = statistics.Store?.TopQueries(teamId, dataSetName, today - 29, today, 15, zeroHitsOnly: true).Select(q => q.QueryText).ToList() ?? [];

            var settings = searchOrAsk.Load(teamId, dataSetName) ?? new SearchOrAsk.SearchOrAskSettings();
            var effective = SearchOrAsk.SearchOrAskService.Fields(settings, engine.GetFieldList());
            var qp = queryParameters.Load(teamId, dataSetName);
            var current = new ObservrSearchOrAskCurrent(settings.Shown, settings.Enabled, settings.Kind, settings.SiteDescription,
                searchOrAskService.SiteDescription(teamId, dataSetName, settings), settings.TitleField, settings.SectionFields, settings.SnippetFields,
                effective.TitleField, effective.SectionFields.ToList(), effective.SnippetFields.ToList(), settings.FilterNotes, settings.Answers,
                settings.AnswerInstructions, settings.AnswerLanguage, settings.Thresholds.Strong, settings.Thresholds.Invite, qp?.CoverageDepth, qp?.CoverageSetup?.Truncate,
                settings.AskPhrases ?? SearchOrAsk.SearchOrAskPhrases.Default, settings.AskPhrases == null, settings.RefocusWhileTyping, settings.InviteOnAskPhrase);
            var ask = new ObservrSearchOrAskSetupAsk(SetupKind, teamId, dataSetName, review.Site, review.FieldEvidence.Select(f => f.Name).ToList(),
                review.Records, review.Sampled, review.FieldEvidence, names, top, zero, current);
            return await SendAsync(token, teamId, dataSetName, SetupKind, "", ask, ct);
        }

        private static List<string> SearchableFields(IServerSearchEngine engine) => engine.GetFieldList().Where(f => f.Searchable).Select(f => f.Name).ToList();

        /// <summary>The digest: the bounded summary of the last week goes up (<see cref="ObservrSummary"/>),
        /// and each thing Observr finds worth attention comes back as a finding of its own in the
        /// menu. Run daily for every dataset with Observr on, and on the Options tab's button.</summary>
        public async Task<ObservrDigestOutcome> SendDigestAsync(string teamId, string dataSetName, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrDigestOutcome(ObservrStatus.NoToken);
            var summary = summaries.Build(teamId, dataSetName);
            if (summary == null) return new ObservrDigestOutcome(ObservrStatus.Failed, "noStatistics");
            var (status, body, reason) = await PostAsync(token, "/api/summary", summary, teamId, dataSetName, ct);
            if (status != HttpStatusCode.OK) return new ObservrDigestOutcome(Status(status), reason);
            var digest = JsonSerializer.Deserialize<ObservrDigest>(body, Json);
            if (digest == null) return new ObservrDigestOutcome(ObservrStatus.Failed, "badAnswer");
            var ids = new List<long>();
            foreach (var item in digest.Items)
            {
                var where = DigestTabs.Contains(item.Where) ? item.Where : "statistics";
                ids.Add(findings.Add(teamId, dataSetName, $"{DigestKind}/{where}", item.Title.Trim(),
                    new ObservrAnswer(digest.AskId, digest.Outcome, item.Text, item.Proposals, digest.Model), unread: true));
            }
            return new ObservrDigestOutcome(ObservrStatus.Ok, FindingIds: ids);
        }

        private static ObservrStatus Status(HttpStatusCode code) => code switch
        {
            HttpStatusCode.OK => ObservrStatus.Ok,
            HttpStatusCode.Unauthorized => ObservrStatus.UnknownToken,
            HttpStatusCode.PaymentRequired => ObservrStatus.NoSubscription,
            HttpStatusCode.ServiceUnavailable => ObservrStatus.Unavailable,
            _ => ObservrStatus.Failed,
        };

        /// <summary>One request to Observr with the licence token. A network failure is a 503 with
        /// reason <c>unreachable</c>, so callers read one shape.</summary>
        private async Task<(HttpStatusCode Status, string Body, string? Reason)> PostAsync(string token, string path, object payload,
            string teamId, string dataSetName, CancellationToken ct)
        {
            var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Url.TrimEnd('/')}{path}")
            {
                Content = JsonContent.Create(payload, payload.GetType(), options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Which installation this is: Observr tells one licence used from several apart.
            var (instanceId, instanceName) = Instance();
            request.Headers.TryAddWithoutValidation("X-Indx-Instance", instanceId);
            request.Headers.TryAddWithoutValidation("X-Indx-Instance-Name", instanceName);
            request.Headers.TryAddWithoutValidation(ObservrProposal.AcceptsHeader, string.Join(',', ObservrProposal.Accepted));
            // Which release asked, for Observr's support and for knowing when an old one is gone;
            // what it can carry out is the accepts list above, never inferred from this.
            request.Headers.TryAddWithoutValidation("X-Indx-Server-Version", ServerVersion);
            try
            {
                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                string? reason = null;
                try { reason = JsonDocument.Parse(body).RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null; }
                catch (JsonException) { }
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
                    logger.LogWarning("Observr answered {Status} for {Team}/{Dataset}", (int)response.StatusCode, teamId, dataSetName);
                // Health: the service answered, whatever it said about this licence; a 503 is the
                // service (or its portal) being down, which is the one thing the admin page reports.
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable) LastFailure = new ObservrPortalFailure(DateTime.UtcNow, reason ?? "unavailable");
                else { LastOkUtc = DateTime.UtcNow; LastFailure = null; }
                return (response.StatusCode, body, reason ?? (response.StatusCode == HttpStatusCode.OK ? null : $"http{(int)response.StatusCode}"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Observr unreachable");
                LastFailure = new ObservrPortalFailure(DateTime.UtcNow, "unreachable");
                return (HttpStatusCode.ServiceUnavailable, "", "unreachable");
            }
        }

        /// <summary>This build's version as released ("2.0.0-beta.1"), without the commit suffix.</summary>
        internal static readonly string ServerVersion =
            (typeof(ObservrClient).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown").Split('+')[0];

        /// <summary>The installation's id and name; the id is made once and kept in the settings.</summary>
        private (string Id, string Name) Instance()
        {
            var s = settings.Load();
            if (string.IsNullOrWhiteSpace(s.InstanceId))
            {
                s.InstanceId = Guid.NewGuid().ToString("N");
                try { settings.Save(s); } catch (Exception ex) { logger.LogWarning(ex, "Could not keep the instance id"); }
            }
            var name = string.IsNullOrWhiteSpace(s.InstanceName) ? "Indx" : s.InstanceName.Trim();
            return (s.InstanceId!, new string(name.Where(c => c < 128 && !char.IsControl(c)).ToArray()));
        }

        private async Task<ObservrOutcome> SendAsync(string token, string teamId, string dataSetName, string kind, string query, object ask, CancellationToken ct)
        {
            var (status, body, reason) = await PostAsync(token, "/api/ask", ask, teamId, dataSetName, ct);
            {
                switch (status)
                {
                    case HttpStatusCode.OK:
                        var answer = JsonSerializer.Deserialize<ObservrAnswer>(body, Json);
                        if (answer == null) return new ObservrOutcome(ObservrStatus.Failed, Reason: "badAnswer");
                        // A field change on a field this dataset does not have is dropped here: the
                        // proposal would have nothing to apply to.
                        if (kind is FieldReviewKind or SetupKind && engines.FindSearchEngine(dataSetName, teamId) is { } engine)
                        {
                            var names = engine.GetFieldList().Select(f => f.Name).ToHashSet();
                            answer = answer with { Proposals = answer.Proposals.Where(p => p.Kind != ObservrProposal.FieldKind || p.Field != null && names.Contains(p.Field.Name)).ToList() };
                        }
                        // Kept as a finding: in the Observr menu while its proposal is open, in the history after.
                        var id = findings.Add(teamId, dataSetName, kind, query, answer);
                        return new ObservrOutcome(ObservrStatus.Ok, answer, FindingId: id);
                    case HttpStatusCode.Unauthorized:
                        return new ObservrOutcome(ObservrStatus.UnknownToken);
                    case HttpStatusCode.PaymentRequired:
                        return new ObservrOutcome(ObservrStatus.NoSubscription, Reason: reason ?? "noSubscription");
                    case HttpStatusCode.ServiceUnavailable:
                        return new ObservrOutcome(ObservrStatus.Unavailable, Reason: reason ?? "unavailable");
                    default:
                        return new ObservrOutcome(ObservrStatus.Failed, Reason: reason);
                }
            }
        }

        /// <summary>The evidence for a query that found nothing: the dataset's description and
        /// searchable fields, the same search with strictness off (pattern matches the visitor was
        /// not shown), and what each word finds alone. Searches here never reach the statistics.</summary>
        public ObservrZeroHitAsk BuildZeroHitAsk(string teamId, string dataSetName, IServerSearchEngine engine, string query, long searches)
        {
            var label = StatisticsLabels.For(engine, statistics.Store?.LabelField(teamId, dataSetName));
            List<ObservrNamed> Named(IEnumerable<long> keys) => keys.Select(k => new ObservrNamed(k, label(k) ?? k.ToString())).ToList();
            List<long> Keys(string text, int take, bool strict) =>
                (IndxServerInternalApi.Manager.Search(new QueryProxy
                {
                    Text = text,
                    MaxNumberOfRecordsToReturn = take,
                    EnableCoverage = true,
                    CoverageSetup = strict ? null : new CoverageSetupProxy { Truncate = false, IncludePatternMatches = true },
                }, dataSetName, teamId).Records ?? []).Select(r => r.DocumentKey).ToList();

            var near = Named(Keys(query, NearMatches, strict: false));
            var words = Regex.Split(query.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+")
                .Where(w => w.Length >= 2).Distinct().Take(MaxWords)
                .Select(w => new ObservrWordMatches(w, Named(Keys(w, PerWord, strict: true)))).ToList();
            var fields = engine.GetFieldList().Where(f => f.Searchable).Select(f => f.Name).ToList();
            return new ObservrZeroHitAsk(ZeroHitKind, teamId, dataSetName, Site(teamId, dataSetName), fields, query.Trim(), searches, near, words);
        }

        /// <summary>The evidence for a field review: each field's roles and what a sample of the
        /// records holds in it (how many have it, how many distinct values, the commonest few, a
        /// number's range), with the key field and the month's top queries. The sample is read
        /// through an empty search, which never reaches the statistics.</summary>
        public ObservrFieldReviewAsk BuildFieldReviewAsk(string teamId, string dataSetName, IServerSearchEngine engine)
        {
            // An empty search lists records only as a faceted browse, and only where a field is
            // facetable; without one the sample is empty and Observr is told so (Sampled = 0).
            var (keys, facets, total) = Spread(teamId, dataSetName, ReviewSample);
            var docs = keys.Select(k => Parse(engine.GetJsonDataOfKey(k))).Where(d => d != null).ToList();
            var evidence = new List<ObservrFieldEvidence>();
            foreach (var f in engine.GetFieldList())
            {
                var values = docs.SelectMany(d => Leaves(d!, f.Name)).ToList();
                var filled = docs.Count(d => Leaves(d!, f.Name).Any());
                var texts = values.Select(v => v.ToString()).Where(s => s.Length > 0).ToList();
                int distinct;
                List<string> top;
                long over;
                if (facets != null && facets.TryGetValue(f.Name, out var facet))
                {
                    // A facetable field is counted over every record by the search itself: the
                    // sample cannot mislead about what the dataset mostly holds. A JSON null is
                    // its own facet value and reads as no value.
                    var named = facet.Where(v => v.Key != "null").ToList();
                    distinct = named.Count;
                    top = named.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).Take(ReviewTopValues).Select(v => Shown(v.Key, v.Value)).ToList();
                    if (facet.FirstOrDefault(v => v.Key == "null") is { Value: > 0 } none) top.Add(Shown("(none)", none.Value));
                    over = total;
                }
                else
                {
                    distinct = texts.Distinct().Count();
                    // The commonest values only where values repeat (a category, a type): a field
                    // whose values are mostly their own (names, descriptions) sends none, so no
                    // record goes.
                    top = distinct <= texts.Count / 2
                        ? texts.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                            .Take(ReviewTopValues).Select(g => Shown(g.Key, g.Count())).ToList()
                        : [];
                    over = docs.Count;
                }
                var numbers = f.Type == JsonValueKind.Number
                    ? values.Select(v => v.TryGetValue<double>(out var n) ? n : (double?)null).Where(n => n != null).Select(n => n!.Value).ToList() : [];
                evidence.Add(new ObservrFieldEvidence(f.Name, TypeName(f.Type), f.IsArray, f.Searchable, f.Filterable, f.Facetable, f.Sortable, f.Weight,
                    filled, distinct, top, numbers.Count > 0 ? numbers.Min() : null, numbers.Count > 0 ? numbers.Max() : null,
                    texts.Count == 0 ? 0 : (int)texts.Average(s => s.Length), over));
            }
            var today = StatisticsDays.Today(TimeZoneInfo.Utc);
            var queries = statistics.Store?.TopQueries(teamId, dataSetName, today - ReviewQueryDays, today, ReviewTopQueries)
                .Select(q => q.QueryText).ToList() ?? [];
            return new ObservrFieldReviewAsk(FieldReviewKind, teamId, dataSetName, Site(teamId, dataSetName), engine.DocumentFields?.NameOfDocumentKeyField ?? "",
                engine.Status.DocumentCount, docs.Count, evidence, queries);
        }

        /// <summary>A value with its count, as the evidence lists it: "E-bok (10,595)".</summary>
        private static string Shown(string value, int count) =>
            (value.Length > 80 ? value[..80] + "…" : value) + " (" + count.ToString("N0", CultureInfo.InvariantCulture) + ")";

        /// <summary>Up to <paramref name="count"/> record keys spread evenly over the dataset in its
        /// own order, with the facets over every record and how many there are. An empty search
        /// walks the records as they were loaded and facets them all; the first few hundred are
        /// whatever the file begins with, so the spread takes every n-th instead.</summary>
        private static (List<long> Keys, Dictionary<string, KeyValuePair<string, int>[]>? Facets, long Total) Spread(string teamId, string dataSetName, int count)
        {
            var result = IndxServerInternalApi.Manager.Search(new QueryProxy { Text = "", EnableFacets = true, MaxNumberOfRecordsToReturn = SampleWalk }, dataSetName, teamId);
            var all = result.Records ?? [];
            var stride = Math.Max(1, all.Length / Math.Max(1, count));
            var keys = new List<long>();
            for (var i = 0; i < all.Length && keys.Count < count; i += stride) keys.Add(all[i].DocumentKey);
            return (keys, result.Facets, all.Length);
        }

        private string Site(string teamId, string dataSetName)
        {
            var site = metadata.Load(teamId, dataSetName);
            return string.IsNullOrWhiteSpace(site) ? "" : site.Trim();
        }

        internal static string TypeName(JsonValueKind kind) => kind switch
        {
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "true/false",
            JsonValueKind.String => "text",
            _ => kind.ToString().ToLowerInvariant(),
        };

        private static JsonNode? Parse(string? json)
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json); }
            catch (JsonException) { return null; }
        }

        /// <summary>The values at a field's path in a record: a dotted name walks into objects, an
        /// array along the way yields one value per element.</summary>
        private static IEnumerable<JsonValue> Leaves(JsonNode node, string path)
        {
            static IEnumerable<JsonNode?> Flat(JsonNode? n) => n is JsonArray a ? a.AsEnumerable() : new[] { n };
            IEnumerable<JsonNode?> current = new[] { node };
            foreach (var part in path.Split('.'))
                current = current.SelectMany(Flat).Select(n => n is JsonObject o ? o[part] : null).Where(n => n != null);
            return current.SelectMany(Flat).OfType<JsonValue>();
        }

        /// <summary>What the query would find with the proposed synonym, as visitors would get it:
        /// the local test of a proposal, since only the instance can run it.</summary>
        public ObservrSynonymTest TestSynonym(string teamId, string dataSetName, string query, string[] terms)
        {
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null) return new ObservrSynonymTest(0, []);
            var list = new SynonymList { Entries = [new SynonymEntry { Direction = SynonymDirection.Multidirectional, Terms = terms }] };
            var expanded = list.Expand(query);
            var result = IndxServerInternalApi.Manager.Search(new QueryProxy { Text = expanded, MaxNumberOfRecordsToReturn = 5, EnableCoverage = true }, dataSetName, teamId);
            var label = StatisticsLabels.For(engine, statistics.Store?.LabelField(teamId, dataSetName));
            var records = result.Records ?? [];
            return new ObservrSynonymTest(records.Length, records.Select(r => label(r.DocumentKey) ?? r.DocumentKey.ToString()).ToList());
        }

        /// <summary>After a yes has carried a finding's proposals out: which dataset, and the kinds of
        /// change (<see cref="ObservrProposal.Synonym"/>, <see cref="ObservrProposal.FieldKind"/>, …).
        /// The console's tabs reload what they show from it: a yes in the Agent panel, the Observr
        /// menu or the Status tab changed the saved settings under a tab that had read them.</summary>
        public event Action<ObservrApplied>? Applied;

        /// <summary>The owners' decision on a finding: a yes carries out its proposals (all of them, or
        /// only the ones at <paramref name="onlyProposals"/>, indices into the finding's list), a no
        /// closes it. Returns why a yes could not be carried out (the finding stays open), or null.</summary>
        public string? Decide(long findingId, bool yes, IReadOnlyCollection<int>? onlyProposals = null)
        {
            var f = findings.Get(findingId);
            if (f == null) return "There is no such finding.";
            if (yes)
            {
                var chosen = f.Proposals.Where((p, i) => onlyProposals == null || onlyProposals.Contains(i)).ToList();
                // The store keeps only what can be carried out; this holds for a finding kept before it did.
                if (chosen.Any(p => !p.CanApply)) return "This suggestion needs a newer Indx Server. Nothing was changed.";
                foreach (var p in chosen.Where(p => p.Kind == ObservrProposal.Synonym))
                    if (!ApplySynonym(f.TeamId, f.DataSetName, p.Terms)) return "The synonym could not be stored.";
                var changes = chosen.Where(p => p.Kind == ObservrProposal.FieldKind && p.Field != null).Select(p => p.Field!).ToList();
                if (changes.Count > 0 && ApplyFields(f.TeamId, f.DataSetName, changes) is { } why) return why;
                foreach (var p in chosen.Where(p => p.Kind == ObservrProposal.SearchOrAskKind && p.SearchOrAsk != null))
                    if (ApplySearchOrAsk(f.TeamId, f.DataSetName, p.SearchOrAsk!) is { } whyNot) return whyNot;
                foreach (var p in chosen.Where(p => p.Kind == ObservrProposal.QueryParametersKind && p.QueryParameters != null))
                    ApplyQueryParameters(f.TeamId, f.DataSetName, p.QueryParameters!);
            }
            findings.Decide(findingId, yes ? ObservrFinding.Accepted : ObservrFinding.Declined);
            if (yes && f.Proposals.Count > 0)
            {
                var kinds = f.Proposals.Where((p, i) => onlyProposals == null || onlyProposals.Contains(i)).Select(p => p.Kind).ToHashSet();
                // A listener that throws (a tab gone mid-call) must not turn an applied change into a failure.
                foreach (var listener in Applied?.GetInvocationList() ?? [])
                    try { ((Action<ObservrApplied>)listener)(new ObservrApplied(f.TeamId, f.DataSetName, kinds)); }
                    catch (Exception ex) { logger.LogWarning(ex, "An Observr applied listener failed"); }
            }
            return null;
        }

        public bool ApplySynonym(string teamId, string dataSetName, string[] terms)
        {
            var clean = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (clean.Length < 2) return false;
            var list = engines.GetSynonyms(dataSetName, teamId) ?? new SynonymList();
            if (list.Entries.Any(e => clean.All(t => e.Terms.Contains(t, StringComparer.OrdinalIgnoreCase))))
                return true;
            list.Entries.Add(new SynonymEntry { Direction = SynonymDirection.Multidirectional, Terms = clean });
            list.Invalidate();
            return engines.SetSynonyms(dataSetName, teamId, list);
        }

        /// <summary>Carries out Search or ask settings as the tab's Save does: the stored settings with
        /// the change laid over, validated as a whole, saved. The switches and the budget are never
        /// touched. Returns why not, or null.</summary>
        public string? ApplySearchOrAsk(string teamId, string dataSetName, ObservrSearchOrAskChange c)
        {
            var s = searchOrAsk.Load(teamId, dataSetName) ?? new SearchOrAsk.SearchOrAskSettings();
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            var names = engine?.GetFieldList().Select(f => f.Name).ToHashSet() ?? [];
            if (c.TitleField != null && !names.Contains(c.TitleField)) return $"Field '{c.TitleField}' does not exist in this dataset.";
            if (c.SectionFields?.FirstOrDefault(n => !names.Contains(n)) is { } s1) return $"Field '{s1}' does not exist in this dataset.";
            if (c.SnippetFields?.FirstOrDefault(n => !names.Contains(n)) is { } s2) return $"Field '{s2}' does not exist in this dataset.";
            if (c.Kind != null) s.Kind = c.Kind;
            if (c.SiteDescription != null) s.SiteDescription = c.SiteDescription.Trim().Length == 0 ? null : c.SiteDescription.Trim();
            if (c.TitleField != null) s.TitleField = c.TitleField;
            if (c.SectionFields != null) s.SectionFields = c.SectionFields;
            if (c.SnippetFields != null) s.SnippetFields = c.SnippetFields;
            if (c.FilterNotes != null) s.FilterNotes = c.FilterNotes.Trim().Length == 0 ? null : c.FilterNotes.Trim();
            if (c.Answers != null) s.Answers = c.Answers;
            if (c.AnswerInstructions != null) s.AnswerInstructions = c.AnswerInstructions.Trim().Length == 0 ? null : c.AnswerInstructions.Trim();
            if (c.AnswerLanguage != null) s.AnswerLanguage = c.AnswerLanguage.Trim().Length == 0 ? null : c.AnswerLanguage.Trim();
            if (c.AddLeadIns is { Count: > 0 } || c.AddFrames is { Count: > 0 })
            {
                // Onto the list in effect: the dataset's own, or the defaults it then makes its own.
                var basis = s.AskPhrases ?? SearchOrAsk.SearchOrAskPhrases.Default;
                static List<string> Grow(List<string> have, List<string>? add) =>
                    [.. have, .. (add ?? []).Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0)
                        .Distinct().Where(p => !have.Contains(p, StringComparer.OrdinalIgnoreCase))];
                s.AskPhrases = new SearchOrAsk.SearchOrAskPhrases
                {
                    LeadIns = Grow(basis.LeadIns, c.AddLeadIns),
                    Frames = Grow(basis.Frames, c.AddFrames),
                    Links = [.. basis.Links],
                    Articles = [.. basis.Articles],
                };
            }
            if (s.Validate() is { } invalid) return invalid;
            searchOrAsk.Save(teamId, dataSetName, s);
            return null;
        }

        /// <summary>Carries out query parameters as the tab's Save does: the stored ones with the
        /// change laid over, saved; the store records the change.</summary>
        public void ApplyQueryParameters(string teamId, string dataSetName, ObservrQueryParametersChange c)
        {
            var p = queryParameters.Load(teamId, dataSetName) ?? new DatasetQueryParameters();
            if (c.CoverageDepth != null) p.CoverageDepth = c.CoverageDepth.Value <= 0 ? null : c.CoverageDepth;
            if (c.Truncate != null) { p.CoverageSetup ??= new CoverageSetupProxy(); p.CoverageSetup.Truncate = c.Truncate; }
            queryParameters.Save(teamId, dataSetName, p);
        }

        /// <summary>Carries out field changes as the Fields tab's Save does: refused as a whole when
        /// invalid, announced, then on a shadow when the change needs a rebuild (this blocks until the
        /// swap) or in place. Returns the reason it could not, or null.</summary>
        public string? ApplyFields(string teamId, string dataSetName, IReadOnlyList<ObservrFieldChange> changes)
        {
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready) return "The dataset is not ready.";
            var proposed = changes.GroupBy(c => c.Name).Select(g => new FieldProxy
            {
                FieldName = g.Key,
                Searchable = g.Select(c => c.Searchable).LastOrDefault(v => v != null),
                Filterable = g.Select(c => c.Filterable).LastOrDefault(v => v != null),
                Facetable = g.Select(c => c.Facetable).LastOrDefault(v => v != null),
                Sortable = g.Select(c => c.Sortable).LastOrDefault(v => v != null),
                Weight = g.Select(c => c.Weight).LastOrDefault(v => v != null),
            }).ToArray();
            try
            {
                if (!engine.TryValidateFieldConfiguration(proposed, out var invalid)) return invalid;
                var df = engine.DocumentFields;
                FieldConfigurationChange.Announce(df, proposed, dataSetName, teamId);
                if (df != null && FieldConfigurationChange.NeedsRebuild(df, proposed))
                    engines.RunFieldConfigurationOnShadow(dataSetName, teamId, proposed);
                else if (FieldConfigurationChange.ApplyInPlace(engine, proposed, dataSetName, teamId) is { } unknown)
                    return $"Field '{unknown}' does not exist in this dataset.";
                return null;
            }
            catch (ShadowBusyException) { return "A rebuild is already in progress. Try again when it is done."; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Observr field change failed for {Team}/{Dataset}", teamId, dataSetName);
                return ex.Message;
            }
        }
    }
}
