using IndxServer.Data;
using Microsoft.EntityFrameworkCore;

namespace IndxServer.Services
{
    /// <summary>
    /// Where a notification takes you when you open it (Nov 2026; until then the bell and the
    /// Notifications page only marked it read): the boost rules of the dataset whose rule ended,
    /// the keys page that holds the key, the dataset's webhooks, the release on the version page,
    /// the users or the monitor for an admin's notices, the support ticket. Read from what the row
    /// carries: a link in <see cref="Notification.Metadata"/> where it was made with one, else the
    /// type and its source id, so rows from before links still open somewhere.
    /// </summary>
    internal sealed class NotificationLinks(ApplicationDbContext db)
    {
        /// <summary>The link of each notification that has one, by id.</summary>
        public async Task<Dictionary<int, string>> ForAsync(IReadOnlyCollection<Notification> notifications)
        {
            var teamIds = new HashSet<Guid>();
            var keyIds = new HashSet<int>();
            foreach (var n in notifications)
            {
                if (n.Type == NotificationType.BoostRuleExpired && TeamOf(n.SourceId) is { } team) teamIds.Add(team);
                if (n.Type is NotificationType.ApiKeyExpired or NotificationType.ApiKeyExpiringSoon && KeyOf(n.SourceId) is { } key) keyIds.Add(key);
            }
            var keys = keyIds.Count == 0 ? [] : await db.ApiKeys.AsNoTracking().Where(k => keyIds.Contains(k.Id))
                .Select(k => new { k.Id, k.TeamId, k.UserId }).ToListAsync();
            foreach (var k in keys) if (k.UserId == null && k.TeamId is { } t) teamIds.Add(t);
            var teams = teamIds.Count == 0 ? [] : await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name);

            var keyTeam = keys.ToDictionary(k => k.Id, k => k.UserId == null ? k.TeamId : null);
            var links = new Dictionary<int, string>();
            foreach (var n in notifications)
            {
                var href = Link(n, teams, keyTeam);
                if (href != null) links[n.Id] = href;
            }
            return links;
        }

        private static string? Link(Notification n, Dictionary<Guid, string> teams, Dictionary<int, Guid?> keyTeam)
        {
            // A link the row was made with. "/datasets/{name}" was the boost rule job's, an address
            // that lost its team when datasets moved under teams: read from the source id instead.
            if (n.Metadata is { Length: > 1 } meta && meta[0] == '/' && !meta.StartsWith("/datasets/", StringComparison.Ordinal))
                return meta;
            return n.Type switch
            {
                NotificationType.BoostRuleExpired => BoostRules(n.SourceId, teams),
                NotificationType.ApiKeyExpired or NotificationType.ApiKeyExpiringSoon =>
                    KeyOf(n.SourceId) is { } key && keyTeam.TryGetValue(key, out var team) && team is { } t && teams.TryGetValue(t, out var name)
                        ? TeamRoutes.TeamKeys(name) : "/account/api-key",
                NotificationType.UserRegistered or NotificationType.UserConfirmedEmail => "/admin/users",
                NotificationType.BackupFailed or NotificationType.LowDiskSpace or NotificationType.RepeatedApiErrors => "/admin/monitor",
                NotificationType.UpdateAvailable or NotificationType.SecurityUpdate => "/admin/settings/version",
                _ => null,
            };
        }

        /// <summary>The boost rule job's source id is "{teamId}/{dataset}/{rule}/{date}".</summary>
        private static string? BoostRules(string? sourceId, Dictionary<Guid, string> teams)
        {
            var parts = sourceId?.Split('/');
            if (parts is not { Length: >= 2 } || !Guid.TryParse(parts[0], out var team) || !teams.TryGetValue(team, out var name)) return null;
            return $"{TeamRoutes.Dataset(name, parts[1])}?tab=boosts";
        }

        private static Guid? TeamOf(string? sourceId) =>
            sourceId?.Split('/') is { Length: >= 2 } p && Guid.TryParse(p[0], out var g) ? g : null;

        /// <summary>A key's notice has the key's id as its source, a team key's warning with ":days" after it.</summary>
        private static int? KeyOf(string? sourceId) =>
            sourceId?.Split(':')[0] is { } id && int.TryParse(id, out var key) ? key : null;
    }
}
