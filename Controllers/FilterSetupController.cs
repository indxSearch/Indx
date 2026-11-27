using IndxServer.Engine;
using IndxServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// A dataset's filter setup (26 Nov 2026): which fields a page offers as filters, named, in order,
    /// each with its panel, as the owner set them in the console's Filters, else as the rules choose
    /// (<see cref="DatasetFilters"/>). Any page reads it, with or without intrface: intrface's
    /// AutoFilters reads it by itself. Not the filters themselves (tokens, the filter cache): those
    /// are FiltersController's, under filters/.
    /// </summary>
    public class FilterSetupController(TeamContextResolver resolver, FilterSetupStore setups, DatasetMetadataStore metadata) : DatasetApiController(resolver)
    {
        /// <summary>
        /// The filters a page shows, in order: each a field (or the category menu, with its levels),
        /// its name and its panel (values, buttons, range, buckets with their edges, toggle,
        /// hierarchy). Search level: a search page reads it with the key it searches with, and it
        /// tells nothing the field configuration does not.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/filter-setup")]
        public ActionResult<IReadOnlyList<PlannedFilter>> GetFilterSetup(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null) return ApiProblems.DatasetNotFound(dataSetName);
            var rows = DatasetFilters.Read(engine, metadata.LoadCategory(ctx.OwnerKey, dataSetName),
                setups.Load(ctx.OwnerKey, dataSetName), out _, out _);
            return Ok(DatasetFilters.Plan(rows));
        }

        /// <summary>
        /// Sets the dataset's filters: the fields in their order, each with a name, a panel and
        /// whether it is shown; a field left out of the list is placed after it, as the rules choose.
        /// An empty list sets the filters back to the rules'. Returns the filters a page now reads.
        /// </summary>
        [HttpPut(DataSetRoute + "/filter-setup")]
        public ActionResult<IReadOnlyList<PlannedFilter>> SetFilterSetup(string teamName, string dataSetName, [FromBody] FilterSetup setup)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null) return ApiProblems.DatasetNotFound(dataSetName);
            var category = metadata.LoadCategory(ctx.OwnerKey, dataSetName);
            DatasetFilters.Read(engine, category, null, out var dataset, out var levels);
            if (DatasetFilters.Validate(setup ?? new FilterSetup(), dataset, levels) is { } problem)
                return ApiProblems.InvalidArgument(problem);
            setups.Save(ctx.OwnerKey, dataSetName, setup);
            return Ok(DatasetFilters.Plan(DatasetFilters.Rows(dataset, levels, setups.Load(ctx.OwnerKey, dataSetName))));
        }
    }
}
