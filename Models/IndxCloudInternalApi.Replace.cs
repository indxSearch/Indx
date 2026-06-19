using Indx.Api;
using Indx.Storage;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace IndxCloudApi.Models
{
    /// <summary>Summary of how the new JSON's schema differed from the dataset's current field config.</summary>
    public sealed record ReplaceSchemaChange(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> TypeChanged);

    /// <summary>
    /// Atomic full-dataset replace: builds a fresh engine from a new JSON stream, carries over the
    /// dataset's field config (reconciled against the new schema), and swaps it in — preserving the
    /// dataset's identity, boost rules and description (those are keyed by name in separate stores
    /// and are never touched here). Reuses the shadow-swap orchestration. State-transparent: works on
    /// a Ready, hibernated or idle-evicted dataset without ever reloading the old documents.
    /// </summary>
    internal sealed partial class IndxCloudInternalApi
    {
        /// <summary>
        /// Replaces all documents in the dataset with the contents of <paramref name="jsonStream"/>.
        /// Returns the schema-change summary. The old dataset keeps serving until the swap; on any
        /// build failure the old dataset is left untouched (nothing is swapped in).
        /// </summary>
        internal ReplaceSchemaChange RunReplaceFromJson(string dataSetName, string teamId, Stream jsonStream)
        {
            var key = MakeKey(dataSetName, teamId);
            if (!_shadowBuildsInProgress.TryAdd(key, TimeProvider.GetUtcNow().UtcDateTime))
                throw new ShadowBusyException(dataSetName);

            var monitor = new ProcessMonitor();
            _shadowMonitors[key] = monitor;

            SearchEngine? shadow = null;
            try
            {
                // GetOrCreateInstance (NOT ResolveEngine) gives the container shell without
                // auto-loading the old documents from the database — we're replacing them.
                var container = GetOrCreateInstance(dataSetName, teamId);
                if (container == null)
                    throw new InvalidOperationException($"Dataset '{dataSetName}' does not exist");

                ReplaceSchemaChange summary;
                (shadow, summary) = BuildShadowFromJson(dataSetName, teamId, jsonStream, monitor);

                // Atomic install. In-flight searches on the old engine keep working via the
                // SearchEngineInstance indirection; only the inner pointer flips.
                ICloudSearchEngine? swappedOut;
                lock (_dictionaryLock)
                {
                    swappedOut = container.theInstance;
                    container.theInstance = shadow;
                }

                // Persist the reconciled field config so a future wake restores it.
                var df = shadow.DocumentFields;
                if (df != null)
                    shadow.Persistence?.SaveDocumentFields(df.GetSerialized());

                container.Touch(TimeProvider.GetUtcNow());
                shadow = null; // ownership transferred

                if (swappedOut != null)
                    _ = Task.Run(() => DisposeAfterGraceAsync(swappedOut, dataSetName, teamId));

                return summary;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Prefix}RunReplaceFromJson failed", MakeLogPrefix(teamId, dataSetName));
                throw;
            }
            finally
            {
                _shadowMonitors.TryRemove(key, out _);
                if (shadow != null)
                {
                    try { shadow.Dispose(); }
                    catch (Exception disposeEx)
                    {
                        _logger.LogError(disposeEx, "{Prefix}Failed to dispose abandoned replace shadow",
                            MakeLogPrefix(teamId, dataSetName));
                    }
                }
                _shadowBuildsInProgress.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Builds a fresh, Ready engine from <paramref name="jsonStream"/>: read the current field
        /// config (no doc load) → Init the new JSON → carry over config for fields that survive by
        /// name+type → Load → Index. The stream is read twice (Init resets to 0 after, Load resets to
        /// 0 before), so it must be seekable/buffered.
        /// </summary>
        private (SearchEngine shadow, ReplaceSchemaChange summary) BuildShadowFromJson(
            string dataSetName, string teamId, Stream jsonStream, ProcessMonitor monitor)
        {
            var persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId);
            var configuration = persistence.ReadDataSetConfiguration()
                ?? throw new InvalidOperationException($"Dataset '{dataSetName}' has no configuration");

            var shadow = new SearchEngine(
                MakeLogPrefix(teamId, dataSetName),
                Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
                (int)configuration,
                GetLicensePath())
            {
                Persistence = persistence
            };

            // Current (old) field config — restored from the db without loading any documents.
            FieldProxy[] oldConfig = shadow.LoadDocumentFieldsFromDb()
                ? shadow.GetFieldConfiguration()
                : Array.Empty<FieldProxy>();

            // Detect the new schema (default key field "id", matching today's analyze path).
            shadow.Init(jsonStream);
            var newSchema = shadow.GetFieldConfiguration();
            var newByName = newSchema.ToDictionary(f => f.FieldName, StringComparer.Ordinal);

            // Reconcile: carry old roles onto fields that survive by name AND type.
            var carry = new List<FieldProxy>();
            var typeChanged = new List<string>();
            foreach (var o in oldConfig)
            {
                if (!newByName.TryGetValue(o.FieldName, out var n)) continue; // removed
                if (string.Equals(o.FieldType ?? "", n.FieldType ?? "", StringComparison.OrdinalIgnoreCase))
                    carry.Add(o);
                else
                    typeChanged.Add(o.FieldName); // reset (don't carry roles onto a changed type)
            }
            var oldNames = oldConfig.Select(o => o.FieldName).ToHashSet(StringComparer.Ordinal);
            var added = newSchema.Where(n => !oldNames.Contains(n.FieldName)).Select(n => n.FieldName).ToList();
            var removed = oldConfig.Where(o => !newByName.ContainsKey(o.FieldName)).Select(o => o.FieldName).ToList();

            // Replace preserves the existing field config; if the new JSON keeps none of the
            // currently-searchable fields there is nothing to index. Fail with a clear message
            // rather than the cryptic "no documents to load" from the index step.
            if (oldConfig.Any(o => o.Searchable == true) && !carry.Any(f => f.Searchable == true))
                throw new InvalidOperationException(
                    "The new data keeps none of this dataset's searchable fields, so it can't be indexed. " +
                    "Replace preserves the existing field configuration — the new JSON must include at least " +
                    "one of the currently searchable fields. To load a different schema, configure the " +
                    "dataset's fields for it first.");

            if (carry.Count > 0)
            {
                var err = shadow.SetFieldConfiguration(carry.ToArray());
                if (!string.IsNullOrEmpty(err))
                    throw new InvalidOperationException($"Replace field-config carry-over failed: {err}");
            }

            // Load the new documents (clears + persists in the engine's external-load path) and index.
            shadow.Load(jsonStream);
            shadow.Index(monitor);
            monitor.WaitForCompletion();
            if (!monitor.Succeeded)
                throw new InvalidOperationException($"Replace index failed: {monitor.ErrorMessage ?? "unknown error"}");

            return (shadow, new ReplaceSchemaChange(added, removed, typeChanged));
        }
    }
}
