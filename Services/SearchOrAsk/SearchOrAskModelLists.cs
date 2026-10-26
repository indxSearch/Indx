using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>One model a provider offers, for the console's picker: its id, its display name when
    /// the provider gives one, and its list price when the provider says (only OpenRouter does).</summary>
    public sealed record ModelOption(string Id, string? Name = null, string? Price = null);

    /// <summary>
    /// The models a provider offers, read from the provider itself so the console can offer them
    /// without a list of ours to keep up: OpenRouter's catalogue is public and priced (its EU
    /// endpoint's is the shorter list of what it serves there); OpenAI, Anthropic and a compatible
    /// endpoint answer <c>GET /models</c> with the saved key, names only. Kept ten minutes per list.
    /// A provider that cannot list returns an empty list with the reason, and the model fields stay
    /// free text either way.
    /// </summary>
    public sealed class SearchOrAskModelLists(IHttpClientFactory http, SearchOrAskSetupProvider setup)
    {
        private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
        private readonly Dictionary<string, (IReadOnlyList<ModelOption> Models, DateTime At)> _cache = [];
        private readonly Lock _lock = new();

        /// <summary>The provider's models, or none with why. <paramref name="eu"/> lists what the
        /// provider serves in the EU, where it says (OpenRouter).</summary>
        public async Task<(IReadOnlyList<ModelOption> Models, string? Problem)> ListAsync(string provider, bool eu, string? baseUrl, CancellationToken ct)
        {
            var s = setup.Current;
            var key = s.KeyFor(provider);
            var url = provider switch
            {
                SearchOrAskProviders.OpenRouter => (eu ? SearchOrAskProviders.OpenRouterEu : SearchOrAskProviders.OpenRouterGlobal) + "/models",
                SearchOrAskProviders.OpenAi => (eu ? SearchOrAskProviders.OpenAiEu : SearchOrAskProviders.OpenAiDefault) + "/models",
                SearchOrAskProviders.Anthropic => SearchOrAskProviders.AnthropicModels + "?limit=1000",
                _ => string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/') + "/models",
            };
            if (url == null) return ([], "Save a base URL first.");
            if (provider != SearchOrAskProviders.OpenRouter && string.IsNullOrWhiteSpace(key)) return ([], "Save a key first: the list is read with it.");

            var cacheKey = $"{provider}|{url}|{key?.GetHashCode()}";
            lock (_lock)
                if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < Keep) return (hit.Models, null);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (provider == SearchOrAskProviders.Anthropic)
                {
                    request.Headers.Add("x-api-key", key);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                }
                else if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await http.CreateClient(SearchOrAskModelClients.HttpClientName).SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return ([], $"The provider answered {(int)response.StatusCode}: {SearchOrAskModelClients.Reason(body)}");
                var models = Parse(body);
                lock (_lock) _cache[cacheKey] = (models, DateTime.UtcNow);
                return (models, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ([], "The provider did not answer in time."); }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { return ([], $"Could not read the list: {ex.Message}"); }
        }

        /// <summary>A <c>/models</c> answer as options: OpenAI's shape (<c>data[].id</c>), which
        /// OpenRouter and Anthropic share, with a name and OpenRouter's per-token prices where given.
        /// Batch variants are left out: the answers are never batched.</summary>
        public static IReadOnlyList<ModelOption> Parse(string json)
        {
            var list = new List<ModelOption>();
            if (JsonNode.Parse(json)?["data"] is not JsonArray data) return list;
            foreach (var m in data)
            {
                var id = m?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || id.EndsWith(":batch", StringComparison.Ordinal)) continue;
                var name = m!["name"]?.GetValue<string>() ?? m["display_name"]?.GetValue<string>();
                list.Add(new ModelOption(id, name, Price(m["pricing"])));
            }
            return list.OrderBy(o => o.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>OpenRouter's prices are USD per token, as strings: shown per million.</summary>
        private static string? Price(JsonNode? pricing)
        {
            static double? PerMillion(JsonNode? v) =>
                double.TryParse(v?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d * 1_000_000 : null;
            if (PerMillion(pricing?["prompt"]) is not { } input || PerMillion(pricing?["completion"]) is not { } output) return null;
            return string.Create(CultureInfo.InvariantCulture, $"${input:0.##} in, ${output:0.##} out per million tokens");
        }
    }
}
