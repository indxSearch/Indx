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
        /// Combines two or more filters with AND or OR into one: one node and one token for the
        /// whole list, where pairs combined in turn were a request and a filter per pair. A filter
        /// listed twice counts once and the order does not matter; one filter gives its own token.
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
            if (combineFilters?.Filters == null || combineFilters.Filters.Length == 0)
                return ApiProblems.InvalidArgument("A body with a list of filters to combine is required: 'filters', at least one.");
            var operands = new Filter[combineFilters.Filters.Length];
            for (int i = 0; i < operands.Length; i++)
            {
                var operand = ResolveFilter(matcher, combineFilters.Filters[i], $"filters[{i}]", out var operandError);
                if (operand == null) return operandError!;
                operands[i] = operand;
            }
            var result = combineFilters.UseAndOperation ? Filter.And(operands) : Filter.Or(operands);
            // Loaded here, so that an operand that cannot load is this request's 400 and not a
            // later search's. An AND loads cost-aware: an array-field operand is checked on the
            // candidates the others leave, not loaded in full.
            if (!matcher.LoadFilters([result], out var loadError))
                return ApiProblems.OperationFailed(loadError);
            return Ok(new FilterProxy(result.SerializedKey));
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
            if (!matcher.LoadFilters(new Filter[] { filter }, out var loadError))
                return ApiProblems.OperationFailed(loadError);
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
        /// Creates the filter a filter expression describes, e.g.
        /// <c>brand IN ["nike", "puma"] AND price BETWEEN 100 AND 2000</c>, and answers its token and
        /// the expression as the engine reads it. The token is the one the other filter routes make
        /// for the same filter. An expression that makes none is 400 <c>invalidFilterExpression</c>
        /// with every error and its place; a placeholder's token that does not resolve is 400
        /// <c>unknownFilter</c>.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/filters/expression")]
        public ActionResult<FilterExpressionResultProxy> CreateFilterFromExpression(string teamName, string dataSetName, [FromBody] FilterExpressionProxy body)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateFilterFromExpression", SystemState.Ready) is { } stateError)
                return stateError;
            if (body == null)
                return ApiProblems.InvalidArgument("A body with an expression is required.");
            Filter filter;
            try
            {
                filter = IndxServerInternalApi.ResolveRequestFilter(matcher, null, body.Expression ?? string.Empty, body.Parameters)!;
            }
            catch (UnknownFilterException ex)
            {
                return ApiProblems.UnknownFilter(ex.Message);
            }
            catch (InvalidFilterExpressionException ex)
            {
                return ApiProblems.InvalidFilterExpression(ex.Expression, ex.Errors);
            }
            // Loaded here, as filters/combine loads, so an operand that cannot load is this
            // request's 400 and not a later search's.
            if (!matcher.LoadFilters([filter], out var loadError))
                return ApiProblems.OperationFailed(loadError);
            return Ok(new FilterExpressionResultProxy { HashString = filter.SerializedKey, Canonical = filter.ToExpression() });
        }

        /// <summary>
        /// Creates a filter matching documents whose field holds one of a set of values - one filter
        /// for the whole set, where value filters combined with OR are 2n - 1 requests and filters.
        /// Like a value or range filter, its token describes it.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/filters/in")]
        public ActionResult<FilterProxy> CreateInFilter(string teamName, string dataSetName, [FromBody] InFilterProxy inFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateInFilter", SystemState.Ready) is { } stateError)
                return stateError;
            if (inFilter == null || inFilter.Values == null)
                return ApiProblems.InvalidArgument("A body with fieldName and a list of values is required.");
            // Each value arrives as a JsonElement; unwrapped as filters/value unwraps its one value.
            var values = inFilter.Values.Select(v => UnwrapJsonElement(v)!).ToArray();
            var filter = matcher.CreateInFilter(inFilter.FieldName, values, inFilter.IsCaseSensitive, out var filterError);
            if (filter == null)
                return ApiProblems.InvalidArgument(filterError ?? "invalid filter arguments");
            return Ok(new FilterProxy(filter.SerializedKey));
        }

        /// <summary>
        /// Creates a filter matching documents whose position lies within a distance, in metres, of
        /// the given position. The position field is an object with the numbers <c>latitude</c> and
        /// <c>longitude</c>, both Filterable. Like a range filter, its token describes it.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/filters/position")]
        public ActionResult<FilterProxy> CreatePositionFilter(string teamName, string dataSetName, [FromBody] PositionFilterProxy positionFilter)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreatePositionFilter", SystemState.Ready) is { } stateError)
                return stateError;
            if (positionFilter == null)
                return ApiProblems.InvalidArgument("A body with fieldName, latitude, longitude and withinMeter is required.");
            var filter = matcher.CreatePositionFilter(positionFilter.FieldName,
                new GeoPosition(positionFilter.Latitude, positionFilter.Longitude), positionFilter.WithinMeter, out var filterError);
            if (filter == null)
                return ApiProblems.InvalidArgument(filterError ?? "invalid filter arguments");
            return Ok(new FilterProxy(filter.SerializedKey));
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
