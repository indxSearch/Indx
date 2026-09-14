using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IndxServer.Services
{
    /// <summary>
    /// Four rate limits, one middleware. Two are per caller, two protect the instance from itself:
    /// <list type="bullet">
    ///   <item><b>Auth</b> — the endpoints anyone can hit without a token (API login, the
    ///   dashboard's login / register / forgot-password / reset / resend forms): a fixed window
    ///   per client IP. On by default.</item>
    ///   <item><b>Api</b> — authenticated /api and /mcp traffic: a token bucket per API key (the
    ///   token's jti; a user with several keys is limited per key, so one runaway app does not
    ///   starve their others). Off by default for self-host, where the ceiling is the hardware;
    ///   the Managed App turns it on. Generous by design: it catches loops, not load.</item>
    ///   <item><b>Heavy</b> — an instance-wide cap on the operations that rebuild an index (load,
    ///   replace, index, wake-up, analyze, field configuration and the batch document mutations
    ///   that go through a shadow build). A small number may run at once; a few more wait in a
    ///   queue; beyond that the request is rejected at once. Endpoints opt in with
    ///   <c>[EnableRateLimiting(HeavyPolicy)]</c>. On by default.</item>
    ///   <item><b>Search</b> — a bounded queue in front of the engine's search slots (one per
    ///   core plus one, per dataset). Without it a saturated instance made every extra search
    ///   wait its full timeout and then return an empty result; with it the overflow gets an
    ///   immediate 429 so clients back off instead of piling up latency. On by default.</item>
    /// </list>
    /// The instance-wide caps count HTTP requests: work the dashboard starts, and the automatic
    /// reload of a hibernated dataset on first use, run outside them.
    /// A rejection is an RFC 9457 problem with <c>code: rateLimited</c>, <c>retryAfterSeconds</c>
    /// and a <c>Retry-After</c> header, like every other API error.
    ///
    /// <para>Behind a proxy (Azure App Service) the client IP arrives in X-Forwarded-For; set
    /// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> there so <c>RemoteIpAddress</c> is the
    /// client and not the front end — otherwise one window is shared by every caller.</para>
    /// </summary>
    public static class AuthRateLimiting
    {
        public const string Code = "rateLimited";
        /// <summary>Policy name for <c>[EnableRateLimiting]</c> on index-rebuilding endpoints.</summary>
        public const string HeavyPolicy = "heavy";
        /// <summary>Policy name for <c>[EnableRateLimiting]</c> on the search endpoints.</summary>
        public const string SearchPolicy = "search";

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

        public sealed class HeavyOptions
        {
            public bool Enabled { get; set; } = true;
            /// <summary>Heavy operations allowed to run at the same time, instance-wide.</summary>
            public int MaxConcurrent { get; set; } = 2;
            /// <summary>Requests allowed to wait for a free slot. The next one is rejected at once.</summary>
            public int QueueLimit { get; set; } = 2;
            /// <summary>Hint sent with the rejection; a rebuild takes a while, so a short retry is pointless.</summary>
            public int RetryAfterSeconds { get; set; } = 10;
        }

        public sealed class SearchOptions
        {
            public bool Enabled { get; set; } = true;
            /// <summary>Searches in flight at once, instance-wide. 0 means processor count + 1,
            /// which matches the engine's per-dataset slot pool, so a search that gets past the
            /// queue never has to wait for a slot.</summary>
            public int MaxConcurrent { get; set; } = 0;
            /// <summary>Searches allowed to wait for a slot before the next one is rejected.</summary>
            public int QueueLimit { get; set; } = 100;

            public int EffectiveMaxConcurrent => MaxConcurrent > 0 ? MaxConcurrent : Environment.ProcessorCount + 1;
        }

        /// <summary>A single instance-wide concurrency limiter with its own rejection text. The
        /// middleware chains it after the global (per-caller) limiter for endpoints that name it.</summary>
        private sealed class InstanceCapPolicy : IRateLimiterPolicy<string>
        {
            private readonly string _name;
            private readonly bool _enabled;
            private readonly int _permits, _queue, _retryAfter;
            private readonly Func<int, string> _detail;

            public InstanceCapPolicy(string name, bool enabled, int permits, int queue, int retryAfter, Func<int, string> detail)
            {
                _name = name; _enabled = enabled; _permits = permits; _queue = queue; _retryAfter = retryAfter; _detail = detail;
            }

            public RateLimitPartition<string> GetPartition(HttpContext httpContext)
            {
                if (!_enabled) return RateLimitPartition.GetNoLimiter("none");
                return RateLimitPartition.GetConcurrencyLimiter(_name, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = _permits,
                    QueueLimit = _queue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            }

            public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => async (context, ct) =>
            {
                var http = context.HttpContext;
                http.Response.Headers.RetryAfter = _retryAfter.ToString();
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Server busy",
                    Detail = _detail(_retryAfter),
                    Extensions = { ["code"] = Code, ["retryAfterSeconds"] = _retryAfter },
                };
                await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", ct);
            };
        }

        /// <summary>The API surface a key calls: /api/* and the MCP endpoint, with a bearer token
        /// present. Login is excluded (anonymous; it has the Auth window). The token is NOT
        /// validated here — Bearer is authenticated in the authorization stage, after this
        /// middleware — but the key only chooses a bucket: a bad token limits its own sender and
        /// still gets its 401.</summary>
        public static bool IsApiCall(HttpContext ctx) =>
            (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/mcp"))
            && !ctx.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase)
            && BearerToken(ctx) != null;

        private static string? BearerToken(HttpContext ctx)
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && h.Length > 7 ? h[7..].Trim() : null;
        }

        /// <summary>The limiting key: the token's jti (one per issued API key), else a digest of
        /// the token itself, so distinct keys never share a bucket.</summary>
        public static string ApiKeyOf(HttpContext ctx)
        {
            var token = BearerToken(ctx) ?? "";
            try
            {
                var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
                if (!string.IsNullOrEmpty(jwt.Id)) return jwt.Id;
            }
            catch { /* not a JWT: fall through to the digest */ }
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))[..16];
        }

        public static bool IsAuthPost(HttpContext ctx) =>
            HttpMethods.IsPost(ctx.Request.Method) &&
            AuthPostPaths.Any(p => ctx.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase));

        public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection("RateLimits:Auth").Get<Options>() ?? new Options();
            var api = configuration.GetSection("RateLimits:Api").Get<ApiOptions>() ?? new ApiOptions();
            var heavy = configuration.GetSection("RateLimits:Heavy").Get<HeavyOptions>() ?? new HeavyOptions();
            var search = configuration.GetSection("RateLimits:Search").Get<SearchOptions>() ?? new SearchOptions();
            services.AddSingleton(options);
            services.AddSingleton(api);
            services.AddSingleton(heavy);
            services.AddSingleton(search);
            services.AddRateLimiter(limiter =>
            {
                limiter.AddPolicy(HeavyPolicy, new InstanceCapPolicy(HeavyPolicy, heavy.Enabled,
                    Math.Max(1, heavy.MaxConcurrent), Math.Max(0, heavy.QueueLimit), Math.Max(1, heavy.RetryAfterSeconds),
                    retry => $"The server is already running {Math.Max(1, heavy.MaxConcurrent)} heavy operations (loads, replaces, index builds). Try again in {retry} seconds."));
                limiter.AddPolicy(SearchPolicy, new InstanceCapPolicy(SearchPolicy, search.Enabled,
                    search.EffectiveMaxConcurrent, Math.Max(0, search.QueueLimit), 1,
                    retry => $"Search capacity is saturated: {search.EffectiveMaxConcurrent} searches in flight and {Math.Max(0, search.QueueLimit)} waiting. Try again in {retry} second."));
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                {
                    if (options.Enabled && IsAuthPost(ctx))
                    {
                        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                        return RateLimitPartition.GetFixedWindowLimiter("auth:" + ip, _ => new FixedWindowRateLimiterOptions
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
                    return RateLimitPartition.GetNoLimiter("none");
                });
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = async (context, ct) =>
                {
                    var http = context.HttpContext;
                    var isKey = IsApiCall(http) && !IsAuthPost(http);
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra)
                        ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : (isKey ? 1 : options.WindowSeconds);
                    http.Response.Headers.RetryAfter = retryAfter.ToString();
                    http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    if (http.Request.Path.StartsWithSegments("/api"))
                    {
                        var problem = new ProblemDetails
                        {
                            Status = StatusCodes.Status429TooManyRequests,
                            Title = isKey ? "Rate limit exceeded" : "Too many attempts",
                            Detail = isKey
                                ? $"This API key exceeded {api.RequestsPerSecond} requests per second (burst {api.Burst}). Try again in {retryAfter} seconds."
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
