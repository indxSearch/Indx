using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace IndxServer.Services.Observr
{
    /// <summary>What the portal says about this instance's licence: whether its subscription includes
    /// Observr, which tier, whose it is, and why not when not. <see cref="Known"/> is false until the
    /// portal has answered once.</summary>
    public sealed record ObservrSubscriptionState(bool Known, bool Included, string Tier, string LicensedTo, string? Reason, DateTime? CheckedUtc,
        string? Access = null, DateTime? ExpiresAt = null)
    {
        public static readonly ObservrSubscriptionState Unknown = new(false, false, "", "", null, null);
        /// <summary>Let in while Observr is in preview, without a paid edition.</summary>
        public bool EarlyAccess => Access == "earlyAccess";
    }

    /// <summary>
    /// The instance's own view of whether Observr is part of its subscription, asked of the licence
    /// portal with the licence token (the same question IndxObservr asks for every request). A free
    /// developer instance never sees Observr: no menu, no admin page content, no buttons. Remembered
    /// for an hour per token, refreshed by the digest job and on the admin page's request.
    /// </summary>
    internal sealed class ObservrSubscription(IHttpClientFactory http, ObservrOptions options, InstanceSettingsService settings,
        IConfiguration configuration, ILogger<ObservrSubscription> logger)
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
        private readonly ConcurrentDictionary<string, ObservrSubscriptionState> _byToken = new();
        private int _refreshing;

        private string? Token()
        {
            var token = settings.Load().LicenseToken;
            if (string.IsNullOrWhiteSpace(token)) token = configuration["Indx:LicenseToken"];
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        /// <summary>An instance with an Observr token of its own (Indx's, built from source) has no
        /// licence to check: IndxObservr trusts the token by configuration.</summary>
        private static readonly ObservrSubscriptionState Internal = new(true, true, "Internal", "this instance's own Observr token", null, DateTime.UtcNow);
        private bool HasOwnToken => !string.IsNullOrWhiteSpace(options.Token);

        /// <summary>The last answer for the current token, without asking: what a page reads.</summary>
        public ObservrSubscriptionState Current => HasOwnToken ? Internal
            : Token() is { } t && _byToken.TryGetValue(t, out var s) ? s : ObservrSubscriptionState.Unknown;

        /// <summary>Whether anything of Observr is shown on this instance: an admin activated it, there
        /// is a token, and the subscription includes it.</summary>
        public bool Visible => settings.Load().ObservrEnabled && Current.Included;

        /// <summary>Asks the portal when the answer is missing or old (or always, with <paramref name="force"/>).</summary>
        public async Task<ObservrSubscriptionState> GetAsync(bool force = false, CancellationToken ct = default)
        {
            if (HasOwnToken) return Internal;
            if (Token() is not { } token) return ObservrSubscriptionState.Unknown;
            if (!force && _byToken.TryGetValue(token, out var cached) && cached.Known && DateTime.UtcNow - cached.CheckedUtc < Ttl)
                return cached;
            var client = http.CreateClient(ObservrClient.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.PortalUrl.TrimEnd('/')}/api/license/subscription");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return _byToken[token] = new ObservrSubscriptionState(true, false, "", "", "unknownToken", DateTime.UtcNow);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("The licence portal answered {Status} to the subscription check", (int)response.StatusCode);
                    return Current;
                }
                var body = await response.Content.ReadFromJsonAsync<PortalAnswer>(ct) ?? new PortalAnswer();
                return _byToken[token] = new ObservrSubscriptionState(true, body.Valid, body.Tier ?? "", body.LicensedTo ?? "", body.Reason, DateTime.UtcNow, body.Access, body.ExpiresAt);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "The licence portal could not be reached for the subscription check");
                // The last answer stands through an outage; an instance that never got one stays without Observr.
                return Current;
            }
        }

        /// <summary>Raised when a background check has answered, so the header's button and chip,
        /// which read <see cref="Current"/>, can show on the page that started it.</summary>
        public event Action? Changed;

        /// <summary>Starts a check in the background when none has answered yet: the header calls
        /// this on every page and redraws on <see cref="Changed"/>.</summary>
        public void EnsureChecked()
        {
            if (Current.Known || Token() == null || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                try { await GetAsync(); }
                finally { Interlocked.Exchange(ref _refreshing, 0); }
                if (Current.Known) Changed?.Invoke();
            });
        }

        private sealed class PortalAnswer
        {
            public bool Valid { get; set; }
            public string? Tier { get; set; }
            public string? LicensedTo { get; set; }
            public string? Reason { get; set; }
            public string? Access { get; set; }
            public DateTime? ExpiresAt { get; set; }
        }
    }
}
