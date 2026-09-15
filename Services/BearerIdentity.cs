using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

using System.Security.Claims;

namespace IndxServer.Services
{
    /// <summary>
    /// Resolves the bearer token once, early, and hands the result to everything downstream
    /// through <see cref="HttpContext.Items"/>.
    ///
    /// <para>It exists because the default authentication scheme is the Identity cookie, so
    /// <c>UseAuthentication</c> does not populate <c>ctx.User</c> from a bearer token — JWT bearer
    /// is only triggered lazily by <c>[Authorize]</c>, inside the authorization stage. Anything
    /// that needs to know who is calling *before* that point has to authenticate the scheme
    /// itself.</para>
    ///
    /// <para>Two things did, independently: the rate limiter (which ran before authentication and
    /// therefore read the token's jti unvalidated) and the password-change gate (which called
    /// <c>AuthenticateAsync</c> a second time). Doing it here once means a request's token is
    /// parsed and its signature checked exactly once.</para>
    ///
    /// <para><b>The security property that matters:</b> a token that fails validation leaves no
    /// principal here, so the rate limiter treats the request as unauthenticated and bounds it by
    /// IP. Without that, <c>Authorization: Bearer anything</c> was enough to fall out of the
    /// anonymous window and into a per-token bucket nobody could exhaust — a one-header bypass of
    /// the limit, measured and confirmed before this was written.</para>
    /// </summary>
    public static class BearerIdentity
    {
        private const string PrincipalKey = "IndxServer.BearerIdentity.Principal";
        private const string TokenKey = "IndxServer.BearerIdentity.Token";
        private const string ResolvedKey = "IndxServer.BearerIdentity.Resolved";

        /// <summary>The raw bearer token, or null when the request carries no bearer header.
        /// Present whether or not the token turned out to be valid.</summary>
        public static string? Token(HttpContext ctx) => ctx.Items[TokenKey] as string;

        /// <summary>The principal from a bearer token that passed validation, else null. Null
        /// means "not an authenticated API caller" — no token, a forged one, an expired one, or
        /// one whose key was revoked.</summary>
        public static ClaimsPrincipal? Principal(HttpContext ctx) => ctx.Items[PrincipalKey] as ClaimsPrincipal;

        /// <summary>True once the middleware has run for this request. Guards callers against
        /// silently treating "middleware not wired up" as "not authenticated".</summary>
        public static bool Resolved(HttpContext ctx) => ctx.Items.ContainsKey(ResolvedKey);

        /// <summary>The validated token's jti claim, or null. Only ever non-null for a token that
        /// passed validation, which is what makes it safe as a rate-limit partition key.</summary>
        public static string? ValidatedJti(HttpContext ctx) =>
            Principal(ctx)?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;

        private static string? BearerHeader(HttpContext ctx)
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && h.Length > 7 ? h[7..].Trim() : null;
        }

        /// <summary>Must be registered after <c>UseRouting</c> and before <c>UseRateLimiter</c>.</summary>
        public static IApplicationBuilder UseBearerIdentity(this IApplicationBuilder app) =>
            app.Use(async (ctx, next) =>
            {
                ctx.Items[ResolvedKey] = true;
                var token = BearerHeader(ctx);
                if (token != null)
                {
                    ctx.Items[TokenKey] = token;
                    try
                    {
                        var result = await ctx.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                        if (result.Succeeded && result.Principal != null)
                            ctx.Items[PrincipalKey] = result.Principal;
                    }
                    catch
                    {
                        // A malformed token is ordinary traffic, not an error: leaving the
                        // principal unset is the whole answer, and the request gets its 401
                        // from the authorization stage as before.
                    }
                }
                await next();
            });
    }
}
