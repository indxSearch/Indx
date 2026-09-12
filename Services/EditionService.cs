namespace IndxServer.Services;

/// <summary>How this instance is deployed. Fixed at deploy time via Indx:Edition.</summary>
public enum IndxEdition
{
    /// <summary>Open-source self-host: every feature enabled, no tier gates.</summary>
    SelfHost,
    /// <summary>Azure Managed Application: features gated by the purchased plan.</summary>
    Managed
}

#pragma warning disable 1591 // self-describing enum members
/// <summary>Subscription plan for a Managed instance. App-owned and mutable at runtime so a
/// customer can upgrade in place (no redeploy). Ignored in SelfHost.</summary>
public enum IndxPlan
{
    Free,
    Professional,
    Enterprise
}

/// <summary>Individually gateable capabilities, mapped to plans by the edition service. Kept to
/// what is actually enforced today — add members as real gated features ship.</summary>
public enum EditionFeature
{
    /// <summary>More than one team and more than one user. Free Managed = a single team, single user.</summary>
    MultipleUsers,

    /// <summary>Time-planning on boost rules (ActiveFrom/ActiveUntil schedule window). Free Managed
    /// can create boost rules but not schedule them.</summary>
    BoostRuleScheduling
}
#pragma warning restore 1591

/// <summary>
/// Single source of truth for what this deployment is allowed to do. The same source ships two
/// ways — open-source self-host (everything on) and the Azure Managed Application (gated by the
/// purchased plan). Every feature gate asks this service via <see cref="IsEnabled"/> so the rules
/// live in one place. In Managed mode the plan is app-owned state (mutable at runtime) so a customer
/// can upgrade Free → Professional in place without redeploying.
/// </summary>
public interface IEditionService
{
    /// <summary>How this instance is deployed (fixed at deploy time).</summary>
    IndxEdition Edition { get; }

    /// <summary>The current subscription plan (Managed only; informational in SelfHost).</summary>
    IndxPlan Plan { get; }

    /// <summary>True if the given feature is available under the current edition/plan.</summary>
    bool IsEnabled(EditionFeature feature);

    /// <summary>Whether the License page and license fetch/refresh apply (SelfHost only).</summary>
    bool LicensingVisible { get; }

    /// <summary>Reads the deployment edition from configuration. Static so startup code can branch
    /// before the DI container is built (which implementation to register, whether to run the
    /// license job). Defaults to SelfHost so self-hosters need no configuration.</summary>
    static IndxEdition ReadEdition(IConfiguration configuration) =>
        string.Equals(configuration["Indx:Edition"], "Managed", StringComparison.OrdinalIgnoreCase)
            ? IndxEdition.Managed
            : IndxEdition.SelfHost;
}

/// <summary>Self-host edition: everything enabled, no gates. See <see cref="IEditionService"/>.</summary>
internal sealed class SelfHostEditionService : IEditionService
{
    /// <inheritdoc />
    public IndxEdition Edition => IndxEdition.SelfHost;

    /// <inheritdoc />
    public IndxPlan Plan => IndxPlan.Professional; // informational; all features are on regardless

    /// <inheritdoc />
    public bool IsEnabled(EditionFeature feature) => true;

    /// <inheritdoc />
    public bool LicensingVisible => true;
}

/// <summary>
/// Managed (Azure) edition: features gated by the current plan. The plan is resolved per call from
/// app-owned state (<see cref="InstanceSettings.Plan"/>), falling back to the ARM-injected Indx:Plan
/// default, so an in-app upgrade takes effect immediately without a redeploy.
/// </summary>
internal sealed class ManagedEditionService : IEditionService
{
    private readonly InstanceSettingsService _settings;
    private readonly IndxPlan _defaultPlan;

    public ManagedEditionService(InstanceSettingsService settings, IConfiguration configuration)
    {
        _settings = settings;
        _defaultPlan = ParsePlan(configuration["Indx:Plan"]) ?? IndxPlan.Free;
    }

    /// <inheritdoc />
    public IndxEdition Edition => IndxEdition.Managed;

    /// <inheritdoc />
    public IndxPlan Plan => _settings.Load().Plan ?? _defaultPlan;

    /// <inheritdoc />
    public bool LicensingVisible => false;

    /// <inheritdoc />
    public bool IsEnabled(EditionFeature feature) => Plan switch
    {
        IndxPlan.Free => false,                                   // Free gates every paid feature
        IndxPlan.Professional => ProfessionalFeatures.Contains(feature),
        IndxPlan.Enterprise => true,                             // Enterprise enables everything
        _ => false
    };

    // Professional (and Enterprise) unlock team collaboration and boost-rule scheduling. Free gets
    // a single team / single user and unscheduled boost rules.
    private static readonly HashSet<EditionFeature> ProfessionalFeatures =
    [
        EditionFeature.MultipleUsers,
        EditionFeature.BoostRuleScheduling,
    ];

    private static IndxPlan? ParsePlan(string? value) =>
        Enum.TryParse<IndxPlan>(value, ignoreCase: true, out var plan) ? plan : null;
}
