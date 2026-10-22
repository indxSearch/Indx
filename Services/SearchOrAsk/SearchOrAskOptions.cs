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

        /// <summary>The answer provider's key (OpenRouter's, by default).</summary>
        public string? ApiKey { get; set; }
        /// <summary>openrouter, openai or anthropic.</summary>
        public string? Provider { get; set; }
        /// <summary>The OpenAI-compatible endpoint's base URL (…/v1).</summary>
        public string? BaseUrl { get; set; }
        /// <summary>OpenRouter's EU endpoint, where requests stay in the EU.</summary>
        public bool? EuRouting { get; set; }
        /// <summary>openrouter or typesafe (Jev), or openai (the Decisions API).</summary>
        public string? JudgeProvider { get; set; }
        /// <summary>The judge's own key, when its door is not the answer provider's.</summary>
        public string? JudgeApiKey { get; set; }

        /// <summary>Pinned, not an alias: the thresholds were measured on this model, and an alias
        /// would move under them.</summary>
        public string JudgeModel { get; set; } = "typesafe/jev-1.13";
        /// <summary>TypeSafe's spelling of the same pinned model, on their own endpoint.</summary>
        public string TypeSafeJudgeModel { get; set; } = "jev-1.13.0";
        /// <summary>OpenAI's Decisions API model, when the judge is that instead of Jev.</summary>
        public string DecisionsModel { get; set; } = "gpt-6-luna";
        /// <summary>The model a dataset's <c>answers: best</c> writes with, the default. Measured on
        /// the docs (11 Oct 2026): first words after 3.5 to 5 s, about $0.018 an answer.</summary>
        public string AnswerModel { get; set; } = "anthropic/claude-sonnet-5.5";

        /// <summary>The model for <c>answers: fast</c>: first words after about 1.2 s, about $0.005.</summary>
        public string FastAnswerModel { get; set; } = "anthropic/claude-haiku-4.5";

        /// <summary>Turns a catalogue visitor's description into filters, as JSON. Haiku kept the source
        /// record in 40 of 40 descriptions on the school register, in 1.2 to 2 s.</summary>
        public string FilterModel { get; set; } = "anthropic/claude-haiku-4.5";


        /// <summary>A judgement later than this is no use to a visitor who has typed on; the
        /// search is never held up either way.</summary>
        public int JudgeTimeoutMs { get; set; } = 3000;

        /// <summary>Per client address, on top of the instance's own limits: every call costs
        /// money. Intent is generous (a visitor pauses perhaps once a second at most); ask is not.</summary>
        public int IntentPerMinute { get; set; } = 90;
        public int AskPerMinute { get; set; } = 8;

        /// <summary>Whether the configuration alone names a key (the console may still).</summary>
        public bool Available => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
