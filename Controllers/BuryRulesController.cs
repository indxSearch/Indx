using Indx.Api;
using Indx.Http;
using Indx.Utilities;
using IndxServer.Engine;
using IndxServer.Models;
using IndxServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// The bury routes, the boost routes' twins (25 Oct 2026): <c>buries/from-filter</c> for a bury
    /// a search carries itself, and <c>GET</c>/<c>PUT</c>/<c>DELETE buries</c> for the dataset's
    /// saved bury rules, applied to every search that runs with <c>enableBury</c>.
    /// </summary>
    public class BuryRulesController(TeamContextResolver resolver, BuryRuleStore buryStore, IEditionService edition) : DatasetApiController(resolver)
    {
        /// <summary>A bury for the documents a filter holds, to send in a search's <c>buries</c>.</summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/buries/from-filter")]
        public ActionResult<BuryProxy> CreateBury(string teamName, string dataSetName, [FromBody] BuryProxy bury)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateBury", SystemState.Ready) is { } stateError)
                return stateError;
            if (bury == null)
                return ApiProblems.InvalidArgument("A body with a filter to bury is required.");
            var filter = ResolveFilter(matcher, bury.FilterProxy, "filterProxy", out var filterError);
            if (filter == null) return filterError!;
            if (matcher.CreateBury(filter, bury.BuryStrength, out var buryError) == null)
                return ApiProblems.InvalidArgument("'buryStrength': " + buryError);
            return Ok(bury);
        }

        /// <summary>The dataset's saved bury rules.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/buries")]
        public ActionResult<BuryRule[]> GetBuryRules(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return buryStore.Load(ctx.OwnerKey, dataSetName).ToArray();
        }

        /// <summary>Replaces the dataset's saved bury rules.</summary>
        [HttpPut(DataSetRoute + "/buries")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetBuryRules(string teamName, string dataSetName, [FromBody] BuryRule[] rules)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            if (rules == null)
                return ApiProblems.InvalidArgument("rules body is required");

            // Best-effort field validation when the engine is loaded; never hard-fail on a
            // hibernated dataset (apply-time skips unbuildable conditions anyway).
            var fields = IndxServerInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey)?.DocumentFields;
            if (RankingRuleValidation.Objection(rules, fields, r => Enum.IsDefined(((BuryRule)r).Strength)) is { } objection)
                return ApiProblems.InvalidArgument(objection);

            // Scheduling is the boost rules' gated feature, and the bury rules' with it.
            if (!edition.IsEnabled(EditionFeature.BoostRuleScheduling))
                foreach (var rule in rules) { rule.ActiveFrom = null; rule.ActiveUntil = null; }

            buryStore.Save(ctx.OwnerKey, dataSetName, rules);
            return NoContent();
        }

        /// <summary>Deletes all the dataset's saved bury rules.</summary>
        [HttpDelete(DataSetRoute + "/buries")]
        public IActionResult DeleteBuryRules(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            buryStore.Delete(ctx.OwnerKey, dataSetName);
            return NoContent();
        }
    }
}
