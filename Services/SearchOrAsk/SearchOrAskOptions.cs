namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// The instance's model access for Search or ask (<c>Indx:SearchOrAsk</c>). One OpenRouter key
    /// serves the judge (Jev) and the answer model. Without a key the feature is unavailable and
    /// nothing else changes: a self-hosted install needs no configuration for it.
    /// See Notes/search-or-ask-server.md.
    /// </summary>
    public sealed class SearchOrAskOptions
    {
        public const string Section = "Indx:SearchOrAsk";

        public string? ApiKey { get; set; }

        /// <summary>OpenRouter's System One endpoint, where Jev is served.</summary>
        public string Endpoint { get; set; } = "https://openrouter.ai/api/v1/systemone";

        /// <summary>Pinned, not an alias: the thresholds were measured on this model, and an alias
        /// would move under them.</summary>
        public string JudgeModel { get; set; } = "typesafe/jev-1.13";

        public string AnswerEndpoint { get; set; } = "https://openrouter.ai/api/v1/chat/completions";
        public string AnswerModel { get; set; } = "anthropic/claude-sonnet-5.5";

        /// <summary>A judgement later than this is no use to a visitor who has typed on; the
        /// search is never held up either way.</summary>
        public int JudgeTimeoutMs { get; set; } = 3000;

        /// <summary>Per client address, on top of the instance's own limits: every call costs
        /// money. Intent is generous (a visitor pauses perhaps once a second at most); ask is not.</summary>
        public int IntentPerMinute { get; set; } = 90;
        public int AskPerMinute { get; set; } = 8;

        public bool Available => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
