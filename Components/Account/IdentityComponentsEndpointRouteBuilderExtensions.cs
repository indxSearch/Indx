using Indx.Storage;
using IndxCloudApi.Components.Account.Pages;
using IndxCloudApi.Components.Account.Pages.Manage;
using IndxCloudApi.Data;
using IndxCloudApi.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;
using System.Text.Json;


namespace Microsoft.AspNetCore.Routing
{
    internal static class IdentityComponentsEndpointRouteBuilderExtensions
    {
        #region Public Methods
        // These endpoints are required by the Identity Razor components defined in the /Components/Account/Pages directory of this project.
        public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            var accountGroup = endpoints.MapGroup("/Account");

            accountGroup.MapPost("/PerformExternalLogin", (
                HttpContext context,
                [FromServices] SignInManager<ApplicationUser> signInManager,
                [FromForm] string provider,
                [FromForm] string returnUrl) =>
            {
                IEnumerable<KeyValuePair<string, StringValues>> query = [
                    new("ReturnUrl", returnUrl),
                    new("Action", ExternalLogin.LoginCallbackAction)];

                var redirectUrl = UriHelper.BuildRelative(
                    context.Request.PathBase,
                    "/Account/ExternalLogin",
                    QueryString.Create(query));

                var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
                return TypedResults.Challenge(properties, [provider]);
            });

            accountGroup.MapPost("/Logout", async (
                ClaimsPrincipal user,
                SignInManager<ApplicationUser> signInManager,
                [FromForm] string returnUrl) =>
            {
                await signInManager.SignOutAsync();
                return TypedResults.LocalRedirect($"~/{returnUrl}");
            });

            var manageGroup = accountGroup.MapGroup("/Manage").RequireAuthorization();

            manageGroup.MapPost("/DeleteAccount", async (
                ClaimsPrincipal user,
                HttpContext context,
                [FromServices] SignInManager<ApplicationUser> signInManager,
                [FromServices] UserManager<ApplicationUser> userManager,
                [FromServices] ILoggerFactory loggerFactory,
                [FromServices] IMemoryCache memoryCache) =>
            {
                var logger = loggerFactory.CreateLogger("DeleteAccount");
                var appUser = await userManager.GetUserAsync(user);
                if (appUser == null)
                {
                    return Results.BadRequest("User not found");
                }

                var userId = appUser.Id;
                logger.LogInformation($"Starting account deletion for user {userId}");

                // Datasets are owned by teams, not by users — so deleting a user must NOT delete
                // any datasets (that continuity is the whole point of team ownership). The user's
                // TeamMember rows cascade-delete via the FK to AspNetUsers when Identity removes
                // the account below; the team and its datasets survive.

                // Step 3: Delete from Identity database
                memoryCache.Remove($"user_exists_{userId}");
                var result = await userManager.DeleteAsync(appUser);
                if (!result.Succeeded)
                {
                    logger.LogError($"Failed to delete ApplicationUser: {string.Join(", ", result.Errors.Select(e => e.Description))}");
                    return Results.BadRequest(string.Join(", ", result.Errors.Select(e => e.Description)));
                }

                logger.LogInformation($"Successfully deleted account for user {userId}");
                await signInManager.SignOutAsync();
                return TypedResults.LocalRedirect("~/");
            });

            // Removed: LinkExternalLogin endpoint - external login linking functionality has been removed
            // manageGroup.MapPost("/LinkExternalLogin", async (
            //     HttpContext context,
            //     [FromServices] SignInManager<ApplicationUser> signInManager,
            //     [FromForm] string provider) =>
            // {
            //     // Clear the existing external cookie to ensure a clean login process
            //     await context.SignOutAsync(IdentityConstants.ExternalScheme);
            //
            //     var redirectUrl = UriHelper.BuildRelative(
            //         context.Request.PathBase,
            //         "/Account/Manage/ExternalLogins",
            //         QueryString.Create("Action", ExternalLogins.LinkLoginCallbackAction));
            //
            //     var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl, signInManager.UserManager.GetUserId(context.User));
            //     return TypedResults.Challenge(properties, [provider]);
            // });

            var loggerFactory = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var downloadLogger = loggerFactory.CreateLogger("DownloadPersonalData");

            manageGroup.MapPost("/DownloadPersonalData", async (
                HttpContext context,
                [FromServices] UserManager<ApplicationUser> userManager,
                [FromServices] AuthenticationStateProvider authenticationStateProvider) =>
            {
                var user = await userManager.GetUserAsync(context.User);
                if (user is null)
                {
                    return Results.NotFound($"Unable to load user with ID '{userManager.GetUserId(context.User)}'.");
                }

                var userId = await userManager.GetUserIdAsync(user);
                downloadLogger.LogInformation("User with ID '{UserId}' asked for their personal data.", userId);

                // Only include personal data for download
                var personalData = new Dictionary<string, string>();
                var personalDataProps = typeof(ApplicationUser).GetProperties().Where(
                    prop => Attribute.IsDefined(prop, typeof(PersonalDataAttribute)));
                foreach (var p in personalDataProps)
                {
                    personalData.Add(p.Name, p.GetValue(user)?.ToString() ?? "null");
                }

                var logins = await userManager.GetLoginsAsync(user);
                foreach (var l in logins)
                {
                    personalData.Add($"{l.LoginProvider} external login provider key", l.ProviderKey);
                }

                personalData.Add("Authenticator Key", (await userManager.GetAuthenticatorKeyAsync(user))!);
                var fileBytes = JsonSerializer.SerializeToUtf8Bytes(personalData);

                context.Response.Headers.TryAdd("Content-Disposition", "attachment; filename=PersonalData.json");
                return TypedResults.File(fileBytes, contentType: "application/json", fileDownloadName: "PersonalData.json");
            });

            return accountGroup;
        }
        #endregion Public Methods
    }
}