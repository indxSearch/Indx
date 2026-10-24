using Asp.Versioning;
using Indx.Utilities;
using IndxServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Downloads a dataset's documents as JSON. Separate from <see cref="SearchController"/> for
    /// one reason: it accepts the console's login cookie as well as a bearer token, so the Options
    /// tab can offer it as a plain link and the browser handles the download natively — the
    /// console's other exports go through the Blazor connection as one string, which does not
    /// scale to a dataset. A GET that changes nothing needs no antiforgery token.
    /// </summary>
    [ApiVersion("2.0-beta")]
    [Route("api")]
    [ApiController]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + "," + "Identity.Application")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public class DatasetExportController(TeamContextResolver resolver, ExportProgress progress) : ControllerBase
    {
        /// <summary>
        /// Downloads every document in the dataset as one JSON array, ordered by document key —
        /// the records as they were loaded, replaced or last updated, including fields that are not
        /// configured. Streams, so it works on a dataset of any size, and works whether the dataset
        /// is Ready, hibernated or idle-evicted. Any team member may export. Field configuration and
        /// boost rules are separate exports.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet("teams/{teamName}/datasets/{dataSetName}/export")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        /// <param name="progressToken">The console's: it shows a progress bar from the count written under it. Leave it out.</param>
        public async Task<IActionResult> Export(string teamName, string dataSetName, [FromQuery(Name = "progress")] Guid? progressToken = null)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var keyScope = ApiKeyScope.For(HttpContext);
            if (string.IsNullOrEmpty(userId) && keyScope?.IsTeamKey != true) return Unauthorized();

            // Non-members get the same 404 as a team that does not exist, as everywhere else.
            var ctx = resolver.Resolve(teamName, userId, keyScope);
            if (ctx == null) return ApiProblems.TeamNotFound(teamName);
            if (!FileNameValidity.IsValid(dataSetName)) return ApiProblems.InvalidDatasetName(dataSetName);
            if (!DatasetExport.Exists(ctx.OwnerKey, dataSetName)) return ApiProblems.DatasetNotFound(dataSetName);

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/json; charset=utf-8";
            Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition
            {
                FileName = DatasetExport.FileName(dataSetName),
                DispositionType = "attachment",
            }.ToString();
            Response.Headers.CacheControl = "no-store";

            // The headers go now: the browser shows the download as started at once, rather than
            // nothing at all while the database sorts a large dataset before its first record.
            await Response.StartAsync(HttpContext.RequestAborted);
            if (progressToken is { } token) progress.Started(token);
            var failed = true;
            try
            {
                await DatasetExport.WriteJsonArrayAsync(ctx.OwnerKey, dataSetName, Response.Body, HttpContext.RequestAborted,
                    progressToken is { } t ? n => progress.Wrote(t, n) : null);
                failed = false;
            }
            finally
            {
                if (progressToken is { } t) progress.Finished(t, failed);
            }
            return new EmptyResult();
        }
    }
}
