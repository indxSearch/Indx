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

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
            if (!ProjectIdShape().IsMatch(ProjectId ?? "")) return "sanity.projectId is the project's id: lowercase letters and digits.";
            if (!DatasetShape().IsMatch(Dataset ?? "")) return "sanity.dataset is the dataset's name: lowercase letters, digits, - and _.";
            if (string.IsNullOrWhiteSpace(Query) || Query.Length > 10_000) return "sanity.query is a GROQ query of at most 10,000 characters, such as *[_type == \"product\"]{_id, title, price}.";
            if (!ApiVersionShape().IsMatch(ApiVersion ?? "")) return "sanity.apiVersion reads like v2025-02-19.";
            if (SyncEveryHours is < 1 or > 24 * 7) return "sanity.syncEveryHours is between 1 and 168, or null for never.";
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

        /// <summary>The query's result as a JSON array of documents. Throws with Sanity's own
        /// message when the query fails, so the delivery log says what to fix.</summary>
        public async Task<string> FetchAllAsync(SanitySource source, CancellationToken ct = default)
        {
            var client = http.CreateClient(HttpClientName);
            var url = $"https://{source.ProjectId}.api.sanity.io/{source.ApiVersion}/data/query/{source.Dataset}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { query = source.Query, perspective = "published" }), Encoding.UTF8, "application/json"),
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
}
