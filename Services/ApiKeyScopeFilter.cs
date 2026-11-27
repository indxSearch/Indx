using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IndxServer.Services
{
    /// <summary>
    /// Enforces a scoped API key's limits on every MVC action, before model binding — so a request
    /// the key may not make is refused before its body is read. Registered globally, which is what
    /// makes it impossible for a controller to forget: the team roles are still checked by each
    /// action (ResolveTeam), and this runs in front of that.
    /// <list type="number">
    /// <item><b>Team.</b> Not here: TeamContextResolver refuses a team other than the key's with
    ///   the same <c>404 teamNotFound</c> as a team you are not in, using the team row it already
    ///   loads, so enforcement costs no extra query.</item>
    /// <item><b>Dataset.</b> A key limited to datasets must name the route's <c>{dataSetName}</c>,
    ///   else <c>404 datasetNotFound</c>, whether or not the dataset exists.</item>
    /// <item><b>Level.</b> The action's <see cref="KeyAccessAttribute"/> minimum, Full when absent,
    ///   else <c>403 insufficientKeyScope</c>.</item>
    /// <item><b>No team in the route.</b> Refused unless the action declares
    ///   <see cref="KeyAccessAttribute.FiltersToKeyScope"/> — login-token minting and password
    ///   changes are therefore unreachable with a scoped key.</item>
    /// </list>
    /// Principals without a scope (login tokens, console sessions, keys created before scopes)
    /// pass through untouched.
    /// </summary>
    public sealed class ApiKeyScopeFilter : IAuthorizationFilter
    {
        // Action descriptors live for the application's lifetime; read each one's attribute once.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ActionDescriptor, KeyAccessAttribute?> AccessByAction = new();

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            // Unscoped principals — every login token, console session and pre-scope key — leave
            // here, after reading one claim.
            var scope = ApiKeyScope.For(context.HttpContext);
            if (scope == null) return;

            var access = AccessByAction.GetOrAdd(context.ActionDescriptor, static d =>
                (d as ControllerActionDescriptor)?.MethodInfo
                    .GetCustomAttributes(typeof(KeyAccessAttribute), inherit: true)
                    .OfType<KeyAccessAttribute>()
                    .FirstOrDefault());
            var required = access?.Minimum ?? ApiKeyLevel.Full;

            var route = context.RouteData.Values;
            var hasTeam = route.ContainsKey("teamName");
            var dataSetName = route.TryGetValue("dataSetName", out var d) ? d as string : null;

            if (!hasTeam && access?.FiltersToKeyScope != true)
            {
                context.Result = ApiProblems.InsufficientKeyScope(required);
                return;
            }

            // The team itself is checked by TeamContextResolver, on the team row every team-scoped
            // action loads anyway, so this filter never touches the database.
            if (dataSetName != null && !scope.AllowsDataset(dataSetName))
            {
                context.Result = ApiProblems.DatasetNotFound(dataSetName);
                return;
            }

            if (!scope.AllowsLevel(required))
                context.Result = ApiProblems.InsufficientKeyScope(required);
        }
    }
}
