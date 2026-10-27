using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndxServer.Services.Webhooks
{
    /// <summary>
    /// Who calls a webhook, and what Indx does with it: how a call is authenticated, how it becomes
    /// the events the planner reads, and, for a source with an API, how the full set is fetched.
    /// One class per source (Indx's own signed calls, Sanity, Contentful), registered in
    /// <see cref="WebhookServices.AddIndxWebhooks"/>; the receiver, the applier, the worker and the
    /// console ask <see cref="WebhookSourceRegistry"/> rather than naming a source. A new source is
    /// a new class: its settings draw its part of the console's form.
    /// </summary>
    public interface IWebhookSource
    {
        /// <summary>Stored with the webhook and sent by the API: "indx", "sanity", "contentful".</summary>
        string Id { get; }
        /// <summary>Its name in the console.</summary>
        string Label { get; }
        /// <summary>One line under the console's choice of source.</summary>
        string Description { get; }
        string DefaultIdField { get; }
        /// <summary>What the admin fills in. Empty for a source with nothing to set.</summary>
        IReadOnlyList<SourceSetting> Settings { get; }
        /// <summary>Whether Indx fetches the full set from the source (on create, on request and on
        /// a schedule), as opposed to the sender pushing it.</summary>
        bool Pulls { get; }

        /// <summary>The settings as stored, defaults filled in, or why they will not do. Null config
        /// and null problem for a source with nothing to set.</summary>
        (JsonObject? Config, string? Problem) Configure(JsonObject? settings);
        /// <summary>A new secret in the form the source's own settings accept.</summary>
        string NewSecret();
        /// <summary>Null when the call is the source's and signed with <paramref name="secret"/>, else why not.</summary>
        string? CheckCall(SourceCall call, string secret, DateTimeOffset now);
        /// <summary>The call as the planner's events (one object or a list), or null when it holds
        /// nothing to index (a draft, an asset). Throws <see cref="FormatException"/> for a body it cannot read.</summary>
        string? ToEvents(SourceCall call, Webhook hook);
        /// <summary>A document in a full set that is not published (a Sanity draft): left out.</summary>
        bool LeavesOut(string id);
        /// <summary>The full set as a JSON list of documents. Throws <see cref="InvalidOperationException"/>
        /// with the source's own message when it refuses.</summary>
        Task<string> FetchAllAsync(JsonObject config, CancellationToken ct = default);
        /// <summary>What to set up on the source's side once the webhook is made.</summary>
        SetupGuide Setup(JsonObject? config);
    }

    /// <summary>One setting of a source, as the console's form shows it and the API takes it (by <see cref="Key"/>).</summary>
    public sealed record SourceSetting(string Key, string Label, string? Placeholder = null, string? Hint = null,
        bool Required = false, bool Secret = false, bool Multiline = false, string? Default = null,
        IReadOnlyList<SourceOption>? Options = null);

    public sealed record SourceOption(string Label, string Value);

    /// <summary>A call as a source reads it. Header names are lowercase.</summary>
    public sealed record SourceCall(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>The steps on the source's side, shown with the address and the secret after Create.</summary>
    public sealed record SetupGuide(string Intro, IReadOnlyList<SetupStep> Steps);

    /// <summary>One row of a <see cref="SetupGuide"/>; <see cref="Code"/> when the value is typed as is.</summary>
    public sealed record SetupStep(string Label, string Value, bool Code = false);

    /// <summary>The settings every pulling source shares, and reading them from what the API or the form sent.</summary>
    public static class SourceSettings
    {
        public const string SyncEveryHoursKey = "syncEveryHours";
        public const int DefaultSyncEveryHours = 24;

        public static readonly SourceSetting SyncEveryHours = new(SyncEveryHoursKey, "Fetch the full set every",
            Placeholder: "hours", Hint: "Hours, 1 to 168. Empty for never. The fetch also catches changes a webhook call missed.",
            Default: "24");

        /// <summary>How often a pulling webhook fetches its set: the stored number, null for never,
        /// the default when the setting is absent.</summary>
        public static int? Hours(JsonObject config) =>
            !config.TryGetPropertyValue(SyncEveryHoursKey, out var node) ? DefaultSyncEveryHours
            : node is null ? null
            : node is JsonValue v && v.TryGetValue<int>(out var n) ? n
            : int.TryParse(node.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : DefaultSyncEveryHours;

        /// <summary>The hours as sent (a number, a string from the form, null or absent), checked.</summary>
        public static (int? Hours, string? Problem) ReadHours(JsonObject? settings)
        {
            if (settings == null || !settings.TryGetPropertyValue(SyncEveryHoursKey, out var node)) return (DefaultSyncEveryHours, null);
            if (node is null || node is JsonValue s && s.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)) return (null, null);
            int hours;
            if (node is JsonValue v && v.TryGetValue<int>(out var n)) hours = n;
            else if (!int.TryParse(node.ToString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out hours))
                return (null, "Fetch the full set every: a whole number of hours, 1 to 168, or empty for never.");
            return hours is < 1 or > 24 * 7 ? (null, "Fetch the full set every: 1 to 168 hours, or empty for never.") : (hours, null);
        }

        /// <summary>A text setting, trimmed; null when absent or blank.</summary>
        public static string? Text(JsonObject? settings, string key) =>
            settings != null && settings.TryGetPropertyValue(key, out var node) && node is JsonValue v
                && (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) is { } text && !string.IsNullOrWhiteSpace(text)
                ? text.Trim() : null;

        /// <summary>The stored settings of a webhook, as its source reads them.</summary>
        public static JsonObject? Parse(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try { return JsonNode.Parse(stored) as JsonObject; }
            catch (JsonException) { return null; }
        }

        /// <summary>The settings safe to show: every one but the secret ones.</summary>
        public static JsonObject? Public(IWebhookSource source, JsonObject? config)
        {
            if (config == null) return null;
            var copy = (JsonObject)config.DeepClone();
            foreach (var s in source.Settings.Where(s => s.Secret)) copy.Remove(s.Key);
            return copy;
        }

        /// <summary>The secret settings that hold a value, by key, so a list can say a token is set.</summary>
        public static string[] SecretsSet(IWebhookSource source, JsonObject? config) => config == null ? [] :
            source.Settings.Where(s => s.Secret && Text(config, s.Key) != null).Select(s => s.Key).ToArray();
    }

    /// <summary>The sources this instance knows, by id. Indx's own signed calls are the fallback.</summary>
    public sealed class WebhookSourceRegistry(IEnumerable<IWebhookSource> sources)
    {
        private readonly IReadOnlyList<IWebhookSource> _all = sources.ToList();

        /// <summary>In the order they were registered: Indx's own first.</summary>
        public IReadOnlyList<IWebhookSource> All => _all;

        public IWebhookSource? Get(string? id) =>
            string.IsNullOrWhiteSpace(id) ? null : _all.FirstOrDefault(s => s.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

        public IWebhookSource For(Webhook hook) => Get(hook.Source) ?? Get(WebhookSources.Indx)!;

        public string Known => string.Join(", ", _all.Select(s => $"\"{s.Id}\""));
    }

    /// <summary>The generic scheme: the sender signs each call with <c>X-Indx-Signature</c> and
    /// sends the full set itself. Own code, or an add-on such as Indx.Connector.Optimizely.</summary>
    public sealed class IndxWebhookSource : IWebhookSource
    {
        public string Id => WebhookSources.Indx;
        public string Label => "Signed calls";
        public string Description => "Your own code, or an add-on such as Indx.Connector.Optimizely, signs each call with the secret.";
        public string DefaultIdField => "id";
        public IReadOnlyList<SourceSetting> Settings => [];
        public bool Pulls => false;

        public (JsonObject? Config, string? Problem) Configure(JsonObject? settings) => (null, null);
        public string NewSecret() => WebhookStore.NewSecret();

        public string? CheckCall(SourceCall call, string secret, DateTimeOffset now) =>
            WebhookSignature.Check(secret, call.Header(WebhookSignature.Header.ToLowerInvariant()), call.Body, now);

        public string? ToEvents(SourceCall call, Webhook hook) => call.Body;
        public bool LeavesOut(string id) => false;

        public Task<string> FetchAllAsync(JsonObject config, CancellationToken ct = default) =>
            throw new InvalidOperationException("The sender sends the full set itself.");

        public SetupGuide Setup(JsonObject? config) => new(
            "Sign each call with the secret. Send changes to the address, and the full set to the address followed by /sync.",
            [
                new("Header", "X-Indx-Signature: t=<unix seconds>,sha256=<hex HMAC-SHA256 of \"t.body\">", Code: true),
                new("Changes", "POST <address> with [{\"id\": …, \"event\": \"upsert\" | \"delete\", …}]", Code: true),
                new("Full set", "POST <address>/sync with [{\"id\": …, …}]", Code: true),
            ]);
    }
}
