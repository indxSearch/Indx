using System.Security.Claims;

using IndxCloudApi.Data;
using IndxCloudApi.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace IndxCloudApi.Components.Shared
{
    /// <summary>
    /// Base for the pages under <c>/teams/{TeamName}</c>. Resolves the team named in the URL
    /// against the signed-in user's memberships, makes it the active (and remembered) team, and
    /// bounces anything else: unauthenticated → login, onboarding unfinished → setup, not a
    /// member (or no such team) → <c>/</c>, which lands on a team the user does belong to.
    /// </summary>
    public abstract class TeamRouteBase : ComponentBase, IDisposable
    {
        [Inject] protected NavigationManager NavigationManager { get; set; } = default!;
        [Inject] protected AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
        [Inject] internal InstanceSettingsService InstanceSettingsService { get; set; } = default!;
        [Inject] protected ActiveTeamState ActiveTeam { get; set; } = default!;

        [Parameter] public string TeamName { get; set; } = "";

        protected string UserId { get; private set; } = "";
        protected Team? Team { get; private set; }
        protected string? Role { get; private set; }
        protected string TeamId => Team?.Id.ToString() ?? "";
        protected bool TeamResolved => Team != null;

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
            if (UserId.Length == 0) return;
            await ActiveTeam.EnsureInitializedAsync(UserId);
            ActiveTeam.OnChange += HandleActiveTeamChanged;
            await ResolveTeamAsync();
        }

        // The same page instance is reused when only the route parameter changes
        // (switcher, back/forward), so re-resolve on every parameter set.
        protected override async Task OnParametersSetAsync()
        {
            if (UserId.Length > 0 && _resolvedFor != TeamName) await ResolveTeamAsync();
        }

        private async Task ResolveTeamAsync()
        {
            _resolvedFor = TeamName;
            var entry = ActiveTeam.Find(TeamName);
            if (entry == null)
            {
                Team = null;
                Role = null;
                NavigationManager.NavigateTo("/");
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

        /// <summary>Called once per team the page lands on (first load and every switch).</summary>
        protected virtual Task OnTeamResolvedAsync() => Task.CompletedTask;

        /// <summary>Teams were reloaded (rename, delete, leave). Re-read this page's team from the
        /// list; if it is gone, or renamed, follow the active team.</summary>
        private async void HandleActiveTeamChanged()
        {
            if (_resolving) return; // this page caused the change; OnTeamResolvedAsync covers it
            var entry = ActiveTeam.Teams.FirstOrDefault(t => t.Team.Id == Team?.Id);
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
