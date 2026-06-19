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
        /// Builds a fresh, Ready engine from <paramref name="jsonStream"/>, carrying over the
        /// dataset's field config (reconciled against the new schema).
        ///
        /// Single pass: the engine's external Load is now atomic (the lib clears + appends in one
        /// transaction and rolls back on failure — <c>590c2c58</c>), and the caller only swaps this
        /// engine in after a successful build, so a failed build leaves the persisted dataset and the
        /// live engine untouched. No in-memory dry-run pass is needed for safety.
        ///
        /// The stream is re-read (Init resets to 0 after; Load resets to 0 before), so it must be
        /// seekable/buffered.
        /// </summary>
        private (SearchEngine shadow, ReplaceSchemaChange summary) BuildShadowFromJson(
            string dataSetName, string teamId, Stream jsonStream, ProcessMonitor monitor)
        {
            int configuration;
            using (var cfgRead = new Persistence(SearchDbConnectionString, dataSetName, teamId))
                configuration = (int)(cfgRead.ReadDataSetConfiguration()
                    ?? throw new InvalidOperationException($"Dataset '{dataSetName}' has no configuration"));

            var shadow = NewReplaceEngine(configuration, dataSetName, teamId);
            try
            {
                // Current field config — restored from the db without loading any documents.
                FieldProxy[] oldConfig = shadow.LoadDocumentFieldsFromDb()
                    ? shadow.GetFieldConfiguration()
                    : Array.Empty<FieldProxy>();

                // Detach persistence while we Init + reconcile (which can reject the new schema). This
                // phase must hold no db connection, so a rejected build leaves the dataset's db
                // completely untouched — no lingering lock to break the next access.
                shadow.Persistence?.Dispose();
                shadow.Persistence = null;

                jsonStream.Position = 0;
                shadow.Init(jsonStream);
                ApplyDeclaredKeyField(shadow, dataSetName, teamId); // re-key from the declared field
                var (carry, summary) = ReconcileFieldConfig(oldConfig, shadow.GetFieldConfiguration());
                ApplyCarry(shadow, carry);

                // Reconcile passed → commit for real. The lib's external Load is atomic (clears +
                // appends in one transaction, rolls back on failure — 590c2c58), and the caller only
                // swaps this engine in after a successful build, so no in-memory dry-run is needed.
                shadow.Persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId);
                jsonStream.Position = 0;
                shadow.Load(jsonStream);
                shadow.Index(monitor);
                monitor.WaitForCompletion();
                if (!monitor.Succeeded)
                    throw new InvalidOperationException($"Replace index failed: {monitor.ErrorMessage ?? "unknown error"}");

                return (shadow, summary);
            }
            catch
            {
                shadow.Dispose();
                throw;
            }
        }

        private SearchEngine NewReplaceEngine(int configuration, string dataSetName, string teamId) =>
            new SearchEngine(
                MakeLogPrefix(teamId, dataSetName),
                Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
                (ConfigurationProfile)configuration,
                GetLicensePath())
            {
                Persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId)
            };

        private static void ApplyCarry(SearchEngine engine, FieldProxy[] carry)
        {
            if (carry.Length == 0) return;
            var err = engine.SetFieldConfiguration(carry);
            if (!string.IsNullOrEmpty(err))
                throw new InvalidOperationException($"Replace field-config carry-over failed: {err}");
        }

        /// <summary>
        /// Carries old field roles onto fields that survive by name AND type; returns the carry set
        /// plus a schema-change summary. Throws if the new schema keeps none of the currently
        /// searchable fields (nothing to index — fail clearly instead of "no documents to load").
        /// </summary>
        private static (FieldProxy[] carry, ReplaceSchemaChange summary) ReconcileFieldConfig(
            FieldProxy[] oldConfig, FieldProxy[] newSchema)
        {
            var newByName = newSchema.ToDictionary(f => f.FieldName, StringComparer.Ordinal);
            var carry = new List<FieldProxy>();
            var typeChanged = new List<string>();
            foreach (var o in oldConfig)
            {
                if (!newByName.TryGetValue(o.FieldName, out var n)) continue; // removed
                if (string.Equals(o.FieldType ?? "", n.FieldType ?? "", StringComparison.OrdinalIgnoreCase))
                    carry.Add(o);
                else
                    typeChanged.Add(o.FieldName);
            }
            var oldNames = oldConfig.Select(o => o.FieldName).ToHashSet(StringComparer.Ordinal);
            var added = newSchema.Where(n => !oldNames.Contains(n.FieldName)).Select(n => n.FieldName).ToList();
            var removed = oldConfig.Where(o => !newByName.ContainsKey(o.FieldName)).Select(o => o.FieldName).ToList();

            if (oldConfig.Any(o => o.Searchable == true) && !carry.Any(f => f.Searchable == true))
                throw new InvalidOperationException(
                    "The new data keeps none of this dataset's searchable fields, so it can't be indexed. " +
                    "Replace preserves the existing field configuration — the new JSON must include at least " +
                    "one of the currently searchable fields. To load a different schema, configure the " +
                    "dataset's fields for it first.");

            return (carry.ToArray(), new ReplaceSchemaChange(added, removed, typeChanged));
        }
    }
}
