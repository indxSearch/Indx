using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IndxServer.Services.Webhooks
{
    /// <summary>
    /// Where a Sanity webhook's documents come from: the project, the dataset and the GROQ query that
    /// selects and shapes them (the same filter and projection as the webhook in Sanity's settings).
    /// With these Indx fetches the full set itself: once when the webhook is made, then every
    /// <see cref="SyncEveryHours"/>, which also catches the changes a webhook can miss (Sanity
    /// retries a failed delivery twice, then gives up). A token is needed only for a private dataset.
    /// </summary>
    public sealed partial record SanitySource(string ProjectId, string Dataset, string Query, string? Token = null,
        string ApiVersion = SanitySource.DefaultApiVersion, int? SyncEveryHours = 24)
    {
        /// <summary>The first API version whose default perspective is "published": no drafts.</summary>
        public const string DefaultApiVersion = "v2025-02-19";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        };

        public static SanitySource? From(JsonObject? config)
        {
            if (config == null) return null;
            try { return config.Deserialize<SanitySource>(Json); }
            catch (JsonException) { return null; }
        }

        public JsonObject ToJson() => (JsonObject)JsonSerializer.SerializeToNode(this, Json)!;

        public string Serialize() => JsonSerializer.Serialize(this, Json);

        public static SanitySource? Parse(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<SanitySource>(json, Json); }
            catch (JsonException) { return null; }
        }

        /// <summary>Null when it will do, else what is wrong. The project id and dataset become part
        /// of a host name and a path, so they are held to Sanity's own alphabet: nothing else can
        /// steer the request to another server.</summary>
        public string? Problem()
        {
            if (!ProjectIdShape().IsMatch(ProjectId ?? "")) return "The Sanity project id takes lowercase letters and digits only.";
            if (!DatasetShape().IsMatch(Dataset ?? "")) return "The Sanity dataset takes lowercase letters, digits, - and _ only.";
            if (string.IsNullOrWhiteSpace(Query) || Query.Length > 10_000) return "The GROQ query is needed, at most 10,000 characters, such as *[_type == \"product\"]{_id, title, price}.";
            if (!ApiVersionShape().IsMatch(ApiVersion ?? "")) return "The API version reads like v2025-02-19.";
            if (SyncEveryHours is < 1 or > 24 * 7) return "Fetch the full set every: 1 to 168 hours, or empty for never.";
            return null;
        }

        [GeneratedRegex("^[a-z0-9]{1,64}$")] private static partial Regex ProjectIdShape();
        [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")] private static partial Regex DatasetShape();
        [GeneratedRegex("^v\\d{4}-\\d{2}-\\d{2}$|^v1$|^vX$")] private static partial Regex ApiVersionShape();

        /// <summary>The ids Sanity gives drafts and release versions: not published, not indexed.</summary>
        public static bool IsUnpublished(string id) => id.StartsWith("drafts.", StringComparison.Ordinal) || id.StartsWith("versions.", StringComparison.Ordinal);
    }

    /// <summary>
    /// The signature Sanity puts on a webhook call when the webhook has a secret:
    /// <c>sanity-webhook-signature: t=&lt;milliseconds&gt;,v1=&lt;base64url HMAC-SHA256 of "t.body"&gt;</c>,
    /// as @sanity/webhook makes and checks it. Sanity's toolkit does not bound the time; Indx holds
    /// it to the same five minutes as its own scheme, which Sanity's deliveries (sent at once,
    /// retried within a minute) always meet.
    /// </summary>
    public static class SanitySignature
    {
        public const string Header = "sanity-webhook-signature";
        public const string OperationHeader = "sanity-operation";
        public const string DocumentIdHeader = "sanity-document-id";

        public static string Sign(string secret, string body, DateTimeOffset at)
        {
            var t = at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return $"t={t},v1={Hash(secret, t, body)}";
        }

        public static string? Check(string secret, string? header, string body, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(header)) return $"No {Header} header.";
            var m = Regex.Match(header.Trim(), "^t=(\\d+)[, ]+v1=([^, ]+)$");
            if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
                return $"{Header} must read t=<milliseconds>,v1=<signature>.";
            if ((now - DateTimeOffset.FromUnixTimeMilliseconds(ms)).Duration() > WebhookSignature.Tolerance)
                return "The signature's time is more than five minutes from now.";
            var expected = Encoding.ASCII.GetBytes(Hash(secret, m.Groups[1].Value, body));
            var given = Encoding.ASCII.GetBytes(m.Groups[2].Value);
            return CryptographicOperations.FixedTimeEquals(expected, given) ? null : "The signature does not match the body and the webhook's secret.";
        }

        private static string Hash(string secret, string t, string body)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{body}")))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        /// <summary>
        /// A Sanity delivery as the planner reads events: the projected document with its id (from
        /// the header, which every delivery carries, so a projection without _id still works) and
        /// the operation as <c>event</c>. Null for a draft or release version, which is not indexed.
        /// Throws <see cref="FormatException"/> when the body is not a JSON object.
        /// </summary>
        public static string? ToEvent(string body, string? operation, string? documentId, string idField)
        {
            var doc = (string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)) as JsonObject
                ?? throw new FormatException("A Sanity webhook's projection must give a JSON object, such as {_id, title, price}.");
            var id = !string.IsNullOrEmpty(documentId) ? documentId
                : doc[idField] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (id != null)
            {
                if (SanitySource.IsUnpublished(id)) return null;
                doc[idField] = id;
            }
            var op = operation?.Trim().ToLowerInvariant();
            doc[WebhookPlanner.EventField] = op == "delete" ? "delete" : "upsert";
            return doc.ToJsonString();
        }
    }

    /// <summary>Fetches a Sanity webhook's full set through Sanity's query API.</summary>
    public sealed class SanityClient(IHttpClientFactory http)
    {
        public const string HttpClientName = "sanity";

        /// <summary>Runs a query with params against the published documents; its result.</summary>
        public async Task<JsonNode?> QueryAsync(SanitySource source, string query, JsonObject parameters, CancellationToken ct = default)
        {
            var client = http.CreateClient(HttpClientName);
            var url = $"https://{source.ProjectId}.api.sanity.io/{source.ApiVersion}/data/query/{source.Dataset}?perspective=published";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(new JsonObject { ["query"] = query, ["params"] = parameters }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(source.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.Token);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException) { throw new InvalidOperationException($"Sanity answered {(int)response.StatusCode} with something other than JSON."); }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Sanity answered {(int)response.StatusCode}: {root?["error"]?["description"]?.ToString() ?? root?["message"]?.ToString() ?? "no reason given"}");
            return root?["result"];
        }

        /// <summary>The query's result as a JSON array of documents. Throws with Sanity's own
        /// message when the query fails, so the delivery log says what to fix.</summary>
        public async Task<string> FetchAllAsync(SanitySource source, CancellationToken ct = default)
        {
            var client = http.CreateClient(HttpClientName);
            // The perspective goes in the address: the POST body takes only the query and its
            // params, and Sanity refuses anything else in it ("unknown field").
            var url = $"https://{source.ProjectId}.api.sanity.io/{source.ApiVersion}/data/query/{source.Dataset}?perspective=published";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { query = source.Query }), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(source.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.Token);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException) { throw new InvalidOperationException($"Sanity answered {(int)response.StatusCode} with something other than JSON."); }
            if (!response.IsSuccessStatusCode)
            {
                var message = root?["error"]?["description"]?.ToString() ?? root?["message"]?.ToString() ?? root?["error"]?.ToString();
                throw new InvalidOperationException($"Sanity answered {(int)response.StatusCode}: {message ?? "no reason given"}");
            }
            return root?["result"] switch
            {
                JsonArray a => a.ToJsonString(),
                JsonObject o => new JsonArray(o.DeepClone()).ToJsonString(),
                _ => throw new InvalidOperationException("The query gave no documents: it should select a list, such as *[_type == \"product\"]{...}."),
            };
        }
    }

    /// <summary>
    /// Sanity as a source: its GROQ-powered webhooks call the address signed their own way, and Indx
    /// fetches the full set through the query API. No code on the customer's side.
    /// </summary>
    public sealed class SanityWebhookSource(SanityClient client) : IWebhookSource
    {
        public string Id => WebhookSources.Sanity;
        public string Label => "Sanity";
        public string Description => "A Sanity webhook calls it, and Indx fetches the full set from Sanity itself. No code needed.";
        public string DefaultIdField => "_id";
        public bool Pulls => true;

        public IReadOnlyList<SourceSetting> Settings { get; } =
        [
            new("projectId", "Sanity project id", Placeholder: "abc123de", Required: true),
            new("dataset", "Sanity dataset", Placeholder: "production", Required: true, Default: "production"),
            new("query", "GROQ query", Placeholder: "*[_type == \"product\"]{_id, title, price}", Required: true, Multiline: true,
                Hint: "Picks and shapes the documents. Use the same filter and projection in Sanity's webhook."),
            new("token", "Read token", Secret: true, Hint: "Only for a private dataset. Never shown again."),
            SourceSettings.SyncEveryHours,
        ];

        public (JsonObject? Config, string? Problem) Configure(JsonObject? settings)
        {
            if (settings == null) return (null, "A Sanity webhook needs settings: { projectId, dataset, query }.");
            var (hours, hoursProblem) = SourceSettings.ReadHours(settings);
            if (hoursProblem != null) return (null, hoursProblem);
            var source = new SanitySource(SourceSettings.Text(settings, "projectId") ?? "", SourceSettings.Text(settings, "dataset") ?? "production",
                SourceSettings.Text(settings, "query") ?? "", SourceSettings.Text(settings, "token"),
                SourceSettings.Text(settings, "apiVersion") ?? SanitySource.DefaultApiVersion, hours);
            return source.Problem() is { } problem ? (null, problem) : (source.ToJson(), null);
        }

        public string NewSecret() => WebhookStore.NewSecret();

        public string? CheckCall(SourceCall call, string secret, DateTimeOffset now) =>
            SanitySignature.Check(secret, call.Header(SanitySignature.Header), call.Body, now);

        public string? ToEvents(SourceCall call, Webhook hook) =>
            SanitySignature.ToEvent(call.Body, call.Header(SanitySignature.OperationHeader), call.Header(SanitySignature.DocumentIdHeader), hook.IdField);

        /// <summary>The most documents a change may bring along before a full sync is the better way.</summary>
        public const int MaxFollowing = 500;

        /// <summary>
        /// Reads the delivered documents fresh, through the webhook's own query, and adds the ones
        /// that refer to them: a renamed category reaches its products at once, when Sanity's
        /// webhook also covers categories. A delivered document the query does not cover (the
        /// category itself) is not indexed. When Sanity cannot be asked, the call goes as delivered.
        /// </summary>
        public async Task<SourceResolution> ResolveAsync(Webhook hook, string events, CancellationToken ct = default)
        {
            if (SanitySource.From(SourceSettings.Parse(hook.SourceConfig)) is not { } source) return new(events);
            List<JsonObject> delivered;
            try { delivered = WebhookPlanner.Events(JsonNode.Parse(events)); }
            catch (Exception ex) when (ex is JsonException or FormatException) { return new(events); }
            var ids = delivered.Select(e => e[hook.IdField]?.ToString()).OfType<string>().Where(i => i.Length > 0).Distinct().ToList();
            if (ids.Count == 0) return new(events);

            JsonNode? current;
            try
            {
                current = await client.QueryAsync(source, FollowQuery(source.Query, MaxFollowing), new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()) }, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return new(events, $"Sanity could not be asked for the documents as they are now ({ex.Message}); applied as delivered.");
            }
            return Follow(delivered, current as JsonObject, hook.IdField);
        }

        /// <summary>The query that reads the delivered ids through the webhook's query ("self"), says
        /// which of them exist at all ("exists"), and finds the documents of the query that refer to
        /// them ("following", with their count). Built into the query's own filter when it has the
        /// usual shape, so Sanity filters before it projects; wrapped around it otherwise.</summary>
        public static string FollowQuery(string query, int max)
        {
            var (filter, projection) = SplitQuery(query);
            string self, following, followingCount;
            if (filter != null)
            {
                self = $"*[({filter}) && _id in $ids]{projection}";
                following = $"*[({filter}) && references($ids) && !(_id in $ids)]";
                followingCount = $"count({following})";
                following = $"{following}[0...{max}]{projection}";
            }
            else
            {
                self = $"({query})[_id in $ids]";
                following = $"({query})[!(_id in $ids) && _id in *[references($ids)]._id]";
                followingCount = $"count({following})";
                following = $"{following}[0...{max}]";
            }
            return $"{{\"self\": {self}, \"exists\": *[_id in $ids]._id, \"following\": {following}, \"followingCount\": {followingCount}}}";
        }

        /// <summary>
        /// The events to plan, from what was delivered and what Sanity holds now. A delivered
        /// document the query covers is its current version. One that exists but the query does not
        /// cover (a category; a product moved out of the filter) is removed, which does nothing when
        /// it was never indexed. One that does not exist was deleted or unpublished, unless it came
        /// as a change, which Sanity may not show yet: that one goes as delivered. Then the
        /// documents that refer to them, or, when there are too many, a full sync instead.
        /// </summary>
        public static SourceResolution Follow(IReadOnlyList<JsonObject> delivered, JsonObject? current, string idField)
        {
            var self = (current?["self"] as JsonArray)?.OfType<JsonObject>()
                .Where(d => d[idField] != null).GroupBy(d => d[idField]!.ToString()).ToDictionary(g => g.Key, g => g.Last()) ?? [];
            var exists = (current?["exists"] as JsonArray)?.Select(n => n?.ToString()).OfType<string>().ToHashSet() ?? [];
            var result = new JsonArray();
            var seen = new HashSet<string>();
            foreach (var e in delivered)
            {
                var id = e[idField]?.ToString();
                if (string.IsNullOrEmpty(id)) { result.Add(e.DeepClone()); continue; }
                if (!seen.Add(id)) continue;
                if (self.TryGetValue(id, out var doc)) result.Add(Event(doc, "upsert"));
                else if (exists.Contains(id)) result.Add(new JsonObject { [idField] = id, [WebhookPlanner.EventField] = "delete" });
                else if (e[WebhookPlanner.EventField]?.ToString() == "delete") result.Add(new JsonObject { [idField] = id, [WebhookPlanner.EventField] = "delete" });
                else result.Add(e.DeepClone());
            }
            var count = current?["followingCount"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
            if (count > MaxFollowing)
                return new(result.ToJsonString(), $"{count:N0} documents refer to what changed; a full sync brings them.", NeedsFullSync: true);
            var following = (current?["following"] as JsonArray)?.OfType<JsonObject>().Where(d => d[idField] is { } i && seen.Add(i.ToString())).ToList() ?? [];
            foreach (var d in following) result.Add(Event(d, "upsert"));
            return new(result.ToJsonString(), following.Count > 0 ? $"{following.Count} that refer to it came along." : null);
        }

        private static JsonObject Event(JsonObject doc, string op)
        {
            var e = (JsonObject)doc.DeepClone();
            e[WebhookPlanner.EventField] = op;
            return e;
        }

        public bool LeavesOut(string id) => SanitySource.IsUnpublished(id);

        public Task<string> FetchAllAsync(JsonObject config, CancellationToken ct = default) =>
            client.FetchAllAsync(SanitySource.From(config) ?? throw new InvalidOperationException("The Sanity settings are unreadable."), ct);

        public SetupGuide Setup(JsonObject? config)
        {
            var (filter, projection) = SplitQuery(SanitySource.From(config)?.Query ?? "");
            return new("In Sanity, under API and Webhooks, add a webhook with these settings. Indx is fetching the full set now.",
            [
                new("URL", "the address above"),
                new("Trigger on", "Create, Update, Delete"),
                new("Filter", filter ?? "the filter from your query", Code: filter != null),
                new("Wider filter", "If your query follows references with ->, add the types it follows, such as " +
                    "_type in [\"product\", \"category\"]: a renamed category then reaches its products at once."),
                new("Projection", projection ?? "the projection from your query", Code: projection != null),
                new("HTTP method", "POST"),
                new("Secret", "the secret above"),
            ]);
        }

        /// <summary>A query of the usual shape, <c>*[filter]{projection}</c>, split into the two
        /// settings Sanity's webhook form asks for; nulls when it has another shape.</summary>
        public static (string? Filter, string? Projection) SplitQuery(string query)
        {
            var m = Regex.Match(query.Trim(), @"^\*\s*\[(?<f>.*)\]\s*(?<p>\{.*\})$", RegexOptions.Singleline);
            return m.Success ? (m.Groups["f"].Value.Trim(), m.Groups["p"].Value.Trim()) : (null, null);
        }
    }
}
