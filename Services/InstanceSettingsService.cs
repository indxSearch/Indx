using System.Text.Json;
using Microsoft.Extensions.Options;

namespace IndxServer.Services;

internal sealed class ObservrSubscriptionMemo
{
    public string TokenHash { get; set; } = "";
    public bool Included { get; set; }
    public string Tier { get; set; } = "";
    public string LicensedTo { get; set; } = "";
    public string? Reason { get; set; }
    public string? Access { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CheckedUtc { get; set; }
}

internal class InstanceSettings
{
    public RegistrationMode RegistrationMode { get; set; } = RegistrationMode.Open;
    public List<string> AllowedDomains { get; set; } = [];
    public List<string> AllowedEmails { get; set; } = [];

    /// <summary>The web origins whose browser apps may call the API (CORS), set on the instance
    /// settings page. Null until an admin saves a list: until then Cors:AllowedOrigins applies.
    /// See <see cref="InstanceCorsPolicyProvider"/>.</summary>
    public List<string>? AllowedOrigins { get; set; }
    public bool RequireEmailVerification { get; set; } = false;
    public bool AllowUserSelfDeletion { get; set; } = true;
    public string InstanceName { get; set; } = "Indx";

    /// <summary>A stable, random id for this installation, made the first time it is needed and
    /// kept in settings.json. It tells Observr which installation an ask came from, so one licence
    /// token used by several installations is seen as such. Not a secret, not shown to users.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Master switch for the MCP server (/mcp). On by default (frictionless self-host);
    /// admins can disable the agent-facing surface. Enforced at runtime, so it takes effect without
    /// a restart.</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>Observr on this instance (Notes/Anders/observr-plan.md): an admin activates it once;
    /// off, nothing of Observr is shown and nothing is sent, whatever the subscription. On, each team
    /// chooses per dataset what Observr does on its own Observr page.</summary>
    public bool ObservrEnabled { get; set; } = false;

    /// <summary>Shows Search or ask on this instance (Notes/search-or-ask-server.md). Experimental,
    /// so off by default: while off, its dataset tab, its statistics and its routes are absent, and
    /// nothing is sent to a model. On, each dataset still switches it on in its own tab, and the
    /// instance needs a model key (Indx:SearchOrAsk:ApiKey).</summary>
    public bool SearchOrAskShown { get; set; } = false;

    /// <summary>The OpenRouter key Search or ask uses, set in the console (Admin, Settings,
    /// Experimental), so a self-hosted instance needs no server configuration for it. A key in the
    /// configuration (Indx:SearchOrAsk:ApiKey, or its environment variable) wins over this one.</summary>
    public string? SearchOrAskApiKey { get; set; }

    /// <summary>The provider choices beside the key (<see cref="SearchOrAsk.SearchOrAskProviderSettings"/>):
    /// the answer provider (openrouter, openai, anthropic), the OpenAI-compatible base URL, EU
    /// routing on OpenRouter, the judge's door (openrouter, typesafe) and its own key when it has
    /// one, and model names where the provider's defaults are not wanted. Configuration wins.</summary>
    public string? SearchOrAskProvider { get; set; }
    public string? SearchOrAskBaseUrl { get; set; }
    public bool SearchOrAskEuRouting { get; set; }
    public string? SearchOrAskJudgeProvider { get; set; }
    public string? SearchOrAskJudgeApiKey { get; set; }
    public string? SearchOrAskAnswerModel { get; set; }
    public string? SearchOrAskFastAnswerModel { get; set; }
    public string? SearchOrAskFilterModel { get; set; }

    /// <summary>True once the first-run onboarding (admin account + team + instance settings)
    /// has completed. Until then, the app routes visitors into the setup flow. Gated on this
    /// flag rather than "are there users?" because OAuth creates the admin mid-flow.</summary>
    public bool SetupComplete { get; set; } = false;

    /// <summary>Bearer token for the Indx license portal. When set, overrides the
    /// Indx:LicenseToken app setting. Stored locally (settings.json) like other secrets.
    /// The portal URL itself is hardcoded (see LicenseBootstrapper.DefaultDownloadUrl).</summary>
    public string? LicenseToken { get; set; }

    /// <summary>The licence portal's last answer about Observr, kept so a restart during a portal
    /// outage keeps Observr as a running instance would (ObservrSubscription): the answer, when it
    /// came, and a hash of the token it was for, so a new token asks afresh.</summary>
    public ObservrSubscriptionMemo? ObservrSubscription { get; set; }

    /// <summary>App-owned subscription plan for Managed (Azure) deployments. Null until an
    /// in-app upgrade sets it, in which case ManagedEditionService falls back to the ARM-injected
    /// Indx:Plan default. Persisting it here (not config) is what lets a customer upgrade Free →
    /// Professional in place without redeploying. Ignored in SelfHost mode.</summary>
    public IndxPlan? Plan { get; set; }
}

internal class InstanceSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IOptions<RegistrationOptions> _configFallback;
    private readonly ILogger<InstanceSettingsService> _logger;

    /// <summary>Directory holding settings.json. <c>Indx:DataDirectory</c> if configured, else
    /// <c>{ContentRoot}/IndxData</c> — the same anchor as jwt.key, so the file does not move
    /// with the process working directory.</summary>
    public string DataDirectory { get; }
    public string SettingsPath { get; }

    public InstanceSettingsService(
        IOptions<RegistrationOptions> configFallback,
        ILogger<InstanceSettingsService> logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _configFallback = configFallback;
        _logger = logger;
        var configured = configuration["Indx:DataDirectory"];
        DataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "IndxData")
            : Path.GetFullPath(configured);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
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

    /// <summary>Raised by every save, so what is derived from the settings (the CORS policy) knows
    /// to derive again.</summary>
    public static int Version => _version;
    private static int _version;

    public void Save(InstanceSettings settings)
    {
        Interlocked.Increment(ref _version);
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
        _logger.LogInformation("Instance settings saved to {Path}", SettingsPath);
    }
}
