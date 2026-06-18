using System.Text.Json;
using Microsoft.Extensions.Options;

namespace IndxCloudApi.Services;

internal class InstanceSettings
{
    public RegistrationMode RegistrationMode { get; set; } = RegistrationMode.Open;
    public List<string> AllowedDomains { get; set; } = [];
    public List<string> AllowedEmails { get; set; } = [];
    public bool RequireEmailVerification { get; set; } = false;
    public bool AllowUserSelfDeletion { get; set; } = true;
    public string InstanceName { get; set; } = "Indx";

    /// <summary>True once the first-run onboarding (admin account + team + instance settings)
    /// has completed. Until then, the app routes visitors into the setup flow. Gated on this
    /// flag rather than "are there users?" because OAuth creates the admin mid-flow.</summary>
    public bool SetupComplete { get; set; } = false;

    /// <summary>Bearer token for the Indx license portal. When set, overrides the
    /// Indx:LicenseToken app setting. Stored locally (settings.json) like other secrets.
    /// The portal URL itself is hardcoded (see LicenseBootstrapper.DefaultDownloadUrl).</summary>
    public string? LicenseToken { get; set; }

    /// <summary>App-owned subscription plan for Managed (Azure) deployments. Null until an
    /// in-app upgrade sets it, in which case ManagedEditionService falls back to the ARM-injected
    /// Indx:Plan default. Persisting it here (not config) is what lets a customer upgrade Free →
    /// Professional in place without redeploying. Ignored in SelfHost mode.</summary>
    public IndxPlan? Plan { get; set; }
}

internal class InstanceSettingsService
{
    private static readonly string SettingsPath = "./IndxData/settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IOptions<RegistrationOptions> _configFallback;
    private readonly ILogger<InstanceSettingsService> _logger;

    public InstanceSettingsService(
        IOptions<RegistrationOptions> configFallback,
        ILogger<InstanceSettingsService> logger)
    {
        _configFallback = configFallback;
        _logger = logger;
    }

    public InstanceSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<InstanceSettings>(json, JsonOptions);
                if (settings != null) return settings;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read {Path}, falling back to appsettings", SettingsPath);
            }
        }

        // Fall back to appsettings.json / env vars
        return new InstanceSettings
        {
            RegistrationMode = _configFallback.Value.Mode,
            AllowedDomains = _configFallback.Value.AllowedDomains ?? [],
            RequireEmailVerification = _configFallback.Value.RequireEmailVerification,
        };
    }

    public void Save(InstanceSettings settings)
    {
        Directory.CreateDirectory("./IndxData");
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
        _logger.LogInformation("Instance settings saved to {Path}", SettingsPath);
    }
}
