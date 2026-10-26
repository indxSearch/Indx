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
    /// Only OpenRouter reports what a call cost; on any other provider the cost is left unknown
    /// (null), never estimated, and the Statistics tab says so (<see cref="SearchOrAskSetup.CostsReported"/>).
    /// </summary>
    public sealed class SearchOrAskModelClients(IHttpClientFactory http, SearchOrAskSetupProvider setup)
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
            if (s.JudgeProvider == SearchOrAskProviders.OpenAiDecisions) return await DecisionsAsync(s, state, questions, ct);
            var body = new JsonObject
            {
                ["model"] = s.JudgeModel,
                ["state"] = state.DeepClone(),
                ["questions"] = new JsonObject(questions.Select(q =>
                    new KeyValuePair<string, JsonNode?>(q.Key, q.Value.DeepClone()))),
            };
            using var response = await Client(s.JudgeKey).PostAsync(s.JudgeEndpoint,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) await FailAsync(response, "Judge", ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseJudgement(json, questions.Keys);
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

        /// <summary>OpenAI's Decisions API as the judge (JevProbe/Decisions.cs is the measured
        /// client): the state goes as text, each question as a predicate with the recipe's two
        /// criteria written into its instruction, the answers come back as an array by name.</summary>
        private async Task<JudgeAnswer> DecisionsAsync(SearchOrAskSetup s, JsonObject state, IReadOnlyDictionary<string, JsonObject> questions, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = s.JudgeModel,
                ["input"] = state.ToJsonString(),
                ["questions"] = new JsonArray(questions.Select(q => (JsonNode)DecisionsPredicate(q.Key, q.Value)).ToArray()),
            };
            using var response = await Client(s.JudgeKey).PostAsync(s.JudgeEndpoint,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) await FailAsync(response, "Judge", ct);
            return ParseDecisions(await response.Content.ReadAsStringAsync(ct), questions.Keys);
        }

        /// <summary>A recipe question (Jev's noul: instructions and two criteria) as a Decisions
        /// predicate, the criteria written into the one instruction in the same words.</summary>
        public static JsonObject DecisionsPredicate(string name, JsonObject noul) => new()
        {
            ["name"] = name,
            ["type"] = "predicate",
            ["instructions"] = $"{noul["instructions"]?.GetValue<string>()}\n\nTrue when: {noul["criteria"]?["true"]?.GetValue<string>()}\n\nFalse when: {noul["criteria"]?["false"]?.GetValue<string>()}",
        };

        /// <summary>The Decisions API's answer: the probabilities, and no cost, since it reports only tokens.</summary>
        public static JudgeAnswer ParseDecisions(string json, IEnumerable<string> questions)
        {
            var parsed = JsonNode.Parse(json);
            var probabilities = new Dictionary<string, double>();
            if (parsed?["answers"] is JsonArray answers)
                foreach (var q in questions)
                    foreach (var answer in answers)
                        if (answer?["name"]?.GetValue<string>() == q && answer["probability"] is JsonValue v && v.TryGetValue<double>(out var p))
                        {
                            probabilities[q] = p;
                            break;
                        }
            return new JudgeAnswer(probabilities, null);
        }

        // ── The answers: dispatch by provider ────────────────────────────────

        public Task<(JsonNode Message, double? Cost)> ChatAsync(string model, JsonArray messages, JsonArray tools, CancellationToken ct)
        {
            var s = Answering();
            return s.Provider switch
            {
                SearchOrAskProviders.Anthropic => AnthropicChatAsync(s, model, messages, tools, ct),
                SearchOrAskProviders.OpenAi => ResponsesChatAsync(s, model, messages, tools, ct),
                _ => OpenAiChatAsync(s, model, messages, tools, ct),
            };
        }

        public Task<(string Content, double? Cost)> CompleteAsync(string model, string system, string user, CancellationToken ct)
        {
            var s = Answering();
            return s.Provider switch
            {
                SearchOrAskProviders.Anthropic => AnthropicCompleteAsync(s, model, system, user, ct),
                SearchOrAskProviders.OpenAi => ResponsesCompleteAsync(s, model, system, user, ct),
                _ => OpenAiCompleteAsync(s, model, system, user, ct),
            };
        }

        public IAsyncEnumerable<string> AnswerAsync(string model, string system, string user, IReadOnlyList<AskTurn> history, AnswerUsage usage, CancellationToken ct)
        {
            var s = Answering();
            return s.Provider switch
            {
                SearchOrAskProviders.Anthropic => AnthropicAnswerAsync(s, model, system, user, history, usage, ct),
                SearchOrAskProviders.OpenAi => ResponsesAnswerAsync(s, model, system, user, history, ct),
                _ => OpenAiAnswerAsync(s, model, system, user, history, usage, ct),
            };
        }

        private SearchOrAskSetup Answering()
        {
            var s = setup.Current;
            if (!s.AnswersAvailable) throw new SearchOrAskModelException(409, "No key for the answer model");
            return s;
        }

        /// <summary>The call's cost as the provider reported it (OpenRouter's <c>usage.cost</c>), or null.</summary>
        private static double? Cost(SearchOrAskSetup s, string model, JsonNode? usage) =>
            usage?["cost"] is JsonValue c && c.TryGetValue<double>(out var usd) ? usd : null;

        /// <summary>A failed call, with the provider's own reason in the message: a 403 from
        /// OpenRouter is a spent key budget, a guardrail or a moderation flag, and only the body
        /// says which (19 Oct 2026: the log said "403" and the instance could not say why).</summary>
        private static async Task FailAsync(HttpResponseMessage response, string what, CancellationToken ct)
        {
            string reason;
            try { reason = Reason(await response.Content.ReadAsStringAsync(ct)); }
            catch (Exception) { reason = ""; }
            throw new SearchOrAskModelException((int)response.StatusCode, $"{what} answered {(int)response.StatusCode}{(reason.Length > 0 ? ": " + reason : "")}");
        }

        /// <summary>The provider's message out of an error body (OpenAI's and OpenRouter's
        /// <c>error.message</c>, Anthropic's too), else the body itself; short, since it is logged.</summary>
        public static string Reason(string body)
        {
            var text = body.Trim();
            try
            {
                if (JsonNode.Parse(text)?["error"] is { } error)
                {
                    var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
                    var metadata = error["metadata"];
                    if (metadata != null) message += " " + metadata.ToJsonString();
                    text = message;
                }
            }
            catch (Exception) { }
            text = text.Replace('\n', ' ').Replace('\r', ' ');
            return text.Length > 300 ? text[..300] + "…" : text;
        }

        // ── OpenAI's chat completions: OpenRouter and every compatible endpoint ──

        private string ChatCompletions(SearchOrAskSetup s) => s.AnswerBaseUrl + "/chat/completions";



        private async Task<(JsonNode Message, double? Cost)> OpenAiChatAsync(SearchOrAskSetup s, string model, JsonArray messages, JsonArray tools, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
            };
            body["max_tokens"] = 1500;
            if (s.ReportsCost) body["usage"] = new JsonObject { ["include"] = true };
            var reply = await OpenAiPostAsync(s, body, ct);
            var message = FirstChoice(reply)?["message"] ?? throw new SearchOrAskModelException(502, "No message in the reply");
            return (message.DeepClone(), Cost(s, model, reply?["usage"]));
        }

        private async Task<(string Content, double? Cost)> OpenAiCompleteAsync(SearchOrAskSetup s, string model, string system, string user, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = user }),
            };
            body["max_tokens"] = 600;
            body["temperature"] = 0;
            if (s.ReportsCost) body["usage"] = new JsonObject { ["include"] = true };
            var reply = await OpenAiPostAsync(s, body, ct);
            var content = FirstChoice(reply)?["message"]?["content"]?.GetValue<string>() ?? "";
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
            if (!response.IsSuccessStatusCode) await FailAsync(response, "Model", ct);
            return JsonNode.Parse(text);
        }

        private async IAsyncEnumerable<string> OpenAiAnswerAsync(SearchOrAskSetup s, string model, string system, string user, IReadOnlyList<AskTurn> history,
            AnswerUsage usage, [EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
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
            body["max_tokens"] = 1500;
            if (s.ReportsCost) body["usage"] = new JsonObject { ["include"] = true };
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletions(s))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Client(s.AnswerKey).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) await FailAsync(response, "Answer model", ct);

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

        /// <summary>The text in a streamed chunk. OpenAI's last chunk, the one with the usage, has an
        /// empty <c>choices</c> list (OpenRouter's never does), so the first choice is looked for,
        /// not indexed: a JsonArray's indexer throws past its end.</summary>
        public static string? DeltaOf(string chunkJson)
        {
            try
            {
                return FirstChoice(JsonNode.Parse(chunkJson))?["delta"]?["content"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>A reply's first choice, or null when it has none.</summary>
        private static JsonNode? FirstChoice(JsonNode? reply) =>
            reply?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;

        // ── OpenAI's Responses API: OpenAI itself ────────────────────────────
        //
        // OpenAI keeps two things off chat completions for its reasoning models: function tools (the
        // catalogue agent: "use /v1/responses or set reasoning_effort to 'none'") and the Ultrafast
        // tier ("Invalid service_tier argument"), both found on 26 Oct 2026 with the first OpenAI
        // customer's models. So OpenAI is called here, and every other provider keeps chat
        // completions. Nothing is stored at OpenAI (store: false); the reasoning a tool call came
        // with travels back encrypted in the reply's own items, which the agent's message carries.

        /// <summary>The field on an agent message that holds the Responses API's output items, so the
        /// next turn can hand them back whole: reasoning, calls and text, in their order.</summary>
        public const string ResponsesItems = "responses_output";

        private string ResponsesUrl(SearchOrAskSetup s) => s.AnswerBaseUrl + "/responses";

        /// <summary>A request's common fields. Low reasoning effort: a visitor waits for the first
        /// words, and the records hold the facts. The cap covers the reasoning as well as the reply,
        /// so it is generous.</summary>
        public static JsonObject ResponsesBody(string model, string? instructions, JsonArray input, int maxTokens, string? speed)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["store"] = false,
                ["max_output_tokens"] = maxTokens,
                ["reasoning"] = new JsonObject { ["effort"] = "low" },
                ["input"] = input,
            };
            if (instructions != null) body["instructions"] = instructions;
            if (speed != null) body["service_tier"] = speed;
            return body;
        }

        private async Task<(JsonNode Message, double? Cost)> ResponsesChatAsync(SearchOrAskSetup s, string model, JsonArray messages, JsonArray tools, CancellationToken ct)
        {
            var (instructions, input) = ResponsesInput(messages);
            var body = ResponsesBody(model, instructions, input, 16000, null);
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = t!["function"]?["name"]?.GetValue<string>(),
                ["description"] = t["function"]?["description"]?.GetValue<string>(),
                ["parameters"] = t["function"]?["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
            }).ToArray());
            var reply = await ResponsesPostAsync(s, body, ct);
            return (MessageOfResponse(reply), null);
        }

        private async Task<(string Content, double? Cost)> ResponsesCompleteAsync(SearchOrAskSetup s, string model, string system, string user, CancellationToken ct)
        {
            var body = ResponsesBody(model, system, new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user }), 8000, null);
            var reply = await ResponsesPostAsync(s, body, ct);
            return (MessageOfResponse(reply)["content"]?.GetValue<string>() ?? "", null);
        }

        /// <summary>Posts a request; a model that takes no reasoning setting (an older one an admin
        /// chose) answers 400 naming it, and is asked once more without.</summary>
        private async Task<JsonNode?> ResponsesPostAsync(SearchOrAskSetup s, JsonObject body, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesUrl(s))
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                using var response = await Client(s.AnswerKey).SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) return JsonNode.Parse(text);
                if (attempt == 0 && RefusesReasoning((int)response.StatusCode, text) && body.Remove("reasoning")) continue;
                throw new SearchOrAskModelException((int)response.StatusCode, $"Model answered {(int)response.StatusCode}: {Reason(text)}");
            }
        }

        private static bool RefusesReasoning(int status, string body) =>
            status == 400 && Reason(body).Contains("reasoning", StringComparison.OrdinalIgnoreCase);

        private async IAsyncEnumerable<string> ResponsesAnswerAsync(SearchOrAskSetup s, string model, string system, string user, IReadOnlyList<AskTurn> history,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var input = new JsonArray([
                .. history.SelectMany(t => new JsonNode[]
                {
                    new JsonObject { ["role"] = "user", ["content"] = t.Question },
                    new JsonObject { ["role"] = "assistant", ["content"] = t.Answer },
                }),
                new JsonObject { ["role"] = "user", ["content"] = user }]);
            var body = ResponsesBody(model, system, input, 16000, s.AnswerSpeed);
            body["stream"] = true;

            HttpResponseMessage? response = null;
            for (var attempt = 0; ; attempt++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ResponsesUrl(s))
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                response = await Client(s.AnswerKey).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) break;
                var text = await response.Content.ReadAsStringAsync(ct);
                response.Dispose();
                if (attempt == 0 && RefusesReasoning((int)response.StatusCode, text) && body.Remove("reasoning")) continue;
                throw new SearchOrAskModelException((int)response.StatusCode, $"Answer model answered {(int)response.StatusCode}: {Reason(text)}");
            }

            // OpenAI reports tokens, not a cost: the answer's cost stays unknown.
            using (response)
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var (delta, done, error) = ResponsesEvent(line[5..].Trim());
                    if (error != null) throw new SearchOrAskModelException(502, $"Answer model failed: {error}");
                    if (delta is { Length: > 0 }) yield return delta;
                    if (done) break;
                }
            }
        }

        /// <summary>One streamed event: a text delta, whether the response ended, or why it failed.</summary>
        public static (string? Delta, bool Done, string? Error) ResponsesEvent(string json)
        {
            try
            {
                var e = JsonNode.Parse(json);
                return (e?["type"]?.GetValue<string>()) switch
                {
                    "response.output_text.delta" => (e?["delta"]?.GetValue<string>(), false, null),
                    "response.completed" or "response.incomplete" => (null, true, null),
                    "response.failed" => (null, true, e?["response"]?["error"]?["message"]?.GetValue<string>() ?? "the response failed"),
                    "error" => (null, true, e?["message"]?.GetValue<string>() ?? "error"),
                    _ => (null, false, null),
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return (null, false, null);
            }
        }

        /// <summary>The agent's OpenAI-chat-shaped conversation as the Responses API reads it: system
        /// messages become the instructions; an assistant turn that came from this API is handed back
        /// as its own output items (its encrypted reasoning among them, which a tool call needs), one
        /// written elsewhere as text and function calls; a tool result as a function call output.</summary>
        public static (string? Instructions, JsonArray Input) ResponsesInput(JsonArray messages)
        {
            var instructions = new List<string>();
            var input = new JsonArray();
            foreach (var m in messages)
            {
                var role = m?["role"]?.GetValue<string>();
                var content = m?["content"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
                switch (role)
                {
                    case "system":
                        if (content != null) instructions.Add(content);
                        break;
                    case "assistant" when m![ResponsesItems] is JsonArray items:
                        foreach (var item in items) input.Add(item?.DeepClone());
                        break;
                    case "assistant":
                        if (!string.IsNullOrEmpty(content)) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                        foreach (var call in m!["tool_calls"]?.AsArray() ?? [])
                            input.Add(new JsonObject
                            {
                                ["type"] = "function_call",
                                ["call_id"] = call?["id"]?.GetValue<string>(),
                                ["name"] = call?["function"]?["name"]?.GetValue<string>(),
                                ["arguments"] = call?["function"]?["arguments"]?.GetValue<string>() ?? "{}",
                            });
                        break;
                    case "tool":
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = m!["tool_call_id"]?.GetValue<string>(),
                            ["output"] = content ?? "",
                        });
                        break;
                    default:
                        input.Add(new JsonObject { ["role"] = "user", ["content"] = content ?? "" });
                        break;
                }
            }
            return (instructions.Count > 0 ? string.Join("\n\n", instructions) : null, input);
        }

        /// <summary>A Responses API reply as the OpenAI-chat-shaped message the agent reads: its text
        /// and its function calls, with the output items kept for the next turn. A reply that ran out
        /// of tokens before saying anything is a failure, not an empty answer.</summary>
        public static JsonObject MessageOfResponse(JsonNode? reply)
        {
            var output = reply?["output"] as JsonArray ?? [];
            var text = new StringBuilder();
            var calls = new JsonArray();
            foreach (var item in output)
            {
                switch (item?["type"]?.GetValue<string>())
                {
                    case "message":
                        foreach (var part in item["content"]?.AsArray() ?? [])
                            if (part?["type"]?.GetValue<string>() == "output_text") text.Append(part["text"]?.GetValue<string>());
                        break;
                    case "function_call":
                        calls.Add(new JsonObject
                        {
                            ["id"] = item["call_id"]?.GetValue<string>(),
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = item["name"]?.GetValue<string>(),
                                ["arguments"] = item["arguments"]?.GetValue<string>() ?? "{}",
                            },
                        });
                        break;
                }
            }
            if (text.Length == 0 && calls.Count == 0 && reply?["status"]?.GetValue<string>() == "incomplete")
                throw new SearchOrAskModelException(502, $"The model stopped before answering ({reply["incomplete_details"]?["reason"]?.GetValue<string>() ?? "incomplete"})");
            var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text.ToString() : null };
            if (calls.Count > 0) message["tool_calls"] = calls;
            message[ResponsesItems] = output.DeepClone();
            return message;
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
            if (!response.IsSuccessStatusCode) await FailAsync(response, "Model", ct);
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
            if (!response.IsSuccessStatusCode) await FailAsync(response, "Answer model", ct);

            // Anthropic reports tokens, not a cost: the answer's cost stays unknown.
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var (delta, _, _, done) = AnthropicEvent(line[5..].Trim());
                if (delta is { Length: > 0 }) yield return delta;
                if (done) break;
            }
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
