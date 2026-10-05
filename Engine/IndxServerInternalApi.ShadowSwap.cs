using Indx.Api;
using Indx.Storage;
using Indx.Utilities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using IndxServer.Models;
namespace IndxServer.Engine
{
    /// <summary>
    /// Shadow-swap orchestration for heavy mutations.
    ///
    /// When a heavy mutation arrives on a dataset that is in <see cref="SystemState.Ready"/>,
    /// we build a fresh SearchEngine that shares the active instance's persistence, replay
    /// the document state via <see cref="IServerSearchEngine.LoadFromDatabaseSync"/>, apply
    /// the caller's mutation, re-index, and swap the new engine into the dictionary under
    /// <c>_dictionaryLock</c>. The previous engine is queued for disposal once its in-flight
    /// searches have drained (best-effort with a grace timeout).
    ///
    /// Single-flight semantics: a second heavy mutation on the same dataset while a shadow
    /// build is in progress throws <see cref="ShadowBusyException"/>, which the controller
    /// maps to HTTP 409.
    /// </summary>
    internal sealed partial class IndxServerInternalApi
    {
        private readonly ConcurrentDictionary<string, DateTime> _shadowBuildsInProgress = new();
        private readonly ConcurrentDictionary<string, ProcessMonitor> _shadowMonitors = new();

        /// <summary>How the last shadow build of a dataset ended: when, and why it failed (null
        /// when it did not). A build started over HTTP answers 202 before it is done, so this is
        /// the only place its caller can read the outcome; it is what
        /// <c>ShadowBuildFinishedUtc</c> and <c>ShadowBuildError</c> on GET status report.
        /// Cleared when the next build of the dataset starts.</summary>
        private sealed record ShadowOutcome(DateTime FinishedUtc, string? Error);
        private readonly ConcurrentDictionary<string, ShadowOutcome> _shadowOutcomes = new();

        /// <summary>Test seam: called on the build thread once a field-configuration build has
        /// claimed its dataset and before it builds, with the name of the dataset, so a test can
        /// hold it there or fail it. A small test dataset is otherwise rebuilt before a second
        /// request can be aimed at it.</summary>
        internal Action<string>? ShadowBuildStarting { get; set; }

        /// <summary>Claims the dataset's single shadow build, or throws
        /// <see cref="ShadowBusyException"/> when one is running. Paired with
        /// <see cref="ReleaseShadowBuild"/>.</summary>
        private void ClaimShadowBuild(string key, string dataSetName)
        {
            if (!_shadowBuildsInProgress.TryAdd(key, TimeProvider.GetUtcNow().UtcDateTime))
                throw new ShadowBusyException(dataSetName);
            _shadowOutcomes.TryRemove(key, out _);
        }

