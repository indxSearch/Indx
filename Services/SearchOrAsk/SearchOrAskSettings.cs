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
            if (FilterNotes is { Length: > MaxFilterNotes })
                return $"filterNotes is at most {MaxFilterNotes} characters.";
            if (AnswerInstructions is { Length: > MaxAnswerInstructions })
                return $"answerInstructions is at most {MaxAnswerInstructions} characters.";
            if (AnswerLanguage is { Length: > MaxAnswerLanguage })
                return $"answerLanguage is at most {MaxAnswerLanguage} characters.";
            if (DailyCallBudget < 0)
                return "dailyCallBudget cannot be negative.";
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
        /// client with no logic of its own shows.</summary>
        public string OfferFor(double p) => p >= Strong ? "strong" : p >= Invite ? "invite" : "none";
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
        IReadOnlyList<string> SectionFields, IReadOnlyList<string> SnippetFields);
}
