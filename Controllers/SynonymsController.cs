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
    /// The dataset's synonym list.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class SynonymsController(TeamContextResolver resolver) : DatasetApiController(resolver)
    {
        /// <summary>
        /// Returns the dataset's synonym list, or a <c>null</c> body when it has none — always 200.
        /// The list widens every search on this dataset: a query word that matches an entry gets that
        /// entry's terms appended to the query text before scoring.
        /// <para>One list per dataset — there is no name to supply, and no other dataset is affected.</para>
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/synonyms")]
        [ProducesResponseType(typeof(SynonymList), StatusCodes.Status200OK)]
        public ActionResult<SynonymList?> GetSynonymList(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);

            // JsonResult, not Ok(): Ok(null) is turned into 204 with an empty body by
            // HttpNoContentOutputFormatter, and an empty body makes the idiomatic client call
            // (GetFromJsonAsync<SynonymList>) throw instead of yielding null. This writes a literal
            // JSON null with 200, so "no list" and "here is the list" are one code path for callers.
            return new JsonResult(IndxServerInternalApi.Manager.GetSynonyms(dataSetName, ctx.OwnerKey));
        }

        /// <summary>
        /// Sets the dataset's synonym list, replacing any list it already had. Send a body of
        /// <c>null</c> to remove the list — the dataset then searches without synonyms again.
        /// <para>Takes effect on the very next search: synonyms are applied to the query text, so
        /// nothing is re-indexed and the dataset does not have to be in any particular state. The
        /// list is stored with the dataset, so it survives a restart and follows the dataset if it
        /// is transferred to another team.</para>
        /// <para>Note that widening a query lowers Coverage scores in proportion to how much text is
        /// added — see the remarks on <c>Indx.Api.SynonymList</c>.</para>
        /// </summary>
        [HttpPut(DataSetRoute + "/synonyms")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSynonymList(string teamName, string dataSetName, [FromBody] SynonymList? list)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);

            if (!IndxServerInternalApi.Manager.SetSynonyms(dataSetName, ctx.OwnerKey, list))
                return ApiProblems.OperationFailed(
                    "The synonym list could not be stored — the dataset has no storage attached.");
            return NoContent();
        }
    }
}
