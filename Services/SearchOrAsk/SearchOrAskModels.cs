using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>The two model calls Search or ask makes. An interface so tests answer with canned
    /// bodies and no key; production is <see cref="OpenRouterModels"/>.</summary>
    public interface ISearchOrAskModels
    {
        /// <summary>Asks the judge the named questions about one state.</summary>
        Task<JudgeAnswer> JudgeAsync(JsonObject state, IReadOnlyDictionary<string, JsonObject> questions, CancellationToken ct);

        /// <summary>Streams an answer from the named model as text deltas.</summary>
        IAsyncEnumerable<string> AnswerAsync(string model, string system, string user, CancellationToken ct);
    }

    /// <summary>A judgement: the probability each question came back with (absent when the judge
    /// left one out), and what it cost when the provider says.</summary>
    public sealed record JudgeAnswer(IReadOnlyDictionary<string, double> Probabilities, double? Cost)
    {
        public double? this[string question] => Probabilities.TryGetValue(question, out var p) ? p : null;
    }

    /// <summary>A model call that failed, with the provider's status, so the caller can say why
    /// without guessing (402 is out of credits, 429 is rate limited).</summary>
    public sealed class SearchOrAskModelException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>
    /// Jev through OpenRouter's System One endpoint, and the answer model through its chat
    /// completions. Raw HTTP, as JevProbe does: there is no .NET SDK for System One, and the wire
    /// shape is the contract (JevProbe's tests pin it against the documented bodies).
    /// </summary>
    public sealed class OpenRouterModels(IHttpClientFactory http, SearchOrAskOptions options) : ISearchOrAskModels
    {
        public const string HttpClientName = "SearchOrAsk";

        private HttpClient Client()
        {
            var client = http.CreateClient(HttpClientName);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            return client;
        }

        public async Task<JudgeAnswer> JudgeAsync(JsonObject state, IReadOnlyDictionary<string, JsonObject> questions, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = options.JudgeModel,
                ["state"] = state.DeepClone(),
                ["questions"] = new JsonObject(questions.Select(q =>
                    new KeyValuePair<string, JsonNode?>(q.Key, q.Value.DeepClone()))),
            };
            using var response = await Client().PostAsync(options.Endpoint,
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

        public async IAsyncEnumerable<string> AnswerAsync(string model, string system, string user, [EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["max_tokens"] = 1500,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = user }),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, options.AnswerEndpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new SearchOrAskModelException((int)response.StatusCode, $"Answer model answered {(int)response.StatusCode}");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                // Server-sent events: "data: {json}" lines, comments (": OPENROUTER PROCESSING")
                // while it waits, and "data: [DONE]" at the end.
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                if (DeltaOf(data) is { Length: > 0 } delta) yield return delta;
            }
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
    }
}
