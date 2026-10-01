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
    /// Single documents and batches: insert, update, patch, delete, lookup and count.
    /// Team scoping, authentication and the error contract are declared on
    /// <see cref="DatasetApiController"/>.
    /// </summary>
    public class DocumentsController(TeamContextResolver resolver, StatisticsService statistics) : DatasetApiController(resolver)
    {
        /// <summary>
        /// Counts a successful request's document changes into the dataset's statistics
        /// (Notes/statistics-design.md, "Change events"): the day's totals, and an event of its
        /// own for a mass change or a by-filter operation. Every mutating route calls it once,
        /// just before its success response; this controller is the only place documents change.
        /// </summary>
        private void Count(string dataSetName, string teamId, IServerSearchEngine engine, string operation,
            long inserted = 0, long updated = 0, long deleted = 0, bool byFilter = false) =>
            statistics.RecordDocuments(teamId, dataSetName, operation, inserted, updated, deleted,
                engine.Status.DocumentCount, byFilter);

        /// <summary>
        /// Deletes a document from the dataset by its key.
        /// </summary>
        [HttpDelete(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteJsonRecord(string teamName, string dataSetName, long documentKey)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "DeleteJsonRecord", SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.DeleteJsonRecord(documentKey);
            if (!result)
                return ApiProblems.DocumentNotFound(documentKey);
            Count(dataSetName, ctx.OwnerKey, matcher, "delete", deleted: 1);
            return NoContent();
        }

        /// <summary>
        /// Deletes documents from the dataset by their keys.
        /// </summary>
        [HttpDelete(DataSetRoute + "/documents")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteJsonRecords(string teamName, string dataSetName, [FromBody] long[] documentKeys)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "DeleteJsonRecords", engine =>
            {
                // All-or-nothing: validate every key before deleting anything, so one bad
                // key can't leave the batch half-deleted.
                var missing = documentKeys.Where(k => string.IsNullOrEmpty(engine.GetJsonDataOfKey(k))).ToArray();
                if (missing.Length > 0)
                    return ApiProblems.DocumentsNotFound(missing);
                foreach (var documentKey in documentKeys)
                {
                    var result = engine.DeleteJsonRecord(documentKey);
                    if (!result)
                        return ApiProblems.DocumentNotFound(documentKey);
                }
                Count(dataSetName, ctx.OwnerKey, engine, "delete", deleted: documentKeys.Length);
                return NoContent();
            }, SystemState.Ready);
        }

        /// <summary>
        /// Returns the raw json records as string[] for the keys.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Search)]
        [HttpPost(DataSetRoute + "/documents/lookup")]
        public ActionResult<string[]> GetJson(string teamName, string dataSetName, [FromBody] long[] keys)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            IServerSearchEngine? engine = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(engine, "GetJson", SystemState.Loaded, SystemState.Indexing, SystemState.Ready) is { } stateError)
                return stateError;
            var jsonStrings = new string[keys.Length];
            for (int i = 0; i < jsonStrings.Length; i++)
                jsonStrings[i] = engine.GetJsonDataOfKey(keys[i]);
            return jsonStrings;
        }

        /// <summary>
        /// Returns the number of JSON records in the database for the given dataset.
        /// </summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/documents/count")]
        public ActionResult<CountResponse> GetNumberOfJsonRecordsInDb(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (engine == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            return Ok(new CountResponse(engine.Persistence?.NumberOfJsonRecords() ?? 0));
        }

        /// <summary>
        /// Inserts one single Json record. The route key must match the document's key field
        /// (the engine keys documents from the body, so a disagreeing route would otherwise
        /// silently insert under a different key than the URL claims).
        /// </summary>
        [HttpPost(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        public ActionResult InsertJsonRecord(string teamName, string dataSetName, long documentKey, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "InsertJsonRecord", SystemState.Created, SystemState.Loaded, SystemState.Ready) is { } stateError)
                return stateError;
            if (TryReadBodyKey(matcher, jsonData, out long bodyKey) && bodyKey != documentKey)
                return ApiProblems.InvalidArgument(
                    $"The route addresses document {documentKey} but the body's key field says {bodyKey}. Nothing was inserted.");
            var result = matcher.InsertJsonRecord(jsonData, out string error2);
            if (!result)
                return ApiProblems.InvalidArgument(error2);
            Count(dataSetName, ctx.OwnerKey, matcher, "insert", inserted: 1);
            return StatusCode(StatusCodes.Status201Created);
        }

        /// <summary>
        /// Inserts new JSON records into the dataset.
        /// </summary>
        [HttpPost(DataSetRoute + "/documents")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        public ActionResult InsertJsonRecords(string teamName, string dataSetName, [FromBody] string[] jsonRecords)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "InsertJsonRecords", engine =>
            {
                var result = engine.InsertJsonRecords(jsonRecords, null, out string error2);
                if (!result)
                    return ApiProblems.InvalidArgument(error2);
                Count(dataSetName, ctx.OwnerKey, engine, "insert", inserted: jsonRecords.Length);
                return StatusCode(StatusCodes.Status201Created);
                // Created and Ready, not Loaded: the engine inserts into a Created dataset (it loads
                // and indexes the records) and into a Ready one, and refuses Loaded with "must be
                // Ready". Loaded used to be allowed here, so a loaded-but-not-indexed dataset got the
                // engine's refusal as a 400 invalidArgument - "fix your request" - where the API's
                // rule is that a wrong lifecycle state is a 409 invalidState with what to do next.
                // Under a full test run, where every class shares one dataset name, that 400 was
                // the one ShadowSwapTests.SecondBulkInsert saw instead of its 409.
            }, SystemState.Created, SystemState.Ready);
        }

        /// <summary>
        /// Updates existing JSON records in the dataset.
        /// </summary>
        [HttpPut(DataSetRoute + "/documents")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult UpdateJsonRecords(string teamName, string dataSetName, [FromBody] string[] jsonRecords)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "UpdateJsonRecords", engine =>
            {
                // The engine's batch update validates every record's key before mutating
                // anything, so a bad record rejects the whole batch instead of leaving it
                // half-applied (the old per-record loop aborted mid-way). Records whose key
                // matches no live document are skipped, by the engine's batch contract.
                var result = engine.UpdateJsonRecords(jsonRecords, null, out string error2);
                if (!result)
                    return ApiProblems.InvalidArgument(error2);
                // The records sent: the engine skips any whose key matches no document, so this is
                // an upper bound - the volume of the sync, which is what the count is for.
                Count(dataSetName, ctx.OwnerKey, engine, "update", updated: jsonRecords.Length);
                return NoContent();
            }, SystemState.Ready);
        }

        /// <summary>
        /// Updates one single Document. The route key must address an existing document and
        /// match the document's key field (the engine keys documents from the body, so a
        /// disagreeing route would otherwise silently update a different document than the
        /// URL claims).
        /// </summary>
        [HttpPut(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult UpdateJsonRecord(string teamName, string dataSetName, long documentKey, [FromBody] string jsonData)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "UpdateJsonRecord", SystemState.Ready) is { } stateError)
                return stateError;
            if (string.IsNullOrEmpty(matcher.GetJsonDataOfKey(documentKey)))
                return ApiProblems.DocumentNotFound(documentKey);
            if (TryReadBodyKey(matcher, jsonData, out long bodyKey) && bodyKey != documentKey)
                return ApiProblems.InvalidArgument(
                    $"The route addresses document {documentKey} but the body's key field says {bodyKey}. Nothing was updated.");
            var result = matcher.UpdateJsonRecord(jsonData, out string error2);
            if (!result)
            {
                // The engine skips records whose key matches no live document; for a single
                // update that means the addressed document is gone (deleted tombstone).
                if (error2.EndsWith("no valid records to update"))
                    return ApiProblems.DocumentNotFound(documentKey);
                return ApiProblems.InvalidArgument(error2);
            }
            Count(dataSetName, ctx.OwnerKey, matcher, "update", updated: 1);
            return NoContent();
        }

        /// <summary>
        /// Updates a single field on a document identified by its key.
        /// </summary>
        [HttpPatch(DataSetRoute + "/documents/{documentKey:long}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult UpdateField(string teamName, string dataSetName, long documentKey, [FromBody] UpdateFieldProxy update)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            var matcher = IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey);
            if (matcher == null)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (RequireState(matcher, "UpdateField", SystemState.Loaded, SystemState.Ready) is { } stateError)
                return stateError;
            var result = matcher.UpdateField(documentKey, update.FieldName, UnwrapJsonElement(update.Value)!, out string error2);
            if (!result)
                return ApiProblems.InvalidArgument(error2);
            Count(dataSetName, ctx.OwnerKey, matcher, "updateField", updated: 1);
            return NoContent();
        }

        /// <summary>
        /// Deletes all documents matching the given filter.
        /// </summary>
        [HttpPost(DataSetRoute + "/documents/delete-by-filter")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DeleteRecordsInFilter(string teamName, string dataSetName, [FromBody] FilterProxy filterProxy)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            return RunHeavy(dataSetName, ctx.OwnerKey, "DeleteRecordsInFilter", engine =>
            {
                var filter = ResolveFilter(engine, filterProxy, "filter", out var filterError);
                if (filter == null) return filterError!;
                engine.LoadFilters(new[] { filter });
                // The engine does not say how many it deleted; the count before and after does.
                long before = engine.Status.DocumentCount;
                engine.DeleteRecordsInFilter(filter);
                Count(dataSetName, ctx.OwnerKey, engine, "deleteByFilter",
                    deleted: Math.Max(0, before - engine.Status.DocumentCount), byFilter: true);
                return NoContent();
            }, SystemState.Ready);
        }

        /// <summary>
        /// Updates a field on all documents matching the given filter. Returns the number of updated documents.
        /// </summary>
        [HttpPost(DataSetRoute + "/documents/update-by-filter")]
        public ActionResult<CountResponse> UpdateFieldInFilter(string teamName, string dataSetName, [FromBody] FilterFieldUpdateProxy payload)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName))
                return ApiProblems.InvalidDatasetName(dataSetName);
            if (payload == null)
                return ApiProblems.InvalidArgument("A request body with 'filter', 'fieldName' and 'value' is required.");
            return RunHeavy(dataSetName, ctx.OwnerKey, "UpdateFieldInFilter", engine =>
            {
                var filter = ResolveFilter(engine, payload.Filter, "filter", out var filterError);
                if (filter == null) return filterError!;
                var count = engine.UpdateFieldInFilter(filter, payload.FieldName, UnwrapJsonElement(payload.Value)!, out string error2);
                if (count == 0 && !string.IsNullOrEmpty(error2))
                    return ApiProblems.InvalidArgument(error2);
                Count(dataSetName, ctx.OwnerKey, engine, "updateByFilter", updated: count, byFilter: true);
                return Ok(new CountResponse(count));
            }, SystemState.Ready);
        }

        /// <summary>
        /// Reads the dataset's key-field value out of a single JSON document body, so the
        /// single-record routes can honor their {documentKey} instead of silently ignoring it.
        /// Returns false when the body carries no readable key — the engine then reports its
        /// own, more specific error.
        /// </summary>
        private static bool TryReadBodyKey(IServerSearchEngine engine, string jsonData, out long key)
        {
            key = 0;
            var keyField = engine.DocumentFields?.NameOfDocumentKeyField;
            if (string.IsNullOrEmpty(keyField) || string.IsNullOrEmpty(jsonData))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty(keyField, out var el))
                    return false;
                return el.ValueKind switch
                {
                    JsonValueKind.Number => el.TryGetInt64(out key),
                    JsonValueKind.String => long.TryParse(el.GetString(), out key),
                    _ => false
                };
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
