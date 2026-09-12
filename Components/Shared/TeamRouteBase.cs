using System.Security.Claims;

using IndxServer.Data;
using IndxServer.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace IndxServer.Components.Shared
{
    /// <summary>
    /// Base for the console page. Resolves the team named in the URL against the signed-in
    /// user's memberships, makes it the active (and remembered) team, and bounces anything
    /// else: unauthenticated → login, onboarding unfinished → setup, not a member (or no such
    /// team) → the remembered team. With no team in the URL (<c>/</c>, the legacy routes) it
    /// forwards to the remembered team via <see cref="ForwardToActiveTeam"/>, or reports
    /// <see cref="NoTeam"/> when the user belongs to none.
    /// </summary>
    public abstract class TeamRouteBase : ComponentBase, IDisposable
    {
        [Inject] protected NavigationManager NavigationManager { get; set; } = default!;
        [Inject] protected AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
        [Inject] internal InstanceSettingsService InstanceSettingsService { get; set; } = default!;
        [Inject] protected ActiveTeamState ActiveTeam { get; set; } = default!;

        [Parameter] public string? TeamName { get; set; }

        protected string UserId { get; private set; } = "";
        protected Team? Team { get; private set; }
        protected string? Role { get; private set; }
        protected string TeamId => Team?.Id.ToString() ?? "";
        protected bool TeamResolved => Team != null;
        /// <summary>The user belongs to no team, so there is nothing to land on.</summary>
        protected bool NoTeam { get; private set; }

        private string? _resolvedFor;
        private bool _resolving;

        protected override async Task OnInitializedAsync()
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;
            if (user.Identity == null || !user.Identity.IsAuthenticated)
            {
                NavigationManager.NavigateTo("/account/login", forceLoad: true);
                return;
            }
            if (!InstanceSettingsService.Load().SetupComplete)
            {
                NavigationManager.NavigateTo("/account/setup/configure", forceLoad: true);
                return;
            }
            UserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            if (UserId.Length == 0) { NoTeam = true; return; }
            await ActiveTeam.EnsureInitializedAsync(UserId);
            ActiveTeam.OnChange += HandleActiveTeamChanged;
            await ResolveTeamAsync();
        }

        // The same instance is reused when only the route changes (switcher, links,
        // back/forward), so re-resolve on every parameter set.
        protected override async Task OnParametersSetAsync()
        {
            if (UserId.Length > 0 && _resolvedFor != TeamName) await ResolveTeamAsync();
        }

        private async Task ResolveTeamAsync()
        {
            _resolvedFor = TeamName;
            if (string.IsNullOrEmpty(TeamName))
            {
                if (ActiveTeam.ActiveTeamName == null) { NoTeam = true; return; }
                NavigationManager.NavigateTo(ForwardToActiveTeam(ActiveTeam.ActiveTeamName), replace: true);
                return;
            }
            var entry = ActiveTeam.Find(TeamName);
            if (entry == null)
            {
                Team = null;
                Role = null;
                NavigationManager.NavigateTo("/", replace: true);
                return;
            }
            var wasTeam = Team?.Id;
            Team = entry.Value.Team;
            Role = entry.Value.Role;
            // SwitchAsync raises OnChange; that handler must not run its own load concurrently
            // with OnTeamResolvedAsync on the same scoped DbContext.
            _resolving = true;
            try
            {
                await ActiveTeam.SwitchAsync(Team.Id);
                if (wasTeam != Team.Id) await OnTeamResolvedAsync();
            }
            finally { _resolving = false; }
        }

        /// <summary>Where a team-less URL should land, given the active team's name.</summary>
        protected virtual string ForwardToActiveTeam(string activeTeamName) => TeamRoutes.Team(activeTeamName);

        /// <summary>Called once per team the page lands on (first load and every switch).</summary>
        protected virtual Task OnTeamResolvedAsync() => Task.CompletedTask;

        /// <summary>Teams were reloaded (rename, delete, leave). Re-read this page's team from the
        /// list; if it is gone, or renamed, follow the active team.</summary>
        private async void HandleActiveTeamChanged()
        {
            if (_resolving) return; // this page caused the change; OnTeamResolvedAsync covers it
            if (Team == null) return;
            var entry = ActiveTeam.Teams.FirstOrDefault(t => t.Team.Id == Team.Id);
            if (entry.Team == null)
            {
                NavigationManager.NavigateTo("/");
                return;
            }
            if (entry.Team.Name != TeamName)
            {
                NavigationManager.NavigateTo(NavigationManager.Uri.Replace($"/teams/{TeamName}", $"/teams/{entry.Team.Name}"), replace: true);
                return;
            }
            Team = entry.Team;
            Role = entry.Role;
            await OnActiveTeamChangedAsync();
            await InvokeAsync(StateHasChanged);
        }

        /// <summary>Membership or role changed on the current team.</summary>
        protected virtual Task OnActiveTeamChangedAsync() => Task.CompletedTask;

        public virtual void Dispose()
        {
            ActiveTeam.OnChange -= HandleActiveTeamChanged;
        }
    }
}
