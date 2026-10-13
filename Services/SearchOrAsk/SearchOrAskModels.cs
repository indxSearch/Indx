using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>The model calls Search or ask makes. An interface so tests answer with canned
    /// bodies and no key; production is <see cref="SearchOrAskModelClients"/>.</summary>
    public interface ISearchOrAskModels
    {
        /// <summary>Asks the judge the named questions about one state.</summary>
        Task<JudgeAnswer> JudgeAsync(JsonObject state, IReadOnlyDictionary<string, JsonObject> questions, CancellationToken ct);

        /// <summary>One turn of a conversation with tools, in OpenAI's shape (messages with
        /// <c>tool_calls</c> and <c>tool</c> roles, tools as functions): the assistant's message
        /// (its content, or its tool_calls) and the turn's cost.</summary>
        Task<(JsonNode Message, double? Cost)> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct);

        /// <summary>One completion, not streamed: the reply's text and its cost.</summary>
        Task<(string Content, double? Cost)> CompleteAsync(string model, string system, string user, CancellationToken ct);

        /// <summary>Streams an answer from the named model as text deltas, after <paramref name="history"/>,
        /// the conversation so far, oldest first. The cost, reported or estimated, is left in
        /// <paramref name="usage"/> once the stream ends.</summary>
        IAsyncEnumerable<string> AnswerAsync(string model, string system, string user, IReadOnlyList<AskTurn> history,
            AnswerUsage usage, CancellationToken ct);
    }

    /// <summary>A judgement: the probability each question came back with (absent when the judge
    /// left one out), and what it cost when the provider says.</summary>
    public sealed record JudgeAnswer(IReadOnlyDictionary<string, double> Probabilities, double? Cost)
    {
        public double? this[string question] => Probabilities.TryGetValue(question, out var p) ? p : null;
    }

    /// <summary>What an answer cost, filled in by the model call as its stream ends.</summary>
    public sealed class AnswerUsage
    {
        public double? Cost { get; set; }
    }

    /// <summary>A model call that failed, with the provider's status, so the caller can say why
    /// without guessing (402 is out of credits, 429 is rate limited).</summary>
    public sealed class SearchOrAskModelException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>The resolved setup, read at most every five seconds (the judge asks on every typing
    /// pause, and the console's settings are a file). The configuration's values win over the
    /// console's. Shared by the service and the clients, so a request reads one setup.</summary>
    public sealed class SearchOrAskSetupProvider(SearchOrAskOptions options, ISearchOrAskSwitch? instance = null)
    {
        private (SearchOrAskSetup? Setup, long At) _cached;

        public SearchOrAskSetup Current
        {
            get
            {
                var now = Environment.TickCount64;
                var cached = _cached;
                if (cached.Setup != null && now - cached.At < 5000) return cached.Setup;
                var setup = SearchOrAskSetup.Resolve(options, instance?.Providers);
                _cached = (setup, now);
                return setup;
            }
        }

        /// <summary>The console saved: read again on the next call.</summary>
        public void Changed() => _cached = default;
    }

    /// <summary>
    /// The clients, one door per provider: Jev on OpenRouter's or TypeSafe's System One endpoint;
    /// the answers and the filters on OpenRouter, an OpenAI-compatible endpoint or Anthropic. Raw
    /// HTTP, as JevProbe does: the wire shapes are the contract, and nothing here needs an SDK.
    /// A provider that reports no cost per call gets an estimate from the price table.
    /// </summary>
    public sealed class SearchOrAskModelClients(IHttpClientFactory http, SearchOrAskOptions options, SearchOrAskSetupProvider setup)
        : ISearchOrAskModels
    {
        public const string HttpClientName = "SearchOrAsk";

        private HttpClient Client(string? key, bool anthropic = false)
        {
            var client = http.CreateClient(HttpClientName);
            if (anthropic)
            {
                client.DefaultRequestHeaders.Add("x-api-key", key);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }
            else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return client;
        }

        // ── The judge ────────────────────────────────────────────────────────

        public async Task<JudgeAnswer> JudgeAsync(JsonObject state, IReadOnlyDictionary<string, JsonObject> questions, CancellationToken ct)
        {
            var s = setup.Current;
            if (!s.JudgeAvailable) throw new SearchOrAskModelException(409, "No key for the judge");
            var body = new JsonObject
            {
                ["model"] = s.JudgeModel,
                ["state"] = state.DeepClone(),
                ["questions"] = new JsonObject(questions.Select(q =>
                    new KeyValuePair<string, JsonNode?>(q.Key, q.Value.DeepClone()))),
            };
            using var response = await Client(s.JudgeKey).PostAsync(s.JudgeEndpoint,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new SearchOrAskModelException((int)response.StatusCode, $"Judge answered {(int)response.StatusCode}");
            return ParseJudgement(text, questions.Keys);
        }

        /// <summary>Reads the probabilities out of a System One response. "noul" is the documented
        /// field; a probability under another name is still read rather than failed on.</summary>
        public static JudgeAnswer ParseJudgement(string json, IEnumerable<string> questions)
        {
            var parsed = JsonNode.Parse(json);
            var probabilities = new Dictionary<string, double>();
            foreach (var q in questions)
            {
                var answer = parsed?["answers"]?[q];
                foreach (var name in new[] { "noul", "bool", "probability" })
                    if (answer?[name] is JsonValue v && v.TryGetValue<double>(out var d))
                    {
                        probabilities[q] = d;
                        break;
                    }
            }
            double? cost = parsed?["usage"]?["cost"] is JsonValue c && c.TryGetValue<double>(out var usd) ? usd : null;
            return new JudgeAnswer(probabilities, cost);
        }

        // ── The answers: dispatch by provider ────────────────────────────────

        public Task<(JsonNode Message, double? Cost)> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct)
        {
            var s = Answering();
            return s.Provider == SearchOrAskProviders.Anthropic ? AnthropicChatAsync(s, model, messages, tools, ct) : OpenAiChatAsync(s, model, messages, tools, ct);
        }

        public Task<(string Content, double? Cost)> CompleteAsync(string model, string system, string user, CancellationToken ct)
        {
            var s = Answering();
            return s.Provider == SearchOrAskProviders.Anthropic ? AnthropicCompleteAsync(s, model, system, user, ct) : OpenAiCompleteAsync(s, model, system, user, ct);
        }

        public IAsyncEnumerable<string> AnswerAsync(string model, string system, string user, IReadOnlyList<AskTurn> history, AnswerUsage usage, CancellationToken ct)
        {
            var s = Answering();
            return s.Provider == SearchOrAskProviders.Anthropic ? AnthropicAnswerAsync(s, model, system, user, history, usage, ct) : OpenAiAnswerAsync(s, model, system, user, history, usage, ct);
        }

        private SearchOrAskSetup Answering()
        {
            var s = setup.Current;
            if (!s.AnswersAvailable) throw new SearchOrAskModelException(409, "No key for the answer model");
            return s;
        }

        /// <summary>The call's cost: reported by OpenRouter, else estimated from the tokens.</summary>
        private double? Cost(SearchOrAskSetup s, string model, JsonNode? usage)
        {
            if (usage == null) return null;
            if (usage["cost"] is JsonValue c && c.TryGetValue<double>(out var usd)) return usd;
            long In(string a, string b) => usage[a]?.GetValue<long>() ?? usage[b]?.GetValue<long>() ?? 0;
            var input = In("prompt_tokens", "input_tokens");
            var output = In("completion_tokens", "output_tokens");
            return input + output == 0 ? null : ModelPrices.Estimate(model, input, output, options.Prices);
        }

        private static void Throw(HttpResponseMessage response, string what) =>
            throw new SearchOrAskModelException((int)response.StatusCode, $"{what} answered {(int)response.StatusCode}");

        // ── OpenAI's chat completions: OpenRouter and every compatible endpoint ──

        private string ChatCompletions(SearchOrAskSetup s) => s.AnswerBaseUrl + "/chat/completions";

        private async Task<(JsonNode Message, double? Cost)> OpenAiChatAsync(SearchOrAskSetup s, string model, JsonArray messages, JsonArray tools, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 1500,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
            };
            if (s.ReportsCost) body["usage"] = new JsonObject { ["include"] = true };
            var reply = await OpenAiPostAsync(s, body, ct);
            var message = reply?["choices"]?[0]?["message"] ?? throw new SearchOrAskModelException(502, "No message in the reply");
            return (message.DeepClone(), Cost(s, model, reply?["usage"]));
        }

        private async Task<(string Content, double? Cost)> OpenAiCompleteAsync(SearchOrAskSetup s, string model, string system, string user, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 600,
                ["temperature"] = 0,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = user }),
            };
            if (s.ReportsCost) body["usage"] = new JsonObject { ["include"] = true };
            var reply = await OpenAiPostAsync(s, body, ct);
            var content = reply?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return (content, Cost(s, model, reply?["usage"]));
        }

        private async Task<JsonNode?> OpenAiPostAsync(SearchOrAskSetup s, JsonObject body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletions(s))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Client(s.AnswerKey).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) Throw(response, "Model");
            return JsonNode.Parse(text);
        }

        private async IAsyncEnumerable<string> OpenAiAnswerAsync(SearchOrAskSetup s, string model, string system, string user, IReadOnlyList<AskTurn> history,
            AnswerUsage usage, [EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["max_tokens"] = 1500,
                // The last chunk then carries the usage: OpenRouter's cost in USD, or the tokens an
                // estimate is made from.
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
                ["messages"] = new JsonArray([
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    .. history.SelectMany(t => new JsonNode[]
                    {
                        new JsonObject { ["role"] = "user", ["content"] = t.Question },
                        new JsonObject { ["role"] = "assistant", ["content"] = t.Answer },
                    }),
                    new JsonObject { ["role"] = "user", ["content"] = user }]),
            };
            if (s.ReportsCost) body["usage"] = new JsonObject { ["include"] = true };
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletions(s))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Client(s.AnswerKey).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) Throw(response, "Answer model");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                // Server-sent events: "data: {json}" lines, comments (": OPENROUTER PROCESSING")
                // while it waits, and "data: [DONE]" at the end.
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                if (UsageOf(data) is { } u && Cost(s, model, u) is { } cost) usage.Cost = cost;
                if (DeltaOf(data) is { Length: > 0 } delta) yield return delta;
            }
        }

        /// <summary>OpenRouter's cost in a streamed chunk, for the tests that pin the wire.</summary>
        public static double? CostOf(string chunkJson) => UsageOf(chunkJson)?["cost"]?.GetValue<double>();

        private static JsonNode? UsageOf(string chunkJson)
        {
            try { return JsonNode.Parse(chunkJson)?["usage"]; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
        }

        public static string? DeltaOf(string chunkJson)
        {
            try
            {
                return JsonNode.Parse(chunkJson)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ── Anthropic's messages API ─────────────────────────────────────────

        private async Task<(JsonNode Message, double? Cost)> AnthropicChatAsync(SearchOrAskSetup s, string model, JsonArray messages, JsonArray tools, CancellationToken ct)
        {
            var (system, converted) = AnthropicMessages(messages);
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 1500,
                ["messages"] = converted,
                ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t!["function"]?["name"]?.GetValue<string>(),
                    ["description"] = t["function"]?["description"]?.GetValue<string>(),
                    ["input_schema"] = t["function"]?["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                }).ToArray()),
            };
            if (system != null) body["system"] = system;
            var reply = await AnthropicPostAsync(s, body, ct);
            return (OpenAiMessageOf(reply), Cost(s, model, reply?["usage"]));
        }

        private async Task<(string Content, double? Cost)> AnthropicCompleteAsync(SearchOrAskSetup s, string model, string system, string user, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 600,
                ["temperature"] = 0,
                ["system"] = system,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user }),
            };
            var reply = await AnthropicPostAsync(s, body, ct);
            var content = string.Concat(reply?["content"]?.AsArray().Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]?.GetValue<string>()) ?? []);
            return (content, Cost(s, model, reply?["usage"]));
        }

        private async Task<JsonNode?> AnthropicPostAsync(SearchOrAskSetup s, JsonObject body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, s.AnswerBaseUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Client(s.AnswerKey, anthropic: true).SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) Throw(response, "Model");
            return JsonNode.Parse(text);
        }

        private async IAsyncEnumerable<string> AnthropicAnswerAsync(SearchOrAskSetup s, string model, string system, string user, IReadOnlyList<AskTurn> history,
            AnswerUsage usage, [EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 1500,
                ["stream"] = true,
                ["system"] = system,
                ["messages"] = new JsonArray([
                    .. history.SelectMany(t => new JsonNode[]
                    {
                        new JsonObject { ["role"] = "user", ["content"] = t.Question },
                        new JsonObject { ["role"] = "assistant", ["content"] = t.Answer },
                    }),
                    new JsonObject { ["role"] = "user", ["content"] = user }]),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, s.AnswerBaseUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Client(s.AnswerKey, anthropic: true).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) Throw(response, "Answer model");

            long input = 0, output = 0;
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var (delta, tokensIn, tokensOut, done) = AnthropicEvent(line[5..].Trim());
                if (tokensIn is { } i) input = i;
                if (tokensOut is { } o) output = o;
                if (delta is { Length: > 0 }) yield return delta;
                if (done) break;
            }
            usage.Cost = ModelPrices.Estimate(model, input, output, options.Prices);
        }

        /// <summary>One streamed event: a text delta, the input tokens (message_start), the output
        /// tokens (message_delta), and whether the message ended.</summary>
        public static (string? Delta, long? InputTokens, long? OutputTokens, bool Done) AnthropicEvent(string json)
        {
            try
            {
                var e = JsonNode.Parse(json);
                return (e?["type"]?.GetValue<string>()) switch
                {
                    "content_block_delta" => (e?["delta"]?["text"]?.GetValue<string>(), null, null, false),
                    "message_start" => (null, e?["message"]?["usage"]?["input_tokens"]?.GetValue<long>(), null, false),
                    "message_delta" => (null, null, e?["usage"]?["output_tokens"]?.GetValue<long>(), false),
                    "message_stop" => (null, null, null, true),
                    _ => (null, null, null, false),
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return (null, null, null, false);
            }
        }

        /// <summary>OpenAI-shaped messages as Anthropic reads them: the system message apart, an
        /// assistant's tool_calls as tool_use blocks, a tool role as a tool_result block.</summary>
        public static (string? System, JsonArray Messages) AnthropicMessages(JsonArray messages)
        {
            string? system = null;
            var result = new JsonArray();
            foreach (var m in messages)
            {
                var role = m?["role"]?.GetValue<string>();
                switch (role)
                {
                    case "system":
                        system = m?["content"]?.GetValue<string>();
                        break;
                    case "assistant":
                        var blocks = new JsonArray();
                        if (m?["content"]?.GetValue<string>() is { Length: > 0 } text) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                        foreach (var call in m?["tool_calls"]?.AsArray() ?? [])
                        {
                            JsonNode input;
                            try { input = JsonNode.Parse(call?["function"]?["arguments"]?.GetValue<string>() ?? "{}") ?? new JsonObject(); }
                            catch (JsonException) { input = new JsonObject(); }
                            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call?["id"]?.GetValue<string>(), ["name"] = call?["function"]?["name"]?.GetValue<string>(), ["input"] = input });
                        }
                        if (blocks.Count > 0) result.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                        break;
                    case "tool":
                        var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m?["tool_call_id"]?.GetValue<string>(), ["content"] = m?["content"]?.GetValue<string>() ?? "" };
                        // Several tool results in a row belong in one user message.
                        if (result.Count > 0 && result[^1]?["role"]?.GetValue<string>() == "user" && result[^1]?["content"] is JsonArray previous && previous.Count > 0 && previous[0]?["type"]?.GetValue<string>() == "tool_result")
                            previous.Add(block);
                        else result.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block) });
                        break;
                    default:
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = m?["content"]?.GetValue<string>() ?? "" });
                        break;
                }
            }
            return (system, result);
        }

        /// <summary>Anthropic's reply as the OpenAI-shaped message the agent reads: content and tool_calls.</summary>
        public static JsonNode OpenAiMessageOf(JsonNode? reply)
        {
            var text = new StringBuilder();
            var calls = new JsonArray();
            foreach (var block in reply?["content"]?.AsArray() ?? [])
            {
                switch (block?["type"]?.GetValue<string>())
                {
                    case "text": text.Append(block["text"]?.GetValue<string>()); break;
                    case "tool_use":
                        calls.Add(new JsonObject
                        {
                            ["id"] = block["id"]?.GetValue<string>(),
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = block["name"]?.GetValue<string>(), ["arguments"] = block["input"]?.ToJsonString() ?? "{}" },
                        });
                        break;
                }
            }
            var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text.ToString() : null };
            if (calls.Count > 0) message["tool_calls"] = calls;
            return message;
        }
    }
}
