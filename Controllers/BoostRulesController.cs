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
    /// The dataset's boost rules, and boosts built from filters.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class BoostRulesController(TeamContextResolver resolver, BoostRuleStore boostStore, IEditionService edition) : DatasetApiController(resolver)
    {
        /// <summary>
        /// CreateBoost will create a Boost setup which may be passed to any search.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/boosts/from-filter")]
        public ActionResult<BoostProxy> CreateBoost(string teamName, string dataSetName, [FromBody] BoostProxy boost)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "CreateBoost", SystemState.Ready) is { } stateError)
                return stateError;
            if (boost == null)
                return ApiProblems.InvalidArgument("A body with a filter to boost is required.");
            var filter = ResolveFilter(matcher, boost.FilterProxy, "filterProxy", out var filterError);
            if (filter == null) return filterError!;
            if (matcher.CreateBoost(filter, boost.BoostStrength, out var boostError) == null)
                return ApiProblems.InvalidArgument("'boostStrength': " + boostError);
            return Ok(boost);
        }

        /// <summary>
        /// Returns the dataset's persisted boost rules (server-side ranking rules applied when a
        /// search sets enableBoost). Config, not state-gated — available even when not Ready.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/boosts")]
        public ActionResult<BoostRule[]> GetBoostRules(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return boostStore.Load(ctx.OwnerKey, dataSetName).ToArray();
        }

        /// <summary>
        /// Replaces the dataset's boost rules (whole list). Each rule needs a name, at least one
        /// condition, and every condition must reference a Filterable field and be either a value
        /// match or a numeric range (not both).
        /// </summary>
        [HttpPut(DataSetRoute + "/boosts")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetBoostRules(string teamName, string dataSetName, [FromBody] BoostRule[] rules)
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
            if (RankingRuleValidation.Objection(rules, fields, r => Enum.IsDefined(((BoostRule)r).Strength)) is { } objection)
                return ApiProblems.InvalidArgument(objection);

            // Boost-rule scheduling is a gated feature (Free Managed can't schedule). Strip any
            // schedule window when it's disabled — defensive (the UI hides the control) and
            // self-healing on a plan downgrade.
            if (!edition.IsEnabled(EditionFeature.BoostRuleScheduling))
                foreach (var rule in rules) { rule.ActiveFrom = null; rule.ActiveUntil = null; }

            boostStore.Save(ctx.OwnerKey, dataSetName, rules);
            return NoContent();
        }

        /// <summary>Clears all boost rules for the dataset.</summary>
        [HttpDelete(DataSetRoute + "/boosts")]
        public IActionResult DeleteBoostRules(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            boostStore.Delete(ctx.OwnerKey, dataSetName);
            return NoContent();
        }
    }
}
