using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Services
{
    /// <summary>
    /// Three rate limits, one middleware, all per caller:
    /// <list type="bullet">
    ///   <item><b>Auth</b> — the endpoints anyone can hit without a token (API login, the
    ///   dashboard's login / register / forgot-password / reset / resend forms): a fixed window
    ///   per client IP. On by default.</item>
    ///   <item><b>Api</b> — authenticated /api and /mcp traffic: a token bucket per API key (the
    ///   token's jti; a user with several keys is limited per key, so one runaway app does not
    ///   starve their others). Off by default for self-host, where the ceiling is the hardware;
    ///   the Managed App turns it on. Generous by design: it catches loops, not load.</item>
    ///   <item><b>Anon</b> — /api and /mcp traffic with no bearer token <b>that validates</b>: a
    ///   fixed window per client IP. Every one of these ends in 401, so a real client produces
    ///   them only briefly (an expired token) while a bot scanning the API produces nothing else.
    ///   On by default: unlike Api this is a defence, not a courtesy, and the traffic it bounds is
    ///   worthless by construction. A forged or expired token counts here, not as a key — see
    ///   <see cref="BearerIdentity"/> for why that distinction is the whole point. Preflight
    ///   OPTIONS is excluded — it carries no Authorization header by definition, and a
    ///   cross-origin page would otherwise spend the window on preflights before making a single
    ///   real call.</item>
    /// </list>
    /// A rejection is an RFC 9457 problem with <c>code: rateLimited</c>, <c>retryAfterSeconds</c>
    /// and a <c>Retry-After</c> header, like every other API error.
    ///
    /// <para>There is deliberately no instance-wide cap here. Two existed briefly (Heavy and
    /// Search, 14 Sep 2026) and were removed the same day: both counted requests in flight, which
    /// is not what either was protecting. Heavy guarded memory, where two concurrent replaces of an
    /// 8 GB dataset and two single-document inserts cost the same one permit each; Search
    /// duplicated the engine's own <c>SearchContext</c> slot pool, but instance-wide rather than
    /// per dataset, so it became the binding constraint the moment a second dataset existed.
    /// Concurrency per dataset is already enforced where it belongs — <c>ShadowBusyException</c>
    /// answers 409 for a dataset that is mid-rebuild, and the engine's slot pool bounds
    /// searches.</para>
    ///
    /// <para>Behind a proxy (Azure App Service) the client IP arrives in X-Forwarded-For; set
    /// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> there so <c>RemoteIpAddress</c> is the
    /// client and not the front end — otherwise one window is shared by every caller.</para>
    /// </summary>
    public static class AuthRateLimiting
    {
        public const string Code = "rateLimited";

        /// <summary>Paths that take credentials or trigger email from anyone. POST only: the GETs
        /// render forms and must stay reachable while a window is exhausted.</summary>
        private static readonly string[] AuthPostPaths =
        [
            "/api/login",
            "/account/login",
            "/account/register",
            "/account/forgot-password",
            "/account/reset-password",
            "/account/resend-email-confirmation",
            "/account/perform-external-login",
        ];

        public sealed class Options
        {
            public bool Enabled { get; set; } = true;
            /// <summary>Attempts per window, per client IP, across all auth endpoints together.</summary>
            public int PermitLimit { get; set; } = 10;
            public int WindowSeconds { get; set; } = 60;
        }

        public sealed class ApiOptions
        {
            public bool Enabled { get; set; } = false;
            /// <summary>Sustained requests per second, per API key.</summary>
            public int RequestsPerSecond { get; set; } = 50;
            /// <summary>Burst allowance: how many requests a key can make at once before the
            /// per-second rate applies.</summary>
            public int Burst { get; set; } = 200;
        }

        public sealed class AnonOptions
        {
            public bool Enabled { get; set; } = true;
            /// <summary>Tokenless /api and /mcp requests per window, per client IP. Generous
            /// enough for a client whose token expired mid-session and is retrying while it
            /// refreshes; far below what a scanner produces.</summary>
            public int PermitLimit { get; set; } = 30;
            public int WindowSeconds { get; set; } = 60;
        }

        /// <summary>Says once, at startup, when a configured value does not mean what it looks
        /// like. The bucket size is <c>Math.Max(Burst, RequestsPerSecond)</c>, so a Burst below
        /// the per-second rate is silently raised to it: the operator's number is in the file and
        /// has no effect. Honouring it literally would be worse — the bucket would cap each
        /// replenishment and the effective rate would become the Burst — so the behaviour stays
        /// and the silence goes.</summary>
        private sealed class ConfigurationWarnings(ApiOptions api, ILoggerFactory loggers) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                if (api.Burst < api.RequestsPerSecond)
                    loggers.CreateLogger("IndxServer.RateLimit").LogWarning(
                        "RateLimits:Api:Burst ({Burst}) is below RequestsPerSecond ({Rate}); the bucket holds " +
                        "{Effective} and the configured Burst has no effect. Set Burst to at least RequestsPerSecond.",
                        api.Burst, api.RequestsPerSecond, Math.Max(api.Burst, api.RequestsPerSecond));
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        /// <summary>The API surface a key calls: /api/* and the MCP endpoint, carrying a bearer
        /// token that <b>passed validation</b>. Login is excluded (anonymous; it has the Auth
        /// window).
        ///
        /// <para>Validity is what decides the bucket, not the mere presence of a header. When this
        /// asked only whether an Authorization header existed, <c>Bearer anything</c> was enough to
        /// leave the Anon window and land in a per-token bucket that a fresh garbage token reset
        /// every time — measured at 40 requests, 40 rejections by the auth stage, and not one 429.
        /// <see cref="BearerIdentity"/> resolves the token before the limiter so this can ask the
        /// real question.</para></summary>
        public static bool IsApiCall(HttpContext ctx) => IsApiPath(ctx) && BearerIdentity.Principal(ctx) != null;

        /// <summary>The same surface without a caller the server recognises — no token, or one
        /// that failed validation — and not a CORS preflight. Bounded by the Anon window, since
        /// there is no trustworthy key to bound it by.</summary>
        public static bool IsAnonymousApiCall(HttpContext ctx) =>
            IsApiPath(ctx) && BearerIdentity.Principal(ctx) == null && !HttpMethods.IsOptions(ctx.Request.Method);

        private static bool IsApiPath(HttpContext ctx) =>
            (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/mcp"))
            && !ctx.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase);

        /// <summary>The client address used as a partition key. IPv6 is masked to its /64 prefix:
        /// a routed /64 is what one subscriber is handed, so partitioning on the full address
        /// gives a single caller 2^64 windows and bounds nothing.</summary>
        public static string ClientIp(HttpContext ctx)
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null) return "unknown";
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

            Span<byte> bytes = stackalloc byte[16];
            if (!ip.TryWriteBytes(bytes, out _)) return ip.ToString();
            bytes[8..].Clear();
            return new IPAddress(bytes).ToString() + "/64";
        }

        /// <summary>The limiting key for a validated caller: the token's jti, which is one per
        /// issued credential — a dashboard API key for its whole life, a login token for its 24
        /// hours. Tokens predating the jti claim fall back to a digest of the token itself, which
        /// is equally stable per token; the digest is used rather than the token so a credential
        /// never becomes a partition key or reaches a log line.
        ///
        /// <para>Only ever called for a request whose token validated, so the value cannot be
        /// chosen by the caller.</para></summary>
        public static string ApiKeyOf(HttpContext ctx)
        {
            var jti = BearerIdentity.ValidatedJti(ctx);
            if (!string.IsNullOrEmpty(jti)) return jti;
            var token = BearerIdentity.Token(ctx) ?? "";
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))[..16];
        }

        public static bool IsAuthPost(HttpContext ctx) =>
            HttpMethods.IsPost(ctx.Request.Method) &&
            AuthPostPaths.Any(p => ctx.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase));

        /// <summary>Rejections, by limit. A Meter rather than a log counter so Application
        /// Insights can chart it: "is the limiter doing anything" is otherwise unanswerable from
        /// a running instance.</summary>
        private static readonly Meter Meter = new("IndxServer.RateLimit");
        private static readonly Counter<long> Rejections =
            Meter.CreateCounter<long>("indx.ratelimit.rejections", unit: "{request}",
                description: "Requests rejected with 429, tagged by which limit rejected them.");

        private static int _proxyWarned;

        /// <summary>Warns once per process if an IP-partitioned limit is live in Production while
        /// the address we partition on is not a real client address. That is the silent
        /// catastrophic case: behind App Service without ASPNETCORE_FORWARDEDHEADERS_ENABLED,
        /// every caller on earth shares one window and the instance locks itself out.</summary>
        private static void WarnOnceIfAddressLooksLikeAProxy(HttpContext ctx)
        {
            if (Volatile.Read(ref _proxyWarned) != 0) return;

            var env = ctx.RequestServices.GetService<IHostEnvironment>();
            if (env is null || !env.IsProduction()) return;

            var ip = ctx.Connection.RemoteIpAddress;
            var suspect = ip is null
                || IPAddress.IsLoopback(ip)
                || (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..]);
            if (!suspect) return;

            if (Interlocked.Exchange(ref _proxyWarned, 1) != 0) return;
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("IndxServer.RateLimit")
                .LogWarning(
                    "Per-address rate limits are partitioning on {Address}, which is not a public client address. " +
                    "Behind a reverse proxy or Azure App Service set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true, " +
                    "or every caller shares one window.",
                    ip?.ToString() ?? "(none)");
        }

        public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection("RateLimits:Auth").Get<Options>() ?? new Options();
            var api = configuration.GetSection("RateLimits:Api").Get<ApiOptions>() ?? new ApiOptions();
            var anon = configuration.GetSection("RateLimits:Anon").Get<AnonOptions>() ?? new AnonOptions();
            services.AddSingleton(options);
            services.AddSingleton(api);
            services.AddSingleton(anon);
            services.AddHostedService(sp => new ConfigurationWarnings(api, sp.GetRequiredService<ILoggerFactory>()));
            services.AddRateLimiter(limiter =>
            {
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                {
                    if (options.Enabled && IsAuthPost(ctx))
                    {
                        WarnOnceIfAddressLooksLikeAProxy(ctx);
                        return RateLimitPartition.GetFixedWindowLimiter("auth:" + ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.PermitLimit,
                            Window = TimeSpan.FromSeconds(options.WindowSeconds),
                            QueueLimit = 0,
                        });
                    }
                    if (api.Enabled && IsApiCall(ctx))
                    {
                        return RateLimitPartition.GetTokenBucketLimiter("key:" + ApiKeyOf(ctx), _ => new TokenBucketRateLimiterOptions
                        {
                            TokenLimit = Math.Max(api.Burst, api.RequestsPerSecond),
                            TokensPerPeriod = api.RequestsPerSecond,
                            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        });
                    }
                    if (anon.Enabled && IsAnonymousApiCall(ctx))
                    {
                        WarnOnceIfAddressLooksLikeAProxy(ctx);
                        return RateLimitPartition.GetFixedWindowLimiter("anon:" + ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = anon.PermitLimit,
                            Window = TimeSpan.FromSeconds(anon.WindowSeconds),
                            QueueLimit = 0,
                        });
                    }
                    return RateLimitPartition.GetNoLimiter("none");
                });
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = async (context, ct) =>
                {
                    var http = context.HttpContext;
                    var isKey = IsApiCall(http) && !IsAuthPost(http);
                    var isAnon = !isKey && !IsAuthPost(http) && IsAnonymousApiCall(http);
                    // Both limiters report RetryAfter on a failed lease; the fallback is for a
                    // limiter type that does not, and must name the window that actually rejected.
                    var fallback = isKey ? 1 : isAnon ? anon.WindowSeconds : options.WindowSeconds;
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra)
                        ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : fallback;

                    // Which limit fired, and against whom. Without this a running instance cannot
                    // answer "is it rejecting anything" or "are we turning away a real customer".
                    var limit = isKey ? "api" : isAnon ? "anon" : "auth";
                    var partition = isKey ? "key:" + ApiKeyOf(http) : ClientIp(http);
                    Rejections.Add(1, new KeyValuePair<string, object?>("limit", limit));
                    http.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("IndxServer.RateLimit")
                        .LogWarning("Rate limit {Limit} rejected {Method} {Path} for {Partition}; Retry-After {RetryAfter}s",
                            limit, http.Request.Method, http.Request.Path, partition, retryAfter);

                    http.Response.Headers.RetryAfter = retryAfter.ToString();
                    http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    // The plain-text branch below exists for the dashboard's HTML form posts.
                    // /mcp is an API surface and must get the problem document like /api — it fell
                    // into the form branch, answering an MCP client with "Too many attempts from
                    // this address" even when it was the key limit that fired.
                    if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/mcp"))
                    {
                        var problem = new ProblemDetails
                        {
                            Status = StatusCodes.Status429TooManyRequests,
                            Title = isKey ? "Rate limit exceeded" : "Too many attempts",
                            Detail = isKey
                                ? $"This API key exceeded {api.RequestsPerSecond} requests per second (burst {api.Burst}). Try again in {retryAfter} seconds."
                                : isAnon
                                ? $"Too many requests from this address without a valid bearer token. Authenticate first; try again in {retryAfter} seconds."
                                : $"Too many attempts from this address. Try again in {retryAfter} seconds.",
                            Extensions = { ["code"] = Code, ["retryAfterSeconds"] = retryAfter },
                        };
                        await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", ct);
                    }
                    else
                    {
                        http.Response.ContentType = "text/plain; charset=utf-8";
                        await http.Response.WriteAsync($"Too many attempts from this address. Try again in {retryAfter} seconds.", ct);
                    }
                };
            });
            return services;
        }
    }
}
