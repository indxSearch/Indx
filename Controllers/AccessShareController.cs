using Asp.Versioning;
using IndxCloudApi.Data;
using IndxCloudApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace IndxCloudApi.Controllers
{
    /// <summary>
    /// Endpoints for sharing dataset access and transferring ownership between users.
    /// All operations require the caller to be the dataset owner.
    /// </summary>
    [ApiVersion("2.0-alpha")]
    [Route("api/datasets")]
    [ApiController]
    public class AccessShareController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;

        /// <summary>Initializes a new instance of <see cref="AccessShareController"/>.</summary>
        public AccessShareController(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        /// <summary>
        /// Lists all current access grants for a dataset.
        /// </summary>
        [HttpGet("{dataSetName}/access")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("NewPolicy")]
        public async Task<ActionResult<AccessGrantDto[]>> GetAccessGrants(string dataSetName)
        {
            var ownerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(ownerId)) return Unauthorized();

            if (IndxCloudInternalApi.Manager.GetEffectiveRole(dataSetName, ownerId, ownerId) != "owner")
                return Forbid();

            var grants = IndxCloudInternalApi.Manager.GetAccessGrants(dataSetName, ownerId);
            var dtos = new List<AccessGrantDto>();
            foreach (var (granteeId, role) in grants)
            {
                var user = await _userManager.FindByIdAsync(granteeId);
                dtos.Add(new AccessGrantDto(user?.Email ?? granteeId, role));
            }
            return dtos.ToArray();
        }

        /// <summary>
        /// Grants (or updates) access for a user on a dataset. Role must be "editor" or "viewer".
        /// </summary>
        [HttpPost("{dataSetName}/access")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("NewPolicy")]
        public async Task<IActionResult> GrantAccess(string dataSetName, [FromBody] GrantAccessRequest request)
        {
            var ownerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(ownerId)) return Unauthorized();
            if (string.IsNullOrEmpty(request?.GranteeEmail)) return BadRequest("granteeEmail is required");
            if (request.Role != "editor" && request.Role != "viewer")
                return BadRequest("role must be 'editor' or 'viewer'");

            if (IndxCloudInternalApi.Manager.GetEffectiveRole(dataSetName, ownerId, ownerId) != "owner")
                return Forbid();

            var grantee = await _userManager.FindByEmailAsync(request.GranteeEmail);
            if (grantee == null) return BadRequest("User not found");
            if (grantee.Id == ownerId) return BadRequest("Cannot grant access to yourself");

            IndxCloudInternalApi.Manager.GrantAccess(dataSetName, ownerId, grantee.Id, request.Role);
            return Ok();
        }

        /// <summary>
        /// Revokes a user's access to a dataset.
        /// </summary>
        [HttpDelete("{dataSetName}/access/{granteeEmail}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("NewPolicy")]
        public async Task<IActionResult> RevokeAccess(string dataSetName, string granteeEmail)
        {
            var ownerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(ownerId)) return Unauthorized();

            if (IndxCloudInternalApi.Manager.GetEffectiveRole(dataSetName, ownerId, ownerId) != "owner")
                return Forbid();

            var grantee = await _userManager.FindByEmailAsync(granteeEmail);
            if (grantee == null) return BadRequest("User not found");

            IndxCloudInternalApi.Manager.RevokeAccess(dataSetName, ownerId, grantee.Id);
            return Ok();
        }

        /// <summary>
        /// Transfers dataset ownership to another user. Returns 409 if a shadow build is in progress.
        /// </summary>
        [HttpPost("{dataSetName}/transfer")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [EnableCors("NewPolicy")]
        public async Task<IActionResult> TransferOwnership(string dataSetName, [FromBody] TransferOwnershipRequest request)
        {
            var currentOwnerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(currentOwnerId)) return Unauthorized();
            if (string.IsNullOrEmpty(request?.NewOwnerEmail)) return BadRequest("newOwnerEmail is required");

            if (IndxCloudInternalApi.Manager.GetEffectiveRole(dataSetName, currentOwnerId, currentOwnerId) != "owner")
                return Forbid();

            var newOwner = await _userManager.FindByEmailAsync(request.NewOwnerEmail);
            if (newOwner == null) return BadRequest("User not found");
            if (newOwner.Id == currentOwnerId) return BadRequest("Cannot transfer to yourself");

            try
            {
                IndxCloudInternalApi.Manager.TransferOwnership(dataSetName, currentOwnerId, newOwner.Id);
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }

#pragma warning disable 1591
    public record GrantAccessRequest(string GranteeEmail, string Role);
    public record TransferOwnershipRequest(string NewOwnerEmail);
    public record AccessGrantDto(string Email, string Role);
#pragma warning restore 1591
}
