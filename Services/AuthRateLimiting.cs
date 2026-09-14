using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IndxServer.Services
{
    /// <summary>
    /// Rate limiting for the endpoints anyone on the internet can hit without a token: API login,
    /// and the dashboard's login / register / forgot-password / reset / resend-confirmation forms.
    /// One fixed window per client IP; everything else passes through untouched (authenticated
    /// traffic gets its own, per-API-key policy later). A rejection is an RFC 9457 problem with
    /// <c>code: rateLimited</c> and a <c>Retry-After</c> header, like every other API error.
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

        public static bool IsAuthPost(HttpContext ctx) =>
            HttpMethods.IsPost(ctx.Request.Method) &&
            AuthPostPaths.Any(p => ctx.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase));

        public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection("RateLimits:Auth").Get<Options>() ?? new Options();
            services.AddSingleton(options);
            services.AddRateLimiter(limiter =>
            {
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                {
                    if (!options.Enabled || !IsAuthPost(ctx))
                        return RateLimitPartition.GetNoLimiter("none");
                    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter("auth:" + ip, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.PermitLimit,
                        Window = TimeSpan.FromSeconds(options.WindowSeconds),
                        QueueLimit = 0,
                    });
                });
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = async (context, ct) =>
                {
                    var http = context.HttpContext;
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra)
                        ? (int)Math.Ceiling(ra.TotalSeconds) : options.WindowSeconds;
                    http.Response.Headers.RetryAfter = retryAfter.ToString();
                    http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    if (http.Request.Path.StartsWithSegments("/api"))
                    {
                        var problem = new ProblemDetails
                        {
                            Status = StatusCodes.Status429TooManyRequests,
                            Title = "Too many attempts",
                            Detail = $"Too many attempts from this address. Try again in {retryAfter} seconds.",
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
