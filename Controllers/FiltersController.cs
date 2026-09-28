using Asp.Versioning;
using IndxServer.Engine;
using Indx.Api;
using Indx.Http;
using Indx.Core;
using Indx.Storage;
using Indx.Utilities;
using IndxServer.Models;
using IndxServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Filter creation (value, range, combine, not) and the filter cache (load, count, delete).
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class FiltersController(TeamContextResolver resolver) : DatasetApiController(resolver)
    {
        /// <summary>
        /// CombineFilters will combine two filters using AND or OR operation.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/filters/combine")]
        public ActionResult<FilterProxy> CombineFilters(string teamName, string dataSetName, [FromBody] CombinedFilterProxy combineFilters)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CombineFilters", SystemState.Ready) is { } stateError)
                return stateError;
            if (combineFilters == null)
                return ApiProblems.InvalidArgument("A body with two filters to combine is required.");
            var fa = ResolveFilter(matcher, combineFilters.A, "a", out var errorA);
            if (fa == null) return errorA!;
            var fb = ResolveFilter(matcher, combineFilters.B, "b", out var errorB);
            if (fb == null) return errorB!;
            matcher.LoadFilters(new Filter[] { fa, fb });
            Filter result;
            if (combineFilters.UseAndOperation)
                result = fa & fb;
            else
                result = fa | fb;
            var filterProxy = new FilterProxy(result.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// NegateFilter returns a filter matching every document the given filter does not:
        /// the NOT of a value, range or combined filter. The result is a token like any other
        /// and can be combined further.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/filters/not")]
        public ActionResult<FilterProxy> NegateFilter(string teamName, string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "NegateFilter", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = ResolveFilter(matcher, filterProxy, "filter", out var filterError);
            if (filter == null) return filterError!;
            matcher.LoadFilters(new Filter[] { filter });
            var result = !filter;
            return Ok(new FilterProxy(result.SerializedKey));
        }

        /// <summary>
        /// CreateRangeFilter will create a RangeFilter which may be passed to any search.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/filters/range")]
        public ActionResult<FilterProxy> CreateRangeFilter(string teamName, string dataSetName, [FromBody] RangeFilterProxy rangeFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateRangeFilter", SystemState.Ready) is { } stateError)
                return stateError;
            var filter = matcher.CreateRangeFilter(rangeFilter.FieldName, rangeFilter.LowerLimit, rangeFilter.UpperLimit, out var filterError);
            if (filter == null)
                return ApiProblems.InvalidArgument(filterError ?? "invalid filter arguments");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// CreateValueFilter will create a ValueFilter which may be passed to any search.
        /// Case-insensitive unless <c>isCaseSensitive</c> is set; see <see cref="ValueFilterProxy"/>
        /// for when it should be, and what it costs.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/filters/value")]
        public ActionResult<FilterProxy> CreateValueFilter(string teamName, string dataSetName, [FromBody] ValueFilterProxy valueFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateValueFilter", SystemState.Ready) is { } stateError)
                return stateError;
            if (valueFilter == null)
                return ApiProblems.InvalidArgument("A body with fieldName and value is required.");
            // Unwrapped like every other 'object' the API takes (UpdateField, UpdateFieldInFilter):
            // ASP.NET Core binds it as a JsonElement, whose ToString happened to decode a string
            // token, so this worked by coincidence and differed from its siblings.
            var filter = matcher.CreateValueFilter(valueFilter.FieldName, UnwrapJsonElement(valueFilter.Value)!,
                valueFilter.IsCaseSensitive, out var filterError);
            if (filter == null)
                return ApiProblems.InvalidArgument(filterError ?? "invalid filter arguments");
            var filterProxy = new FilterProxy(filter.SerializedKey);
            return Ok(filterProxy);
        }

        /// <summary>
        /// Deletes a single filter from the filter cache.
        /// </summary>
        [HttpPost(DataSetRoute + "/filters/delete")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteFilter(string teamName, string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var filter = ResolveFilter(matcher, filterProxy, "filter", out var filterError);
            if (filter == null) return filterError!;
            var result = matcher.DeleteFilter(filter);
            if (!result)
                return ApiProblems.InvalidArgument("The filter is not registered on this dataset (it may already have been deleted).");
            return NoContent();
        }

        /// <summary>
        /// Deletes all filters from the filter cache.
        /// </summary>
        [HttpDelete(DataSetRoute + "/filters")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteAllFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            matcher.DeleteAllFilters();
            return NoContent();
        }

        /// <summary>
        /// Pre-loads all registered filters in the background.
        /// </summary>
        [HttpPost(DataSetRoute + "/filters/load")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult LoadAllFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "LoadAllFilters", SystemState.Loaded, SystemState.Indexing, SystemState.Ready) is { } stateError)
                return stateError;
            matcher.LoadAllFilters();
            return NoContent();
        }

        /// <summary>
        /// Returns the number of filters currently registered in the filter cache.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/filters/count")]
        public ActionResult<CountResponse> GetNumberOfFilters(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return Ok(new CountResponse(matcher.NumberOfFilters));
        }
    }
}
