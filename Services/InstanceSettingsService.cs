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

    /// <summary>Bearer token for the Indx license portal. When set, overrides the
    /// Indx:LicenseToken app setting. Stored locally (settings.json) like other secrets.
    /// The portal URL itself is hardcoded (see LicenseBootstrapper.DefaultDownloadUrl).</summary>
    public string? LicenseToken { get; set; }
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
