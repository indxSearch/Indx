using Asp.Versioning;
using IndxCloudApi.Data;
using IndxCloudApi.Models;
using IndxCloudApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace IndxCloudApi.Controllers
{
    /// <summary>
    /// Moving a dataset between teams. Per-user dataset sharing is gone in the team model —
    /// access follows team membership, which is managed from the Blazor team pages, not the API.
    /// The only remaining cross-boundary operation is reassigning a dataset to a different team,
    /// which requires the caller to be an Admin of both the source and target teams.
    /// </summary>
    [ApiVersion("2.0-alpha")]
    [Route("api")]
    [ApiController]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AccessShareController(
        TeamContextResolver resolver,
        UserManager<ApplicationUser> userManager,
        NotificationService notificationService) : Controller
    {
        /// <summary>
        /// Moves a dataset from <c>{teamName}</c> to another team. Caller must be Admin of both.
        /// Returns 409 if a shadow build is in progress, 400 if the target team already owns a
        /// dataset with the same name.
        /// </summary>
        [HttpPost("teams/{teamName}/datasets/{dataSetName}/transfer")]
        [EnableCors("NewPolicy")]
        public async Task<IActionResult> TransferToTeam(string teamName, string dataSetName, [FromBody] TransferToTeamRequest request)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(request?.TargetTeamName)) return BadRequest("targetTeamName is required");

            var source = resolver.Resolve(teamName, userId);
            if (source == null || !TeamRoles.CanAdmin(source.Role))
                return StatusCode(StatusCodes.Status403Forbidden, "Team Admin role required on the source team");

            var target = resolver.Resolve(request.TargetTeamName, userId);
            if (target == null || !TeamRoles.CanAdmin(target.Role))
                return StatusCode(StatusCodes.Status403Forbidden, "Team Admin role required on the target team");

            if (source.TeamId == target.TeamId)
                return BadRequest("Source and target team are the same");

            try
            {
                IndxCloudInternalApi.Manager.TransferOwnership(dataSetName, source.OwnerKey, target.OwnerKey);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Microsoft.Data.Sqlite.SqliteException)
            {
                return BadRequest($"Transfer failed: {ex.Message}");
            }

            var actor = await userManager.FindByIdAsync(userId);
            await notificationService.CreateAsync(
                userId,
                NotificationType.DatasetShared,
                $"Dataset \"{dataSetName}\" moved to team \"{target.TeamName}\"",
                $"{actor?.Email ?? "You"} moved the dataset \"{dataSetName}\" from team \"{source.TeamName}\" to \"{target.TeamName}\".");

            return Ok();
        }
    }

#pragma warning disable 1591
    public record TransferToTeamRequest(string TargetTeamName);
#pragma warning restore 1591
}
