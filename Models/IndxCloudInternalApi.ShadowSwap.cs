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
        /// Runs <paramref name="mutation"/> on a shadow SearchEngine and swaps it in
        /// atomically on success. The active instance keeps serving searches throughout.
        /// Throws <see cref="ShadowBusyException"/> if a build is already in progress for
        /// the same (dataSetName, userId) key.
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
            string userId,
            Func<ICloudSearchEngine, TResult> mutation)
        {
            var key = MakeKey(dataSetName, userId);
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
                shadow = BuildShadowFrom(original, dataSetName, userId);

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
                    _ = Task.Run(() => DisposeAfterGraceAsync(swappedOut, dataSetName, userId));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Prefix}RunMutationOnShadow failed",
                    MakeLogPrefix(userId, dataSetName));
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
                            MakeLogPrefix(userId, dataSetName));
                    }
                }
                _shadowBuildsInProgress.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Resolves the engine for (dataSetName, userId). If the engine is in Ready state,
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
            string userId,
            Func<ICloudSearchEngine, TResult> mutation)
        {
            ICloudSearchEngine? matcher = FindSearchEngine(dataSetName, userId);
            if (matcher == null)
                throw new KeyNotFoundException($"non existing dataset name: {dataSetName}");

            if (matcher.Status.SystemState != SystemState.Ready)
                return mutation(matcher);

            return RunMutationOnShadow(dataSetName, userId, mutation);
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
            string userId,
            FieldProxy[] fields)
        {
            var key = MakeKey(dataSetName, userId);
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

                shadow = BuildShadowFrom(original, dataSetName, userId, fields);

                ICloudSearchEngine? swappedOut;
                lock (_dictionaryLock)
                {
                    swappedOut = container.theInstance;
                    container.theInstance = shadow;
                }
                shadow = null;

                if (swappedOut != null)
                    _ = Task.Run(() => DisposeAfterGraceAsync(swappedOut, dataSetName, userId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Prefix}RunFieldConfigurationOnShadow failed",
                    MakeLogPrefix(userId, dataSetName));
                throw;
            }
            finally
            {
                if (shadow != null)
                {
                    try { shadow.Dispose(); }
                    catch (Exception disposeEx)
                    {
                        _logger.LogError(disposeEx,
                            "{Prefix}Failed to dispose abandoned shadow",
                            MakeLogPrefix(userId, dataSetName));
                    }
                }
                _shadowBuildsInProgress.TryRemove(key, out _);
            }
        }

        /// <summary>True while a shadow build is in progress for the given dataset.</summary>
        internal bool IsShadowBuildInProgress(string dataSetName, string userId)
            => _shadowBuildsInProgress.ContainsKey(MakeKey(dataSetName, userId));

        /// <summary>UTC timestamp at which the in-progress shadow build started, or null if none.</summary>
        internal DateTime? ShadowBuildStartedUtc(string dataSetName, string userId)
            => _shadowBuildsInProgress.TryGetValue(MakeKey(dataSetName, userId), out var started)
                ? started : null;

        /// <summary>Progress percentage (0–100) of the current shadow build's index phase, or 0 if none.</summary>
        internal int GetShadowBuildPercent(string dataSetName, string userId)
            => _shadowMonitors.TryGetValue(MakeKey(dataSetName, userId), out var m) ? m.ProgressPercent : 0;

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
            string userId,
            FieldProxy[]? fieldOverrides = null)
        {
            if (original is not SearchEngine concreteOriginal)
                throw new InvalidOperationException(
                    $"Cannot build shadow for '{dataSetName}': original is not a SearchEngine instance");

            var shadow = concreteOriginal.CreateInMemoryClone(fieldOverrides: fieldOverrides);

            // Attach a fresh Persistence pointing at the same SQLite file. SQLite supports
            // multiple connections; the original's persistence is left alone so disposing
            // the original after the swap does not break the shadow.
            shadow.Persistence = new Persistence(SearchDbConnectionString, dataSetName, userId);

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
        private async Task DisposeAfterGraceAsync(ICloudSearchEngine engine, string dataSetName, string userId)
        {
            try
            {
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
                    MakeLogPrefix(userId, dataSetName));
            }
        }
    }
}
