using System.Security.Claims;
using IndxServer.Data;

namespace IndxServer.Services.Support
{
    /// <summary>
    /// The instance's view of its support tickets: the last list the Portal gave, so the side
    /// column's Support link can show the unread replies and the background check knows whether
    /// anything is open. One per instance: the tickets are the customer's, the same for every
    /// console user who may see them.
    /// </summary>
    internal sealed class SupportState(SupportClient client)
    {
        private readonly object _gate = new();

        public SupportList? Last { get; private set; }
        public SupportProblem LastProblem { get; private set; }
        public DateTime? CheckedUtc { get; private set; }
        public event Action? Changed;

        /// <summary>Called with every list the Portal gives, whoever asked for it: SupportPollJob tells
        /// the new replies in it, so a reply the console notices on a page rings the bell as well.</summary>
        public Func<SupportList, Task>? OnRefreshed { get; set; }

        public int Unread => Last?.Tickets.Count(t => t.Unread) ?? 0;
        /// <summary>Whether a ticket is not closed, or nothing is known yet: then the check runs.</summary>
        public bool AnyOpen => Last is null || Last.Tickets.Any(t => t.Status != "closed");

        /// <summary>Asks the Portal for the list and keeps it.</summary>
        public async Task<SupportResult<SupportList>> RefreshAsync(CancellationToken ct = default)
        {
            var result = await client.ListAsync(ct);
            lock (_gate)
            {
                if (result.Ok) Last = result.Value;
                LastProblem = result.Problem;
                CheckedUtc = DateTime.UtcNow;
            }
            Changed?.Invoke();
            if (result.Ok && result.Value is { } list && OnRefreshed is { } tell) await tell(list);
            return result;
        }

        /// <summary>A thread was read or written in the console: its unread mark goes at once, not
        /// at the next check.</summary>
        public void Seen(int ticketId)
        {
            lock (_gate)
            {
                if (Last is not { } last) return;
                Last = last with { Tickets = last.Tickets.Select(t => t.Id == ticketId ? t with { Unread = false } : t).ToList() };
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Who may write to Indx and read the answers: the instance's admins, and the
    /// editors and admins of any team. A viewer may not, nor anyone while support is off.</summary>
    public static class SupportAccess
    {
        public static bool CanUse(ClaimsPrincipal user, IEnumerable<(Team Team, string Role)> teams) =>
            user.Identity?.IsAuthenticated == true
            && (user.IsInRole("Admin") || teams.Any(t => TeamRoles.CanWrite(t.Role)));
    }
}
