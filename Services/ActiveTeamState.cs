using IndxCloudApi.Data;

namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Per-circuit holder of the signed-in user's teams and the one currently-active team.
    /// Shared by the nav switcher, the dashboard, and the Team page so "active team" is a single
    /// session-wide value. Scoped: one instance per Blazor circuit.
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

            // Keep the current active team if it still exists, else fall back to the first.
            var keep = Teams.FirstOrDefault(t => t.Team.Id == ActiveTeamId);
            if (keep.Team != null) SetActive(keep);
            else if (Teams.Count > 0) SetActive(Teams[0]);
            else { ActiveTeamId = null; ActiveTeamName = null; ActiveRole = null; }

            OnChange?.Invoke();
        }

        /// <summary>Switch the active team (must be one the user belongs to).</summary>
        public void Switch(Guid teamId)
        {
            var entry = Teams.FirstOrDefault(t => t.Team.Id == teamId);
            if (entry.Team != null)
            {
                SetActive(entry);
                OnChange?.Invoke();
            }
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
