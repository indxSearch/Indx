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
    /// Where a Contentful webhook's entries come from: the space, the environment, optionally one
    /// content type, the locale whose values are indexed, and a Content Delivery API token, with
    /// which Indx fetches the published entries itself. <see cref="Region"/> "eu" reads from
    /// Contentful's EU data residency host.
    /// </summary>
    public sealed partial record ContentfulSettings(string SpaceId, string Environment, string? ContentType, string Locale,
        string DeliveryToken, string Region = "us")
    {
        public const string DefaultEnvironment = "master";
        public const string DefaultLocale = "en-US";

        /// <summary>Contentful's hosts, and only these: the settings never name a host.</summary>
        public string Host => Region == "eu" ? "cdn.eu.contentful.com" : "cdn.contentful.com";

        public static ContentfulSettings? From(JsonObject? config) => config == null ? null : new(
            SourceSettings.Text(config, "spaceId") ?? "", SourceSettings.Text(config, "environment") ?? DefaultEnvironment,
            SourceSettings.Text(config, "contentType"), SourceSettings.Text(config, "locale") ?? DefaultLocale,
            SourceSettings.Text(config, "deliveryToken") ?? "", SourceSettings.Text(config, "region") ?? "us");

        /// <summary>Null when they will do, else what is wrong. Each value lands in a path or a query
        /// string, so each is held to Contentful's own alphabet.</summary>
        public string? Problem()
        {
            if (!SpaceIdShape().IsMatch(SpaceId)) return "The Contentful space id takes lowercase letters and digits only.";
            if (!EnvironmentShape().IsMatch(Environment)) return "The environment id takes letters, digits, -, _ and . only.";
            if (ContentType != null && !ContentTypeShape().IsMatch(ContentType)) return "The content type id takes letters, digits, - and _ only.";
            if (!LocaleShape().IsMatch(Locale)) return "The locale reads like en-US.";
            if (!TokenShape().IsMatch(DeliveryToken)) return "The Content Delivery API token is needed: it is how Indx reads the published entries.";
            if (Region is not ("us" or "eu")) return "The region is \"us\" or \"eu\".";
            return null;
        }

        [GeneratedRegex("^[a-z0-9]{1,64}$")] private static partial Regex SpaceIdShape();
        [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")] private static partial Regex EnvironmentShape();
        [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")] private static partial Regex ContentTypeShape();
        [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")] private static partial Regex LocaleShape();
        [GeneratedRegex("^[A-Za-z0-9_-]{10,256}$")] private static partial Regex TokenShape();
    }

    /// <summary>
    /// The signature Contentful puts on a webhook call when the space has request verification:
    /// <c>x-contentful-signature</c>, a hex HMAC-SHA256 with the space's signing secret over
    /// "method \n path \n signed headers \n body", the headers named in <c>x-contentful-signed-headers</c>
    /// as <c>name:value</c>, sorted, joined with <c>;</c>; <c>x-contentful-timestamp</c> is in
    /// milliseconds. As @contentful/node-apps-toolkit 3.10.0 makes and checks it.
    /// </summary>
    public static class ContentfulSignature
    {
        public const string Header = "x-contentful-signature";
        public const string SignedHeadersHeader = "x-contentful-signed-headers";
        public const string TimestampHeader = "x-contentful-timestamp";

        /// <summary>The signature of a call with these headers (lowercase names), signed at <paramref name="timestampMs"/>.</summary>
        public static string Sign(string secret, string method, string path, IReadOnlyDictionary<string, string> headers, string body, long timestampMs)
        {
            var picked = headers.Where(h => h.Key != Header).ToDictionary(h => h.Key.Trim().ToLowerInvariant(), h => h.Value.Trim(), StringComparer.Ordinal);
            var names = picked.Keys.Append(SignedHeadersHeader).Append(TimestampHeader).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
            picked[TimestampHeader] = timestampMs.ToString(CultureInfo.InvariantCulture);
            picked[SignedHeadersHeader] = string.Join(",", names);
            var canonical = string.Join("\n", method, path,
                string.Join(";", names.Select(n => $"{n}:{picked[n]}")), body);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        /// <summary>Null when the call carries a good signature made with <paramref name="secret"/>, else why not.</summary>
        public static string? Check(string secret, SourceCall call, DateTimeOffset now)
        {
            var given = call.Header(Header);
            var signedList = call.Header(SignedHeadersHeader);
            if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(signedList)
                || !long.TryParse(call.Header(TimestampHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
                return "No Contentful signature headers.";
            if ((now - DateTimeOffset.FromUnixTimeMilliseconds(ms)).Duration() > WebhookSignature.Tolerance)
                return "The signature's time is more than five minutes from now.";
            var signed = signedList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var picked = call.Headers.Where(h => signed.Contains(h.Key)).ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);
            var expected = Encoding.ASCII.GetBytes(Sign(secret, call.Method.ToUpperInvariant(), call.Path, picked, call.Body, ms));
            return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(given.Trim().ToLowerInvariant()))
                ? null : "The Contentful signature does not match the body and the webhook's secret.";
        }
    }

    /// <summary>
    /// A Contentful entry as an Indx document, the same from a webhook call (the Management API's
    /// shape, every field per locale) and from the Delivery API (one locale): <c>_id</c>,
    /// <c>_contentType</c> and <c>_updatedAt</c> from sys, then the fields of the chosen locale. A
    /// reference becomes the id it points to, rich text its plain text.
    /// </summary>
    public static class ContentfulEntries
    {
        public const string Topic = "x-contentful-topic";

        public static JsonObject ToDocument(JsonObject entry, string locale, bool localized)
        {
            var sys = entry["sys"] as JsonObject;
            var doc = new JsonObject
            {
                ["_id"] = sys?["id"]?.DeepClone(),
                ["_contentType"] = sys?["contentType"]?["sys"]?["id"]?.DeepClone(),
                ["_updatedAt"] = sys?["updatedAt"]?.DeepClone(),
            };
            if (entry["fields"] is JsonObject fields)
                foreach (var (name, value) in fields)
                {
                    var v = localized ? (value as JsonObject)?[locale] : value;
                    if (v != null) doc[name] = Plain(v);
                }
            return doc;
        }

        private static JsonNode? Plain(JsonNode node) => node switch
        {
            JsonObject o when o["sys"] is JsonObject s && s["type"]?.ToString() == "Link" => s["id"]?.DeepClone(),
            JsonObject o when o["nodeType"]?.ToString() == "document" => RichText(o),
            JsonArray a => new JsonArray(a.Select(i => i == null ? null : Plain(i)).ToArray()),
            _ => node.DeepClone(),
        };

        private static string RichText(JsonObject document)
        {
            var text = new StringBuilder();
            void Walk(JsonNode? n)
            {
                if (n is not JsonObject o) return;
                if (o["nodeType"]?.ToString() == "text") text.Append(o["value"]?.ToString());
                if (o["content"] is JsonArray children) foreach (var c in children) Walk(c);
                if (o["nodeType"]?.ToString() is { } t && (t.StartsWith("heading", StringComparison.Ordinal) || t is "paragraph" or "list-item" or "blockquote"))
                    text.Append('\n');
            }
            Walk(document);
            return text.ToString().Trim();
        }

        /// <summary>
        /// A webhook call as an event: an entry published is an upsert, one unpublished, archived or
        /// deleted is a delete. Null for what is not indexed: assets, drafts saved, another
        /// environment or another content type.
        /// </summary>
        public static string? ToEvent(SourceCall call, ContentfulSettings settings, string idField)
        {
            var parts = (call.Header(Topic) ?? "").Split('.');
            if (parts.Length < 3 || parts[^2] != "Entry") return null;
            var op = parts[^1] switch
            {
                "publish" => "upsert",
                "unpublish" or "archive" or "delete" => "delete",
                _ => null,
            };
            if (op == null) return null;
            var entry = (string.IsNullOrWhiteSpace(call.Body) ? null : JsonNode.Parse(call.Body)) as JsonObject
                ?? throw new FormatException("A Contentful webhook call carries the entry: keep the default payload.");
            var sys = entry["sys"] as JsonObject;
            if (sys?["environment"]?["sys"]?["id"]?.ToString() is { } env && env != settings.Environment) return null;
            if (settings.ContentType != null && sys?["contentType"]?["sys"]?["id"]?.ToString() is { } type && type != settings.ContentType) return null;
            var id = sys?["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) throw new FormatException("A Contentful webhook call carries the entry with its sys.id: keep the default payload.");
            var doc = op == "upsert" ? ToDocument(entry, settings.Locale, localized: true) : new JsonObject { ["_id"] = id };
            doc[idField] = id;
            doc[WebhookPlanner.EventField] = op;
            return doc.ToJsonString();
        }
    }

    /// <summary>Fetches a Contentful webhook's published entries through the Content Delivery API.</summary>
    public sealed class ContentfulClient(IHttpClientFactory http)
    {
        public const string HttpClientName = "contentful";
        private const int PageSize = 1000;

        public async Task<string> FetchAllAsync(ContentfulSettings settings, CancellationToken ct = default)
        {
            var client = http.CreateClient(HttpClientName);
            var all = new JsonArray();
            int skip = 0, limit = PageSize;
            while (true)
            {
                var url = $"https://{settings.Host}/spaces/{settings.SpaceId}/environments/{Uri.EscapeDataString(settings.Environment)}/entries" +
                          $"?locale={Uri.EscapeDataString(settings.Locale)}&limit={limit}&skip={skip}&order=sys.id&include=0" +
                          (settings.ContentType != null ? $"&content_type={Uri.EscapeDataString(settings.ContentType)}" : "");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeliveryToken);
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonNode? root;
                try { root = JsonNode.Parse(text); }
                catch (JsonException) { throw new InvalidOperationException($"Contentful answered {(int)response.StatusCode} with something other than JSON."); }
                if (!response.IsSuccessStatusCode)
                {
                    var message = root?["message"]?.ToString();
                    // A page of large entries can pass the size Contentful will answer with: ask for fewer.
                    if (message?.Contains("Response size too big", StringComparison.OrdinalIgnoreCase) == true && limit > 10) { limit /= 2; continue; }
                    throw new InvalidOperationException($"Contentful answered {(int)response.StatusCode}: {message ?? "no reason given"}");
                }
                if (root?["items"] is not JsonArray items) throw new InvalidOperationException("Contentful's answer held no entries.");
                foreach (var item in items)
                    if (item is JsonObject entry) all.Add(ContentfulEntries.ToDocument(entry, settings.Locale, localized: false));
                skip += items.Count;
                var total = root["total"]?.GetValue<int>() ?? 0;
                if (items.Count == 0 || skip >= total) break;
            }
            return all.ToJsonString();
        }
    }

    /// <summary>
    /// Contentful as a source: its webhooks call the address, authenticated by a secret header
    /// (<c>X-Indx-Secret</c>, set as a secret header on the webhook in Contentful) or by
    /// Contentful's own signature when the space's signing secret is the webhook's secret; and Indx
    /// fetches the published entries through the Content Delivery API.
    /// </summary>
    public sealed class ContentfulWebhookSource(ContentfulClient client) : IWebhookSource
    {
        public const string SecretHeader = "X-Indx-Secret";

        public string Id => WebhookSources.Contentful;
        public string Label => "Contentful";
        public string Description => "A Contentful webhook calls it, and Indx reads the published entries from Contentful itself. No code needed.";
        public string DefaultIdField => "_id";
        public bool Pulls => true;

        public IReadOnlyList<SourceSetting> Settings { get; } =
        [
            new("spaceId", "Contentful space id", Placeholder: "a1b2c3d4e5f6", Required: true),
            new("environment", "Environment", Placeholder: ContentfulSettings.DefaultEnvironment, Default: ContentfulSettings.DefaultEnvironment),
            new("contentType", "Content type id", Placeholder: "product", Hint: "Empty for entries of every type."),
            new("locale", "Locale", Placeholder: ContentfulSettings.DefaultLocale, Default: ContentfulSettings.DefaultLocale,
                Hint: "The locale whose values are indexed."),
            new("deliveryToken", "Content Delivery API token", Required: true, Secret: true,
                Hint: "Under Settings and API keys in Contentful. Never shown again."),
            new("region", "Region", Default: "us", Options: [new("Global", "us"), new("EU data residency", "eu")]),
            SourceSettings.SyncEveryHours,
        ];

        public (JsonObject? Config, string? Problem) Configure(JsonObject? settings)
        {
            if (settings == null) return (null, "A Contentful webhook needs settings: { spaceId, deliveryToken }.");
            var (hours, hoursProblem) = SourceSettings.ReadHours(settings);
            if (hoursProblem != null) return (null, hoursProblem);
            var s = ContentfulSettings.From(settings)!;
            if (s.Problem() is { } problem) return (null, problem);
            return (new JsonObject
            {
                ["spaceId"] = s.SpaceId, ["environment"] = s.Environment, ["contentType"] = s.ContentType, ["locale"] = s.Locale,
                ["deliveryToken"] = s.DeliveryToken, ["region"] = s.Region, [SourceSettings.SyncEveryHoursKey] = hours,
            }, null);
        }

        /// <summary>64 hex characters: the form Contentful takes for a space's signing secret too.</summary>
        public string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        public string? CheckCall(SourceCall call, string secret, DateTimeOffset now)
        {
            if (call.Header(SecretHeader.ToLowerInvariant()) is { } given)
                return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given.Trim()), Encoding.UTF8.GetBytes(secret))
                    ? null : $"The {SecretHeader} header does not match the webhook's secret.";
            if (call.Header(ContentfulSignature.Header) != null) return ContentfulSignature.Check(secret, call, now);
            return $"No {SecretHeader} header: add it to the webhook in Contentful as a secret header, with the webhook's secret.";
        }

        public string? ToEvents(SourceCall call, Webhook hook) =>
            ContentfulEntries.ToEvent(call, ContentfulSettings.From(SourceSettings.Parse(hook.SourceConfig))
                ?? throw new FormatException("The webhook's Contentful settings are unreadable."), hook.IdField);

        public bool LeavesOut(string id) => false;

        public Task<string> FetchAllAsync(JsonObject config, CancellationToken ct = default) =>
            client.FetchAllAsync(ContentfulSettings.From(config)!, ct);

        public SetupGuide Setup(JsonObject? config)
        {
            var s = ContentfulSettings.From(config);
            var steps = new List<SetupStep>
            {
                new("URL", "POST, the address above"),
                new("Triggers", "Entry: Publish, Unpublish, Archive, Delete"),
                new("Filter", $"Environment ID (sys.environment.sys.id) equals {s?.Environment ?? ContentfulSettings.DefaultEnvironment}"),
            };
            if (s?.ContentType != null) steps.Add(new("Filter", $"Content type ID (sys.contentType.sys.id) equals {s.ContentType}"));
            steps.Add(new("Secret header", $"{SecretHeader}: the secret above"));
            steps.Add(new("Payload", "the default"));
            return new("In Contentful, under Settings and Webhooks, add a webhook with these settings. Indx is reading the published entries now.", steps);
        }
    }
}
