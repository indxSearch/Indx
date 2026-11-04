using System.Text.Json.Serialization;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// A dataset's Search or ask settings (<see cref="SearchOrAskStore"/>). Text and numbers only:
    /// the request the judge receives has a fixed shape, so an owner, or Observr after a yes, can
    /// make the judge better or worse but cannot break the endpoint.
    /// </summary>
    public sealed class SearchOrAskSettings
    {
        /// <summary>Off until an owner switches it on: typed text leaves the server.</summary>
        public bool Enabled { get; set; }

        /// <summary>Search or ask exists on this dataset: the switch in its Options tab, a team admin's
        /// (PUT search-or-ask/shown). Experimental, so off by default. Shown, the dataset gets its
        /// Search or ask tab to set up and try; <see cref="Enabled"/> then takes it live. A settings
        /// save keeps what this was: it is not an editor's to change.</summary>
        public bool Shown { get; set; }

        /// <summary>Which recipe the judge reads (<see cref="SearchOrAskRecipes"/>).</summary>
        public string Kind { get; set; } = SearchOrAskRecipes.Documentation;

        /// <summary>The sentence or two the judge reads about the site. Null: the dataset's description.</summary>
        public string? SiteDescription { get; set; }

        /// <summary>What the judge sees of each result. Null: chosen from the field configuration.</summary>
        public string? TitleField { get; set; }
        public List<string>? SectionFields { get; set; }
        public List<string>? SnippetFields { get; set; }

        public SearchOrAskThresholds Thresholds { get; set; } = new();

        /// <summary>How forward the offer is (27 Oct 2026), from the least to the most:
        /// <c>strict</c>, only the full offer, once the judge is sure it is a question;
        /// <c>soft</c> (the default, as it always was), the quiet "Ask about this" early and the full
        /// offer once the judge is sure; <c>open</c>, asking is always there once something is
        /// typed (Enter asks, whatever the judge says), the judge deciding only how strong the offer
        /// looks; <c>inviting</c>, open with an invitation that sweeps as soon as the judge reads a question
        /// starting (the Hint threshold), but no answer until asked; <c>proactive</c>, inviting, and the answer written on its own once the
        /// judge is sure, reads it as finished, and the visitor has stopped, at most <see cref="DailyProactiveAnswers"/> a
        /// day. The thresholds apply in all five.</summary>
        public string Mode { get; set; } = ModeSoft;
        public const string ModeStrict = "strict";
        public const string ModeSoft = "soft";
        public const string ModeOpen = "open";
        public const string ModeInviting = "inviting";
        public const string ModeProactive = "proactive";
        public static readonly string[] Modes = [ModeStrict, ModeSoft, ModeOpen, ModeInviting, ModeProactive];

        /// <summary>Answers written without being asked, in <see cref="ModeProactive"/>, per dataset
        /// per UTC day. Past it the offer stays and a visitor asks as in <see cref="ModeInviting"/>. An
        /// answer costs hundreds of judgements (about 700 on a Best answer, measured on a film
        /// catalogue), so this is the spend proactive adds.</summary>
        public int DailyProactiveAnswers { get; set; } = 500;

        /// <summary>The offer for one judgement on its own (<see cref="SearchOrAskThresholds.OfferFor(double, double?)"/>),
        /// on this judge's thresholds, in this mode: strict has no quiet offer.</summary>
        public string OfferFor(string? judge, double p, double? criteria = null) =>
            Thresholds.Resolved(Kind, judge).OfferFor(p, criteria) is var offer && Mode == ModeStrict && offer == "invite" ? "none" : offer;

        /// <summary>While the offer is strong (the judge says the visitor is asking), the page shows
        /// the result list only up to the truncation point: the coverage hits stay, the pattern
        /// tail goes, and with nothing confirmed the offer stands alone. The rule runs in the
        /// client, which has the offer's hysteresis and the truncation index; this only says
        /// whether to apply it. Off keeps the whole list (decided 14 Oct 2026, the "mixer").</summary>
        public bool CutWhileAsking { get; set; } = true;

        /// <summary>The cut's two lines, on the scores (14 Oct 2026: the truncation index alone was
        /// too lenient, coverage confirms a record on a two-letter word). While asking, a result is
        /// kept while it scores at least <see cref="CutShareOfTop"/> of the top result, and only when
        /// the top result itself scores at least <see cref="CutTopFloor"/> of the scale; a flat, low
        /// list is nothing. Both 0 to 1; measured on the replay's labelled sequences. The truncation
        /// index the client bounds by is the position of the last confirmed result, -1 for none.</summary>
        public double CutShareOfTop { get; set; } = 0.5;
        public double CutTopFloor { get; set; } = 0.2;

        /// <summary>While asking, the page's own searches run with coverage on whole words only (no
        /// fuzzy, prefix or joined matching, words of three letters or more), and the list is
        /// searched once more when the offer turns strong. The forgiving matching that is right for a
        /// typed name is wrong under a question: on the school register "ullern videregående" confirmed
        /// seven other schools by prefix and fuzzy matches of the second word (14 Oct 2026). The
        /// answer still searches with the full setup, so a typo in a name still finds the record.</summary>
        public bool StrictCoverageWhileAsking { get; set; } = true;

        /// <summary>On a catalogue, what the page does with a description while the visitor types
        /// (the judge's criteria score strong): <c>off</c>, filters only after Ask, as before;
        /// <c>facets</c>, a typed word that equals a facet value becomes that filter at once, no
        /// model; <c>model</c>, the facet pass first, and the filters route (a model call, about
        /// 1.5 s) for what it could not name. A filter applied while typing is a strong move, so
        /// off by default; the active filters panel shows every one with its remove (14 Oct 2026).</summary>
        public string FiltersWhileTyping { get; set; } = FiltersOff;
        public const string FiltersOff = "off";
        public const string FiltersFromFacets = "facets";
        public const string FiltersWithModel = "model";

        /// <summary>A catalogue's notes about its fields, for turning a description into filters:
        /// what the values mean in this domain ("barneskole: grades 1 to 7"). Free text.</summary>
        public string? FilterNotes { get; set; }

        public const int MaxFilterNotes = 3000;

        /// <summary>The site's own instructions for the answers: voice, house rules, what to say when
        /// nothing is found ("formal", "say du", "never recommend another shop"). They go into the
        /// answer prompts below ours, so they shape how an answer speaks and cannot loosen what it may
        /// say: grounding in the records and the citations stay. Null: none.</summary>
        public string? AnswerInstructions { get; set; }

        public const int MaxAnswerInstructions = 1500;

        /// <summary>The site's knowledge, markdown: what is true about the site that no record holds
        /// (what the set covers, what a number means, prices with VAT, delivery times, what is not
        /// sold). The counterpart of <see cref="AnswerInstructions"/>, which say how to answer; this
        /// says what to know. Every answer path reads it, never the judge, and an answer may state it
        /// as what the site says, never as a record (decided 21 Oct 2026). Null: none.</summary>
        public string? Knowledge { get; set; }

        /// <summary>About 2,000 tokens, read with every question.</summary>
        public const int MaxKnowledge = 8000;

        /// <summary>The language every answer is written in, as a language name ("Norwegian"). Null:
        /// the language the question was written in, which is the default.</summary>
        public string? AnswerLanguage { get; set; }

        public const int MaxAnswerLanguage = 40;

        public const string BestAnswers = "best";
        public const string FastAnswers = "fast";

        /// <summary>Which answer model writes: <c>best</c> (the default; quality first) or <c>fast</c>
        /// (first words sooner, a third of the cost). The models are the instance's
        /// (<see cref="SearchOrAskOptions.AnswerModel"/>, <see cref="SearchOrAskOptions.FastAnswerModel"/>).
        /// <see cref="AnswerModel"/>, when set, names the model instead.</summary>
        public string Answers { get; set; } = BestAnswers;

        /// <summary>A model the instance offers, by its id, to write the answers with (28 Oct 2026).
        /// Null: the one <see cref="Answers"/> names. One the instance stops offering falls back to
        /// that too (<see cref="SearchOrAskSetup.ModelFor"/>).</summary>
        public string? AnswerModel { get; set; }

        public const int MaxAnswerModel = 200;

        /// <summary>On a catalogue, how a question is answered: <c>several</c>, an agent that searches,
        /// sorts and filters until it has the answer, or <c>one</c>, a single search and a summary of
        /// what it found. Null follows <see cref="Answers"/>, as before the two were apart (28 Oct
        /// 2026): <c>best</c> several, <c>fast</c> one. Apart, a stronger model can answer from one
        /// search, or a quicker one search several times.</summary>
        public string? Searches { get; set; }
        public const string SeveralSearches = "several";
        public const string OneSearch = "one";

        /// <summary><see cref="Searches"/> resolved.</summary>
        public string SearchesResolved() => Searches ?? (Answers == FastAnswers ? OneSearch : SeveralSearches);

        /// <summary>The words a question is built with, taken off its start so the list searches what
        /// it is about while the visitor types (<see cref="SearchOrAskPhrases"/>). Null: the default
        /// English list; the GET's <c>effective.askPhrases</c> is what applies.</summary>
        public SearchOrAskPhrases? AskPhrases { get; set; }

        /// <summary>While a question is typed, the list searches what it is about: the Ask phrases
        /// are taken off the typed text before it is searched, on every keystroke, and the judge
        /// reads the results of the same text. Off searches the text as typed and the phrases do
        /// nothing to the list.</summary>
        public bool RefocusWhileTyping { get; set; } = true;

        /// <summary>The quiet offer ("Ask about this") the moment the typed text takes a question's
        /// shape (an Ask phrase matched), before the judge has read it; the judge's word on the pause
        /// confirms it or takes it back. Off leaves every offer to the judge.</summary>
        public bool InviteOnAskPhrase { get; set; } = true;

        /// <summary>Judge calls per dataset per UTC day. Spend is bounded, not hoped for.</summary>
        public int DailyCallBudget { get; set; } = 5000;

        public const int MaxSiteDescription = 600;

        /// <summary>Null when the settings can be saved, otherwise what is wrong, for a 400. The
        /// thresholds are checked in order with the <paramref name="judge"/>'s own filling the gaps.</summary>
        public string? Validate(string? judge = null)
        {
            if (!SearchOrAskRecipes.IsKnown(Kind))
                return $"kind must be one of: {string.Join(", ", SearchOrAskRecipes.Kinds)}.";
            if (SiteDescription is { Length: > MaxSiteDescription })
                return $"siteDescription is at most {MaxSiteDescription} characters.";
            if (Answers is not (BestAnswers or FastAnswers))
                return $"answers must be {BestAnswers} or {FastAnswers}.";
            if (Searches is not (null or SeveralSearches or OneSearch))
                return $"searches must be {SeveralSearches} or {OneSearch}.";
            if (AnswerModel is { Length: > MaxAnswerModel })
                return $"answerModel is at most {MaxAnswerModel} characters.";
            if (FiltersWhileTyping is not (FiltersOff or FiltersFromFacets or FiltersWithModel))
                return $"filtersWhileTyping must be {FiltersOff}, {FiltersFromFacets} or {FiltersWithModel}.";
            foreach (var (name, v) in new[] { ("cutShareOfTop", CutShareOfTop), ("cutTopFloor", CutTopFloor) })
                if (double.IsNaN(v) || v < 0 || v > 1)
                    return $"{name} must be between 0 and 1.";
            if (FilterNotes is { Length: > MaxFilterNotes })
                return $"filterNotes is at most {MaxFilterNotes} characters.";
            if (AnswerInstructions is { Length: > MaxAnswerInstructions })
                return $"answerInstructions is at most {MaxAnswerInstructions} characters.";
            if (Knowledge is { Length: > MaxKnowledge })
                return $"knowledge is at most {MaxKnowledge} characters.";
            if (AnswerLanguage is { Length: > MaxAnswerLanguage })
                return $"answerLanguage is at most {MaxAnswerLanguage} characters.";
            if (DailyCallBudget < 0)
                return "dailyCallBudget cannot be negative.";
            if (!Modes.Contains(Mode))
                return $"mode must be one of: {string.Join(", ", Modes)}.";
            if (DailyProactiveAnswers < 0)
                return "dailyProactiveAnswers cannot be negative.";
            if (AskPhrases?.Validate() is { } badPhrases)
                return badPhrases;
            if (Thresholds == null)
                return "thresholds are required.";
            return Thresholds.Validate(Kind, judge);
        }
    }

    /// <summary>
    /// Where the judge's probability becomes an offer. Entered at one value and left only below a
    /// lower one, so the offer does not blink while a visitor types; the client applies that, since
    /// it depends on what this visitor has been shown. A value left null is the judge's own
    /// (<see cref="For"/>), measured for it: switching the judge brings its numbers along, and only
    /// what an owner set by hand stays. What a client is sent is always <see cref="Resolved"/>, every
    /// value a number, since a published intrface spreads these over its defaults and would read a
    /// null as 0.
    /// </summary>
    public sealed class SearchOrAskThresholds
    {
        public double? Strong { get; set; }
        public double? StrongHide { get; set; }
        public double? Invite { get; set; }
        public double? InviteHide { get; set; }
        /// <summary>The inviting and proactive modes' hint, "For a quick answer, type a bit more",
        /// from this score up: low, since its job is to show early that the field answers. Null is
        /// the judge's and the kind's own (<see cref="HintFor"/>). For Jev, measured 27 Oct 2026 on
        /// bare openings against lookups of the same length: on a catalogue openings scored from 0.18
        /// ("What") and lookups to 0.15 ("mew"), in Norwegian from 0.19 and to 0.11; on the Indx docs
        /// lookups reach 0.30 ("install"). Then replayed keystroke by keystroke on the labelled sets,
        /// where a name being typed scores above the whole name ("skistua b..."): at 0.16 15 of 80
        /// school-register lookups got the hint, at 0.25 4, every question still reaching it; on
        /// films 1 of 59 either way; on the docs 0.31 gives 4 of 29, all 40 questions.</summary>
        public double? Hint { get; set; }
        /// <summary>Proactive answers on its own only from this "finished" score up, so "what type is"
        /// waits for the rest.</summary>
        public double Finished { get; set; } = 0.5;

        /// <summary>Jev's, measured on the docs set (Notes/search-or-ask.md): no lookup scored above
        /// 0.22 there, and the lowest question that was not missed 0.47.</summary>
        public static readonly SearchOrAskThresholds Jev = new() { Strong = 0.65, StrongHide = 0.45, Invite = 0.40, InviteHide = 0.30 };

        /// <summary>OpenAI's Decisions API's, measured 4 Nov 2026 on the docs set keystroke by keystroke
        /// with its own wording (<see cref="SearchOrAskRecipes.Questions"/>): it hardly has a middle,
        /// 5% of its calls between 0.2 and 0.8 against Jev's 46%, and no lookup scored above 0.01 on
        /// any keystroke. So the invitation can start at 0.2, where two more questions get one than at
        /// 0.4, and the hint at 0.05, with no lookup offered anything; the full offer is the same at
        /// 0.5 as at 0.65 and stays where Jev has it. Measured on the docs only: a catalogue uses the
        /// same numbers until it is.</summary>
        public static readonly SearchOrAskThresholds Decisions = new() { Strong = 0.65, StrongHide = 0.45, Invite = 0.20, InviteHide = 0.10, Hint = 0.05 };

        /// <summary>The judge's own thresholds: Jev's unless it is the Decisions API.</summary>
        public static SearchOrAskThresholds For(string? judge) => judge == SearchOrAskProviders.OpenAiDecisions ? Decisions : Jev;

        public static double HintFor(string kind, string? judge = null) =>
            For(judge).Hint ?? (kind == SearchOrAskRecipes.Catalogue ? 0.25 : 0.31);

        /// <summary>A copy with every value a number: the dataset's where set, else the judge's and
        /// the kind's own. What each judgement reports and what a client is sent.</summary>
        public SearchOrAskThresholds Resolved(string kind, string? judge = null)
        {
            var d = For(judge);
            return new()
            {
                Strong = Strong ?? d.Strong, StrongHide = StrongHide ?? d.StrongHide,
                Invite = Invite ?? d.Invite, InviteHide = InviteHide ?? d.InviteHide,
                Hint = Hint ?? HintFor(kind, judge), Finished = Finished,
            };
        }

        /// <summary>Each value that is the judge's own, or Jev's (every saved value was Jev's default
        /// until the judges had their own, 4 Nov 2026), back to null, so that it follows the judge.
        /// Applied to what is saved and what is read: a client that reads the settings, changes
        /// something else and writes them back has sent the numbers it was given, and must not pin
        /// this judge's thresholds on the next.</summary>
        public void Normalise(string kind, string? judge = null)
        {
            var d = For(judge);
            bool Own(double? v, double? mine, double? jev) => v is { } x && (x == mine || x == jev);
            if (Own(Strong, d.Strong, Jev.Strong)) Strong = null;
            if (Own(StrongHide, d.StrongHide, Jev.StrongHide)) StrongHide = null;
            if (Own(Invite, d.Invite, Jev.Invite)) Invite = null;
            if (Own(InviteHide, d.InviteHide, Jev.InviteHide)) InviteHide = null;
            if (Own(Hint, HintFor(kind, judge), HintFor(kind))) Hint = null;
        }

        /// <summary>Each value between 0 and 1, and in order once the judge's own fill the gaps.</summary>
        public string? Validate(string kind = SearchOrAskRecipes.Documentation, string? judge = null)
        {
            foreach (var (name, v) in new[] { ("strong", Strong), ("strongHide", StrongHide), ("invite", Invite), ("inviteHide", InviteHide), ("hint", Hint) })
                if (v is { } x && (double.IsNaN(x) || x < 0 || x > 1))
                    return $"thresholds.{name} must be between 0 and 1.";
            if (double.IsNaN(Finished) || Finished < 0 || Finished > 1) return "thresholds.finished must be between 0 and 1.";
            var r = Resolved(kind, judge);
            if (r.StrongHide > r.Strong) return "thresholds.strongHide cannot be above thresholds.strong.";
            if (r.InviteHide > r.Invite) return "thresholds.inviteHide cannot be above thresholds.invite.";
            if (r.Invite > r.Strong) return "thresholds.invite cannot be above thresholds.strong.";
            if (Hint > r.Invite) return "thresholds.hint cannot be above thresholds.invite.";
            return null;
        }

        /// <summary>The offer for one judgement on its own, with no memory of the last one: what a
        /// client with no logic of its own shows. Call it on <see cref="Resolved"/> thresholds. On a
        /// catalogue a description of what to list (<paramref name="criteria"/>) is answered too, with
        /// filters and a summary, so a sure one (at <see cref="Strong"/>) earns the full offer, as
        /// intrface has read it from the start; a middling one earns nothing, since a description
        /// scores more generously than a question and inviting on it made "skole" an invitation (19
        /// Oct 2026). Below that the asking score decides alone.</summary>
        public string OfferFor(double p, double? criteria = null) =>
            criteria >= Strong ? "strong" : OfferFor(p);
        private string OfferFor(double p) => p >= Strong ? "strong" : p >= Invite ? "invite" : "none";
    }

    /// <summary>What a search result is reduced to for the judge, resolved from the settings and the
    /// field configuration.</summary>
    public sealed record SearchOrAskFields(string? TitleField, IReadOnlyList<string> SectionFields,
        IReadOnlyList<string> SnippetFields);

    /// <summary>GET …/search-or-ask: what the dataset sets, what it runs with, and whether the
    /// instance can run it at all.</summary>
    public sealed record SearchOrAskSettingsResponse(
        SearchOrAskSettings Settings,
        [property: JsonPropertyName("effective")] SearchOrAskEffective Effective,
        bool Available);

    /// <param name="SiteDescription">What the judge reads about the site: the setting, else the
    /// dataset's description, else a sentence naming the dataset.</param>
    /// <param name="TitleField">The field a result is titled by for the judge (<see cref="SearchOrAskFields"/>).</param>
    /// <param name="SectionFields">The fields a result's section is read from.</param>
    /// <param name="SnippetFields">The fields a result's snippet is read from.</param>
    /// <param name="AskPhrases">The Ask phrases in force: the dataset's, else the defaults.</param>
    /// <param name="AnswerModel">The model the answers are written with.</param>
    /// <param name="Searches">How a catalogue's question is answered: several or one.</param>
    /// <param name="AnswerModels">The models the instance offers for <c>answerModel</c>.</param>
    public sealed record SearchOrAskEffective(string SiteDescription, string? TitleField,
        IReadOnlyList<string> SectionFields, IReadOnlyList<string> SnippetFields, SearchOrAskPhrases AskPhrases,
        string? AnswerModel = null, string? Searches = null, IReadOnlyList<string>? AnswerModels = null);
}
