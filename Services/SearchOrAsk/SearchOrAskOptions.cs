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
        /// <summary>The model a dataset's <c>answers: best</c> writes with, the default. Measured on
        /// the docs (11 Oct 2026): first words after 3.5 to 5 s, about $0.018 an answer.</summary>
        public string AnswerModel { get; set; } = "anthropic/claude-sonnet-5.5";

        /// <summary>The model for <c>answers: fast</c>: first words after about 1.2 s, about $0.005.</summary>
        public string FastAnswerModel { get; set; } = "anthropic/claude-haiku-4.5";

        /// <summary>Turns a catalogue visitor's description into filters, as JSON. Haiku kept the source
        /// record in 40 of 40 descriptions on the school register, in 1.2 to 2 s.</summary>
        public string FilterModel { get; set; } = "anthropic/claude-haiku-4.5";

        /// <summary>The model a dataset's <see cref="SearchOrAskSettings.Answers"/> choice names.</summary>
        public string AnswerModelFor(string answers) => answers == SearchOrAskSettings.FastAnswers ? FastAnswerModel : AnswerModel;

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
