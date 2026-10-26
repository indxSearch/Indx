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
        /// (<see cref="SearchOrAskOptions.AnswerModel"/>, <see cref="SearchOrAskOptions.FastAnswerModel"/>).</summary>
        public string Answers { get; set; } = BestAnswers;

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

        /// <summary>Null when the settings can be saved, otherwise what is wrong, for a 400.</summary>
        public string? Validate()
        {
            if (!SearchOrAskRecipes.IsKnown(Kind))
                return $"kind must be one of: {string.Join(", ", SearchOrAskRecipes.Kinds)}.";
            if (SiteDescription is { Length: > MaxSiteDescription })
                return $"siteDescription is at most {MaxSiteDescription} characters.";
            if (Answers is not (BestAnswers or FastAnswers))
                return $"answers must be {BestAnswers} or {FastAnswers}.";
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
            if (AskPhrases?.Validate() is { } badPhrases)
                return badPhrases;
            if (Thresholds == null)
                return "thresholds are required.";
            return Thresholds.Validate();
        }
    }

    /// <summary>
    /// Where Jev's probability becomes an offer. Entered at one value and left only below a lower
    /// one, so the offer does not blink while a visitor types; the client applies that, since it
    /// depends on what this visitor has been shown. Defaults measured on the docs set
    /// (Notes/search-or-ask.md): no lookup scored above 0.22 there.
    /// </summary>
    public sealed class SearchOrAskThresholds
    {
        public double Strong { get; set; } = 0.65;
        public double StrongHide { get; set; } = 0.45;
        public double Invite { get; set; } = 0.40;
        public double InviteHide { get; set; } = 0.30;

        public string? Validate()
        {
            foreach (var (name, v) in new[] { ("strong", Strong), ("strongHide", StrongHide), ("invite", Invite), ("inviteHide", InviteHide) })
                if (double.IsNaN(v) || v < 0 || v > 1)
                    return $"thresholds.{name} must be between 0 and 1.";
            if (StrongHide > Strong) return "thresholds.strongHide cannot be above thresholds.strong.";
            if (InviteHide > Invite) return "thresholds.inviteHide cannot be above thresholds.invite.";
            if (Invite > Strong) return "thresholds.invite cannot be above thresholds.strong.";
            return null;
        }

        /// <summary>The offer for one judgement on its own, with no memory of the last one: what a
        /// client with no logic of its own shows. On a catalogue a description of what to list
        /// (<paramref name="criteria"/>) is answered too, with filters and a summary, so a sure one
        /// (at <see cref="Strong"/>) earns the full offer, as intrface has read it from the start;
        /// a middling one earns nothing, since a description scores more generously than a
        /// question and inviting on it made "skole" an invitation (19 Oct 2026). Below that the
        /// asking score decides alone.</summary>
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

    public sealed record SearchOrAskEffective(string SiteDescription, string? TitleField,
        IReadOnlyList<string> SectionFields, IReadOnlyList<string> SnippetFields, SearchOrAskPhrases AskPhrases);
}
