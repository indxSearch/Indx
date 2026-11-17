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
    /// since only the instance has the index: the same search with the rest and the fallback on
    /// (the records holding only some of the words), and what each word finds alone. The format is the contract with IndxObservr.</summary>
    public sealed record ObservrZeroHitAsk(string Kind, string Team, string Dataset, string Site, List<string> Fields,
        string Query, long Searches, List<ObservrNamed> NearMatches, List<ObservrWordMatches> Words);

    /// <summary>One field as Observr reviews it: its roles today and what the records hold in it.
    /// <see cref="Filled"/> is of the sample; <see cref="Distinct"/> and <see cref="TopValues"/>
    /// (each with its count) are over <see cref="ValuesOver"/> records: every record for a
    /// facetable field, whose facets count the whole dataset, the sample for the rest. Values are
    /// the top few only, so a review sends little of the data.</summary>
    public sealed record ObservrFieldEvidence(string Name, string Type, bool IsArray, bool WordMatching, bool PatternMatching, bool Filterable,
        bool Facetable, bool Sortable, float Weight, int Filled, int Distinct, List<string> TopValues, double? Min, double? Max, int AvgLength,
        long ValuesOver, string Tolerance, float RerankWeight, string? Profile = null);

    /// <summary>A field profile of the dataset as Observr reads it (Nov 2026): the five built-ins and
    /// the dataset's own, with their values; <see cref="Pattern"/> the pattern setup in words.</summary>
    public sealed record ObservrProfileInfo(string Id, string Name, string About, bool BuiltIn, string Tolerance, float RerankWeight, string Pattern,
        int? TypingPrefixFrom = null)
    {
        public static ObservrProfileInfo Of(FieldProfile p) => new(p.Id, p.Name, p.About ?? "", p.BuiltIn, p.Tolerance.ToString(), p.RerankWeight,
            $"pieces of {string.Join(" and ", p.IndexSizes)} letters, {p.NumberOfStartCharacters} start mark(s){(p.NumberOfStopCharacters > 0 ? ", a whole value first" : "")}",
            p.TypingPrefixFrom);
    }

    /// <summary>The dataset's setup for review: every field with its evidence, the key field, and
    /// what visitors searched for lately, which says what the fields are expected to answer.</summary>
    public sealed record ObservrFieldReviewAsk(string Kind, string Team, string Dataset, string Site, string KeyField, long Records,
        int Sampled, List<ObservrFieldEvidence> FieldEvidence, List<string> TopQueries, ObservrRecordsReport? RecordsReport = null,
        List<ObservrProfileInfo>? Profiles = null);

    /// <summary>What Search or ask runs with today, for the setup help: the settings, the fields the
    /// judge sees once the automatic choice is applied, and the dataset's query parameters as stored
    /// (mode, match, candidates, ranking; null where the dataset sets none).</summary>
    public sealed record ObservrSearchOrAskCurrent(bool Shown, bool Enabled, string Kind, string? SiteDescription, string EffectiveSite,
        string? TitleField, List<string>? SectionFields, List<string>? SnippetFields, string? EffectiveTitle, List<string> EffectiveSection,
        List<string> EffectiveSnippet, string? FilterNotes, string Answers, string? AnswerInstructions, string? AnswerLanguage, double Strong, double Invite,
        DatasetQueryParameters? QueryParameters);

    /// <summary>"Help me set this up" on the Search or ask tab: the dataset as the field review sees it,
    /// a few record names, the queries, and the settings as they stand. No record bodies.</summary>
    public sealed record ObservrSearchOrAskSetupAsk(string Kind, string Team, string Dataset, string Site, List<string> Fields, long Records,
        int Sampled, List<ObservrFieldEvidence> FieldEvidence, List<string> Names, List<ObservrSummaryQuery> TopQueries,
        List<string> ZeroHitQueries, ObservrSearchOrAskCurrent Current, ObservrRecordsReport? RecordsReport = null);

    /// <summary>A change to one field's roles and settings (Indx 6): null leaves that one as it is.
    /// <see cref="Tolerance"/> is a <see cref="Indx.Api.Tolerance"/> by name.</summary>
    public sealed record ObservrFieldChange(string Name, bool? WordMatching, bool? PatternMatching, bool? Filterable, bool? Facetable,
        bool? Sortable, float? Weight, string? Tolerance = null, float? RerankWeight = null, string? Profile = null)
    {
        /// <summary>The roles as words, the way the console names them.</summary>
        public static readonly (string Name, string Label)[] Roles =
            [("wordMatching", "matched by its words"), ("patternMatching", "matched inside its words"), ("filterable", "filterable"), ("facetable", "facetable"), ("sortable", "sortable")];

        [JsonIgnore]
        public IEnumerable<(string Label, bool On)> RoleChanges
        {
            get
            {
                foreach (var (v, label) in new[] { (WordMatching, Roles[0].Label), (PatternMatching, Roles[1].Label), (Filterable, "filterable"), (Facetable, "facetable"), (Sortable, "sortable") })
                    if (v != null) yield return (label, v.Value);
            }
        }

        /// <summary>The settings as words: "the title profile", "weight 2", "rerank weight 0.5",
        /// "exact words only". The profile first, as Observr's own page words it: a change that
        /// only gives a profile reads "Give bookTitle the title profile".</summary>
        [JsonIgnore]
        public IEnumerable<string> SettingChanges
        {
            get
            {
                if (Profile != null) yield return $"the {Profile} profile";
                if (Weight != null) yield return $"weight {Weight.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
                if (RerankWeight != null) yield return $"rerank weight {RerankWeight.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
                if (Tolerance != null) yield return ObservrText.ToleranceText(Tolerance);
            }
        }

        /// <summary>The change in words: "Elevtall: sortable", "Navn: weight 2, no longer facetable".</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                var parts = RoleChanges.Select(r => r.On ? r.Label : $"no longer {r.Label}").Concat(SettingChanges).ToList();
                return parts.Count == 0 ? "no change" : string.Join(", ", parts);
            }
        }

        /// <summary>The change as the engine takes it. Word and pattern matching are set as they come:
        /// pattern matching on keeps a setup the field has, or takes the default.</summary>
        public FieldProxy ToProxy() => new()
        {
            FieldName = Name, WordMatching = WordMatching, PatternMatching = PatternMatching,
            Filterable = Filterable, Facetable = Facetable, Sortable = Sortable, Weight = Weight, RerankWeight = RerankWeight,
            Tolerance = Tolerance != null && Enum.TryParse<Tolerance>(Tolerance, out var t) ? t : null,
        };
    }

    /// <summary>A field profile Observr proposes in a chat, on the owner's request (Nov 2026): a new
    /// one of the dataset's own (no <see cref="Id"/>), or a change to one it has. Null leaves a value
    /// as it is, or for a new profile as the library has it. <see cref="Fields"/> are searched fields
    /// that get the profile with the Yes. A built-in is not changed this way: every dataset's fields
    /// on it would follow.</summary>
    public sealed record ObservrProfileChange(string? Id, string? Name, string? About = null, string? Tolerance = null, float? RerankWeight = null,
        int? TypingPrefixFrom = null, int[]? IndexSizes = null, int? StartCharacters = null, int? StopCharacters = null, List<string>? Fields = null)
    {
        [JsonIgnore]
        public bool IsNew => string.IsNullOrWhiteSpace(Id);

        /// <summary>The values as words: "exact words only", "the start of a longer word from 8 letters".</summary>
        [JsonIgnore]
        public IEnumerable<string> Parts
        {
            get
            {
                if (Tolerance != null) yield return ObservrText.ToleranceText(Tolerance);
                if (TypingPrefixFrom is { } from)
                    yield return from < 0 ? "the start of a longer word as the search has it"
                        : from == 0 ? "never the start of a longer word" : $"the start of a longer word from {from} letters";
                if (RerankWeight != null) yield return $"rerank weight {RerankWeight.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
                if (IndexSizes is { Length: > 0 }) yield return $"pieces of {string.Join(" and ", IndexSizes)} letters";
                if (StartCharacters != null) yield return $"{StartCharacters} start mark{(StartCharacters == 1 ? "" : "s")}";
                if (StopCharacters != null) yield return StopCharacters > 0 ? "a whole value first" : "no whole value first";
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
    /// Never the switches (Shown, Enabled, the budget): switching on stays the owner's. Ask phrases were
    /// removed (5 Nov 2026): this server no longer announces them, so Observr proposes none.</summary>
    public sealed record ObservrSearchOrAskChange(string? Kind, string? SiteDescription, string? TitleField, List<string>? SectionFields,
        List<string>? SnippetFields, string? FilterNotes, string? Answers, string? AnswerInstructions = null, string? AnswerLanguage = null)
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
            }
        }
    }

    /// <summary>A change to the dataset's query parameters (Indx 6): null leaves that one as it is.
    /// <see cref="MaxCandidates"/> 0 hands the bound back to the default.</summary>
    public sealed record ObservrQueryParametersChange(int? MaxCandidates = null, string? Tolerance = null, bool? Fallback = null,
        bool? Rest = null, int? FillReach = null, bool? Rerank = null)
    {
        [JsonIgnore]
        public IEnumerable<string> Parts
        {
            get
            {
                if (Tolerance != null) yield return ObservrText.ToleranceText(Tolerance);
                if (MaxCandidates != null) yield return MaxCandidates == 0 ? "the default number of records ranked" : $"{MaxCandidates} records ranked";
                if (Fallback != null) yield return Fallback.Value ? "the closest records when nothing matches" : "nothing when nothing matches";
                if (Rest != null) yield return Rest.Value ? "partial matches below the hits" : "no partial matches below the hits";
                if (FillReach != null) yield return FillReach == 0 ? "short pages not filled" : $"short pages filled from {FillReach} more typo level{(FillReach == 1 ? "" : "s")}";
                if (Rerank != null) yield return Rerank.Value ? "reranked" : "not reranked";
            }
        }
    }

    /// <summary>A support ticket Observr drafted for the owner to send to Indx: support, feature or
    /// feedback, a subject and the text. Sent by the Yes, through the console's support client.</summary>
    public sealed record ObservrTicketDraft(string Kind, string Subject, string Body);

    /// <summary>A proposal Observr made, which this instance can carry out after a yes: a synonym
    /// (<see cref="Terms"/>), a field change (<see cref="Field"/>), Search or ask settings
    /// (<see cref="SearchOrAsk"/>), query parameters (<see cref="QueryParameters"/>) or a support
    /// ticket to send to Indx (<see cref="Ticket"/>).</summary>
    public sealed record ObservrProposal(string Kind, string[] Terms, string Direction, string Reason, ObservrFieldChange? Field = null,
        ObservrSearchOrAskChange? SearchOrAsk = null, ObservrQueryParametersChange? QueryParameters = null, ObservrTicketDraft? Ticket = null,
        ObservrProfileChange? Profile = null)
    {
        /// <summary>What this server can carry out, sent to Observr as <see cref="AcceptsHeader"/> on
        /// every request: the kinds, and as <c>kind.part</c> each part added to a kind after it first
        /// shipped. Observr is deployed ahead of every installed server and narrows what it proposes
        /// to this list. A part added here must be applied in the same change, or Observr sends it
        /// and a yes leaves it out.</summary>
        public static readonly string[] Accepted = [Synonym, FieldKind, SearchOrAskKind, QueryParametersKind, FieldProfilePart, ProfileKind];

        /// <summary>A field change that gives the field one of the dataset's profiles (Nov 2026).</summary>
        public const string FieldProfilePart = "field.profile";

        /// <summary>What this server can carry out right now: <see cref="Accepted"/>, and a support
        /// ticket while support is on (it is sent through the Portal, which an instance with support
        /// switched off never calls).</summary>
        public static IEnumerable<string> AcceptedWith(bool support) => support ? [.. Accepted, SupportTicketKind] : Accepted;
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
            SupportTicketKind => Ticket is { Subject.Length: > 0, Body.Length: > 0 },
            ProfileKind => Profile != null && (!Profile.IsNew || !string.IsNullOrWhiteSpace(Profile.Name)),
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
                        var on = c.RoleChanges.Where(r => r.On).Select(r => r.Label).ToList();
                        var off = c.RoleChanges.Where(r => !r.On).Select(r => r.Label).ToList();
                        var tail = new List<string>();
                        if (on.Count > 0) tail.Add(Join(on));
                        if (off.Count > 0) tail.Add("no longer " + Join(off));
                        var settings = c.SettingChanges.ToList();
                        var weight = settings.Count == 0 ? null : Join(settings);
                        if (tail.Count == 0 && weight != null) return ("Give", [c.Name], weight);
                        if (weight != null) tail.Add("with " + weight);
                        return ("Make", [c.Name], tail.Count == 0 ? "as it is" : string.Join(", ", tail));
                        static string Join(List<string> roles) => roles.Count == 1 ? roles[0] : string.Join(", ", roles[..^1]) + " and " + roles[^1];
                    }
                    case SearchOrAskKind when SearchOrAsk != null:
                        return ("Change Search or ask:", SearchOrAsk.Parts.ToList(), "");
                    case QueryParametersKind when QueryParameters != null:
                        return ("Change the query parameters:", QueryParameters.Parts.ToList(), "");
                    case SupportTicketKind when Ticket != null:
                        return (Ticket.Kind switch { "feature" => "Send Indx a feature request:", "feedback" => "Send Indx feedback:", _ => "Send Indx a support request:" },
                            [Ticket.Subject], "");
                    case ProfileKind when Profile != null:
                    {
                        // "Create the profile [ISBN] with exact words only and the start of a longer
                        // word from 8 letters, for isbn"; "Change the profile [ISBN] to up to one typo".
                        var c = Profile;
                        var parts = c.Parts.ToList();
                        var values = parts.Count == 0 ? "" : (c.IsNew ? "with " : "to ") + (parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1]);
                        var shown = c.Name is { Length: > 0 } name ? name : c.Id ?? "";
                        var fields = c.Fields is { Count: > 0 } ? string.Join(", ", c.Fields) : "";
                        // A profile it has, with nothing changed: only handed to fields.
                        if (!c.IsNew && parts.Count == 0 && fields.Length > 0) return ("Give the profile", [shown], "to " + fields);
                        var given = fields.Length == 0 ? "" : (c.IsNew ? "for " : "and give it to ") + fields;
                        var after = values.Length > 0 && given.Length > 0 ? $"{values}, {given}" : values + given;
                        return (c.IsNew ? "Create the profile" : "Change the profile", [shown], after);
                    }
                    default:
                        return (Label, [], "");
                }
            }
        }

        /// <summary>The sentence as plain text, for a note: "Treat as the same word: tv, telly".</summary>
        [JsonIgnore]
        public string SentenceText
        {
            get
            {
                var (before, names, after) = Sentence;
                return string.Join(" ", new[] { before, string.Join(", ", names), after }.Where(p => p.Length > 0));
            }
        }

        public const string Synonym = "synonym";
        public const string FieldKind = "field";
        public const string SearchOrAskKind = "searchOrAsk";
        public const string QueryParametersKind = "queryParameters";
        public const string SupportTicketKind = "supportTicket";
        /// <summary>A field profile made or changed on the owner's request in a chat (Nov 2026).</summary>
        public const string ProfileKind = "profile";

        /// <summary>What the proposal is, as a label, for a panel that is not a synonym's or a field's.</summary>
        [JsonIgnore]
        public string Label => Kind switch
        {
            Synonym => "Synonym",
            FieldKind => "Field",
            SearchOrAskKind => "Search or ask",
            QueryParametersKind => "Query parameters",
            SupportTicketKind => "Send to Indx",
            ProfileKind => "Profile",
            _ => Kind,
        };

        /// <summary>The proposal as chips, for a kind without a panel of its own.</summary>
        public IEnumerable<string> Describe() => Kind switch
        {
            Synonym => Terms,
            FieldKind when Field != null => [Field.Name + ": " + Field.Summary],
            SearchOrAskKind when SearchOrAsk != null => SearchOrAsk.Parts,
            QueryParametersKind when QueryParameters != null => QueryParameters.Parts,
            SupportTicketKind when Ticket != null => [Ticket.Subject],
            ProfileKind when Profile != null => [(Profile.Name ?? Profile.Id) + ": " + string.Join(", ", Profile.Parts)],
            _ => [],
        };
    }

    /// <summary>What a Yes replaced (ObservrClient.Undo): the synonyms it added, the Search or ask
    /// settings and query parameters as they were (null when there were none), each changed field's
    /// roles as they were.</summary>
    public sealed class ObservrUndo
    {
        public List<string[]> AddedSynonyms { get; set; } = [];
        public bool HasSearchOrAsk { get; set; }
        public string? SearchOrAskBefore { get; set; }
        public bool HasQueryParameters { get; set; }
        public string? QueryParametersBefore { get; set; }
        /// <summary>Each changed field's configuration as it was (Indx 6): roles, pattern setup,
        /// tolerance and weights, so an undo puts back exactly that.</summary>
        public List<FieldProxy> FieldConfigurationBefore { get; set; } = [];
        /// <summary>Each changed field's profile as it was, null where it had none (Nov 2026): an undo
        /// puts the choice back with the values.</summary>
        public Dictionary<string, string?> ProfilesBefore { get; set; } = [];
        /// <summary>The dataset's own profiles and choices as they were before a profile was made or
        /// changed (null when it had none): an undo puts the whole of it back.</summary>
        public bool HasProfileStore { get; set; }
        public string? ProfileStoreBefore { get; set; }
        /// <summary>An undo kept before 9 Nov 2026: each field's roles as one searchable flag.</summary>
        public List<ObservrFieldChange> FieldsBefore { get; set; } = [];
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

    /// <summary>What came of a call to Observr. <see cref="Off"/>: an admin has Observr off on this
    /// instance, so nothing was sent. <see cref="OverQuota"/>: the licence has used the day's
    /// allowance (429 from the service), back at midnight UTC.</summary>
    public enum ObservrStatus { Ok, NoToken, UnknownToken, NoSubscription, Unavailable, Failed, Off, OverQuota }

    public static class ObservrText
    {
        /// <summary>A field change as chips: a role switched on (true), one switched off, a weight
        /// with the value it has today when known.</summary>
        public static List<(string Text, bool On)> Parts(ObservrFieldChange c, float? currentWeight)
        {
            var parts = c.RoleChanges.Select(r => (r.On ? r.Label : $"{r.Label} off", r.On)).ToList();
            if (c.Weight is { } w)
                parts.Add((currentWeight is { } n ? $"weight {n:0.##} → {w:0.##}" : $"weight {w:0.##}", true));
            if (c.Profile != null) parts.Add(($"the {c.Profile} profile", true));
            if (c.RerankWeight is { } rw) parts.Add(($"rerank weight {rw:0.##}", true));
            if (c.Tolerance != null) parts.Add((ToleranceText(c.Tolerance), true));
            return parts;
        }

        /// <summary>A tolerance as the owner reads it.</summary>
        public static string ToleranceText(string tolerance) => tolerance switch
        {
            "Exact" => "exact words only",
            "OneEdit" => "up to one typo",
            "TwoEdits" => "up to two typos",
            "ThreeEdits" => "up to three typos",
            _ => tolerance,
        };

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
            ObservrStatus.Off => "Observr is off on this instance. An admin can switch it on under Admin, Observr.",
            ObservrStatus.OverQuota => o.Reason switch
            {
                "chatEnded" => "This conversation has run as long as one question may. Ask again to start a new one.",
                "monthlyQuota" => $"This licence has used this month's Observr allowance. It renews on {DateTime.UtcNow.Date.AddDays(1 - DateTime.UtcNow.Day).AddMonths(1).ToString("d MMMM", CultureInfo.InvariantCulture)}.",
                _ => $"This licence has used today's Observr allowance. It is back at midnight UTC, {UntilMidnight()}.",
            },
            _ => o.Reason == "datasetNotReady" ? $"The dataset is not ready to search, so there is nothing {nothingYet} yet."
                : o.Reason == "noStatistics" ? "This instance keeps no statistics, so there is nothing to summarise."
                : o.Reason == "tooLong" ? $"The question is too long for Observr: up to {ObservrChat.MaxQuestion:N0} characters."
                : "Observr could not answer this time.",
        };

        /// <summary>"in 5 hours", "in 40 minutes": until the allowance comes back.</summary>
        public static string UntilMidnight(DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var left = now.Date.AddDays(1) - now;
            return left.TotalMinutes < 90 ? $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} minutes" : $"in {(int)Math.Round(left.TotalHours)} hours";
        }
    }

    /// <summary>The licence's allowance as Observr last reported it: what is left of the calendar
    /// month (UTC) and of the day, of how many, and the day the month's renew. Observr sets the
    /// numbers (a plan's, or a licence's own) and enforces them; this instance only shows them, so
    /// a customer given more needs nothing changed here. A number Observr does not limit is null.</summary>
    public sealed record ObservrAllowance(int? MonthLeft, int? MonthLimit, int? DayLeft, int? DayLimit, DateOnly? Renews, DateTime SeenUtc, DateTime? RanOutUtc)
    {
        public const string LeftHeader = "X-Observr-Asks-Left";
        public const string LimitHeader = "X-Observr-Asks-Limit";
        public const string TodayLeftHeader = "X-Observr-Asks-Today-Left";
        public const string TodayLimitHeader = "X-Observr-Asks-Today-Limit";
        public const string RenewHeader = "X-Observr-Asks-Renew";

        /// <summary>"1,000" on every machine, as the rest of the console writes numbers.</summary>
        public static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        private DateOnly RenewsOn => Renews ?? DateOnly.FromDateTime(new DateTime(SeenUtc.Year, SeenUtc.Month, 1).AddMonths(1));
        private bool SameMonth(DateTime now) => DateOnly.FromDateTime(now) < RenewsOn;
        public bool MonthSpent(DateTime? nowUtc = null) => MonthLeft <= 0 && SameMonth(nowUtc ?? DateTime.UtcNow);
        public bool DaySpent(DateTime? nowUtc = null) => DayLeft <= 0 && SeenUtc.Date == (nowUtc ?? DateTime.UtcNow).Date;
        /// <summary>Nothing more is answered: until midnight UTC for the day, until the renewal for the month.</summary>
        public bool Spent(DateTime? nowUtc = null) => MonthSpent(nowUtc) || DaySpent(nowUtc);
        /// <summary>A tenth or less of the month left: worth a word before it runs out.</summary>
        public bool Low(DateTime? nowUtc = null) => !Spent(nowUtc) && SameMonth(nowUtc ?? DateTime.UtcNow) && MonthLeft is > 0 && MonthLimit is > 0 && MonthLeft * 10 <= MonthLimit;

        /// <summary>"Observr's asks are used up for this month; they renew on 1 November." and the like.</summary>
        public string SpentSentence(DateTime? nowUtc = null) => MonthSpent(nowUtc)
            ? $"This licence has used this month's Observr allowance{(MonthLimit is { } m ? $" of {N(m)} asks" : "")}. It renews on {RenewsOn.ToString("d MMMM", CultureInfo.InvariantCulture)}."
            : $"This licence has used today's Observr allowance{(DayLimit is { } d ? $" of {N(d)} asks" : "")}. It is back at midnight UTC, {ObservrText.UntilMidnight(nowUtc)}.";

        /// <summary>"820 of 1,000 asks left this month, renewing on 1 November; at most 100 a day."</summary>
        public string LeftSentence() =>
            (MonthLimit is { } m ? $"{N(MonthLeft ?? 0)} of {N(m)} Observr asks left this month, renewing on {RenewsOn.ToString("d MMMM", CultureInfo.InvariantCulture)}" : "No monthly limit on Observr asks")
            + (DayLimit is { } d ? $"; at most {N(d)} a day, {N(DayLeft ?? 0)} left today." : ".");
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
        SearchOrAsk.SearchOrAskService searchOrAskService, QueryParameterStore queryParameters, ILogger<ObservrClient> logger,
        Support.SupportOptions supportOptions, Support.SupportClient support, Support.SupportState supportState, DataCheckStore dataChecks,
        FieldProfileStore profiles)
    {
        /// <summary>The match of a search the server runs on a finished text (a recorded query, a
        /// word, a synonym's expansion): no word is being typed, so none is met as the start of a
        /// longer one. 6.0 reads an open last word as typed (MatchSetup.CursorAt).</summary>
        private static MatchProxy Finished => new() { CursorAt = 0 };

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
        /// a file may begin with one kind of record, and a sample of those had Observr set up
        /// Search or ask for that kind on a dataset that is mostly something else (19 Oct 2026).</summary>
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
            if (Refused(out var token) is { } refused) return (refused, null, null);
            var (status, body, reason) = await PostAsync(token, "/api/chat", payload, teamId, dataSetName, ct);
            if (status != HttpStatusCode.OK) return (Status(status), null, reason);
            var reply = JsonSerializer.Deserialize<ObservrChatReply>(body, Json);
            return reply == null ? (ObservrStatus.Failed, null, "badAnswer") : (ObservrStatus.Ok, reply, null);
        }

        /// <summary>A long conversation's earlier part as a summary, written by Observr (the previous
        /// summary folded in). Null when it could not be had: a service from before summaries answers
        /// 404, and the panel then keeps its plain line.</summary>
        public async Task<string?> SummarizeAsync(string teamId, string teamName, string? previous, IReadOnlyList<ObservrTurn> turns, CancellationToken ct)
        {
            if (Refused(out var token) is not null) return null;
            var payload = new { team = teamName, previous, turns = turns.Select(t => new { role = t.Role, text = t.Text }).ToList() };
            var (status, body, _) = await PostAsync(token, "/api/chat/summary", payload, teamId, "", ct);
            if (status != HttpStatusCode.OK) return null;
            try { return JsonDocument.Parse(body).RootElement.GetProperty("summary").GetString() is { Length: > 0 } s ? s.Trim() : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Why nothing may be sent, or null with the token to send: an admin's "Observr on this
        /// instance" is the promise that nothing of Observr leaves while it is off, so every call
        /// passes here, whichever button or job made it.</summary>
        private ObservrStatus? Refused(out string token)
        {
            token = "";
            if (!settings.Load().ObservrEnabled) return ObservrStatus.Off;
            if (Token() is not { } t) return ObservrStatus.NoToken;
            token = t;
            return null;
        }

        /// <summary>The day's allowance as the service last reported it, and when the licence last ran
        /// out of it: the admin page and the Agent panel say so before an owner presses a button for
        /// nothing.</summary>
        public ObservrAllowance? Allowance { get; private set; }

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
            if (Refused(out var token) is { } refused) return new ObservrOutcome(refused);
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
            if (Refused(out var token) is { } refused) return new ObservrOutcome(refused);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var ask = BuildFieldReviewAsk(teamId, dataSetName, engine);
            return await SendAsync(token, teamId, dataSetName, FieldReviewKind, "", ask, ct);
        }

        /// <summary>"Why isn't this working?" on a query searched often and rarely clicked.</summary>
        public async Task<ObservrOutcome> AskLowClickAsync(string teamId, string dataSetName, QueryStat q, long fromDay, long toDay, CancellationToken ct)
        {
            if (Refused(out var token) is { } refused) return new ObservrOutcome(refused);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready || statistics.Store is not { } store)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var label = statistics.LabelsFor(engine, teamId, dataSetName);
            var results = (IndxServerInternalApi.Manager.Search(new QueryProxy { Text = q.QueryText, MaxNumberOfRecordsToReturn = 10, Match = Finished }, dataSetName, teamId)
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
            if (Refused(out var token) is { } refused) return new ObservrOutcome(refused);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready || statistics.Store is not { } store)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var today = store.Today(teamId, dataSetName);
            var label = statistics.LabelsFor(engine, teamId, dataSetName);
            List<ObservrNamed> Near(string text) => (IndxServerInternalApi.Manager.Search(new QueryProxy
            {
                Text = text, MaxNumberOfRecordsToReturn = 3, Mode = SearchMode.EditDistance, Match = Finished,
                // The nearest, counted or not: the rest below the counted, and the fallback (6.0;
                // until then Truncate off and IncludePatternMatches on).
                Candidates = new CandidateProxy { Rest = true, Fallback = true },
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
            if (Refused(out var token) is { } refused) return new ObservrOutcome(refused);
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
            if (Refused(out var token) is { } refused) return new ObservrOutcome(refused);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");
            var review = BuildFieldReviewAsk(teamId, dataSetName, engine);
            var label = statistics.LabelsFor(engine, teamId, dataSetName);
            // Names from across the dataset, like the sample: the first dozen records say what the
            // file begins with, not what a record is.
            var names = Spread(teamId, dataSetName, SetupNames).Keys.Select(label).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            var today = statistics.Store?.Today(teamId, dataSetName) ?? 0;
            var top = statistics.Store?.TopQueries(teamId, dataSetName, today - 29, today, 30)
                .Select(q => new ObservrSummaryQuery(q.QueryText, q.Searches, q.ZeroHits, q.ClickedSearches,
                    q.Selects > 0 ? Math.Round((double)q.PositionSum / q.Selects, 1) : null, q.MostChosenLabel)).ToList() ?? [];
            var zero = statistics.Store?.TopQueries(teamId, dataSetName, today - 29, today, 15, zeroHitsOnly: true).Select(q => q.QueryText).ToList() ?? [];

            var settings = searchOrAsk.Load(teamId, dataSetName) ?? new SearchOrAsk.SearchOrAskSettings();
            var effective = searchOrAskService.FieldsFor(teamId, dataSetName, settings, engine);
            var thresholds = settings.Thresholds.Resolved(settings.Kind, searchOrAskService.Setup.JudgeProvider);
            var qp = queryParameters.Load(teamId, dataSetName);
            var current = new ObservrSearchOrAskCurrent(settings.Shown, settings.Enabled, settings.Kind, settings.SiteDescription,
                searchOrAskService.SiteDescription(teamId, dataSetName, settings), settings.TitleField, settings.SectionFields, settings.SnippetFields,
                effective.TitleField, effective.SectionFields.ToList(), effective.SnippetFields.ToList(), settings.FilterNotes, settings.Answers,
                settings.AnswerInstructions, settings.AnswerLanguage, thresholds.Strong!.Value, thresholds.Invite!.Value, qp);
            var ask = new ObservrSearchOrAskSetupAsk(SetupKind, teamId, dataSetName, review.Site, review.FieldEvidence.Select(f => f.Name).ToList(),
                review.Records, review.Sampled, review.FieldEvidence, names, top, zero, current, review.RecordsReport);
            return await SendAsync(token, teamId, dataSetName, SetupKind, "", ask, ct);
        }

        private static List<string> SearchableFields(IServerSearchEngine engine) => engine.GetFieldList().Where(f => f.WordMatching || f.PatternMatching != null).Select(f => f.Name).ToList();

        /// <summary>The digest: the bounded summary of the last week goes up (<see cref="ObservrSummary"/>),
        /// and each thing Observr finds worth attention comes back as a finding of its own in the
        /// menu. Run daily for every dataset with Observr on, and on the Options tab's button.</summary>
        public async Task<ObservrDigestOutcome> SendDigestAsync(string teamId, string dataSetName, CancellationToken ct)
        {
            if (Refused(out var token) is { } refused) return new ObservrDigestOutcome(refused);
            // The key's limit, read now (at most every five minutes), so the summary can warn before it runs out.
            await searchOrAskService.KeyLimitAsync(ct);
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
            HttpStatusCode.TooManyRequests => ObservrStatus.OverQuota,
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
            request.Headers.TryAddWithoutValidation(ObservrProposal.AcceptsHeader, string.Join(',', ObservrProposal.AcceptedWith(supportOptions.Enabled)));
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
                ReadAllowance(response, reason);
                return (response.StatusCode, body, reason ?? (response.StatusCode == HttpStatusCode.OK ? null : $"http{(int)response.StatusCode}"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Observr unreachable");
                LastFailure = new ObservrPortalFailure(DateTime.UtcNow, "unreachable");
                return (HttpStatusCode.ServiceUnavailable, "", "unreachable");
            }
        }

        /// <summary>The allowance headers on every answer (what is left of the month and the day, of how
        /// many, and the renewal), with a 429 <c>quota</c> or <c>monthlyQuota</c> as that one spent.
        /// A service that sends none leaves what was known.</summary>
        private void ReadAllowance(HttpResponseMessage response, string? reason)
        {
            var now = DateTime.UtcNow;
            static int? Header(HttpResponseMessage r, string name) =>
                r.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            var monthLeft = Header(response, ObservrAllowance.LeftHeader);
            var dayLeft = Header(response, ObservrAllowance.TodayLeftHeader);
            var spent = response.StatusCode == HttpStatusCode.TooManyRequests && reason is "quota" or "monthlyQuota";
            if (monthLeft == null && dayLeft == null && !spent) return;
            DateOnly? renews = response.Headers.TryGetValues(ObservrAllowance.RenewHeader, out var r) && DateOnly.TryParseExact(r.FirstOrDefault(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Allowance?.Renews;
            Allowance = new ObservrAllowance(
                reason == "monthlyQuota" && spent ? 0 : monthLeft ?? Allowance?.MonthLeft, Header(response, ObservrAllowance.LimitHeader) ?? Allowance?.MonthLimit,
                reason == "quota" && spent ? 0 : dayLeft ?? Allowance?.DayLeft, Header(response, ObservrAllowance.TodayLimitHeader) ?? Allowance?.DayLimit,
                renews, now, spent ? now : Allowance?.RanOutUtc);
        }

        /// <summary>Asks Observr what this licence has left (GET /api/allowance), which is not an ask:
        /// the admin page's numbers on opening. Null when nothing may be sent or Observr cannot say.</summary>
        public async Task<ObservrAllowance?> FetchAllowanceAsync(CancellationToken ct)
        {
            if (Refused(out var token) is not null) return null;
            var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Url.TrimEnd('/')}/api/allowance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (instanceId, instanceName) = Instance();
            request.Headers.TryAddWithoutValidation("X-Indx-Instance", instanceId);
            request.Headers.TryAddWithoutValidation("X-Indx-Instance-Name", instanceName);
            request.Headers.TryAddWithoutValidation("X-Indx-Server-Version", ServerVersion);
            try
            {
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return Allowance;
                ReadAllowance(response, null);
                return Allowance;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Allowance; }
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
                    case HttpStatusCode.TooManyRequests:
                        return new ObservrOutcome(ObservrStatus.OverQuota, Reason: reason ?? "quota");
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
            var label = statistics.LabelsFor(engine, teamId, dataSetName);
            List<ObservrNamed> Named(IEnumerable<long> keys) => keys.Select(k => new ObservrNamed(k, label(k) ?? k.ToString())).ToList();
            List<long> Keys(string text, int take, bool strict) =>
                (IndxServerInternalApi.Manager.Search(new QueryProxy
                {
                    Text = text,
                    MaxNumberOfRecordsToReturn = take,
                    Mode = SearchMode.EditDistance,
                    Match = Finished,
                    Candidates = strict ? null : new CandidateProxy { Rest = true, Fallback = true },
                }, dataSetName, teamId).Records ?? []).Select(r => r.DocumentKey).ToList();

            var near = Named(Keys(query, NearMatches, strict: false));
            var words = Regex.Split(query.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+")
                .Where(w => w.Length >= 2).Distinct().Take(MaxWords)
                .Select(w => new ObservrWordMatches(w, Named(Keys(w, PerWord, strict: true)))).ToList();
            var fields = engine.GetFieldList().Where(f => f.WordMatching || f.PatternMatching != null).Select(f => f.Name).ToList();
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
            var own = profiles.Load(teamId, dataSetName);
            var configuration = engine.GetFieldConfiguration().ToDictionary(x => x.FieldName, StringComparer.Ordinal);
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
                evidence.Add(new ObservrFieldEvidence(f.Name, TypeName(f.Type), f.IsArray, f.WordMatching, f.PatternMatching != null, f.Filterable, f.Facetable, f.Sortable, f.Weight,
                    filled, distinct, top, numbers.Count > 0 ? numbers.Min() : null, numbers.Count > 0 ? numbers.Max() : null,
                    texts.Count == 0 ? 0 : (int)texts.Average(s => s.Length), over, f.Tolerance.ToString(), f.RerankWeight,
                    configuration.TryGetValue(f.Name, out var proxy) ? FieldProfiles.ProfileOf(proxy, own)?.Id : null));
            }
            var today = StatisticsDays.Today(TimeZoneInfo.Utc);
            var queries = statistics.Store?.TopQueries(teamId, dataSetName, today - ReviewQueryDays, today, ReviewTopQueries)
                .Select(q => q.QueryText).ToList() ?? [];
            return new ObservrFieldReviewAsk(FieldReviewKind, teamId, dataSetName, Site(teamId, dataSetName), engine.DocumentFields?.NameOfDocumentKeyField ?? "",
                engine.Status.DocumentCount, docs.Count, evidence, queries,
                ObservrRecordsReport.From(dataChecks.Load(teamId, dataSetName), engine.GetFieldConfiguration()),
                FieldProfiles.All(own).Select(ObservrProfileInfo.Of).ToList());
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
            var result = IndxServerInternalApi.Manager.Search(new QueryProxy { Text = expanded, MaxNumberOfRecordsToReturn = 5, Mode = SearchMode.EditDistance, Match = Finished }, dataSetName, teamId);
            var label = statistics.LabelsFor(engine, teamId, dataSetName);
            var records = result.Records ?? [];
            return new ObservrSynonymTest(records.Length, records.Select(r => label(r.DocumentKey) ?? r.DocumentKey.ToString()).ToList());
        }

        /// <summary>After a yes has carried a finding's proposals out: which dataset, and the kinds of
        /// change (<see cref="ObservrProposal.Synonym"/>, <see cref="ObservrProposal.FieldKind"/>, …).
        /// The console's tabs reload what they show from it: a yes in the Agent panel, the Observr
        /// menu or the Status tab changed the saved settings under a tab that had read them.</summary>
        public event Action<ObservrApplied>? Applied;

        /// <summary>The findings being decided right now: a second Yes on one (another editor, or the
        /// Agent panel and the Status page at once) waits for nothing and is refused, rather than
        /// carrying the proposals out twice; a field change would otherwise rebuild twice.</summary>
        private readonly HashSet<long> _deciding = [];

        /// <summary>The owners' decision on a finding: a yes carries out its proposals (all of them, or
        /// only the ones at <paramref name="onlyProposals"/>, indices into the finding's list; the rest
        /// are declined with it), a no closes it. Only an open finding is decided. Returns why a yes
        /// could not be carried out, or null. A yes that fails part way says what was done before it
        /// stopped; that part stays done (each kind of change is safe to carry out again) and the
        /// finding stays open, so a second Yes finishes it.</summary>
        public string? Decide(long findingId, bool yes, IReadOnlyCollection<int>? onlyProposals = null)
        {
            lock (_deciding)
                if (!_deciding.Add(findingId)) return "This suggestion is already being carried out.";
            try { return DecideOnce(findingId, yes, onlyProposals); }
            finally { lock (_deciding) _deciding.Remove(findingId); }
        }

        private string? DecideOnce(long findingId, bool yes, IReadOnlyCollection<int>? onlyProposals)
        {
            var f = findings.Get(findingId);
            if (f == null) return "There is no such finding.";
            if (f.Status != ObservrFinding.Open) return f.Status == ObservrFinding.Accepted ? "This was already carried out." : "This was already declined.";
            if (!yes)
            {
                findings.Decide(findingId, ObservrFinding.Declined);
                RememberDeclined(f, f.Proposals);
                return null;
            }
            var chosen = f.Proposals.Where((p, i) => onlyProposals == null || onlyProposals.Contains(i)).ToList();
            // The ones left out of a partial yes are declined with it, and remembered as such.
            RememberDeclined(f, f.Proposals.Where((p, i) => onlyProposals != null && !onlyProposals.Contains(i)));
            // The store keeps only what can be carried out; this holds for a finding kept before it did.
            if (chosen.Any(p => !p.CanApply)) return "This suggestion needs a newer Indx Server. Nothing was changed.";
            var done = new HashSet<string>();
            // What the Yes replaces, kept for Undo; a second Yes after a partial one adds to it.
            var undo = findings.GetUndo(findingId) is { } kept ? JsonSerializer.Deserialize<ObservrUndo>(kept.Json, UndoJson) ?? new() : new ObservrUndo();
            var why = Apply(f, chosen, done, undo);
            if (done.Count > 0) findings.SetUndo(findingId, JsonSerializer.Serialize(undo, UndoJson));
            if (why == null) findings.Decide(findingId, ObservrFinding.Accepted);
            // The tabs reload what changed, also when the yes stopped part way: what was done is saved.
            if (done.Count > 0)
                foreach (var listener in Applied?.GetInvocationList() ?? [])
                    // A listener that throws (a tab gone mid-call) must not turn an applied change into a failure.
                    try { ((Action<ObservrApplied>)listener)(new ObservrApplied(f.TeamId, f.DataSetName, done)); }
                    catch (Exception ex) { logger.LogWarning(ex, "An Observr applied listener failed"); }
            if (why == null) return null;
            if (done.Count == 0) return why + " Nothing was changed.";
            var labels = f.Proposals.Where(p => done.Contains(p.Kind)).Select(p => p.Label.ToLowerInvariant()).Distinct();
            return $"{why} The {string.Join(" and ", labels)} change{(done.Count == 1 ? " was" : "s were")} carried out; the rest was not.";
        }

        /// <summary>A No is kept as a note on the dataset (Notes/observr-agent-plan.md, step 2), so
        /// Observr does not propose the same change again in the next conversation.</summary>
        private void RememberDeclined(ObservrFinding f, IEnumerable<ObservrProposal> declined)
        {
            if (f.DataSetName.Length == 0) return;
            foreach (var p in declined)
                if (p.SentenceText is { Length: > 0 } text) findings.AddNote(f.TeamId, f.DataSetName, "Declined: " + text, ObservrNote.Declined);
        }

        /// <summary>The chosen proposals, synonyms first and field changes (the slow one) after the
        /// settings that cannot fail on their own. Each kind is added to <paramref name="done"/> once
        /// carried out. Returns why it stopped, or null.</summary>
        private string? Apply(ObservrFinding f, List<ObservrProposal> chosen, HashSet<string> done, ObservrUndo undo)
        {
            // Read before anything here changes it: a field change below may record a profile choice.
            var profileChanges = chosen.Where(p => p.Kind == ObservrProposal.ProfileKind && p.Profile != null).Select(p => p.Profile!).ToList();
            var profileStoreBefore = profileChanges.Count > 0 ? profiles.Load(f.TeamId, f.DataSetName) : null;
            foreach (var p in chosen.Where(p => p.Kind == ObservrProposal.Synonym))
            {
                var had = HasSynonym(f.TeamId, f.DataSetName, p.Terms);
                if (!ApplySynonym(f.TeamId, f.DataSetName, p.Terms)) return "The synonym could not be stored.";
                if (!had) undo.AddedSynonyms.Add(p.Terms);
                done.Add(ObservrProposal.Synonym);
            }
            foreach (var p in chosen.Where(p => p.Kind == ObservrProposal.SearchOrAskKind && p.SearchOrAsk != null))
            {
                var before = searchOrAsk.Load(f.TeamId, f.DataSetName);
                if (ApplySearchOrAsk(f.TeamId, f.DataSetName, p.SearchOrAsk!) is { } whyNot) return whyNot;
                if (!undo.HasSearchOrAsk) { undo.HasSearchOrAsk = true; undo.SearchOrAskBefore = before == null ? null : JsonSerializer.Serialize(before, UndoJson); }
                done.Add(ObservrProposal.SearchOrAskKind);
            }
            foreach (var p in chosen.Where(p => p.Kind == ObservrProposal.QueryParametersKind && p.QueryParameters != null))
            {
                var before = queryParameters.Load(f.TeamId, f.DataSetName);
                ApplyQueryParameters(f.TeamId, f.DataSetName, p.QueryParameters!);
                if (!undo.HasQueryParameters) { undo.HasQueryParameters = true; undo.QueryParametersBefore = before == null ? null : JsonSerializer.Serialize(before, UndoJson); }
                done.Add(ObservrProposal.QueryParametersKind);
            }
            foreach (var p in chosen.Where(p => p.Kind == ObservrProposal.SupportTicketKind && p.Ticket != null))
            {
                if (SendTicket(f, p.Ticket!) is { } whyNot) return whyNot;
                done.Add(ObservrProposal.SupportTicketKind);
            }
            var changes = chosen.Where(p => p.Kind == ObservrProposal.FieldKind && p.Field != null).Select(p => p.Field!).ToList();
            if (changes.Count > 0)
            {
                // Each changed field's configuration as it was, read before the change.
                var configuration = engines.FindSearchEngine(f.DataSetName, f.TeamId)?.GetFieldConfiguration() ?? [];
                var before = changes.Select(c => c.Name).Distinct()
                    .Select(n => configuration.FirstOrDefault(x => x.FieldName == n))
                    .Where(x => x != null && !undo.FieldConfigurationBefore.Any(b => b.FieldName == x.FieldName))
                    .Select(x => Restorable(x!)).ToList();
                var own = profiles.Load(f.TeamId, f.DataSetName);
                var profilesBefore = changes.Where(c => c.Profile != null).Select(c => c.Name).Distinct()
                    .Where(n => !undo.ProfilesBefore.ContainsKey(n))
                    .ToDictionary(n => n, n => own?.Assignments.GetValueOrDefault(n));
                if (ApplyFields(f.TeamId, f.DataSetName, changes) is { } why) return why;
                undo.FieldConfigurationBefore.AddRange(before);
                foreach (var (n, id) in profilesBefore) undo.ProfilesBefore[n] = id;
                done.Add(ObservrProposal.FieldKind);
            }
            // After the field changes, so a field made searched by the same Yes can take the profile.
            foreach (var c in profileChanges)
            {
                if (ApplyProfile(f.TeamId, f.DataSetName, c, out var fieldsBefore) is { } why) return why;
                if (!undo.HasProfileStore)
                {
                    undo.HasProfileStore = true;
                    undo.ProfileStoreBefore = profileStoreBefore == null ? null : JsonSerializer.Serialize(profileStoreBefore, UndoJson);
                }
                undo.FieldConfigurationBefore.AddRange(fieldsBefore.Where(x => !undo.FieldConfigurationBefore.Any(b => b.FieldName == x.FieldName)));
                done.Add(ObservrProposal.ProfileKind);
                if (fieldsBefore.Count > 0) done.Add(ObservrProposal.FieldKind);
            }
            return null;
        }

        /// <summary>
        /// Makes or changes one of the dataset's own profiles as the profile editor's Save does: a
        /// new one is added and given to the fields named; a changed one writes its values onto every
        /// field that has it, and onto the fields named. Checked whole first (<see cref="FieldProfiles.Merge"/>),
        /// the fields then the store, as a field change with a profile. A built-in is refused.
        /// <paramref name="fieldsBefore"/> is each field it wrote to as it was. Returns why not, or null.
        /// </summary>
        public string? ApplyProfile(string teamId, string dataSetName, ObservrProfileChange c, out List<FieldProxy> fieldsBefore)
        {
            fieldsBefore = [];
            var own = profiles.Load(teamId, dataSetName)?.Copy() ?? new DatasetFieldProfiles();
            var all = FieldProfiles.All(own);
            var configuration = engines.FindSearchEngine(dataSetName, teamId)?.GetFieldConfiguration().ToList() ?? [];
            FieldProfile profile;
            IReadOnlyList<FieldProxy> having = [];
            if (c.IsNew)
            {
                var name = c.Name?.Trim() ?? "";
                if (name.Length == 0) return "The profile needs a name.";
                if (all.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return $"The dataset already has a profile named {name}.";
                profile = new FieldProfile { Id = FieldProfiles.NewId(name, all), Name = name };
            }
            else
            {
                if (FieldProfiles.IsBuiltInId(c.Id!)) return "A built-in profile is changed in the profile manager on the Fields page: every field on it follows.";
                if (own.Profiles.FirstOrDefault(p => p.Id == c.Id) is not { } existing) return $"The dataset has no profile '{c.Id}'.";
                profile = string.IsNullOrWhiteSpace(c.Name) ? existing : existing with { Name = c.Name.Trim() };
                having = FieldProfiles.FieldsWith(existing.Id, configuration, own);
            }
            var tolerance = profile.Tolerance;
            if (c.Tolerance != null && !Enum.TryParse(c.Tolerance, out tolerance)) return $"'{c.Tolerance}' is not a typo tolerance.";
            profile = profile with
            {
                About = c.About?.Trim() ?? profile.About,
                Tolerance = tolerance,
                RerankWeight = c.RerankWeight ?? profile.RerankWeight,
                // A negative number hands the start of a word back to the search, as a field takes it.
                TypingPrefixFrom = c.TypingPrefixFrom is { } from ? from < 0 ? null : from : profile.TypingPrefixFrom,
                IndexSizes = c.IndexSizes is { Length: > 0 } sizes ? (int[])sizes.Clone() : profile.IndexSizes,
                NumberOfStartCharacters = c.StartCharacters ?? profile.NumberOfStartCharacters,
                NumberOfStopCharacters = c.StopCharacters ?? profile.NumberOfStopCharacters,
            };
            var named = (c.Fields ?? []).Distinct(StringComparer.Ordinal).ToList();
            var (merged, refused) = FieldProfiles.Merge(own, [.. own.Profiles.Where(p => p.Id != profile.Id), profile],
                named.ToDictionary(n => n, _ => (string?)profile.Id), null,
                n => configuration.Any(x => x.FieldName == n),
                n => configuration.FirstOrDefault(x => x.FieldName == n) is { } x && FieldProfiles.IsSearched(x));
            if (merged == null) return refused;

            var targets = having.Select(x => x.FieldName).Concat(named).Distinct(StringComparer.Ordinal).ToList();
            if (targets.Count > 0)
            {
                var proxies = new List<FieldProxy>();
                foreach (var n in targets)
                {
                    var now = configuration.First(x => x.FieldName == n);
                    fieldsBefore.Add(Restorable(now));
                    var proxy = Restorable(now);
                    FieldProfiles.Apply(proxy, profile);
                    proxies.Add(proxy);
                }
                if (ApplyFieldProxies(teamId, dataSetName, proxies.ToArray()) is { } why) { fieldsBefore = []; return why; }
            }
            profiles.Save(teamId, dataSetName, merged);
            return null;
        }

        /// <summary>Sends a ticket Observr drafted, on the owner's Yes, through the support client: to
        /// the Portal, marked as Observr's. Decide runs off the request thread, so waiting is fine.
        /// A ticket cannot be taken back, so Undo leaves it. Returns why not, or null.</summary>
        private string? SendTicket(ObservrFinding f, ObservrTicketDraft draft)
        {
            if (!supportOptions.Enabled) return "Support is switched off on this instance.";
            var where = f.DataSetName.Length > 0 ? $"the dataset {f.DataSetName}" : "the team";
            var result = support.CreateAsync(draft.Kind, draft.Subject, $"{draft.Body}\n\n(Drafted by Observr about {where}, sent by an owner's Yes.)",
                "Observr", null, null, source: "observr").GetAwaiter().GetResult();
            if (!result.Ok) return result.Problem switch
            {
                Support.SupportProblem.NoToken or Support.SupportProblem.Unauthorized => "The ticket needs this instance's licence token, set under Admin, License.",
                Support.SupportProblem.Unreachable => $"Indx's support could not be reached. Try again, or write to {supportOptions.Email}.",
                _ => result.Message ?? "The ticket could not be sent.",
            };
            _ = supportState.RefreshAsync();
            return null;
        }

        private static readonly JsonSerializerOptions UndoJson = new(JsonSerializerDefaults.Web);

        /// <summary>How long after a Yes it can be undone.</summary>
        public static readonly TimeSpan UndoWindow = TimeSpan.FromHours(1);

        /// <summary>Whether a finding's Yes can still be undone: carried out within <see cref="UndoWindow"/>,
        /// with what it replaced on record.</summary>
        public bool CanUndo(ObservrFinding f) =>
            f.Status == ObservrFinding.Accepted && findings.GetUndo(f.Id) is { } u && DateTime.UtcNow - u.CreatedUtc < UndoWindow;

        /// <summary>
        /// Undoes a Yes (Notes/observr-agent-plan.md): the synonyms it added go, the Search or ask
        /// settings and query parameters go back to what they were, and each changed field gets its
        /// roles back (a rebuild, as the change was). The finding then counts as declined and is
        /// kept as a note, so Observr does not propose it again. Returns why not, or null.
        /// </summary>
        public string? Undo(long findingId)
        {
            lock (_deciding)
                if (!_deciding.Add(findingId)) return "This suggestion is already being carried out.";
            try
            {
                var f = findings.Get(findingId);
                if (f == null || !CanUndo(f) || findings.GetUndo(findingId) is not { } kept) return "This can no longer be undone.";
                var undo = JsonSerializer.Deserialize<ObservrUndo>(kept.Json, UndoJson) ?? new();
                var done = new HashSet<string>();
                foreach (var terms in undo.AddedSynonyms)
                {
                    RemoveSynonym(f.TeamId, f.DataSetName, terms);
                    done.Add(ObservrProposal.Synonym);
                }
                if (undo.HasSearchOrAsk)
                {
                    if (undo.SearchOrAskBefore is { } json && JsonSerializer.Deserialize<SearchOrAsk.SearchOrAskSettings>(json, UndoJson) is { } s) searchOrAsk.Save(f.TeamId, f.DataSetName, s);
                    else searchOrAsk.Delete(f.TeamId, f.DataSetName);
                    done.Add(ObservrProposal.SearchOrAskKind);
                }
                if (undo.HasQueryParameters)
                {
                    if (undo.QueryParametersBefore is { } json && JsonSerializer.Deserialize<DatasetQueryParameters>(json, UndoJson) is { } p) queryParameters.Save(f.TeamId, f.DataSetName, p);
                    else queryParameters.Delete(f.TeamId, f.DataSetName);
                    done.Add(ObservrProposal.QueryParametersKind);
                }
                string? why = null;
                if (undo.FieldConfigurationBefore.Count > 0)
                {
                    why = ApplyFieldProxies(f.TeamId, f.DataSetName, undo.FieldConfigurationBefore.ToArray());
                    if (why == null && undo.ProfilesBefore.Count > 0)
                    {
                        var own = profiles.Load(f.TeamId, f.DataSetName) ?? new DatasetFieldProfiles();
                        foreach (var (n, id) in undo.ProfilesBefore)
                            if (id == null) own.Assignments.Remove(n); else own.Assignments[n] = id;
                        profiles.Save(f.TeamId, f.DataSetName, own);
                    }
                    if (why == null) done.Add(ObservrProposal.FieldKind);
                }
                else if (undo.FieldsBefore.Count > 0)
                {
                    // Kept before 9 Nov 2026 as one flag: searched puts both roles back.
                    why = ApplyFieldProxies(f.TeamId, f.DataSetName, undo.FieldsBefore.Select(b => b.ToProxy()).ToArray());
                    if (why == null) done.Add(ObservrProposal.FieldKind);
                }
                if (why == null && undo.HasProfileStore)
                {
                    // The profiles as they were, a made one gone with it; then each field's choice
                    // as it was before this finding's first Yes, which may be older than the copy.
                    var back = undo.ProfileStoreBefore is { } json ? JsonSerializer.Deserialize<DatasetFieldProfiles>(json, UndoJson) : null;
                    if (back == null && undo.ProfilesBefore.Values.Any(id => id != null)) back = new DatasetFieldProfiles();
                    if (back == null) profiles.Delete(f.TeamId, f.DataSetName);
                    else
                    {
                        foreach (var (n, id) in undo.ProfilesBefore)
                            if (id == null) back.Assignments.Remove(n); else back.Assignments[n] = id;
                        profiles.Save(f.TeamId, f.DataSetName, back);
                    }
                    done.Add(ObservrProposal.ProfileKind);
                }
                if (why == null)
                {
                    findings.ClearUndo(findingId);
                    findings.Decide(findingId, ObservrFinding.Declined);
                    foreach (var p in f.Proposals)
                        if (p.SentenceText is { Length: > 0 } text && f.DataSetName.Length > 0)
                            findings.AddNote(f.TeamId, f.DataSetName, "Undone after a Yes: " + text, ObservrNote.Declined);
                }
                if (done.Count > 0)
                    foreach (var listener in Applied?.GetInvocationList() ?? [])
                        try { ((Action<ObservrApplied>)listener)(new ObservrApplied(f.TeamId, f.DataSetName, done)); }
                        catch (Exception ex) { logger.LogWarning(ex, "An Observr applied listener failed"); }
                return why == null ? null : $"{why} The fields keep the change; the rest was undone.";
            }
            finally { lock (_deciding) _deciding.Remove(findingId); }
        }

        private static bool SameTerms(SynonymEntry e, string[] terms)
        {
            var clean = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return clean.Count > 0 && clean.All(t => e.Terms.Contains(t, StringComparer.OrdinalIgnoreCase));
        }

        private bool HasSynonym(string teamId, string dataSetName, string[] terms) =>
            engines.GetSynonyms(dataSetName, teamId)?.Entries.Any(e => SameTerms(e, terms)) == true;

        private void RemoveSynonym(string teamId, string dataSetName, string[] terms)
        {
            if (engines.GetSynonyms(dataSetName, teamId) is not { } list) return;
            var gone = list.Entries.RemoveAll(e => SameTerms(e, terms) && e.Terms.Length == terms.Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            if (gone == 0) return;
            list.Invalidate();
            // Not during a rebuild: the entry stays, and the undo can be asked for again.
            try { engines.SetSynonyms(dataSetName, teamId, list); }
            catch (ShadowBusyException) { }
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
            try { return engines.SetSynonyms(dataSetName, teamId, list); }
            catch (ShadowBusyException) { return false; }
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
            if (s.Validate() is { } invalid) return invalid;
            searchOrAsk.Save(teamId, dataSetName, s);
            return null;
        }

        /// <summary>Carries out query parameters as the tab's Save does: the stored ones with the
        /// change laid over, saved; the store records the change.</summary>
        public void ApplyQueryParameters(string teamId, string dataSetName, ObservrQueryParametersChange c)
        {
            var p = queryParameters.Load(teamId, dataSetName) ?? new DatasetQueryParameters();
            if (c.Tolerance != null && Enum.TryParse<Tolerance>(c.Tolerance, out var tolerance)) (p.Match ??= new MatchProxy()).Tolerance = tolerance;
            if (c.MaxCandidates != null) (p.Candidates ??= new CandidateProxy()).MaxCandidates = c.MaxCandidates.Value <= 0 ? null : c.MaxCandidates;
            if (c.Fallback != null) (p.Candidates ??= new CandidateProxy()).Fallback = c.Fallback;
            if (c.Rest != null) (p.Candidates ??= new CandidateProxy()).Rest = c.Rest;
            if (c.FillReach != null) (p.Candidates ??= new CandidateProxy()).FillReach = Math.Clamp(c.FillReach.Value, 0, 2);
            if (c.Rerank != null) (p.Ranking ??= new RankingProxy()).Rerank = c.Rerank;
            queryParameters.Save(teamId, dataSetName, p);
        }

        /// <summary>A field's configuration as an undo puts it back: its roles, pattern setup,
        /// tolerance and weights. Not its type or sample, which are read, nor its k1, which a BM25F
        /// dataset refuses to have set.</summary>
        private static FieldProxy Restorable(FieldProxy x) => new()
        {
            FieldName = x.FieldName, WordMatching = x.WordMatching, PatternMatching = x.PatternMatching,
            IndexSizes = x.PatternMatching == true ? x.IndexSizes : null,
            NumberOfStartCharacters = x.PatternMatching == true ? x.NumberOfStartCharacters : null,
            NumberOfStopCharacters = x.PatternMatching == true ? x.NumberOfStopCharacters : null,
            Filterable = x.Filterable, Facetable = x.Facetable, Sortable = x.Sortable, Weight = x.Weight,
            // The engine reads back null where the field follows the search, and a proxy's null
            // leaves the value alone: a negative number is how "follows the search" is put back.
            Tolerance = x.Tolerance, RerankWeight = x.RerankWeight, TypingPrefixFrom = FieldProfiles.StartsOf(x) ?? -1,
            // The field's own text rules as they were: an empty list is the engine's again.
            TextRules = x.TextRules is { } rules ? [.. rules] : [],
        };

        /// <summary>Carries out field changes as the Fields tab's Save does: refused as a whole when
        /// invalid, announced, then on a shadow when the change needs a rebuild (this blocks until the
        /// swap) or in place. Returns the reason it could not, or null.</summary>
        public string? ApplyFields(string teamId, string dataSetName, IReadOnlyList<ObservrFieldChange> changes)
        {
            var merged = changes.GroupBy(c => c.Name).Select(g => new ObservrFieldChange(g.Key,
                g.Select(c => c.WordMatching).LastOrDefault(v => v != null), g.Select(c => c.PatternMatching).LastOrDefault(v => v != null),
                g.Select(c => c.Filterable).LastOrDefault(v => v != null), g.Select(c => c.Facetable).LastOrDefault(v => v != null),
                g.Select(c => c.Sortable).LastOrDefault(v => v != null), g.Select(c => c.Weight).LastOrDefault(v => v != null),
                g.Select(c => c.Tolerance).LastOrDefault(v => v != null), g.Select(c => c.RerankWeight).LastOrDefault(v => v != null),
                g.Select(c => c.Profile).LastOrDefault(v => v != null))).ToList();
            // A profile: its values written onto the field as choosing it in the field table does, on
            // the field's pattern matching as the change leaves it, and the choice recorded after.
            var own = profiles.Load(teamId, dataSetName) ?? new DatasetFieldProfiles();
            var all = FieldProfiles.All(own);
            var current = engines.FindSearchEngine(dataSetName, teamId)?.GetFieldConfiguration().ToDictionary(x => x.FieldName, StringComparer.Ordinal) ?? [];
            var proxies = new List<FieldProxy>();
            foreach (var c in merged)
            {
                var proxy = c.ToProxy();
                if (c.Profile != null)
                {
                    if (all.FirstOrDefault(p => p.Id == c.Profile) is not { } profile) return $"The dataset has no profile '{c.Profile}'.";
                    proxy.PatternMatching ??= current.GetValueOrDefault(c.Name)?.PatternMatching;
                    proxy.WordMatching ??= current.GetValueOrDefault(c.Name)?.WordMatching;
                    FieldProfiles.Apply(proxy, profile);
                }
                proxies.Add(proxy);
            }
            if (ApplyFieldProxies(teamId, dataSetName, proxies.ToArray()) is { } why) return why;
            if (merged.Any(c => c.Profile != null))
            {
                foreach (var c in merged.Where(c => c.Profile != null)) own.Assignments[c.Name] = c.Profile!;
                profiles.Save(teamId, dataSetName, own);
            }
            return null;
        }

        /// <summary>Field configuration as the Fields tab's Save carries it out: refused as a whole
        /// when invalid, announced, then on a shadow when it needs a rebuild or in place.</summary>
        private string? ApplyFieldProxies(string teamId, string dataSetName, FieldProxy[] proposed)
        {
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready) return "The dataset is not ready.";
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
