namespace IndxCloudApi.Services;

/// <summary>How this instance is deployed. Selected at runtime via Indx:DeploymentMode so the same
/// source code / build artifact serves both audiences.</summary>
public enum DeploymentMode
{
    /// <summary>Open-source self-host: full features, licensing handled by the engine/license file.</summary>
    SelfHost,
    /// <summary>Azure Managed Application: licensing is irrelevant; subscription-tier guardrails apply.</summary>
    ManagedApp
}

/// <summary>Subscription tier for a ManagedApp instance (one instance == one customer == one plan).
/// Ignored in SelfHost mode.</summary>
public enum PlanTier
{
    /// <summary>Free tier: capped teams/members.</summary>
    Free,
    /// <summary>Paid tier: unlimited (or configured) limits.</summary>
    Pro
}

/// <summary>
/// Single source of truth for what the current deployment is allowed to do. The same source ships
/// two ways — open-source self-host and the Azure Managed Application — and every guardrail asks
/// this policy instead of hard-coding a limit, so the rules live in one auditable place. Resolved
/// once from configuration at startup (Indx:DeploymentMode / Indx:Plan / Indx:Limits:*).
/// </summary>
public interface IDeploymentPolicy
{
    /// <summary>How this instance is deployed (self-host vs Azure Managed Application).</summary>
    DeploymentMode Mode { get; }

    /// <summary>The subscription tier. Only meaningful when <see cref="Mode"/> is ManagedApp.</summary>
    PlanTier Plan { get; }

    /// <summary>Show the License page and run the license fetch/refresh. False in ManagedApp.</summary>
    bool LicensingVisible { get; }

    /// <summary>Allow self-service account registration. False in ManagedApp (admin-provisioned).</summary>
    bool AllowSelfRegistration { get; }

    /// <summary>Max teams in the instance, or -1 for unlimited.</summary>
    int MaxTeams { get; }

    /// <summary>Max members per team including the owner, or -1 for unlimited.</summary>
    int MaxMembersPerTeam { get; }

    /// <summary>True if another team may be created given the current instance team count.</summary>
    bool CanCreateTeam(int currentTeamCount);

    /// <summary>True if another member may be added given the team's current member count.</summary>
    bool CanAddMember(int currentMemberCount);

    /// <summary>Friendly suffix appended to limit errors (e.g. an upgrade hint), or "".</summary>
    string UpgradeHint { get; }
}

/// <inheritdoc />
#pragma warning disable 1591 // members documented on IDeploymentPolicy
public sealed class DeploymentPolicy : IDeploymentPolicy
{
    /// <summary>Sentinel for "no limit".</summary>
    public const int Unlimited = -1;

    public DeploymentMode Mode { get; }
    public PlanTier Plan { get; }
    public bool LicensingVisible { get; }
    public bool AllowSelfRegistration { get; }
    public int MaxTeams { get; }
    public int MaxMembersPerTeam { get; }
    public string UpgradeHint { get; }

    public DeploymentPolicy(IConfiguration configuration)
    {
        Mode = ReadMode(configuration);
        Plan = ReadPlan(configuration);

        if (Mode == DeploymentMode.SelfHost)
        {
            // Open-source self-host: the engine + license file govern capacity; the app imposes
            // no team/member limits and exposes the License page.
            LicensingVisible = true;
            AllowSelfRegistration = ReadBool(configuration, "Indx:AllowSelfRegistration", true);
            MaxTeams = Unlimited;
            MaxMembersPerTeam = Unlimited;
            UpgradeHint = "";
        }
        else
        {
            // Azure Managed Application: licensing is irrelevant; guardrails come from the plan.
            // Limits default by tier but can be overridden via Indx:Limits:* config.
            var free = Plan == PlanTier.Free;
            LicensingVisible = false;
            AllowSelfRegistration = ReadBool(configuration, "Indx:AllowSelfRegistration", false);
            MaxTeams = ReadLimit(configuration, "Indx:Limits:MaxTeams", free ? 1 : Unlimited);
            MaxMembersPerTeam = ReadLimit(configuration, "Indx:Limits:MaxMembersPerTeam", free ? 1 : Unlimited);
            UpgradeHint = free ? " Upgrade to Pro to add more." : "";
        }
    }

    public bool CanCreateTeam(int currentTeamCount) => MaxTeams < 0 || currentTeamCount < MaxTeams;
    public bool CanAddMember(int currentMemberCount) => MaxMembersPerTeam < 0 || currentMemberCount < MaxMembersPerTeam;

    /// <summary>Reads the deployment mode from configuration. Exposed so startup code can branch
    /// before the DI container is built (e.g. to skip the license background job).</summary>
    public static DeploymentMode ReadMode(IConfiguration configuration) =>
        string.Equals(configuration["Indx:DeploymentMode"], "ManagedApp", StringComparison.OrdinalIgnoreCase)
            ? DeploymentMode.ManagedApp
            : DeploymentMode.SelfHost;

    private static PlanTier ReadPlan(IConfiguration configuration) =>
        string.Equals(configuration["Indx:Plan"], "Pro", StringComparison.OrdinalIgnoreCase)
            ? PlanTier.Pro
            : PlanTier.Free;

    private static int ReadLimit(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var v) ? v : fallback;

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback) =>
        bool.TryParse(configuration[key], out var v) ? v : fallback;
}
#pragma warning restore 1591
