using Indx.Utilities;
using IndxServer.Models;
using IndxServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// The dataset's query parameters: the coverage values a search takes when its request leaves
    /// them out. A value a request sends always wins, one value at a time.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class QueryParametersController(TeamContextResolver resolver, QueryParameterStore store) : DatasetApiController(resolver)
    {
        /// <summary>
        /// Returns what the dataset sets (<c>parameters</c>, null where it sets nothing) and what a search
        /// that sends no coverage values runs with (<c>effective</c>, every value filled in).
        /// Config, not state-gated: available while the dataset is asleep. A search key may read it:
        /// it says how this dataset's searches run, and a search page shows it beside its own values.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/query-parameters")]
        public ActionResult<QueryParametersResponse> GetQueryParameters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var set = store.Load(ctx.OwnerKey, dataSetName);
            return new QueryParametersResponse(set ?? new DatasetQueryParameters(), QueryParameterResolution.Effective(set));
        }

        /// <summary>
        /// Replaces the dataset's query parameters. A value left out (or null) is not set, and a
        /// search that leaves it out too gets the engine default. Applies from the next search; no
        /// reindex.
        /// </summary>
        [HttpPut(DataSetRoute + "/query-parameters")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetQueryParameters(string teamName, string dataSetName, [FromBody] DatasetQueryParameters parameters)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            if (parameters == null)
                return ApiProblems.InvalidArgument("A body with the query parameters is required.");
            if (QueryParameterResolution.Validate(parameters) is { } invalid)
                return ApiProblems.InvalidArgument(invalid);
            store.Save(ctx.OwnerKey, dataSetName, parameters);
            return NoContent();
        }

        /// <summary>Clears the dataset's query parameters: searches get the engine defaults again.</summary>
        [HttpDelete(DataSetRoute + "/query-parameters")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult DeleteQueryParameters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            store.Save(ctx.OwnerKey, dataSetName, new DatasetQueryParameters());
            return NoContent();
        }
    }
}
