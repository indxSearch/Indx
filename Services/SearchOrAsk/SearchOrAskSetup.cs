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
        /// <summary>OpenAI itself: its models named by default, its EU data residency
        /// (eu.api.openai.com) behind the EU switch, its Ultrafast tier for the answers, and the one key
        /// the Decisions API shares. Until 26 Oct 2026 this id meant any compatible endpoint; a saved
        /// "openai" with another host's base URL is read as <see cref="Compatible"/>.</summary>
        public const string OpenAi = "openai";
        /// <summary>Any other endpoint that speaks OpenAI's chat completions: Azure OpenAI, Mistral,
        /// Groq, a local model. Needs a base URL and model names.</summary>
        public const string Compatible = "compatible";
        /// <summary>Anthropic's own API, with their key; costs are estimated.</summary>
        public const string Anthropic = "anthropic";
        /// <summary>The judge through TypeSafe's own endpoint, with their key.</summary>
        public const string TypeSafe = "typesafe";
        /// <summary>The judge as OpenAI's Decisions API (gpt-6-luna) instead of Jev: the same named
        /// questions answered with a probability, far more decided (Notes/search-or-ask.md, "The
        /// same run judged by OpenAI's Decisions API"); the thresholds were measured on Jev.</summary>
        public const string OpenAiDecisions = "decisions";

        public static readonly string[] Answer = [OpenRouter, OpenAi, Anthropic, Compatible];
        public static readonly string[] Judge = [OpenRouter, TypeSafe, OpenAiDecisions];
        /// <summary>The providers a key is kept for, one each: a role names a provider, and the
        /// provider's key goes with it, so switching is a choice and never a key typed again.</summary>
        public static readonly string[] KeyHolders = [OpenRouter, OpenAi, Anthropic, TypeSafe, Compatible];

        /// <summary>Whose key a judge door uses: the Decisions API is OpenAI's, on OpenAI's key.</summary>
        public static string JudgeKeyHolder(string judgeProvider) => judgeProvider switch
        {
            TypeSafe => TypeSafe,
            OpenAiDecisions => OpenAi,
            _ => OpenRouter,
        };

        /// <summary>A saved answer provider as it is read now: "openai" with a base URL on another
        /// host was the compatible door before OpenAI had one of its own.</summary>
        public static string Normalize(string? provider, string? baseUrl)
        {
            var p = (provider ?? OpenRouter).Trim().ToLowerInvariant();
            if (p == OpenAi && !string.IsNullOrWhiteSpace(baseUrl) && !IsOpenAiHost(baseUrl)) return Compatible;
            return p;
        }

        private static bool IsOpenAiHost(string url) =>
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Host is "api.openai.com" or "eu.api.openai.com";

        /// <summary>The answer speeds OpenAI offers: its Ultrafast tier runs Sol several times faster
        /// at several times the price (Oct 2026). Sent as <c>service_tier</c> with the answers only.</summary>
        public const string Standard = "standard";
        public const string Ultrafast = "ultrafast";

        public const string OpenRouterGlobal = "https://openrouter.ai/api/v1";
        public const string OpenRouterEu = "https://eu.openrouter.ai/api/v1";
        public const string TypeSafeJudge = "https://api.typesafe.ai/v1/systemone";
        public const string OpenAiDecisionsEndpoint = "https://api.openai.com/v1/decisions";
        public const string OpenAiDefault = "https://api.openai.com/v1";
        public const string OpenAiEu = "https://eu.api.openai.com/v1";
        public const string AnthropicModels = "https://api.anthropic.com/v1/models";
        public const string AnthropicMessages = "https://api.anthropic.com/v1/messages";

        /// <summary>The name a privacy line uses.</summary>
        public static string Label(string provider) => provider switch
        {
            OpenAi => "OpenAI",
            Compatible => "the OpenAI-compatible endpoint",
            Anthropic => "Anthropic",
            TypeSafe => "TypeSafe",
            OpenAiDecisions => "OpenAI",
            _ => "OpenRouter",
        };
    }

    /// <summary>What the console holds about the providers, beside the key: read through
    /// <see cref="ISearchOrAskSwitch"/>, so a test can stand in for the settings file.</summary>
    /// <remarks><see cref="Keys"/> holds one key per provider (<see cref="SearchOrAskProviders.KeyHolders"/>).
    /// <see cref="ApiKey"/> and <see cref="JudgeApiKey"/> are the two keys a console saved before
    /// 26 Oct 2026, read as the keys of the providers they were saved for.</remarks>
    public sealed record SearchOrAskProviderSettings(string? Provider = null, string? ApiKey = null, string? BaseUrl = null, bool EuRouting = false,
        string? JudgeProvider = null, string? JudgeApiKey = null, string? AnswerModel = null, string? FastAnswerModel = null, string? FilterModel = null,
        IReadOnlyDictionary<string, string>? Keys = null, string? AnswerSpeed = null)
    {
        /// <summary>The console's keys by provider, the two older single keys among them.</summary>
        public Dictionary<string, string> AllKeys()
        {
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (holder, key) in Keys ?? new Dictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(key)) keys[holder] = key.Trim();
            if (!string.IsNullOrWhiteSpace(ApiKey))
                keys.TryAdd(SearchOrAskProviders.Normalize(Provider, BaseUrl), ApiKey.Trim());
            if (!string.IsNullOrWhiteSpace(JudgeApiKey))
                keys.TryAdd(SearchOrAskProviders.JudgeKeyHolder((JudgeProvider ?? SearchOrAskProviders.OpenRouter).ToLowerInvariant()), JudgeApiKey.Trim());
            return keys;
        }
    }

    /// <summary>
    /// Where each call goes and with which key, resolved from the configuration (which wins) and
    /// the console. One object per resolution, so a request reads one consistent setup.
    /// </summary>
    public sealed record SearchOrAskSetup(
        string Provider, string? AnswerKey, string AnswerBaseUrl,
        string JudgeProvider, string? JudgeKey, string JudgeEndpoint, string JudgeModel,
        string AnswerModel, string FastAnswerModel, string FilterModel,
        bool EuRouting, IReadOnlyDictionary<string, string>? Keys = null, string? AnswerSpeed = null)
    {
        /// <summary>A provider's key, wherever it came from, or null.</summary>
        public string? KeyFor(string provider) => Keys != null && Keys.TryGetValue(provider, out var k) ? k : null;

        /// <summary>The answers go to OpenAI itself, whose newer models take <c>max_completion_tokens</c>
        /// and no fixed temperature, where every compatible endpoint still reads <c>max_tokens</c>.</summary>
        public bool OpenAiItself => Provider == SearchOrAskProviders.OpenAi;

        /// <summary>Answers and filters can run: the answer provider has a key.</summary>
        public bool AnswersAvailable => !string.IsNullOrWhiteSpace(AnswerKey);
        /// <summary>The judge can run: Jev's door has a key.</summary>
        public bool JudgeAvailable => !string.IsNullOrWhiteSpace(JudgeKey);
        /// <summary>Whether the answer provider reports a cost per call (OpenRouter does). Nothing
        /// else does, and nothing is estimated: a price table drifted from the list prices by as
        /// much as ten times (22 Oct 2026), and a cost that may be wrong is worse than none.</summary>
        public bool ReportsCost => Provider == SearchOrAskProviders.OpenRouter;
        /// <summary>Whether the judge's door reports a cost per call: OpenRouter's does, TypeSafe's
        /// own and OpenAI's Decisions API report only tokens.</summary>
        public bool JudgeReportsCost => JudgeProvider == SearchOrAskProviders.OpenRouter;
        /// <summary>Every call reports what it cost, so the statistics can show a total.</summary>
        public bool CostsReported => ReportsCost && JudgeReportsCost;
        /// <summary>The providers that report no cost per call, by name, for the sentence that says why
        /// no cost is shown.</summary>
        public IReadOnlyList<string> UnpricedProviders =>
            new[] { ReportsCost ? null : SearchOrAskProviders.Label(Provider), JudgeReportsCost ? null : SearchOrAskProviders.Label(JudgeProvider) }
                .Where(n => n != null).Select(n => n!).Distinct().ToList();
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

        /// <summary>The keys the configuration holds, by provider: <c>Keys:OpenRouter</c> and the rest,
        /// and the older single <c>ApiKey</c> and <c>JudgeApiKey</c> for the providers they serve.
        /// Each wins over the console's key for the same provider, and only that one.</summary>
        public static Dictionary<string, string> ConfiguredKeys(SearchOrAskOptions o)
        {
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (holder, key) in o.Keys ?? new Dictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(key)) keys[holder.ToLowerInvariant()] = key.Trim();
            if (!string.IsNullOrWhiteSpace(o.ApiKey))
                keys[SearchOrAskProviders.Normalize(o.Provider, o.BaseUrl)] = o.ApiKey.Trim();
            if (!string.IsNullOrWhiteSpace(o.JudgeApiKey))
                keys[SearchOrAskProviders.JudgeKeyHolder((o.JudgeProvider ?? SearchOrAskProviders.OpenRouter).Trim().ToLowerInvariant())] = o.JudgeApiKey.Trim();
            return keys;
        }

        /// <summary>Resolves the setup: the configuration's values win over the console's, a role's
        /// key is its provider's, and a provider's model names default per provider when neither
        /// names them.</summary>
        public static SearchOrAskSetup Resolve(SearchOrAskOptions o, SearchOrAskProviderSettings? c)
        {
            c ??= new SearchOrAskProviderSettings();
            string Pick(string? config, string? console, string fallback) =>
                !string.IsNullOrWhiteSpace(config) ? config.Trim() : !string.IsNullOrWhiteSpace(console) ? console.Trim() : fallback;

            var baseUrl = Pick(o.BaseUrl, c.BaseUrl, "");
            var provider = SearchOrAskProviders.Normalize(Pick(o.Provider, c.Provider, SearchOrAskProviders.OpenRouter), baseUrl);
            if (!SearchOrAskProviders.Answer.Contains(provider)) provider = SearchOrAskProviders.OpenRouter;
            var judgeProvider = Pick(o.JudgeProvider, c.JudgeProvider, SearchOrAskProviders.OpenRouter).ToLowerInvariant();
            if (!SearchOrAskProviders.Judge.Contains(judgeProvider)) judgeProvider = SearchOrAskProviders.OpenRouter;
            var eu = o.EuRouting ?? c.EuRouting;
            var openRouter = eu ? SearchOrAskProviders.OpenRouterEu : SearchOrAskProviders.OpenRouterGlobal;
            var openAi = eu ? SearchOrAskProviders.OpenAiEu : SearchOrAskProviders.OpenAiDefault;

            var keys = c.AllKeys();
            foreach (var (holder, key) in ConfiguredKeys(o)) keys[holder] = key;
            var answerKey = keys.GetValueOrDefault(provider);
            var judgeKey = keys.GetValueOrDefault(SearchOrAskProviders.JudgeKeyHolder(judgeProvider));

            var answerBase = provider switch
            {
                SearchOrAskProviders.OpenRouter => openRouter,
                SearchOrAskProviders.Anthropic => SearchOrAskProviders.AnthropicMessages,
                SearchOrAskProviders.OpenAi => openAi,
                _ => (baseUrl.Length > 0 ? baseUrl : SearchOrAskProviders.OpenAiDefault).TrimEnd('/'),
            };
            // The judge is never EU-routed on OpenRouter: its EU endpoint serves no Jev (71 models,
            // none of TypeSafe's, 19 Oct 2026), and sending it there answered 403 and stopped
            // Search or ask whole. The answers and the filters keep to the EU; the admin page says
            // where the decisions go. OpenAI's Decisions API is processed in the EEA under EU data
            // residency, so it follows the switch.
            var judgeEndpoint = judgeProvider switch
            {
                SearchOrAskProviders.TypeSafe => SearchOrAskProviders.TypeSafeJudge,
                SearchOrAskProviders.OpenAiDecisions => openAi + "/decisions",
                _ => SearchOrAskProviders.OpenRouterGlobal + "/systemone",
            };
            var judgeModel = judgeProvider switch
            {
                SearchOrAskProviders.TypeSafe => o.TypeSafeJudgeModel,
                SearchOrAskProviders.OpenAiDecisions => o.DecisionsModel,
                _ => o.JudgeModel,
            };
            var speed = provider == SearchOrAskProviders.OpenAi
                && Pick(o.AnswerSpeed, c.AnswerSpeed, SearchOrAskProviders.Standard).ToLowerInvariant() == SearchOrAskProviders.Ultrafast
                ? SearchOrAskProviders.Ultrafast : null;

            var (answer, fast, filter) = ModelDefaults.For(provider);
            return new SearchOrAskSetup(provider, answerKey, answerBase, judgeProvider, judgeKey, judgeEndpoint, judgeModel,
                Pick(provider == SearchOrAskProviders.OpenRouter ? o.AnswerModel : null, c.AnswerModel, answer),
                Pick(provider == SearchOrAskProviders.OpenRouter ? o.FastAnswerModel : null, c.FastAnswerModel, fast),
                Pick(provider == SearchOrAskProviders.OpenRouter ? o.FilterModel : null, c.FilterModel, filter),
                eu, keys, speed);
        }
    }

    /// <summary>The models each answer provider is called with when nobody names them.</summary>
    public static class ModelDefaults
    {
        /// <summary>The model ids a provider is called with when nobody names them.</summary>
        public static (string Answer, string Fast, string Filter) For(string provider) => provider switch
        {
            SearchOrAskProviders.Anthropic => ("claude-sonnet-5-5", "claude-haiku-4-5", "claude-haiku-4-5"),
            // Sol writes the answers, Luna the fast ones and the filters (26 Oct 2026: Sol is what the
            // first OpenAI customer uses; Luna is OpenAI's fast, cost-efficient tier).
            SearchOrAskProviders.OpenAi => ("gpt-6.1-sol", "gpt-6-luna", "gpt-6-luna"),
            SearchOrAskProviders.Compatible => ("", "", ""),
            _ => ("anthropic/claude-sonnet-5.5", "anthropic/claude-haiku-4.5", "anthropic/claude-haiku-4.5"),
        };
    }
}
