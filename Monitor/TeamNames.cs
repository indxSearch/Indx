using IndxServer.Data;
using Microsoft.EntityFrameworkCore;

namespace IndxServer.Monitor
{
    /// <summary>
    /// Team ids to team names, cached.
    ///
    /// <para>A dataset knows its owner as a team id, because that is what the storage layer holds
    /// (<c>DataSet.UserName</c>) — and that is in <c>indx.db</c>, while the name is a row in
    /// <c>identity.db</c>. Joining them means a second database, so the monitor reads it rarely
    /// and remembers: teams are renamed about as often as they are created, and a GUID in a column
    /// helps nobody.</para>
    ///
    /// <para>A miss refreshes sooner than the timer would, so a team created while the monitor is
    /// running gets its name within a tick or two rather than within half a minute.</para>
    /// </summary>
    internal sealed class TeamNames(IServiceScopeFactory scopes)
    {
        private static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MissRetry = TimeSpan.FromSeconds(2);

        private Dictionary<string, string> _names = [];
        private DateTimeOffset _readAt = DateTimeOffset.MinValue;

        /// <summary>Refreshes if due, then resolves. Called once per tick with every id the
        /// snapshot mentions, so one read serves the whole table.</summary>
        internal IReadOnlyDictionary<string, string> Resolve(IEnumerable<string> teamIds, DateTimeOffset now)
        {
            bool stale = now - _readAt > Stale;
            bool missing = teamIds.Any(id => !_names.ContainsKey(id)) && now - _readAt > MissRetry;

            if (stale || missing)
                Read(now);

            return _names;
        }

        private void Read(DateTimeOffset now)
        {
            // Always move the clock, even on failure: a database that cannot be read must not turn
            // into a query per tick.
            _readAt = now;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                _names = db.Teams.AsNoTracking()
                    .Select(t => new { t.Id, t.Name })
                    .ToDictionary(t => t.Id.ToString(), t => t.Name);
            }
            catch
            {
                // The monitor shows ids rather than nothing, and never becomes the reason a
                // request path sees a failure.
            }
        }
    }
}
