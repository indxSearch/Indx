using IndxServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Demo datasets (Nov 2026): a dataset to try Indx with, made in one request. The films from
    /// Wikidata (CC0) are the one there is. Creating a dataset needs team Editor, as any other does.
    /// </summary>
    public class DemoDatasetsController(TeamContextResolver resolver, DemoDatasets demos) : DatasetApiController(resolver)
    {
        /// <summary>The dataset made, and its name.</summary>
        public sealed record DemoCreated(string DataSet);

        /// <summary>
        /// Creates the films demo in the team: about 25,000 films from Wikidata, with their field
        /// roles set, loaded and indexed. Answers 201 with the dataset's name once it is Ready, which
        /// takes a few seconds. The name is "films", or "films-2" and on when the team has one.
        /// </summary>
        [HttpPost("teams/{teamName}/demo-datasets/films")]
        [ProducesResponseType(typeof(DemoCreated), StatusCodes.Status201Created)]
        public async Task<IActionResult> CreateFilms(string teamName, CancellationToken ct)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            // A key held to some datasets makes none: the route names no dataset for its list to
            // be checked against, and the one made would be outside it.
            if (ApiKeyScope.For(HttpContext) is { Datasets: not null })
                return ApiProblems.Problem(StatusCodes.Status403Forbidden, "insufficientKeyScope", "API key scope too narrow",
                    "This key is limited to some datasets and cannot create one. Use a key for the whole team.");
            if (!DemoDatasets.FilmsAvailable)
                return ApiProblems.OperationFailed("This build does not include the demo films.");
            try
            {
                var name = await demos.CreateFilmsAsync(ctx.OwnerKey, ct);
                return StatusCode(StatusCodes.Status201Created, new DemoCreated(name));
            }
            catch (InvalidOperationException ex)
            {
                return ApiProblems.OperationFailed(ex.Message);
            }
        }
    }
}
