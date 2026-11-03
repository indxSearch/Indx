using Microsoft.AspNetCore.Cors.Infrastructure;

namespace IndxServer.Services;

/// <summary>
/// The CORS policy, from the origins an admin set on the instance settings page, or until one
/// has, from <c>Cors:AllowedOrigins</c> (comma-separated; the Managed App wizard sets it). Read
/// per request, so a saved change applies without a restart, which matters on a Managed
/// instance: its customer cannot change app settings or App Service's own CORS list.
///
/// Outside Production any origin is allowed, for local front-end work. In Production with no
/// origins, none are: a browser app elsewhere cannot call the API, while same-origin use (the
/// console, Swagger) and server-to-server clients are unaffected.
/// </summary>
internal sealed class InstanceCorsPolicyProvider(
    InstanceSettingsService settings, IConfiguration configuration, IHostEnvironment environment) : ICorsPolicyProvider
{
    /// <summary>The policy name UseCors asks for.</summary>
    public const string PolicyName = "NewPolicy";

    // Indx-Query-Id must be EXPOSED, not merely sent: a cross-origin client (the storefront) can
    // only read CORS-safelisted response headers, and the statistics select/convert events
    // reference exactly this header's value.
    private static readonly string[] Exposed = ["Indx-Query-Id", "Indx-Ask-Id", QueryParameterResolution.ResponseHeader];

    private readonly object _lock = new();
    private (int Version, CorsPolicy Policy)? _cached;

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var version = InstanceSettingsService.Version;
        lock (_lock)
        {
            if (_cached is not { } c || c.Version != version)
                _cached = (version, Build(Origins()));
            return Task.FromResult<CorsPolicy?>(_cached.Value.Policy);
        }
    }

    /// <summary>The origins in force: the instance's own list once an admin has saved one, else
    /// the configuration's.</summary>
    public IReadOnlyList<string> Origins() =>
        settings.Load().AllowedOrigins ?? FromConfiguration(configuration);

    /// <summary><c>Cors:AllowedOrigins</c>, normalized, the ones that are not origins left out.</summary>
    public static List<string> FromConfiguration(IConfiguration configuration) =>
        (configuration["Cors:AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).OfType<string>().Distinct().ToList();

    private CorsPolicy Build(IReadOnlyList<string> origins)
    {
        var policy = new CorsPolicyBuilder().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders(Exposed);
        if (!environment.IsProduction()) policy.AllowAnyOrigin();
        else policy.WithOrigins([.. origins]).SetIsOriginAllowedToAllowWildcardSubdomains();
        return policy.Build();
    }

    /// <summary>
    /// An origin as a browser sends it, <c>https://shop.example.com</c> (a port when it is not the
    /// scheme's), from what an admin typed: case, a trailing slash or a path do not matter. A
    /// wildcard subdomain (<c>https://*.example.com</c>) is kept as one. Null for anything that is
    /// not an http(s) origin, so a typo is refused instead of saved.
    /// </summary>
    public static string? Normalize(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var wildcard = t.Contains("://*.", StringComparison.Ordinal);
        var parse = wildcard ? t.Replace("://*.", "://wildcard.", StringComparison.Ordinal) : t;
        if (!Uri.TryCreate(parse, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (wildcard) host = "*" + host["wildcard".Length..];
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }
}