        /// <summary>Records how the build ended and gives the dataset up for the next one. In
        /// that order: a caller polling status must never see "no build running" before the
        /// outcome of the one that just ran is there to read.</summary>
        private void ReleaseShadowBuild(string key, string? error)
        {
            _shadowOutcomes[key] = new ShadowOutcome(TimeProvider.GetUtcNow().UtcDateTime, error);
            _shadowBuildsInProgress.TryRemove(key, out _);
        }
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
            Func<IServerSearchEngine, TResult> mutation)
        {
            var key = MakeKey(dataSetName, teamId);
            ClaimShadowBuild(key, dataSetName);

            SearchEngine? shadow = null;
            string? failure = null;
            try
            {
                SearchEngineInstance container;
                IServerSearchEngine original;
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
                IServerSearchEngine? swappedOut;
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
                failure = ex.Message;
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
                ReleaseShadowBuild(key, failure);
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
            Func<IServerSearchEngine, TResult> mutation)
        {
            IServerSearchEngine? matcher = FindSearchEngine(dataSetName, teamId);
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
        /// <remarks>Blocks until the swap is done and throws when the build fails: for the console,
        /// which runs it on a task of its own and shows the progress. A request must not wait on
        /// it - on a large dataset it is minutes - and uses
        /// <see cref="StartFieldConfigurationOnShadow"/>.</remarks>
        internal void RunFieldConfigurationOnShadow(
            string dataSetName,
            string teamId,
            FieldProxy[] fields)
        {
            var key = MakeKey(dataSetName, teamId);
            ClaimShadowBuild(key, dataSetName);
            BuildFieldConfigurationOnShadow(dataSetName, teamId, fields, key);
        }

        /// <summary>
        /// Starts the same build in the background and returns at once. The dataset is claimed
        /// before this returns, so <see cref="IsShadowBuildInProgress"/> is already true for the
        /// caller's first status poll, and a second start throws
        /// <see cref="ShadowBusyException"/> here, on the caller's thread. Everything after that
        /// is reported through status: in progress while it runs, then the finish time and, if it
        /// failed, the error. A failed build swaps nothing in; the dataset keeps serving with the
        /// configuration it had.
        /// </summary>
        internal void StartFieldConfigurationOnShadow(
            string dataSetName,
            string teamId,
            FieldProxy[] fields)
        {
            var key = MakeKey(dataSetName, teamId);
            ClaimShadowBuild(key, dataSetName);
            _ = Task.Run(() =>
            {
                // Logged and recorded as the outcome inside; nobody waits on this task.
                try { BuildFieldConfigurationOnShadow(dataSetName, teamId, fields, key); }
                catch { }
            });
        }

        /// <summary>The build itself, for a dataset the caller has claimed. Releases the claim.</summary>
        private void BuildFieldConfigurationOnShadow(
            string dataSetName,
            string teamId,
            FieldProxy[] fields,
            string key)
        {
            var monitor = new ProcessMonitor();
            _shadowMonitors[key] = monitor;

            SearchEngine? shadow = null;
            string? failure = null;
            try
            {
                ShadowBuildStarting?.Invoke(dataSetName);

                SearchEngineInstance container;
                IServerSearchEngine original;
                lock (_dictionaryLock)
                {
                    if (!_instances.TryGetValue(key, out var found) || found?.theInstance == null)
                        throw new InvalidOperationException($"No active instance for dataset '{dataSetName}'");
                    container = found;
                    original = found.theInstance;
                }

                // Before the build: the swap replaces the configuration this diffs against.
                var changedFields = Services.FieldConfigurationChange.ChangedFields(original.DocumentFields, fields);
                shadow = BuildShadowFrom(original, dataSetName, teamId, fields, monitor);
                monitor.WaitForCompletion();
                if (!monitor.Succeeded)
                    throw new InvalidOperationException(
                        $"Shadow indexing failed: {monitor.ErrorMessage ?? "unknown error"}", monitor.Exception);

                IServerSearchEngine? swappedOut;
                lock (_dictionaryLock)
                {
                    swappedOut = container.theInstance;
                    container.theInstance = shadow;
                }

                var df = shadow.DocumentFields;
                if (df != null)
                    shadow.Persistence?.SaveDocumentFields(df.GetSerialized());

                shadow = null;

                // Every change to a loaded dataset's field configuration ends here (seven fields/*
                // routes and the console); with no fields it is a plain rebuild, POST index on a
                // Ready dataset. Field names only, as configuration.
                if (changedFields.Length == 0)
                    ReportChange(dataSetName, teamId, Services.DatasetChangeKind.Reindex);
                else
                    ReportChange(dataSetName, teamId, Services.DatasetChangeKind.Fields,
                        new { fields = changedFields });

                if (swappedOut != null)
                    _ = Task.Run(() => DisposeAfterGraceAsync(swappedOut, dataSetName, teamId));
            }
            catch (Exception ex)
            {
                failure = ex.Message;
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
                ReleaseShadowBuild(key, failure);
            }
        }

        /// <summary>True while a shadow build is in progress for the given dataset.</summary>
        internal bool IsShadowBuildInProgress(string dataSetName, string teamId)
            => _shadowBuildsInProgress.ContainsKey(MakeKey(dataSetName, teamId));

        /// <summary>UTC timestamp at which the in-progress shadow build started, or null if none.</summary>
        internal DateTime? ShadowBuildStartedUtc(string dataSetName, string teamId)
            => _shadowBuildsInProgress.TryGetValue(MakeKey(dataSetName, teamId), out var started)
                ? started : null;

        /// <summary>UTC time at which the dataset's last shadow build ended, or null when none has
        /// ended since the server started or one is running now.</summary>
        internal DateTime? ShadowBuildFinishedUtc(string dataSetName, string teamId)
            => _shadowOutcomes.TryGetValue(MakeKey(dataSetName, teamId), out var outcome)
                ? outcome.FinishedUtc : null;

        /// <summary>Why the dataset's last shadow build failed, or null: it succeeded, none has
        /// run, or one is running now.</summary>
        internal string? ShadowBuildError(string dataSetName, string teamId)
            => _shadowOutcomes.TryGetValue(MakeKey(dataSetName, teamId), out var outcome)
                ? outcome.Error : null;

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
            IServerSearchEngine original,
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
                        $"Shadow Index ({phase}) failed: {monitor.ErrorMessage ?? "unknown error"}", monitor.Exception);
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
        private async Task DisposeAfterGraceAsync(IServerSearchEngine engine, string dataSetName, string teamId)
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
