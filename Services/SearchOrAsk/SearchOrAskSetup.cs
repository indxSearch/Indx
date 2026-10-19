namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>The doors Search or ask can use. The judge is Jev whichever door; the answers and
    /// the filters go to the provider the admin chose. Decided 13 Oct 2026: a developer's own key
    /// with their own provider, the way they bring their own embeddings, so nothing here is a
    /// lock-in to one broker. Notes/search-or-ask-server.md.</summary>
    public static class SearchOrAskProviders
    {
        /// <summary>OpenRouter: one key for the judge and the answers; reports a cost per call.</summary>
        public const string OpenRouter = "openrouter";
        /// <summary>Any endpoint that speaks OpenAI's chat completions: OpenAI, Azure OpenAI, Mistral,
        /// Groq, a local model. Needs a base URL and model names; costs are estimated.</summary>
        public const string OpenAi = "openai";
        /// <summary>Anthropic's own API, with their key; costs are estimated.</summary>
        public const string Anthropic = "anthropic";
        /// <summary>The judge through TypeSafe's own endpoint, with their key.</summary>
        public const string TypeSafe = "typesafe";
        /// <summary>The judge as OpenAI's Decisions API (gpt-6-luna) instead of Jev: the same named
        /// questions answered with a probability, far more decided (Notes/search-or-ask.md, "The
        /// same run judged by OpenAI's Decisions API"); the thresholds were measured on Jev.</summary>
        public const string OpenAiDecisions = "decisions";

        public static readonly string[] Answer = [OpenRouter, OpenAi, Anthropic];
        public static readonly string[] Judge = [OpenRouter, TypeSafe, OpenAiDecisions];

        public const string OpenRouterGlobal = "https://openrouter.ai/api/v1";
        public const string OpenRouterEu = "https://eu.openrouter.ai/api/v1";
        public const string TypeSafeJudge = "https://api.typesafe.ai/v1/systemone";
        public const string OpenAiDecisionsEndpoint = "https://api.openai.com/v1/decisions";
        public const string OpenAiDefault = "https://api.openai.com/v1";
        public const string AnthropicMessages = "https://api.anthropic.com/v1/messages";

        /// <summary>The name a privacy line uses.</summary>
        public static string Label(string provider) => provider switch
        {
            OpenAi => "the OpenAI-compatible endpoint",
            Anthropic => "Anthropic",
            TypeSafe => "TypeSafe",
            OpenAiDecisions => "OpenAI",
            _ => "OpenRouter",
        };
    }

    /// <summary>What the console holds about the providers, beside the key: read through
    /// <see cref="ISearchOrAskSwitch"/>, so a test can stand in for the settings file.</summary>
    public sealed record SearchOrAskProviderSettings(string? Provider = null, string? ApiKey = null, string? BaseUrl = null, bool EuRouting = false,
        string? JudgeProvider = null, string? JudgeApiKey = null, string? AnswerModel = null, string? FastAnswerModel = null, string? FilterModel = null);

    /// <summary>
    /// Where each call goes and with which key, resolved from the configuration (which wins) and
    /// the console. One object per resolution, so a request reads one consistent setup.
    /// </summary>
    public sealed record SearchOrAskSetup(
        string Provider, string? AnswerKey, string AnswerBaseUrl,
        string JudgeProvider, string? JudgeKey, string JudgeEndpoint, string JudgeModel,
        string AnswerModel, string FastAnswerModel, string FilterModel,
        bool EuRouting)
    {
        /// <summary>Answers and filters can run: the answer provider has a key.</summary>
        public bool AnswersAvailable => !string.IsNullOrWhiteSpace(AnswerKey);
        /// <summary>The judge can run: Jev's door has a key.</summary>
        public bool JudgeAvailable => !string.IsNullOrWhiteSpace(JudgeKey);
        /// <summary>Whether the answer provider reports a cost per call (OpenRouter does); else the
        /// cost is estimated from <see cref="ModelPrices"/>.</summary>
        public bool ReportsCost => Provider == SearchOrAskProviders.OpenRouter;
        /// <summary>The names a privacy line lists: who receives what visitors type.</summary>
        public string Receivers
        {
            get
            {
                var names = new List<string>();
                if (JudgeAvailable) names.Add(SearchOrAskProviders.Label(JudgeProvider));
                var answer = SearchOrAskProviders.Label(Provider);
                if (AnswersAvailable && !names.Contains(answer)) names.Add(answer);
                if (names.Count == 0) return "the model providers";
                return names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
            }
        }

        public string AnswerModelFor(string answers) => answers == SearchOrAskSettings.FastAnswers ? FastAnswerModel : AnswerModel;

        /// <summary>Resolves the setup: the configuration's values win over the console's, and a
        /// provider's model names default per provider when neither names them.</summary>
        public static SearchOrAskSetup Resolve(SearchOrAskOptions o, SearchOrAskProviderSettings? c)
        {
            c ??= new SearchOrAskProviderSettings();
            string Pick(string? config, string? console, string fallback) =>
                !string.IsNullOrWhiteSpace(config) ? config.Trim() : !string.IsNullOrWhiteSpace(console) ? console.Trim() : fallback;
            string? Key(string? config, string? console) =>
                !string.IsNullOrWhiteSpace(config) ? config.Trim() : !string.IsNullOrWhiteSpace(console) ? console.Trim() : null;

            var provider = Pick(o.Provider, c.Provider, SearchOrAskProviders.OpenRouter).ToLowerInvariant();
            if (!SearchOrAskProviders.Answer.Contains(provider)) provider = SearchOrAskProviders.OpenRouter;
            var judgeProvider = Pick(o.JudgeProvider, c.JudgeProvider, SearchOrAskProviders.OpenRouter).ToLowerInvariant();
            if (!SearchOrAskProviders.Judge.Contains(judgeProvider)) judgeProvider = SearchOrAskProviders.OpenRouter;
            var eu = o.EuRouting ?? c.EuRouting;
            var openRouter = eu ? SearchOrAskProviders.OpenRouterEu : SearchOrAskProviders.OpenRouterGlobal;

            var answerKey = Key(o.ApiKey, c.ApiKey);
            // The judge's key: its own when one is set; else the answer key, when both doors are OpenRouter.
            var judgeKey = Key(o.JudgeApiKey, c.JudgeApiKey)
                ?? (judgeProvider == SearchOrAskProviders.OpenRouter && provider == SearchOrAskProviders.OpenRouter ? answerKey : null);

            var answerBase = provider switch
            {
                SearchOrAskProviders.OpenRouter => openRouter,
                SearchOrAskProviders.Anthropic => SearchOrAskProviders.AnthropicMessages,
                _ => Pick(o.BaseUrl, c.BaseUrl, SearchOrAskProviders.OpenAiDefault).TrimEnd('/'),
            };
            // The judge is never EU-routed: OpenRouter's EU endpoint serves no Jev (71 models,
            // none of TypeSafe's, 19 Oct 2026), and sending it there answered 403 and stopped
            // Search or ask whole. The answers and the filters keep to the EU; the admin page says
            // where the decisions go.
            var judgeEndpoint = judgeProvider switch
            {
                SearchOrAskProviders.TypeSafe => SearchOrAskProviders.TypeSafeJudge,
                SearchOrAskProviders.OpenAiDecisions => SearchOrAskProviders.OpenAiDecisionsEndpoint,
                _ => SearchOrAskProviders.OpenRouterGlobal + "/systemone",
            };
            var judgeModel = judgeProvider switch
            {
                SearchOrAskProviders.TypeSafe => o.TypeSafeJudgeModel,
                SearchOrAskProviders.OpenAiDecisions => o.DecisionsModel,
                _ => o.JudgeModel,
            };

            var (answer, fast, filter) = ModelPrices.DefaultModels(provider);
            return new SearchOrAskSetup(provider, answerKey, answerBase, judgeProvider, judgeKey, judgeEndpoint, judgeModel,
                Pick(provider == SearchOrAskProviders.OpenRouter ? o.AnswerModel : null, c.AnswerModel, answer),
                Pick(provider == SearchOrAskProviders.OpenRouter ? o.FastAnswerModel : null, c.FastAnswerModel, fast),
                Pick(provider == SearchOrAskProviders.OpenRouter ? o.FilterModel : null, c.FilterModel, filter),
                eu);
        }
    }

    /// <summary>
    /// Prices per million tokens for the models we name, so a provider that does not report a cost
    /// per call (everyone but OpenRouter) still gives the statistics a number, marked estimated.
    /// Overridable from configuration (<c>Indx:SearchOrAsk:Prices</c>, model id to "in,out" in USD),
    /// since list prices move and a contract may differ.
    /// </summary>
    public static class ModelPrices
    {
        /// <summary>USD per million input and output tokens. Anthropic's list prices for the models
        /// the defaults name, as of Oct 2026; OpenAI ids are not listed since the admin names them.</summary>
        private static readonly Dictionary<string, (double In, double Out)> Defaults = new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-5-5"] = (3, 15),
            ["claude-haiku-4-5"] = (1, 5),
            ["claude-haiku-5-5"] = (1, 5),
            ["claude-opus-5-5"] = (15, 75),
            ["anthropic/claude-sonnet-5.5"] = (3, 15),
            ["anthropic/claude-haiku-4.5"] = (1, 5),
        };

        /// <summary>The model ids a provider is called with when nobody names them.</summary>
        public static (string Answer, string Fast, string Filter) DefaultModels(string provider) => provider switch
        {
            SearchOrAskProviders.Anthropic => ("claude-sonnet-5-5", "claude-haiku-4-5", "claude-haiku-4-5"),
            SearchOrAskProviders.OpenAi => ("", "", ""),
            _ => ("anthropic/claude-sonnet-5.5", "anthropic/claude-haiku-4.5", "anthropic/claude-haiku-4.5"),
        };

        /// <summary>The estimate, or null when the model is not priced.</summary>
        public static double? Estimate(string model, long inputTokens, long outputTokens, IReadOnlyDictionary<string, string>? overrides = null)
        {
            (double In, double Out)? price = null;
            if (overrides != null && overrides.TryGetValue(model, out var text))
            {
                var parts = text.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var i)
                    && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var o))
                    price = (i, o);
            }
            price ??= Defaults.TryGetValue(model, out var d) ? d : null;
            if (price == null) return null;
            return (inputTokens * price.Value.In + outputTokens * price.Value.Out) / 1_000_000;
        }
    }
}
