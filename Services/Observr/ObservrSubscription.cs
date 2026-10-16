using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

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
        /// <summary>The last answer is older than the grace: the portal has not been heard from for a week.</summary>
        public bool Lapsed => Reason == "lapsed";
    }

    /// <summary>The last time the portal could not be asked: when, and why (unreachable, or the
    /// status it answered). Cleared by the next answer.</summary>
    public sealed record ObservrPortalFailure(DateTime AtUtc, string Reason);

    /// <summary>
    /// The instance's own view of whether Observr is part of its subscription, asked of the licence
    /// portal with the licence token (the same question IndxObservr asks for every request). A free
    /// developer instance never sees Observr: no menu, no admin page content, no buttons. Remembered
    /// for an hour per token, refreshed by the digest job and on the admin page's request.
    ///
    /// Through a portal outage the last answer stands (16 Oct 2026): it is kept in the instance
    /// settings as well as in memory, so a restart during the outage keeps Observr as a running
    /// instance would, for up to <see cref="Grace"/> after it was given; then it lapses and the
    /// admin page says so. The failure itself is remembered (<see cref="PortalFailure"/>) so the
    /// page can say the portal could not be reached instead of waiting forever, and background
    /// checks back off while it stays down.
    /// </summary>
    internal sealed class ObservrSubscription(IHttpClientFactory http, ObservrOptions options, InstanceSettingsService settings,
        IConfiguration configuration, ILogger<ObservrSubscription> logger)
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
        /// <summary>How long the last answer stands with the portal unreachable.</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromDays(7);
        /// <summary>How long a background check waits after a failure before asking again.</summary>
        public static readonly TimeSpan Backoff = TimeSpan.FromMinutes(5);
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

        /// <summary>The last time the portal could not be asked, until it answers again.</summary>
        public ObservrPortalFailure? PortalFailure { get; private set; }

        /// <summary>The last answer for the current token, without asking: what a page reads. In
        /// memory first, else the one kept in the settings while it is within the grace.</summary>
        public ObservrSubscriptionState Current
        {
            get
            {
                if (HasOwnToken) return Internal;
                if (Token() is not { } t) return ObservrSubscriptionState.Unknown;
                if (_byToken.TryGetValue(t, out var s)) return Age(s);
                var memo = settings.Load().ObservrSubscription;
                if (memo == null || memo.TokenHash != Hash(t)) return ObservrSubscriptionState.Unknown;
                return Age(new ObservrSubscriptionState(true, memo.Included, memo.Tier, memo.LicensedTo, memo.Reason, memo.CheckedUtc, memo.Access, memo.ExpiresAt));
            }
        }

        /// <summary>An answer older than the grace no longer stands.</summary>
        private static ObservrSubscriptionState Age(ObservrSubscriptionState s) =>
            s.Known && s.CheckedUtc is { } at && DateTime.UtcNow - at > Grace && s.Included
                ? s with { Included = false, Reason = "lapsed" }
                : s;

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
                    return Keep(token, new ObservrSubscriptionState(true, false, "", "", "unknownToken", DateTime.UtcNow));
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("The licence portal answered {Status} to the subscription check", (int)response.StatusCode);
                    PortalFailure = new ObservrPortalFailure(DateTime.UtcNow, $"http{(int)response.StatusCode}");
                    return Current;
                }
                var body = await response.Content.ReadFromJsonAsync<PortalAnswer>(ct) ?? new PortalAnswer();
                return Keep(token, new ObservrSubscriptionState(true, body.Valid, body.Tier ?? "", body.LicensedTo ?? "", body.Reason, DateTime.UtcNow, body.Access, body.ExpiresAt));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "The licence portal could not be reached for the subscription check");
                PortalFailure = new ObservrPortalFailure(DateTime.UtcNow, "unreachable");
                // The last answer stands through an outage; an instance that never got one stays without Observr.
                return Current;
            }
        }

        /// <summary>An answer: in memory, in the settings, and the failure is over.</summary>
        private ObservrSubscriptionState Keep(string token, ObservrSubscriptionState state)
        {
            _byToken[token] = state;
            PortalFailure = null;
            try
            {
                var s = settings.Load();
                s.ObservrSubscription = new ObservrSubscriptionMemo
                {
                    TokenHash = Hash(token), Included = state.Included, Tier = state.Tier, LicensedTo = state.LicensedTo,
                    Reason = state.Reason, Access = state.Access, ExpiresAt = state.ExpiresAt, CheckedUtc = state.CheckedUtc ?? DateTime.UtcNow,
                };
                settings.Save(s);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not keep the subscription answer"); }
            return state;
        }

        private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];

        /// <summary>Raised when a background check has answered, so the header's button and chip,
        /// which read <see cref="Current"/>, can show on the page that started it.</summary>
        public event Action? Changed;

        /// <summary>Starts a check in the background when none has answered yet: the header calls
        /// this on every page and redraws on <see cref="Changed"/>. While the portal stays down it
        /// asks again only after <see cref="Backoff"/>, not on every page.</summary>
        public void EnsureChecked()
        {
            if (Current.Known || Token() == null) return;
            if (PortalFailure is { } f && DateTime.UtcNow - f.AtUtc < Backoff) return;
            if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
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
