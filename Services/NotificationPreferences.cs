using System.Text.Json;
using IndxServer.Data;

namespace IndxServer.Services;

/// <summary>Per-channel preference for one notification type. Null = use the default.</summary>
internal sealed class NotificationChannelPref
{
    public bool? Email { get; set; }
    public bool? InApp { get; set; }
}

/// <summary>Display metadata for a notification type shown in the preferences UI.</summary>
internal sealed record NotificationTypeInfo(NotificationType Type, string Label, string Description, bool AdminOnly);

/// <summary>
/// Per-user notification channel preferences, keyed by <see cref="NotificationType"/> and
/// serialized to <see cref="ApplicationUser.NotificationPreferences"/> (JSON). Personal only —
/// each user controls their own channels; the events that fire are fixed in code.
///
/// Missing entries fall back to defaults: in-app ON; email per the user's legacy
/// <see cref="ApplicationUser.EmailNotificationsEnabled"/> flag (default ON), so existing users
/// keep their prior behaviour until they set an explicit per-type preference.
/// </summary>
internal static class NotificationPreferences
{
    /// <summary>Notification types surfaced in the preferences UI — only those that actually fire
    /// today. <see cref="NotificationTypeInfo.AdminOnly"/> types are targeted at the Admin role and
    /// hidden from regular users (who never receive them).</summary>
    public static readonly IReadOnlyList<NotificationTypeInfo> Catalog =
    [
        new(NotificationType.UserRegistered,     "New user registered",   "A new account is created on this instance.",          AdminOnly: true),
        new(NotificationType.ApiKeyExpiringSoon, "API key expiring soon", "One of your API keys is within 14 days of expiring.", AdminOnly: false),
        new(NotificationType.ApiKeyExpired,      "API key expired",       "One of your API keys has expired.",                   AdminOnly: false),
        new(NotificationType.BoostRuleExpired,   "Boost rule expired",    "A scheduled boost rule on one of your team's datasets has passed its end date and no longer applies.", AdminOnly: false),
    ];

    public static Dictionary<NotificationType, NotificationChannelPref> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<NotificationType, NotificationChannelPref>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static string Serialize(IDictionary<NotificationType, NotificationChannelPref> prefs) =>
        JsonSerializer.Serialize(prefs);

    /// <summary>Effective in-app setting for a type (default: on).</summary>
    public static bool InAppEnabled(ApplicationUser user, NotificationType type)
    {
        var prefs = Parse(user.NotificationPreferences);
        return !(prefs.TryGetValue(type, out var p) && p.InApp == false);
    }

    /// <summary>Effective email setting for a type (default: the legacy global flag).</summary>
    public static bool EmailEnabled(ApplicationUser user, NotificationType type)
    {
        var prefs = Parse(user.NotificationPreferences);
        if (prefs.TryGetValue(type, out var p) && p.Email.HasValue) return p.Email.Value;
        return user.EmailNotificationsEnabled;
    }

    /// <summary>Types the user has switched OFF for in-app display (used to filter their feed).</summary>
    public static List<NotificationType> DisabledInApp(ApplicationUser user) =>
        Parse(user.NotificationPreferences)
            .Where(kv => kv.Value.InApp == false)
            .Select(kv => kv.Key)
            .ToList();
}
