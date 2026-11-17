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
            // Under the dataset's write gate, so no small write is between its check and its write
            // when the build claims the dataset (RunSmallWrite).
            lock (WriteGate(key))
            {
                if (!_shadowBuildsInProgress.TryAdd(key, TimeProvider.GetUtcNow().UtcDateTime))
                    throw new ShadowBusyException(dataSetName);
            }
            _shadowOutcomes.TryRemove(key, out _);
        }

        /// <summary>One lock per dataset (12 Nov 2026), held by a small write for its check and its
        /// write, and by a build while it claims the dataset.</summary>
        private object WriteGate(string key) => _writeGates.GetOrAdd(key, _ => new object());

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _writeGates = new();

        /// <summary>
        /// A write that changes the live engine in place (one document, one field, the synonym list,
        /// a small webhook call), refused with <see cref="ShadowBusyException"/> while a rebuild runs.
        /// A rebuild clones the engine and swaps the clone in, so a write on the live engine after the
        /// clone was taken would be gone at the swap. The check and the write hold the dataset's write
        /// gate, which the build holds to claim the dataset: a write is done before the build claims
        /// it, and is in the clone, or it sees the build and is refused. Until 12 Nov 2026 the check
        /// came before the write with nothing held, and a write that passed it just before a build
        /// claimed the dataset could land after the clone.
        /// <para>The write is handed the engine that is live inside the gate (16 Nov 2026). Until
        /// then the caller wrote to the engine it had resolved before the gate: a build that swapped
        /// and released between the two left the busy check passing and the write going to the
        /// retired engine, acknowledged and not in the live index. Throws
        /// <see cref="KeyNotFoundException"/> when the dataset is gone.</para>
        /// </summary>
        internal T RunSmallWrite<T>(string dataSetName, string teamId, Func<IServerSearchEngine, T> write)
        {
            var key = MakeKey(dataSetName, teamId);
            lock (WriteGate(key))
            {
                if (_shadowBuildsInProgress.ContainsKey(key))
                    throw new ShadowBusyException(dataSetName);
                var live = FindInstance(dataSetName, teamId)
                    ?? throw new KeyNotFoundException($"non existing dataset name: {dataSetName}");
                return write(live);
            }
        }

        /// <summary>
        /// <see cref="RunSmallWrite{T}"/> for a write of documents, refused with
        /// <see cref="DatasetAsleepException"/> while the dataset sleeps with records in the store
        /// (<see cref="RefuseDocumentsWhileAsleep"/>).
        /// </summary>
        internal T RunSmallDocumentWrite<T>(string dataSetName, string teamId, Func<IServerSearchEngine, T> write) =>
            RunSmallWrite(dataSetName, teamId, live =>
            {
                RefuseDocumentsWhileAsleep(live, dataSetName);
                return write(live);
            });

        /// <summary>True for an engine that is unloaded while its dataset has records in the store:
        /// hibernated and not woken by the server (keep-alive Off), or a wake that failed.</summary>
        internal static bool IsAsleepWithRecords(IServerSearchEngine engine) =>
            engine.Status.SystemState == SystemState.Created && (engine.Persistence?.NumberOfJsonRecords() ?? 0) > 0;

        /// <summary>
        /// Every write of documents passes here before it reaches the engine (16 Nov 2026). An
        /// unloaded engine that still holds its field configuration, which is what hibernating
        /// leaves, takes an insert as a load (<c>SearchEngine.InsertJsonRecords</c>), and a load
        /// clears the dataset's stored records first. Until then one record inserted into a
        /// hibernated keep-alive Off dataset, over HTTP or by a webhook, left that record as the
        /// whole dataset and was answered as a success. The load-on-insert is for a dataset with
        /// nothing stored.
        /// </summary>
        private static void RefuseDocumentsWhileAsleep(IServerSearchEngine engine, string dataSetName)
        {
            if (IsAsleepWithRecords(engine))
                throw new DatasetAsleepException(dataSetName, engine.Persistence!.NumberOfJsonRecords());
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
        /// mutations (WordMatching/Embeddable/BM25b/BM25k1/PatternMatching) post-Load. Those
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
            SearchEngineInstance? container = null;
            bool storeTouched = false;
            try
            {
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

                // Apply the caller's mutation on the now-Ready shadow. It writes to the store
                // through the shadow's own connection as it goes.
                storeTouched = true;
                TResult result = mutation(shadow);

                // Re-index to capture any field-config changes the mutation may have made
                // (WordMatching/BM25b/BM25k1/PatternMatching/Embeddable). For pure document
                // mutations (insert/update/delete) this is a no-op against an already
                // up-to-date index, but the safety guarantee is worth the cost.
                RunIndex(shadow, key, "post-mutation");

                // Atomic swap. The container reference returned by FindInstance keeps any
                // already-routed searches pointing at the same SearchEngineInstance; only
                // the inner theInstance pointer flips.
                IServerSearchEngine? swappedOut;
                swappedOut = SwapIn(container, shadow, key, dataSetName);
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
                if (storeTouched && container != null && shadow != null)
                    BringLiveInLineWithStore(container, key, dataSetName, teamId);
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
            {
                RefuseDocumentsWhileAsleep(matcher, dataSetName);
                return mutation(matcher);
            }

            return RunMutationOnShadow(dataSetName, teamId, mutation);
        }

        /// <summary>
        /// Builds a shadow with the supplied field configuration applied between Init and
        /// Load, then atomically swaps it in. Use this for SetFieldConfiguration changes
        /// that flip WordMatching/Embeddable/BM25b/BM25k1/PatternMatching: those flags are
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
                var live = shadow;
                swappedOut = SwapIn(container, shadow, key, dataSetName);
                // The container owns it from here. Given up before anything that can throw: until
                // 16 Nov 2026 the save below came first, and when it threw (the database locked by
                // another dataset's load, a full disk) the finally disposed the engine that had
                // just been made live, and every request for the dataset failed until a restart.
                shadow = null;
                SaveFieldsOfTheSwappedIn(live, dataSetName, teamId);

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

        /// <summary>
        /// Gives the dataset new text rules (17 Nov 2026): saved, and the dataset rebuilt from its
        /// store in the background with them as the engine's normalizer, then swapped in. A clone
        /// cannot do it: it is built with the configuration of the engine it copies. Returns why
        /// not, or null once the rebuild is claimed and started; it is then reported through status
        /// like a field configuration's, and a failed one puts the rules back as they were. Only on
        /// a dataset that is Ready: the fields' own rules are built over the engine's normalizer
        /// and saved with it, so they are made again in the rebuild, which needs the dataset up.
        /// </summary>
        internal string? SetTextRules(string dataSetName, string teamId, Services.DatasetTextRules rules)
        {
            if (_textRuleStore == null) return "Text rules are not available on this server.";
            if (Services.DatasetTextRuleStore.Objection(rules) is { } objection) return objection;
            try { _ = Services.DatasetTextRuleStore.Build(rules); }
            catch (Exception ex) { return $"The rules cannot be used: {ex.Message}"; }

            var key = MakeKey(dataSetName, teamId);
            lock (_dictionaryLock)
            {
                if (!_instances.TryGetValue(key, out var found) || found?.theInstance is not { IsDisposed: false } engine
                    || engine.Status.SystemState != SystemState.Ready)
                    return "The dataset must be loaded and ready: text rules are built into the index. Wake it up, then save again.";
            }

            var before = _textRuleStore.Load(teamId, dataSetName);
            if ((before ?? new Services.DatasetTextRules()).SameAs(rules)) return null;

            try { ClaimShadowBuild(key, dataSetName); }
            catch (ShadowBusyException) { return "The dataset is being rebuilt. Save again when it has finished."; }
            try
            {
                _textRuleStore.Save(teamId, dataSetName, rules);
            }
            catch (Exception ex)
            {
                ReleaseShadowBuild(key, null);
                _logger.LogError(ex, "{Prefix}text rules could not be saved", MakeLogPrefix(teamId, dataSetName));
                return $"Could not save: {ex.Message}";
            }
            _ = Task.Run(() =>
            {
                // Logged and recorded as the outcome inside; nobody waits on this task.
                try { BuildFromStoreOnShadow(dataSetName, teamId, key, before); }
                catch { }
            });
            return null;
        }

        /// <summary>
        /// A rebuild from the store with the configuration the dataset has now, for a dataset the
        /// caller has claimed; releases the claim. The engine is new, with its own connection: the
        /// field configuration, the synonyms and the documents are read from the store, the index
        /// built, and the engine swapped in. A field's own text rules are made again over the new
        /// normalizer first and saved, since a load reads the field configuration from the store;
        /// when the build fails the stored configuration and the rules are put back.
        /// </summary>
        private void BuildFromStoreOnShadow(string dataSetName, string teamId, string key, Services.DatasetTextRules? rulesBefore)
        {
            var monitor = new ProcessMonitor();
            _shadowMonitors[key] = monitor;

            SearchEngine? shadow = null;
            string? failure = null;
            string? fieldsBefore = null;
            try
            {
                ShadowBuildStarting?.Invoke(dataSetName);

                SearchEngineInstance container;
                lock (_dictionaryLock)
                {
                    if (!_instances.TryGetValue(key, out var found) || found?.theInstance == null)
                        throw new InvalidOperationException($"No active instance for dataset '{dataSetName}'");
                    container = found;
                }

                ConfigurationParameters configuration;
                using (var cfg = new Persistence(SearchDbConnectionString, dataSetName, teamId))
                    configuration = ConfigurationFor(cfg.ReadDataSetConfiguration()
                        ?? throw new InvalidOperationException($"Dataset '{dataSetName}' has no configuration"), dataSetName, teamId);

                shadow = NewReplaceEngine(configuration, dataSetName, teamId);
                if (!shadow.LoadDocumentFieldsFromDb(out var fieldsError))
                    throw new InvalidOperationException(fieldsError);

                // A field's own rules are a normalizer built over the engine's and saved whole: made
                // again over the new one. Emptied first, since the same rules are otherwise left.
                var own = shadow.GetFieldConfiguration().Where(f => f.TextRules is { Length: > 0 }).ToArray();
                if (own.Length > 0)
                {
                    var stored = shadow.Persistence!.ReadDocumentFields();
                    ApplyCarry(shadow, own.Select(f => new FieldProxy { FieldName = f.FieldName, TextRules = [] }).ToArray());
                    ApplyCarry(shadow, own.Select(f => new FieldProxy { FieldName = f.FieldName, TextRules = f.TextRules }).ToArray());
                    shadow.Persistence.SaveDocumentFields(shadow.DocumentFields!.GetSerialized());
                    fieldsBefore = stored;
                }

                RestoreSynonyms(shadow, dataSetName, teamId);
                UseSavedEmbeddings(shadow, dataSetName, teamId);
                shadow.LoadFromDatabaseSync(monitor);
                monitor.WaitForCompletion();
                if (!monitor.Succeeded)
                    throw new InvalidOperationException(
                        $"Shadow load from the store failed: {monitor.ErrorMessage ?? "unknown error"}", monitor.Exception);
                RunIndex(shadow, key, "from the store");

                var live = shadow;
                var swappedOut = SwapIn(container, shadow, key, dataSetName);
                shadow = null; // the container owns it from here
                fieldsBefore = null;
                SaveFieldsOfTheSwappedIn(live, dataSetName, teamId);
                ReportChange(dataSetName, teamId, Services.DatasetChangeKind.Reindex);

                if (swappedOut != null)
                    _ = Task.Run(() => DisposeAfterGraceAsync(swappedOut, dataSetName, teamId));
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                _logger.LogError(ex, "{Prefix}rebuild from the store failed; the text rules are put back", MakeLogPrefix(teamId, dataSetName));
                try
                {
                    _textRuleStore?.Save(teamId, dataSetName, rulesBefore);
                    if (fieldsBefore != null)
                        using (var back = new Persistence(SearchDbConnectionString, dataSetName, teamId))
                            back.SaveDocumentFields(fieldsBefore);
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx, "{Prefix}the text rules from before could not be put back", MakeLogPrefix(teamId, dataSetName));
                }
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
                        _logger.LogError(disposeEx, "{Prefix}Failed to dispose abandoned shadow", MakeLogPrefix(teamId, dataSetName));
                    }
                }
                ReleaseShadowBuild(key, failure);
            }
        }

        /// <summary>
        /// The swap: the rebuilt engine takes the place of the one serving, in the instance the
        /// build started from, and the one it replaces is returned for disposal. Only while that
        /// instance is still the dataset's (16 Nov 2026). A dataset deleted, transferred or
        /// disposed while its index was being rebuilt has left the registry, and the engine was
        /// swapped into an instance nothing would find or dispose again: the build now fails
        /// there, and its engine is disposed as any abandoned shadow is.
        /// </summary>
        private IServerSearchEngine? SwapIn(SearchEngineInstance container, SearchEngine shadow, string key, string dataSetName)
        {
            lock (_dictionaryLock)
            {
                if (!_instances.TryGetValue(key, out var current) || !ReferenceEquals(current, container))
                    throw new InvalidOperationException(
                        $"Dataset '{dataSetName}' was deleted, moved or reloaded while its index was being rebuilt; the rebuilt index is discarded.");
                var swappedOut = container.theInstance;
                container.theInstance = shadow;
                return swappedOut;
            }
        }

        /// <summary>
        /// After a build that failed with its changes already in the store (16 Nov 2026): the
        /// engine that serves is loaded from the store again and indexed, in the background, so
        /// the index and the store say the same. A heavy write's mutation, and a replace's load,
        /// go into the store through the shadow before the build is over; when the build then
        /// failed, the shadow was discarded and the live engine went on serving the documents
        /// from before, which the store no longer held, until the next wake. Only for an engine
        /// that is serving: one that sleeps reads the store when it wakes.
        /// </summary>
        private void BringLiveInLineWithStore(SearchEngineInstance container, string key, string dataSetName, string teamId)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_dictionaryLock)
                    {
                        if (!_instances.TryGetValue(key, out var current) || !ReferenceEquals(current, container)) return;
                    }
                    lock (container.DbLock)
                    {
                        var engine = container.theInstance;
                        if (engine == null || engine.IsDisposed || engine.Status.SystemState != SystemState.Ready) return;
                        _logger.LogWarning("{Prefix}a build failed after its changes reached the store; reloading the dataset from the store",
                            MakeLogPrefix(teamId, dataSetName));
                        var load = new ProcessMonitor { TimeoutSeconds = 600 };
                        using (TrackMonitor(dataSetName, teamId, load))
                        {
                            engine.LoadFromDatabaseSync(load);
                            if (!(load.WaitForCompletion() && load.Succeeded))
                            {
                                _logger.LogError(load.Exception, "{Prefix}the reload after a failed build did not complete: {Error}",
                                    MakeLogPrefix(teamId, dataSetName), load.ErrorMessage);
                                return;
                            }
                        }
                        var index = new ProcessMonitor { TimeoutSeconds = 600 };
                        engine.Index(monitor: index);
                        index.WaitForCompletion();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Prefix}the reload after a failed build threw", MakeLogPrefix(teamId, dataSetName));
                }
            });
        }

        /// <summary>What is kept per dataset about its last build and wake, forgotten when the
        /// dataset is deleted or moved: a dataset made later under the name is not told of them.</summary>
        private void ForgetOutcomes(string dataSetName, string teamId)
        {
            var key = MakeKey(dataSetName, teamId);
            _shadowOutcomes.TryRemove(key, out _);
            _wakeErrors.TryRemove(key, out _);
        }

        /// <summary>
        /// Stores the field configuration of the engine a swap has just made live, so a later wake
        /// restores it. A failure is logged and the swap stands: the engine serves with the new
        /// configuration, and the store keeps the old one until the next save.
        /// </summary>
        private void SaveFieldsOfTheSwappedIn(IServerSearchEngine live, string dataSetName, string teamId)
        {
            try
            {
                var df = live.DocumentFields;
                if (df != null)
                    live.Persistence?.SaveDocumentFields(df.GetSerialized());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Prefix}The rebuilt index is live, but its field configuration could not be stored; a wake or restart would bring the stored one back",
                    MakeLogPrefix(teamId, dataSetName));
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
