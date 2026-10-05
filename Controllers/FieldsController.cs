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
    /// Field roles (searchable, filterable, facetable, sortable, word-indexing, embeddable, key) and the field configuration.
    /// The roles are read one list at a time and written in one place, <c>PUT fields/configuration</c>:
    /// until Oct 2026 each list also had a PUT of its own, which set its one flag on the live
    /// engine with no rebuild, so a role that needs one took effect only at the next index build.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class FieldsController(TeamContextResolver resolver) : DatasetApiController(resolver)
    {
        /// <summary>
        /// GetAllFields will return the fields found during analyze.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/fields")]
        public ActionResult<string[]> GetAllFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxServerInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, true, false, false, false, false, false);
        }

        /// <summary>
        /// GetFacetableFields will return the array of facetable field names.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/fields/facetable")]
        public ActionResult<string[]> GetFacetableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxServerInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, false, true, false);
        }

        /// <summary>
        /// GetFilterableFields will return the array of filterable field names.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/fields/filterable")]
        public ActionResult<string[]> GetFilterableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxServerInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, true, false, false);
        }

        /// <summary>
        /// GetSearchableFields will return the array of searchable field names.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/fields/searchable")]
        public ActionResult<string[]> GetSearchableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxServerInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, true, false, false, false, false);
        }

        /// <summary>
        /// GetSortableFields will return the array of sortable field names.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/fields/sortable")]
        public ActionResult<string[]> GetSortableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxServerInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, true, false, false, false);
        }

        /// <summary>
        /// GetWordIndexingFields will return the array of word-indexing field names.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/fields/word-indexing")]
        public ActionResult<string[]> GetWordIndexingFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return IndxServerInternalApi.Manager.GetFields(dataSetName, ctx.OwnerKey, false, false, false, false, false, true);
        }

        /// <summary>
        /// GetEmbeddableFields will return the array of embeddable (vector) field names.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/fields/embeddable")]
        public ActionResult<string[]> GetEmbeddableFields(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return engine.GetFieldList().Where(f => f.Embeddable).Select(f => f.Name).ToArray();
        }

        /// <summary>
        /// SetFieldConfiguration sets any combination of field properties (Searchable, Filterable,
        /// Facetable, Sortable, WordIndexing, Embeddable, PreloadFilters, Weight, BM25b, BM25k1)
        /// in one call. Nullable properties have replace semantics: null = leave untouched,
        /// any value (including false) = overwrite.
        ///
        /// <para>The whole request is checked before anything is changed, and a refused one is a
        /// 400 that changes nothing. An accepted one answers in one of two ways. <b>204</b>: the
        /// change is applied, which is every change on a dataset that is not serving yet and a
        /// change of query-time roles on one that is. <b>202</b> with the status: the change needs
        /// the index rebuilt on a dataset that is Ready, so a rebuild on a shadow engine has been
        /// started and the dataset serves with the old configuration until it is swapped in. Poll
        /// GET status until <c>shadowBuildInProgress</c> is false, then read
        /// <c>shadowBuildError</c>: null means the new configuration is in use. Until Oct 2026
        /// the request waited for the rebuild, minutes on a large dataset.</para>
        /// </summary>
        [HttpPut(DataSetRoute + "/fields/configuration")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(ServerSystemStatus), StatusCodes.Status202Accepted)]
        public IActionResult SetFieldConfiguration(string teamName, string dataSetName, [FromBody] FieldProxy[] fields)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var df = matcher.DocumentFields;
            if (df == null)
                return ApiProblems.InvalidArgument("The dataset has not been analyzed yet, so there are no fields to configure.");

            // Everything that can be refused is refused here, before anything is changed and before
            // a rebuild is started: the rebuild runs after the answer has gone, so what it would
            // have refused the caller would only find in status. The names first, in the wording
            // every field route uses and the docs quote.
            foreach (var cfg in fields)
                if (df.GetField(cfg.FieldName) == null)
                    return ApiProblems.InvalidArgument(
                        $"Field '{cfg.FieldName}' does not exist in this dataset.");
            // Then the values: a negative weight, a BM25b outside [0, 1], a negative BM25k1, a
            // Filterable or Sortable field with no type, a vector field given another role. The
            // caller's mistake, so a 400 with the reason, not the 500 the global handler would
            // produce - that tells the caller we broke and logs it as our incident.
            try
            {
                if (!matcher.TryValidateFieldConfiguration(fields, out var invalid))
                    return ApiProblems.InvalidArgument(invalid);
            }
            catch (ArgumentException ex)
            {
                return ApiProblems.InvalidArgument(ex.Message);
            }

            // Mid-load or mid-build the engine is working on the configuration it was given, and
            // a change now would be half in that work and half out of it. Not the caller's mistake
            // and not ours: retry shortly, as for a document change in the same window.
            var state = matcher.Status.SystemState;
            if (state is SystemState.Loading or SystemState.Indexing)
                return ApiProblems.ShadowBusy(new ShadowBusyException(dataSetName, state).Message);
            // Likewise while a rebuild runs: applied in place the change would be on the engine
            // that is about to be swapped out, and gone with it.
            if (IndxServerInternalApi.Manager.IsShadowBuildInProgress(dataSetName, ctx.OwnerKey))
                return ApiProblems.ShadowBusy(new ShadowBusyException(dataSetName).Message);

            // If any proposed change requires rebuilding the index AND the engine is serving
            // searches, route via the shadow-swap path so live searches are not blocked. The
            // override is applied between Init and Load on the shadow so MakeSearchEngines builds
            // _indexableFields against the new Searchable set.
            // Also for a field getting its first role: see FieldConfigurationChange.
            bool needsReindex = IndxServer.Services.FieldConfigurationChange.NeedsRebuild(df, fields);
            // Before applying: afterwards the old configuration is gone and there is nothing left
            // to diff against.
            IndxServer.Services.FieldConfigurationChange.Announce(df, fields, dataSetName, ctx.OwnerKey);
            if (needsReindex && state == SystemState.Ready)
            {
                try
                {
                    IndxServerInternalApi.Manager.StartFieldConfigurationOnShadow(dataSetName, ctx.OwnerKey, fields);
                }
                catch (ShadowBusyException ex)
                {
                    return ApiProblems.ShadowBusy(ex.Message);
                }
                var status = IndxServerInternalApi.Manager.GetServerStatus(dataSetName, ctx.OwnerKey);
                return status == null ? ApiProblems.DatasetNotFound(dataSetName) : Accepted(status);
            }

            // Inline: only query-time flags changed, or engine is not yet Ready.
            try
            {
                var failed = IndxServer.Services.FieldConfigurationChange.ApplyInPlace(matcher, fields, dataSetName, ctx.OwnerKey);
                if (failed != null)
                    return ApiProblems.InvalidArgument($"Field '{failed}' does not exist in this dataset.");
            }
            catch (ArgumentException ex)
            {
                return ApiProblems.InvalidArgument(ex.Message);
            }
            // The engine said no to something the check above let through. Still the request it
            // refused, not a fault of ours: until Oct 2026 this was a 500.
            catch (InvalidOperationException ex)
            {
                return ApiProblems.InvalidArgument(ex.Message);
            }
            return NoContent();
        }

        /// <summary>
        /// GetFieldConfiguration returns the full configuration of every field in the dataset.
        /// Search level, not Read: a filter panel needs each field's type to know whether a
        /// selected value is a value filter or a range with equal limits, and the name lists it
        /// reads today do not carry it. A Search key can already search and read documents, so
        /// the types and sample values here reveal nothing it does not see.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpGet(DataSetRoute + "/fields/configuration")]
        public ActionResult<FieldProxy[]> GetFieldConfiguration(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            IServerSearchEngine? matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return matcher.GetFieldConfiguration();
        }

        /// <summary>
        /// Returns the dataset's declared key field — the JSON field whose value identifies each
        /// document (the primary key). Empty string means none is declared (the engine auto-generates
        /// keys). Required, when set, to be a numeric field.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/fields/key")]
        public ActionResult<string> GetKeyField(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            return IndxServerInternalApi.Manager.GetDeclaredKeyField(dataSetName, ctx.OwnerKey);
        }

        /// <summary>
        /// Declares the dataset's key field (the JSON field whose value is each document's primary key).
        /// Pass an empty string to clear it (auto-generated keys). The field must be numeric. The choice
        /// is preserved across reloads and applied on the next Load/replace; documents already loaded
        /// keep their existing keys until the data is reloaded.
        /// </summary>
        [HttpPut(DataSetRoute + "/fields/key")]
        public IActionResult SetKeyField(string teamName, string dataSetName, [FromBody] string fieldName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);

            var failure = IndxServerInternalApi.Manager.SetKeyField(
                dataSetName, ctx.OwnerKey, fieldName ?? "", out var needsReloadToReKey);
            if (failure != null)
                return ApiProblems.InvalidArgument(failure);
            return Ok(new { keyField = fieldName ?? "", needsReloadToReKey });
        }
    }
}
