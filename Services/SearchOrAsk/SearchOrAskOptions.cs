namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// The instance's model access for Search or ask (<c>Indx:SearchOrAsk</c>). The customer's own
    /// key with the provider they choose (<see cref="SearchOrAskProviders"/>): OpenRouter by
    /// default, with one key for the judge (Jev) and the answers; else an OpenAI-compatible endpoint
    /// or Anthropic for the answers, and Jev through OpenRouter or TypeSafe. Every value here can
    /// also be set in the console (Admin, Settings); a value here wins. Without a key the feature is
    /// unavailable and nothing else changes: a self-hosted install needs no configuration for it.
    /// See Notes/search-or-ask-server.md.
    /// </summary>
    public sealed class SearchOrAskOptions
    {
        public const string Section = "Indx:SearchOrAsk";

        /// <summary>One key per provider (<c>Keys:OpenRouter</c>, <c>Keys:OpenAi</c>, <c>Keys:Anthropic</c>,
        /// <c>Keys:TypeSafe</c>, <c>Keys:Compatible</c>): each wins over the console's key for that
        /// provider, and the rest can still be set in the console.</summary>
        public Dictionary<string, string>? Keys { get; set; }
        /// <summary>The answer provider's key, from before keys were kept per provider: read as the
        /// key of <see cref="Provider"/> (OpenRouter when none is named).</summary>
        public string? ApiKey { get; set; }
        /// <summary>openrouter, openai, anthropic or compatible.</summary>
        public string? Provider { get; set; }
        /// <summary>The OpenAI-compatible endpoint's base URL (…/v1).</summary>
        public string? BaseUrl { get; set; }
        /// <summary>Keep the requests in the EU where the provider can: OpenRouter's EU endpoint for
        /// the answers, OpenAI's EU data residency for everything.</summary>
        public bool? EuRouting { get; set; }
        /// <summary>standard or ultrafast: OpenAI's speed tier for the answers.</summary>
        public string? AnswerSpeed { get; set; }
        /// <summary>openrouter or typesafe (Jev), or openai (the Decisions API).</summary>
        public string? JudgeProvider { get; set; }
        /// <summary>The judge's key, from before keys were kept per provider: read as the key of the
        /// judge's door.</summary>
        public string? JudgeApiKey { get; set; }

        /// <summary>Pinned, not an alias: the thresholds were measured on this model, and an alias
        /// would move under them.</summary>
        public string JudgeModel { get; set; } = "typesafe/jev-1.13";
        /// <summary>TypeSafe's spelling of the same pinned model, on their own endpoint.</summary>
        public string TypeSafeJudgeModel { get; set; } = "jev-1.13.0";
        /// <summary>OpenAI's Decisions API model, when the judge is that instead of Jev.</summary>
        public string DecisionsModel { get; set; } = "gpt-6-luna";
        /// <summary>The OpenRouter models, when the configuration names them; else the console's, else
        /// <see cref="ModelDefaults"/> (Sonnet for <c>answers: best</c>, first words after 3.5 to 5 s,
        /// about $0.018 an answer on the docs, 11 Oct 2026; Haiku for <c>answers: fast</c>, about
        /// 1.2 s and $0.005, and for the filters, which kept the source record in 40 of 40
        /// descriptions on the school register). Null by default, so the console can choose.</summary>
        public string? AnswerModel { get; set; }
        public string? FastAnswerModel { get; set; }
        public string? FilterModel { get; set; }
        /// <summary>Models offered to the datasets beside the two above, which a dataset owner may
        /// choose to write its answers (<see cref="SearchOrAskSettings.AnswerModel"/>). Null: the
        /// console's list.</summary>
        public string[]? MoreAnswerModels { get; set; }


        /// <summary>A judgement later than this is no use to a visitor who has typed on; the
        /// search is never held up either way.</summary>
        public int JudgeTimeoutMs { get; set; } = 3000;

        /// <summary>Per client address, on top of the instance's own limits: every call costs
        /// money. Intent is generous (a visitor pauses perhaps once a second at most); ask is not.</summary>
        public int IntentPerMinute { get; set; } = 90;
        public int AskPerMinute { get; set; } = 8;

        /// <summary>Whether the configuration alone names a key (the console may still).</summary>
        public bool Available => !string.IsNullOrWhiteSpace(ApiKey) || Keys?.Values.Any(k => !string.IsNullOrWhiteSpace(k)) == true;
    }
}
