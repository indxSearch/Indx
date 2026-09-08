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
    /// <param name="Added">Fields in the new JSON that were not previously configured (they load unconfigured).</param>
    /// <param name="Removed">Previously configured fields absent from the new JSON (their config is dropped).</param>
    /// <param name="TypeChanged">Fields whose detected type changed (their roles are reset).</param>
    /// <param name="LostRoles">Removed or retyped fields that had a role (searchable, filterable, facetable,
    /// sortable) with the roles they carried, as "name (roles)" — the changes a user must know about, since
    /// searches and filters that relied on them now silently miss.</param>
    /// <param name="KeyFieldFallback">Set when the dataset's declared key field is absent from the new data:
    /// the engine fell back to its default key ("id" if present, else auto-generated), so document keys may
    /// differ from before. Null when the declared key applied.</param>
    public sealed record ReplaceSchemaChange(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> TypeChanged,
        IReadOnlyList<string> LostRoles,
        string? KeyFieldFallback);

    /// <summary>Which step of a running replace the dataset is in, for the UI. <c>Percent</c> is the
    /// progress of the current step where the step reports it (analyze, load, index), else -1.</summary>
    public enum ReplaceStep { Preparing, Analyzing, Reconciling, Loading, Indexing, Swapping, Done }

    public sealed record ReplaceProgress(ReplaceStep Step, int Percent);

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
            _replaceProgress[key] = new ReplaceProgress(ReplaceStep.Preparing, -1);

            SearchEngine? shadow = null;
            try
            {
                // GetOrCreateInstance (NOT ResolveEngine) gives the container shell without
                // auto-loading the old documents from the database — we're replacing them.
                var container = GetOrCreateInstance(dataSetName, teamId);
                if (container == null)
                    throw new DataSetNotFoundException(dataSetName);

                ReplaceSchemaChange summary;
                (shadow, summary) = BuildShadowFromJson(dataSetName, teamId, jsonStream, monitor,
                    progress => _replaceProgress[key] = progress);

                // Atomic install. In-flight searches on the old engine keep working via the
                // SearchEngineInstance indirection; only the inner pointer flips.
                _replaceProgress[key] = new ReplaceProgress(ReplaceStep.Swapping, -1);
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

                _replaceProgress[key] = new ReplaceProgress(ReplaceStep.Done, 100);
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
                _replaceProgress.TryRemove(key, out _);
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReplaceProgress> _replaceProgress = new();

        /// <summary>The current step of a replace running on the dataset, or null when none is.</summary>
        internal ReplaceProgress? GetReplaceProgress(string dataSetName, string teamId)
            => _replaceProgress.TryGetValue(MakeKey(dataSetName, teamId), out var p) ? p : null;

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
            string dataSetName, string teamId, Stream jsonStream, ProcessMonitor monitor,
            Action<ReplaceProgress>? report = null)
        {
            report ??= _ => { };
            ConfigurationParameters configuration;
            using (var cfgRead = new Persistence(SearchDbConnectionString, dataSetName, teamId))
                configuration = ResolveConfiguration(cfgRead.ReadDataSetConfiguration()
                    ?? throw new InvalidOperationException($"Dataset '{dataSetName}' has no configuration"), dataSetName);

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
                report(new ReplaceProgress(ReplaceStep.Analyzing, 0));
                var initMonitor = new ProcessMonitor();
                shadow.Init(jsonStream, initMonitor);
                WaitReporting(initMonitor, p => report(new ReplaceProgress(ReplaceStep.Analyzing, p)));
                if (!initMonitor.Succeeded)
                    throw new InvalidOperationException($"Replace analyze failed: {initMonitor.ErrorMessage ?? "unknown error"}");

                report(new ReplaceProgress(ReplaceStep.Reconciling, -1));
                // Re-key from the declared field. If the new data no longer has it, the engine keys
                // by its default instead (what an undeclared dataset does) — reported, not refused.
                string? keyFallback = null;
                if (DeclaredKeyFieldIsMissing(shadow, dataSetName, teamId, out var declaredKey))
                    keyFallback = $"The declared key field '{declaredKey}' is not in the new data; documents were keyed by the engine default instead (an 'id' field if present, otherwise auto-generated).";
                else
                    ApplyDeclaredKeyField(shadow, dataSetName, teamId);
                var (carry, summary) = ReconcileFieldConfig(oldConfig, shadow.GetFieldConfiguration());
                summary = summary with { KeyFieldFallback = keyFallback };
                ApplyCarry(shadow, carry);

                // Reconcile passed → commit for real. The lib's external Load is atomic (clears +
                // appends in one transaction, rolls back on failure — 590c2c58), and the caller only
                // swaps this engine in after a successful build, so no in-memory dry-run is needed.
                shadow.Persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId);
                jsonStream.Position = 0;
                report(new ReplaceProgress(ReplaceStep.Loading, 0));
                var loadMonitor = new ProcessMonitor();
                shadow.Load(jsonStream, loadMonitor);
                WaitReporting(loadMonitor, p => report(new ReplaceProgress(ReplaceStep.Loading, p)));
                if (!loadMonitor.Succeeded)
                    throw new InvalidOperationException($"Replace load failed: {loadMonitor.ErrorMessage ?? "unknown error"}");

                report(new ReplaceProgress(ReplaceStep.Indexing, 0));
                shadow.Index(monitor);
                WaitReporting(monitor, p => report(new ReplaceProgress(ReplaceStep.Indexing, p)));
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

        /// <summary>Blocks until the monitor completes, forwarding its percent as it moves. The lib's
        /// Init/Load/Index dispatch to a background thread and return at once; the synchronous
        /// WaitForCompletion is the one wait that observes MarkStarted correctly (see InitFromStreamAsync).</summary>
        private static void WaitReporting(ProcessMonitor monitor, Action<int> onPercent)
        {
            var last = -1;
            using var done = new System.Threading.ManualResetEventSlim(false);
            var waiter = Task.Run(() => { try { monitor.WaitForCompletion(); } finally { done.Set(); } });
            while (!done.Wait(150))
            {
                var p = monitor.ProgressPercent;
                if (p != last) { last = p; onPercent(p); }
            }
            waiter.GetAwaiter().GetResult();
            onPercent(monitor.ProgressPercent);
        }

        private SearchEngine NewReplaceEngine(ConfigurationParameters configuration, string dataSetName, string teamId) =>
            new SearchEngine(
                MakeLogPrefix(teamId, dataSetName),
                Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
                configuration,
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
        private static List<string> RoleNames(FieldProxy f)
        {
            var r = new List<string>();
            if (f.Searchable == true) r.Add("searchable");
            if (f.Filterable == true) r.Add("filterable");
            if (f.Facetable == true) r.Add("facetable");
            if (f.Sortable == true) r.Add("sortable");
            return r;
        }

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

            // Roles that no longer apply: a removed or retyped field that was searchable/filterable/
            // facetable/sortable. Reported as "name (roles)" so the UI can warn precisely.
            var lostRoles = oldConfig
                .Where(o => !newByName.ContainsKey(o.FieldName) || typeChanged.Contains(o.FieldName))
                .Select(o => (o.FieldName, Roles: RoleNames(o)))
                .Where(x => x.Roles.Count > 0)
                .Select(x => $"{x.FieldName} ({string.Join(", ", x.Roles)})")
                .ToList();

            return (carry.ToArray(), new ReplaceSchemaChange(added, removed, typeChanged, lostRoles, null));
        }
    }
}
