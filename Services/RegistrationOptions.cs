namespace IndxServer.Services;

/// <summary>
/// Configuration options for user registration restrictions
/// </summary>
public class RegistrationOptions
{
    /// <summary>
    /// Registration mode - controls who can register
    /// </summary>
    public RegistrationMode Mode { get; set; } = RegistrationMode.Open;

    /// <summary>
    /// List of allowed email domains when Mode is EmailDomain
    /// Example: ["yourcompany.com", "partner.com"]
    /// </summary>
    public List<string> AllowedDomains { get; set; } = new();

    /// <summary>
    /// When true, users must confirm their email before signing in.
    /// </summary>
    public bool RequireEmailVerification { get; set; } = false;
}

/// <summary>
/// Registration mode enum
/// </summary>
public enum RegistrationMode
{
    /// <summary>
    /// Anyone can register - no restrictions (default)
    /// </summary>
    Open,

    /// <summary>
    /// Only email addresses from allowed domains can register
    /// </summary>
    EmailDomain,

    /// <summary>
    /// Registration is closed - only admins can create accounts
    /// </summary>
    Closed,

    /// <summary>
    /// Only specific email addresses added to the invite list can register
    /// </summary>
    Invite
}
