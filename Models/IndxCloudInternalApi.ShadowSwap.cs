using Indx.Api;
using Indx.Storage;
using Indx.Utilities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IndxCloudApi.Models
{
    /// <summary>
    /// Shadow-swap orchestration for heavy mutations.
    ///
    /// When a heavy mutation arrives on a dataset that is in <see cref="SystemState.Ready"/>,
    /// we build a fresh SearchEngine that shares the active instance's persistence, replay
    /// the document state via <see cref="ICloudSearchEngine.LoadFromDatabaseSync"/>, apply
    /// the caller's mutation, re-index, and swap the new engine into the dictionary under
    /// <c>_dictionaryLock</c>. The previous engine is queued for disposal once its in-flight
    /// searches have drained (best-effort with a grace timeout).
    ///
    /// Single-flight semantics: a second heavy mutation on the same dataset while a shadow
    /// build is in progress throws <see cref="ShadowBusyException"/>, which the controller
    /// maps to HTTP 409.
    /// </summary>
    internal sealed partial class IndxCloudInternalApi
    {
        private readonly ConcurrentDictionary<string, DateTime> _shadowBuildsInProgress = new();
        private readonly ConcurrentDictionary<string, ProcessMonitor> _shadowMonitors = new();
        private const int DisposalGraceSeconds = 30;
        private const int DisposalPollMilliseconds = 200;

        /// <summary>
        /// Waited once before the grace poll begins, covering searches that hold an
        /// engine reference but have not yet registered as active.
        /// </summary>
        private const int DisposalSettleMilliseconds = 500;

        /// <summary>
        /// Runs <paramref name="mutation"/> on a shadow SearchEngine and swaps it in
        /// atomically on success. The active instance keeps serving searches throughout.
        /// Throws <see cref="ShadowBusyException"/> if a build is already in progress for
        /// the same (dataSetName, teamId) key.
        ///
        /// Private so external callers (including Blazor pages in the same assembly)
        /// cannot reach the generic-callback surface and accidentally apply field-config
        /// mutations (Searchable/WordIndexing/Embeddable/BM25b/BM25k1) post-Load. Those
        /// must go through <see cref="RunFieldConfigurationOnShadow"/> which applies the
        /// override before Load so <c>MakeSearchEngines</c> sees the new set when it
        /// builds <c>_indexableFields</c>. Internal callers in this class route through
        /// <see cref="RunHeavyOnShadowIfReady{TResult}"/> for document mutations and
        /// <see cref="RunFieldConfigurationOnShadow"/> for field-config changes.
        /// </summary>
        private TResult RunMutationOnShadow<TResult>(
            string dataSetName,
            string teamId,
            Func<ICloudSearchEngine, TResult> mutation)
        {
            var key = MakeKey(dataSetName, teamId);
            if (!_shadowBuildsInProgress.TryAdd(key, DateTime.UtcNow))
                throw new ShadowBusyException(dataSetName);

            SearchEngine? shadow = null;
            try
            {
                SearchEngineInstance container;
                ICloudSearchEngine original;
                lock (_dictionaryLock)
                {
                    if (!_instances.TryGetValue(key, out var found) || found?.theInstance == null)
                        throw new InvalidOperationException($"No active instance for dataset '{dataSetName}'");
                    container = found;
                    original = found.theInstance;
                }

                // BuildShadowFrom returns a Ready instance (CreateInMemoryClone runs Index
                // internally), so the mutation can be applied directly.
                shadow = BuildShadowFrom(original, dataSetName, teamId);

                // Apply the caller's mutation on the now-Ready shadow.
                TResult result = mutation(shadow);

                // Re-index to capture any field-config changes the mutation may have made
                // (Searchable/BM25b/BM25k1/WordIndexing/Embeddable). For pure document
                // mutations (insert/update/delete) this is a no-op against an already
                // up-to-date index, but the safety guarantee is worth the cost.
                RunIndex(shadow, key, "post-mutation");

                // Atomic swap. The container reference returned by FindInstance keeps any
                // already-routed searches pointing at the same SearchEngineInstance; only
                // the inner theInstance pointer flips.
                ICloudSearchEngine? swappedOut;
                lock (_dictionaryLock)
                {
                    swappedOut = container.theInstance;
                    container.theInstance = shadow;
                }
                shadow = null; // ownership transferred to the container

                if (swappedOut != null)
                    _ = Task.Run(() => DisposeAfterGraceAsync(swappedOut, dataSetName, teamId));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Prefix}RunMutationOnShadow failed",
                    MakeLogPrefix(teamId, dataSetName));
                throw;
            }
            finally
            {
                // If we built a shadow but never swapped it in (mutation/index/swap threw),
                // dispose it immediately so its native pools are released.
                if (shadow != null)
                {
                    try { shadow.Dispose(); }
                    catch (Exception disposeEx)
                    {
                        _logger.LogError(disposeEx,
                            "{Prefix}Failed to dispose abandoned shadow",
                            MakeLogPrefix(teamId, dataSetName));
                    }
                }
                _shadowBuildsInProgress.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Resolves the engine for (dataSetName, teamId). If the engine is in Ready state,
        /// runs <paramref name="mutation"/> on a shadow instance (so live searches are not
        /// blocked) and swaps the result in atomically. Otherwise runs the mutation inline
        /// on the live engine.
        ///
        /// Throws <see cref="KeyNotFoundException"/> if no engine exists for the key, and
        /// propagates <see cref="ShadowBusyException"/> from a concurrent shadow build.
        /// The mutation callback's return value becomes the result, which preserves
        /// callsite-specific early-exit semantics (e.g. an HTTP BadRequest mid-loop).
        ///
        /// Intended for document-level mutations (insert/update/delete records,
        /// filter-scoped updates) — field configuration changes must go through
        /// <see cref="RunFieldConfigurationOnShadow"/> so they are applied pre-Load.
        /// </summary>
        internal TResult RunHeavyOnShadowIfReady<TResult>(
            string dataSetName,
            string teamId,
            Func<ICloudSearchEngine, TResult> mutation)
        {
            ICloudSearchEngine? matcher = FindSearchEngine(dataSetName, teamId);
            if (matcher == null)
                throw new KeyNotFoundException($"non existing dataset name: {dataSetName}");

            // Read the state once. RunHeavy already checked it via RequireState, but a concurrent
            // mutation can move it in between, and re-reading it per branch below would widen that
            // same window inside this method.
            var state = matcher.Status.SystemState;

            // Loading/Indexing means the engine is mid-work, not that the request is wrong. Falling
            // through to the direct call below would reach SearchEngine.InsertJsonRecords' own
            // `!= Ready` guard, come back false, and surface as 400 invalidArgument — telling the
            // client to fix its request when the truth is "retry shortly". Worse, it skips the
            // shadow-busy gate entirely, so the 409 the concurrent-mutation contract promises was
            // unreachable whenever the state had already left Ready.
            if (state is SystemState.Loading or SystemState.Indexing)
                throw new ShadowBusyException(dataSetName, state);

            // Created/Loaded still run directly: there is no live index to protect, and the engine
            // has its own handling for them. Only Ready earns a shadow.
            if (state != SystemState.Ready)
                return mutation(matcher);

            return RunMutationOnShadow(dataSetName, teamId, mutation);
        }

        /// <summary>
        /// Builds a shadow with the supplied field configuration applied between Init and
        /// Load, then atomically swaps it in. Use this for SetFieldConfiguration changes
        /// that flip Searchable/WordIndexing/Embeddable/BM25b/BM25k1: those flags are
        /// consumed during LoadSync to build _indexableFields, so they must be set before
        /// Load runs. Applying them via the post-mutation callback in
        /// <see cref="RunMutationOnShadow{TResult}"/> would leave _indexableFields in the
        /// pre-mutation shape and the change would silently have no search-time effect.
        /// </summary>
        internal void RunFieldConfigurationOnShadow(
            string dataSetName,
            string teamId,
            FieldProxy[] fields)
        {
            var key = MakeKey(dataSetName, teamId);
            if (!_shadowBuildsInProgress.TryAdd(key, DateTime.UtcNow))
                throw new ShadowBusyException(dataSetName);

            var monitor = new ProcessMonitor();
            _shadowMonitors[key] = monitor;

            SearchEngine? shadow = null;
            try
            {
                SearchEngineInstance container;
                ICloudSearchEngine original;
                lock (_dictionaryLock)
                {
                    if (!_instances.TryGetValue(key, out var found) || found?.theInstance == null)
                        throw new InvalidOperationException($"No active instance for dataset '{dataSetName}'");
                    container = found;
                    original = found.theInstance;
                }

                shadow = BuildShadowFrom(original, dataSetName, teamId, fields, monitor);
                monitor.WaitForCompletion();
                if (!monitor.Succeeded)
                    throw new InvalidOperationException(
                        $"Shadow indexing failed: {monitor.ErrorMessage ?? "unknown error"}");

                ICloudSearchEngine? swappedOut;
                lock (_dictionaryLock)
                {
                    swappedOut = container.theInstance;
                    container.theInstance = shadow;
                }

                var df = shadow.DocumentFields;
                if (df != null)
                    shadow.Persistence?.SaveDocumentFields(df.GetSerialized());

                shadow = null;

                if (swappedOut != null)
                    _ = Task.Run(() => DisposeAfterGraceAsync(swappedOut, dataSetName, teamId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Prefix}RunFieldConfigurationOnShadow failed",
                    MakeLogPrefix(teamId, dataSetName));
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
                        _logger.LogError(disposeEx,
                            "{Prefix}Failed to dispose abandoned shadow",
                            MakeLogPrefix(teamId, dataSetName));
                    }
                }
                _shadowBuildsInProgress.TryRemove(key, out _);
            }
        }

        /// <summary>True while a shadow build is in progress for the given dataset.</summary>
        internal bool IsShadowBuildInProgress(string dataSetName, string teamId)
            => _shadowBuildsInProgress.ContainsKey(MakeKey(dataSetName, teamId));

        /// <summary>UTC timestamp at which the in-progress shadow build started, or null if none.</summary>
        internal DateTime? ShadowBuildStartedUtc(string dataSetName, string teamId)
            => _shadowBuildsInProgress.TryGetValue(MakeKey(dataSetName, teamId), out var started)
                ? started : null;

        /// <summary>Progress percentage (0–100) of the current shadow build's index phase, or 0 if none.</summary>
        internal int GetShadowBuildPercent(string dataSetName, string teamId)
            => _shadowMonitors.TryGetValue(MakeKey(dataSetName, teamId), out var m) ? m.ProgressPercent : 0;

        /// <summary>
        /// Builds a shadow SearchEngine from the original's live in-memory state via
        /// <see cref="SearchEngine.CreateInMemoryClone"/>, then attaches a fresh
        /// SQLite connection so mutations applied to the shadow persist independently.
        /// No SQLite roundtrip is involved in copying the document set — only the
        /// subsequent mutation writes.
        /// </summary>
        private SearchEngine BuildShadowFrom(
            ICloudSearchEngine original,
            string dataSetName,
            string teamId,
            FieldProxy[]? fieldOverrides = null,
            ProcessMonitor? monitor = null)
        {
            if (original is not SearchEngine concreteOriginal)
                throw new InvalidOperationException(
                    $"Cannot build shadow for '{dataSetName}': original is not a SearchEngine instance");

            var shadow = concreteOriginal.CreateInMemoryClone(monitor: monitor, fieldOverrides: fieldOverrides);

            // Attach a fresh Persistence pointing at the same SQLite file. SQLite supports
            // multiple connections; the original's persistence is left alone so disposing
            // the original after the swap does not break the shadow.
            shadow.Persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId);

            return shadow;
        }

        private void RunIndex(SearchEngine shadow, string key, string phase)
        {
            var monitor = new ProcessMonitor();
            _shadowMonitors[key] = monitor;
            try
            {
                shadow.Index(monitor: monitor);
                monitor.WaitForCompletion();
                if (!monitor.Succeeded)
                    throw new InvalidOperationException(
                        $"Shadow Index ({phase}) failed: {monitor.ErrorMessage ?? "unknown error"}");
            }
            finally
            {
                _shadowMonitors.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Waits up to <see cref="DisposalGraceSeconds"/> for the engine's in-flight
        /// searches to drain, then disposes it. Failure to drain within the grace period
        /// is logged and we dispose anyway — pending searches will fail their next pool
        /// access and propagate the disposal to the caller.
        /// </summary>
        private async Task DisposeAfterGraceAsync(ICloudSearchEngine engine, string dataSetName, string teamId)
        {
            try
            {
                // Settle first, then poll. A search registers as active only once it
                // takes a thread slot inside Search(), which is after ResolveEngine
                // handed it this engine — so immediately after a swap the counter can
                // read zero while a search is already on its way in. Breaking on that
                // zero would dispose the engine underneath it.
                await Task.Delay(DisposalSettleMilliseconds).ConfigureAwait(false);

                var deadline = DateTime.UtcNow.AddSeconds(DisposalGraceSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (!engine.HasActiveSearches())
                        break;
                    await Task.Delay(DisposalPollMilliseconds).ConfigureAwait(false);
                }

                engine.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Prefix}Failed to dispose swapped-out engine after grace period",
                    MakeLogPrefix(teamId, dataSetName));
            }
        }
    }
}
