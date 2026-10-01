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
        /// SetFieldConfiguration sets any combination of field properties (Searchable, Filterable,
        /// Facetable, Sortable, WordIndexing, Embeddable, PreloadFilters, Weight, BM25b, BM25k1)
        /// in one call. Nullable properties have replace semantics: null = leave untouched,
        /// any value (including false) = overwrite.
        /// </summary>
        [HttpPut(DataSetRoute + "/fields/configuration")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
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

            // If any proposed change requires rebuilding the index AND the engine is serving
            // searches, route via the shadow-swap path so live searches are not blocked. The
            // override is applied between Init and Load on the shadow so MakeSearchEngines builds
            // _indexableFields against the new Searchable set.
            // Also for a field getting its first role: see FieldConfigurationChange.
            bool needsReindex = IndxServer.Services.FieldConfigurationChange.NeedsRebuild(df, fields);
            // Before applying: afterwards the old configuration is gone and there is nothing left
            // to diff against.
            IndxServer.Services.FieldConfigurationChange.Announce(df, fields, dataSetName, ctx.OwnerKey);
            if (needsReindex && matcher.Status.SystemState == SystemState.Ready)
            {
                foreach (var cfg in fields)
                    if (df.GetField(cfg.FieldName) == null)
                        return ApiProblems.InvalidArgument(
                            $"Field '{cfg.FieldName}' does not exist in this dataset.");

                try
                {
                    IndxServerInternalApi.Manager.RunFieldConfigurationOnShadow(dataSetName, ctx.OwnerKey, fields);
                    return NoContent();
                }
                catch (ShadowBusyException ex)
                {
                    return ApiProblems.ShadowBusy(ex.Message);
                }
                // A value the engine refuses — a negative weight, a BM25b outside [0, 1], a
                // negative BM25k1. That is the caller's mistake, so it is a 400 with the reason,
                // not the 500 the global handler would otherwise produce. A 500 tells the caller
                // we broke and logs it as our incident, when the fix is on their side.
                catch (ArgumentException ex)
                {
                    return ApiProblems.InvalidArgument(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return ApiProblems.OperationFailed(ex.Message);
                }
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
            return NoContent();
        }

        /// <summary>Sets the Searchable property and weight on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/searchable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSearchableFields(string teamName, string dataSetName, [FromBody] (string Name, float Weight)[] fields)
        {
            // Checked here rather than left to the Field.Weight setter, for two reasons: the
            // setter throws, which SetFieldFlag would turn into a 500; and SetFieldFlag applies
            // field by field, so a throw partway through would leave the earlier fields already
            // changed. Refusing the whole call up front keeps it all-or-nothing.
            foreach (var f in fields)
                if (f.Weight < 0f)
                    return ApiProblems.InvalidArgument(
                        $"Weight for field '{f.Name}' is {f.Weight}; a field weight cannot be negative.");

            return SetFieldFlag(teamName, dataSetName, fields.Select(f => f.Name),
                (f, t) => { f.Searchable = true; f.Weight = fields.First(x => x.Name == t).Weight; });
        }

        /// <summary>Sets the Filterable property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/filterable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetFilterableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Filterable = true, nameof(Indx.Api.Field.Filterable));

        /// <summary>Sets the Facetable property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/facetable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetFacetableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Facetable = true);

        /// <summary>Sets the Sortable property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/sortable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetSortableFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.Sortable = true, nameof(Indx.Api.Field.Sortable));

        /// <summary>Sets the WordIndexing property on the specified fields.</summary>
        [HttpPut(DataSetRoute + "/fields/word-indexing")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetWordIndexingFields(string teamName, string dataSetName, [FromBody] string[] fields)
            => SetFieldFlag(teamName, dataSetName, fields, (f, _) => f.WordIndexing = true);

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

        /// <summary>
        /// Marks the specified fields as embeddable so that their vector values are indexed
        /// during the next Load. Must be called after AnalyzeStream and before LoadStream.
        /// </summary>
        [HttpPut(DataSetRoute + "/fields/embeddable")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult SetEmbeddableFields(string teamName, string dataSetName, [FromBody] string[] fields)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            // No lifecycle-state guard: SetEmbeddableFields is idempotent and may legitimately be
            // re-sent on an already-Ready dataset (it operates on DocumentFields, which the manager
            // null-checks). Adding a Created-only guard would wrongly reject that idempotent re-send.
            if (!IndxServerInternalApi.Manager.SetEmbeddableFields(fields, dataSetName, ctx.OwnerKey))
                return ApiProblems.InvalidArgument("SetEmbeddableFields failed — dataset not found or unknown field name");
            return NoContent();
        }

        /// <summary>Shared body for the legacy Set*Fields helpers — resolve, validate, mutate each field.</summary>
        private IActionResult SetFieldFlag(string teamName, string dataSetName, IEnumerable<string> fieldNames, Action<Indx.Api.Field, string> apply, string? roleNeedingType = null)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.FindSearchEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            var df = matcher.DocumentFields;
            if (df == null)
                return ApiProblems.InvalidArgument("The dataset has not been analyzed yet, so there are no fields to configure.");
            // Filterable and Sortable are the only roles that read Field.Type, and a field that was
            // null in every analyzed document has none. Refused up front for the same two reasons the
            // weight check above gives: the Field setter throws, which this method would turn into a
            // 500, and it applies field by field, so a throw partway through would leave the earlier
            // fields already changed.
            if (roleNeedingType != null)
                foreach (var name in fieldNames)
                    if (df.GetField(name) is { Type: System.Text.Json.JsonValueKind.Null })
                        return ApiProblems.InvalidArgument(
                            $"Field '{name}' has no type: it was null in every analyzed document, so it "
                            + $"cannot be made {roleNeedingType}. Searchable and Facetable do not need a type.");
            foreach (var name in fieldNames)
            {
                var f = df.GetField(name);
                if (f == null)
                    return ApiProblems.InvalidArgument($"Field '{name}' does not exist in this dataset.");
                apply(f, name);
            }
            // The legacy flag routes set roles directly; recorded like the configuration route,
            // and only on a Ready dataset (before the first index it is setup, not a change).
            if (matcher.Status.SystemState == SystemState.Ready)
                IndxServerInternalApi.Manager.ReportChange(dataSetName, ctx.OwnerKey,
                    IndxServer.Services.DatasetChangeKind.Fields, new { fields = fieldNames.Distinct().ToArray() });
            return NoContent();
        }
    }
}
