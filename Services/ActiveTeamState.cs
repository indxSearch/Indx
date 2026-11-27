using IndxServer.Data;

namespace IndxServer.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Per-circuit holder of the signed-in user's teams and the one currently-active team.
    /// Shared by the breadcrumb switcher, the team page and the dataset page so "active team" is
    /// a single session-wide value. Scoped: one instance per Blazor circuit.
    ///
    /// The active team is remembered on the user (<see cref="ApplicationUser.LastTeamId"/>):
    /// the first load prefers it, and every switch persists it, so <c>/</c> can land on the team
    /// the user was in last time. The routes are the source of truth while navigating — a page
    /// under <c>/teams/{name}</c> calls <see cref="SwitchAsync"/> with the team from its URL.
    /// </summary>
    public sealed class ActiveTeamState(TeamService teams)
    {
        private string? _userId;

        public IReadOnlyList<(Team Team, string Role)> Teams { get; private set; } = new List<(Team, string)>();
        public Guid? ActiveTeamId { get; private set; }
        public string? ActiveTeamName { get; private set; }
        public string? ActiveRole { get; private set; }

        public bool HasTeam => ActiveTeamId != null;

        /// <summary>Raised whenever the team list or the active team changes.</summary>
        public event Action? OnChange;

        /// <summary>Load the user's teams once. Safe to call repeatedly; only re-loads for a new user.</summary>
        public async Task EnsureInitializedAsync(string userId)
        {
            if (_userId == userId && Teams.Count > 0) return;
            _userId = userId;
            await ReloadAsync();
        }

        /// <summary>Re-read teams from the DB (after create/rename/delete or membership changes).</summary>
        public async Task ReloadAsync()
        {
            if (_userId == null) return;
            Teams = await teams.GetTeamsForUserAsync(_userId);

            // Keep the current active team if it still exists; otherwise the remembered one,
            // if the user is still a member; otherwise the first.
            var keep = Teams.FirstOrDefault(t => t.Team.Id == ActiveTeamId);
            if (keep.Team == null)
            {
                var last = await teams.GetLastTeamIdAsync(_userId);
                keep = Teams.FirstOrDefault(t => t.Team.Id == last);
            }
            if (keep.Team != null) SetActive(keep);
            else if (Teams.Count > 0) SetActive(Teams[0]);
            else { ActiveTeamId = null; ActiveTeamName = null; ActiveRole = null; }

            OnChange?.Invoke();
        }

        /// <summary>The team by URL name, or null when the user is not a member of it.</summary>
        public (Team Team, string Role)? Find(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var entry = Teams.FirstOrDefault(t => string.Equals(t.Team.Name, name, StringComparison.OrdinalIgnoreCase));
            return entry.Team == null ? null : entry;
        }

        /// <summary>Switch the active team (must be one the user belongs to) and remember it on
        /// the user. Returns false when the team is not one of the user's.</summary>
        public async Task<bool> SwitchAsync(Guid teamId)
        {
            var entry = Teams.FirstOrDefault(t => t.Team.Id == teamId);
            if (entry.Team == null) return false;
            var changed = ActiveTeamId != entry.Team.Id;
            SetActive(entry);
            if (_userId != null) await teams.SetLastTeamAsync(_userId, teamId);
            if (changed) OnChange?.Invoke();
            return true;
        }

        private void SetActive((Team Team, string Role) entry)
        {
            ActiveTeamId = entry.Team.Id;
            ActiveTeamName = entry.Team.Name;
            ActiveRole = entry.Role;
        }
    }
#pragma warning restore 1591
}
