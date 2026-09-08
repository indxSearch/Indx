using Indx.Api;
using Indx.CloudApi;
using Indx.Embeddings;
using Indx.Storage;
namespace IndxCloudApi.Models
{
    /// <summary>
    /// In-process registry of <see cref="SearchEngine"/> instances, keyed by the owning team.
    /// Datasets are owned by a team: the storage layer's owner column holds a team id (GUID
    /// string) where it historically held a user id. Authorization (who may touch a team's
    /// datasets) is decided in the controllers via team membership, so this class no longer
    /// carries any per-user sharing/grantee logic — it just maps (dataSetName, teamId) to an engine.
    /// </summary>
    internal sealed partial class IndxCloudInternalApi
    {
        #region Public Methods
        public ICloudSearchEngine? FindSearchEngineForInit(string dataSetName, string teamId)
        {
            var matcher = FindInstance(dataSetName, teamId);
            if (matcher == null)
                return null;
            if (matcher.Status.SystemState == SystemState.Created)
                return matcher;
            matcher.Dispose();
            _instances.Remove(MakeKey(dataSetName, teamId));
            var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, dataSetName, teamId);
            // invariant; DataSetExists() == true
            int? configuration = persistence.ReadDataSetConfiguration();
            if (configuration == null)
                return null;
            persistence.CreateOrOpenDataSet((int)configuration);

            var licensePath = GetLicensePath();
            var newMatcher = new SearchEngine(MakeLogPrefix(teamId, dataSetName), Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
               ResolveConfiguration(configuration, dataSetName), licensePath)
            {
                Persistence = persistence
            };
            _instances.Add(MakeKey(dataSetName, teamId), new SearchEngineInstance() { theInstance = newMatcher });
            return newMatcher;
        }
        public ICloudSearchEngine? FindSearchEngine(string dataSetName, string teamId)
        {
            return FindInstance(dataSetName, teamId);
        }
        #endregion Public Methods

        #region Internal Fields
        internal const string logFileName = "IndxCloudApi.log";
        #endregion Internal Fields

        #region Internal Properties
        // API will not get used before after program.cs has executed App.Run. It will however, call
        // IndxCloudInternalAPI.StartUpSystem first, ensuring Manager cannot be null.

        internal static IndxCloudInternalApi Manager
        {
            get => _manager ?? throw new InvalidOperationException(
                "Manager not initialized. Call StartUpSystem during startup.");
            private set => _manager = value;
        }

        #endregion Internal Properties

        #region Internal Methods
        internal static void StartUpSystem(string dbConnectionString, string licensePath = "")
        {
            if (_manager != null)  // Check the backing field directly
            {
                throw new InvalidOperationException("IndxCloudInternalApi.StartUpSystem shall only be called once");
            }
            LicensePath = licensePath;
            Manager = new IndxCloudInternalApi(dbConnectionString);
            Manager.InitializeSystem();
        }

        /// <summary>
        /// Resets the static Manager so the next startup can reinitialize cleanly.
        /// Called from the application host's ApplicationStopped event.
        /// </summary>
        internal static void Shutdown()
        {
            _manager = null;
            SearchDbConnectionString = "";
            LicensePath = "";
        }
        internal static string SearchDbConnectionString { get; private set; } = "";
        internal static string LicensePath { get; private set; } = "";
        // Clock for keep-alive last-used stamping and idle-eviction. Overridable so tests can advance
        // time (FakeTimeProvider) without real waits. Defaults to the system clock.
        internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;

        /// <summary>
        /// The only configuration number ever written to <c>DataSet.IndxConfiguration</c>. Written by
        /// <c>SearchController.CreateOrOpen</c> and read back here — the two must agree.
        /// </summary>
        internal const int DefaultConfigurationNumber = 400;

        /// <summary>
        /// Resolves a dataset's persisted configuration to the parameters its engine is built with.
        /// <para>
        /// The <c>DataSet.IndxConfiguration</c> column stays: it is where a serialized configuration
        /// will live once anything but the default is supported. Until then 400 is the only value
        /// written, and it means <see cref="ConfigurationParameters.Default"/> — so this is the one
        /// place that knows it, instead of the four casts to a profile enum this replaced.
        /// </para>
        /// <para>
        /// An unrecognised number resolves to the default rather than throwing. The engine is built
        /// inside the registry lock on a request path, so throwing here would turn a stale column
        /// value into a 500 for a dataset whose documents are perfectly fine.
        /// </para>
        /// </summary>
        internal static ConfigurationParameters ResolveConfiguration(int? persisted, string dataSetName)
        {
            if (persisted is null or DefaultConfigurationNumber)
                return ConfigurationParameters.Default;

            // Cold path: CreateOrOpen writes DefaultConfigurationNumber and nothing else can write
            // this column, so reaching here means a hand-edited or future-written row.
            Indx.Utilities.ILoggerFactory.Create<IndxCloudInternalApi>(logFileName).LogWarning(
                "Dataset '{DataSet}' carries configuration {Configuration}, which is not supported; opening with the default.",
                dataSetName, persisted);
            return ConfigurationParameters.Default;
        }

        private static string GetLicensePath()
        {
            // If explicitly configured, use that
            if (!string.IsNullOrWhiteSpace(LicensePath) && File.Exists(LicensePath))
                return Path.GetFullPath(LicensePath);

            // Auto-detect in ./IndxData directory
            var dataDir = "./IndxData";
            if (Directory.Exists(dataDir))
            {
                var licenses = Directory.GetFiles(dataDir, "*.license");
                if (licenses.Length > 0)
                {
                    // Prefer company licenses over developer licenses
                    // (company licenses typically have company names, not "developer")
                    var companyLicense = licenses.FirstOrDefault(l =>
                        !Path.GetFileName(l).Equals("indx-developer.license", StringComparison.OrdinalIgnoreCase));

                    if (companyLicense != null)
                        return Path.GetFullPath(companyLicense);

                    // Fall back to first license found
                    return Path.GetFullPath(licenses[0]);
                }
            }

            // No license found - return empty string (100k document limit)
            return string.Empty;
        }
        /// <summary>
        /// After Loading, insertions and deletions call this function
        /// to perform the actual indexing. Use the GetState method
        /// to monitor progress and readiness for Search.
        /// </summary>
        internal bool DoIndex(string dataSetName, string teamId)
        {
            try
            {
                var pm = new ProcessMonitor();
                var engine = FindInstance(dataSetName, teamId);
                if (engine != null && (engine.Status.SystemState == SystemState.Loaded
                    || engine.Status.SystemState == SystemState.Ready))
                {
                    engine.Index(monitor: pm);
                    pm.WaitForCompletion();
                    return true;
                }
                else
                    return false;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(MakeLogPrefix(teamId, dataSetName) + "IndxCloudInternalApi.DoIndexAsync exception" + ex.ToString());
                throw;
            }
        }

        internal string[] GetFields(string dataSetName, string teamId, bool all, bool indexable, bool sortable, bool filterable, bool facetable, bool wordIndexing)
        {
            try
            {
                var engine = ResolveEngine(dataSetName, teamId);
                if (engine == null)
                    return Array.Empty<string>();
                var fields = engine.GetFieldList();
                var returnList = new List<string>(fields.Count);
                for (int i = 0; i < fields.Count; i++)
                {
                    if (all)
                        returnList.Add(fields[i].Name);
                    else if (fields[i].Searchable && indexable)
                        returnList.Add(fields[i].Name);
                    else if (fields[i].Sortable && sortable)
                        returnList.Add(fields[i].Name);
                    else if (fields[i].Filterable && filterable)
                        returnList.Add(fields[i].Name);
                    else if (fields[i].Facetable && facetable)
                        returnList.Add(fields[i].Name);
                    else if (fields[i].WordIndexing && wordIndexing)
                        returnList.Add(fields[i].Name);
                }
                return returnList.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(MakeLogPrefix(teamId, dataSetName) + "IndxCloudInternalAPI.GetFields exception" + ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Returns status of the system see the model
        /// class for details.
        /// </summary>
        internal SystemStatus? GetState(string dataSetName, string teamId)
        {
            try
            {
                return ResolveEngine(dataSetName, teamId)?.Status;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(MakeLogPrefix(teamId, dataSetName) + "IndxCloudInternalAPI.GetState exception" + ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Runs field analysis on <paramref name="jsonStream"/>, sets DocumentFields on the
        /// engine, and persists the field configuration. Must be called after CreateOrOpen and
        /// before Load. Returns null on success or an error message on failure.
        /// </summary>
        /// <remarks>
        /// Goes through <c>Init</c> — the same call the REST analyze endpoint makes
        /// (<c>SearchController.AnalyzeStreamAsync</c>) — rather than
        /// <c>DocumentFields.AnalyzeAsync</c>, which the portal used to call.
        /// <para>The old path handed the WHOLE stream to <c>JsonDocument.ParseAsync</c>: raw
        /// bytes in a rented buffer plus a metadata row per JSON element, both alive at once.
        /// Measured peak managed heap was 517 MB for a 225 MB file and 2048 MB for a 486 MB
        /// one. Init streams through JsonParser a document at a time: 70 MB and 2 MB for the
        /// same two files — its ceiling is the largest single document, not the file. The
        /// numbers are in Notes/portal-analyze-streaming-plan.md; the probe that produced them
        /// is ParserTests/AnalyzePathMemoryProbeTests.</para>
        /// <para>The wait is the SYNCHRONOUS WaitForCompletion pushed onto a threadpool thread,
        /// not <c>WaitForCompletionAsync</c>. Init dispatches the work to a background thread
        /// and returns, so an async awaiter arrives before MarkStarted has run, and
        /// WaitForCompletionAsync completes a not-yet-started monitor immediately by design
        /// (pinned by ProcessMonitorTests.WaitForCompletionAsync_OnFreshMonitor_CompletesImmediately).
        /// Awaiting it here returned instantly with Succeeded still false — every upload would
        /// have reported a failure while the analyze ran on in the background.</para>
        /// <para>Init sets DocumentFields on the engine itself, so there is no
        /// SetDocumentFieldsInternal call, and it rewinds the stream afterwards, which the
        /// caller relies on when it hands the same FileStream to the Load step.</para>
        /// </remarks>
        internal async Task<string?> InitFromStreamAsync(string dataSetName, string teamId, Stream jsonStream)
        {
            var engine = FindSearchEngineForInit(dataSetName, teamId);
            if (engine == null)
                return "Dataset not found";
            using var monitor = new ProcessMonitor();
            engine.Init(jsonStream, monitor);
            await Task.Run(() => monitor.WaitForCompletion());
            if (!monitor.Succeeded)
                return string.IsNullOrEmpty(monitor.ErrorMessage)
                    ? "Analyze failed - the stream is not parseable JSON."
                    : monitor.ErrorMessage;
            if (engine.DocumentFields == null)
                return "Analyze returned no fields";
            engine.Persistence?.SaveDocumentFields(engine.DocumentFields.GetSerialized());
            return null;
        }

        internal bool Load(string dataSetName, string teamId, Stream jsonData, ProcessMonitor pm)
        {
            var instance = FindInstance(dataSetName, teamId);
            if (instance == null)
                return false;
            ApplyDeclaredKeyField(instance, dataSetName, teamId);
            instance.Load(jsonData, pm);
            return true;
        }

        internal bool LoadFromDatabase(string dataSetName, string teamId, ProcessMonitor monitor)
        {
            var instance = FindInstance(dataSetName, teamId);
            if (instance == null)
            {
                monitor.MarkFinished();
                return false;
            }
            else
            {
                if (instance.DocumentFields == null)
                {
                    if (instance.LoadDocumentFieldsFromDb())
                        return false;
                }

                instance.LoadFromDatabaseSync(monitor);
                return true;
            }
        }

        internal async Task<(bool success, string errorMessage)> LoadJsonStreamAsync(string dataSetName, string teamId, Stream jsonData)
        {
            var instance = FindInstance(dataSetName, teamId);
            if (instance == null)
                return (false, $"{nameof(LoadJsonStreamAsync)} SearchEngine not found");
            ApplyDeclaredKeyField(instance, dataSetName, teamId);
            var pm = new ProcessMonitor();
            await instance.LoadAsync(jsonData, pm);
            return (pm.Succeeded, pm.ErrorMessage);
        }

        /// <summary>
        /// Starts an async load and returns the <see cref="ProcessMonitor"/> so the caller can
        /// poll progress. Returns null if the dataset is not found.
        /// The returned task completes when the load finishes.
        /// </summary>
        internal (Task loadTask, ProcessMonitor monitor)? StartLoadAsync(string dataSetName, string teamId, Stream jsonData)
        {
            var instance = FindInstance(dataSetName, teamId);
            if (instance == null)
                return null;
            ApplyDeclaredKeyField(instance, dataSetName, teamId);
            var pm = new ProcessMonitor();
            // Run on thread-pool so MemoryStream reads (which complete synchronously) don't
            // block the Blazor server thread and freeze the UI on large files.
            var task = Task.Run(async () => await instance.LoadAsync(jsonData, pm));
            return (task, pm);
        }

        /// <summary>
        /// Performs a search. See the model class for details.
        /// Make sure to check for search readiness after a call
        /// to DoIndexAsync.
        /// </summary>
        internal Result Search(Indx.CloudApi.CloudQuery cloudQuery, string dataSetName, string teamId)
        {
            try
            {
                var engine = ResolveEngine(dataSetName, teamId);
                // An empty / not-yet-indexed dataset has no DocumentFields, so building the query
                // (FromCloudQuery2Query) would NullReference. Return an empty result instead —
                // "search works, just returns nothing" until documents are loaded and indexed.
                if (engine == null || engine.DocumentFields == null)
                    return Result.MakeEmptyResult();
                Query query = FromCloudQuery2Query(cloudQuery, engine, teamId, dataSetName);
                return engine.Search(query);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(MakeLogPrefix(teamId, dataSetName) + "IndxCloudInternalAPI.Search exception" + ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Removes and disposes all SearchEngine instances owned by a specific team.
        /// Call before deleting a team.
        /// </summary>
        internal void DisposeTeamInstances(string teamId)
        {
            // Collect the team's entries under the lock, then dispose outside. Each
            // SearchEngine.Dispose can take seconds; doing N of them inside _dictionaryLock
            // would freeze every other Blazor session that needs an engine reference.
            List<(string Key, SearchEngineInstance Instance)> toDispose;
            lock (_dictionaryLock)
            {
                var keysToRemove = _instances.Keys
                    .Where(k => k.StartsWith(teamId))
                    .ToList();

                toDispose = new List<(string, SearchEngineInstance)>(keysToRemove.Count);
                foreach (var key in keysToRemove)
                {
                    if (_instances.TryGetValue(key, out var instance) && instance != null)
                    {
                        toDispose.Add((key, instance));
                        _instances.Remove(key);
                    }
                }
            }

            _logger.LogInformation($"Disposing {toDispose.Count} SearchEngine instances for team {teamId}");
            foreach (var (key, instance) in toDispose)
            {
                try
                {
                    instance.theInstance?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error disposing SearchEngine instance {key}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Removes and disposes a specific SearchEngine instance for a dataset.
        /// This should be called before deleting a dataset from the database.
        /// </summary>
        internal void DisposeDataSetInstance(string dataSetName, string teamId)
        {
            // Remove the entry under the lock so concurrent FindInstance calls
            // immediately stop seeing it, but Dispose outside — SearchEngine.Dispose
            // can take seconds (task.Wait timeouts, native pool free) and holding
            // _dictionaryLock during that freezes every other Blazor session that
            // needs an engine reference.
            var key = MakeKey(dataSetName, teamId);
            SearchEngineInstance? instance;
            lock (_dictionaryLock)
            {
                if (!_instances.TryGetValue(key, out instance))
                    return;
                _instances.Remove(key);
            }

            try
            {
                instance?.theInstance?.Dispose();
                _logger.LogInformation($"Disposed SearchEngine instance for team {teamId}, dataset {dataSetName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error disposing SearchEngine instance {key}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets a dataset's keep-alive policy (hours): <see cref="int.MaxValue"/> = autoload + never
        /// dispose, <c>0</c> = no autoload (client-managed), other = idle-eviction countdown. Persists
        /// to the store and updates the live instance's cached value if loaded. Does not load or
        /// dispose anything itself. Intended for the website (admin UI), not the public REST API.
        /// </summary>
        /// <returns><c>true</c> if the dataset existed and was updated.</returns>
        internal bool SetKeepAliveHrs(string dataSetName, string teamId, int keepAliveHrs)
        {
            var db = new SqLiteManager(SearchDbConnectionString);
            bool updated = db.UpdateKeepAliveHrs(dataSetName, teamId, keepAliveHrs);
            if (updated)
            {
                lock (_dictionaryLock)
                    if (_instances.TryGetValue(MakeKey(dataSetName, teamId), out var inst))
                        inst.KeepAliveTimeHrs = keepAliveHrs;
            }
            return updated;
        }

        /// <summary>
        /// Reads a dataset's keep-alive policy plus live runtime state: whether its engine is Ready
        /// (fully loaded and indexed), whether a stale disposed instance lingers, when it was last
        /// used, and how long until idle-eviction. <see cref="KeepAliveInfo.Remaining"/> is null for
        /// non-Ready datasets and for the non-counting policies (0 and <see cref="int.MaxValue"/>).
        /// </summary>
        internal KeepAliveInfo GetKeepAliveInfo(string dataSetName, string teamId)
        {
            SearchEngineInstance? inst;
            lock (_dictionaryLock)
                _instances.TryGetValue(MakeKey(dataSetName, teamId), out inst);

            var db = new SqLiteManager(SearchDbConnectionString);
            int hrs = inst?.KeepAliveTimeHrs ?? db.ReadKeepAliveHrs(dataSetName, teamId);

            var engine = inst?.theInstance;
            // A disposed engine can briefly remain referenced; check IsDisposed first so we never read
            // Status off it (would throw ObjectDisposedException) nor report its stale state as Ready.
            bool disposed = engine?.IsDisposed == true;
            bool ready = !disposed && engine?.Status.SystemState == SystemState.Ready;
            DateTimeOffset? lastUsed = inst != null ? inst.LastUsedUtc : null;

            TimeSpan? remaining = null;
            if (ready && inst != null && hrs != 0 && hrs != int.MaxValue)
            {
                var rem = TimeSpan.FromHours(hrs) - (TimeProvider.GetUtcNow() - inst.LastUsedUtc);
                remaining = rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
            }

            // On-disk record count — non-zero on a Created dataset means it's hibernated (data
            // persisted, engine not loaded) rather than empty. The website uses this to offer a wake.
            int recordCount = db.NumberOfJsonRecordsInDataSet(dataSetName, teamId);
            return new KeepAliveInfo(hrs, ready, lastUsed, remaining, recordCount, disposed);
        }

        /// <summary>
        /// Disposes loaded instances whose idle time has passed their keep-alive countdown. Policies
        /// 0 (client-managed) and <see cref="int.MaxValue"/> (pinned) are never evicted. Called on an
        /// interval by <c>DatasetIdleSweeper</c>; also callable directly in tests after advancing the
        /// clock. The brief window between selecting a victim and disposing it can race a fresh
        /// request, but the threshold is hours, so it's negligible.
        /// </summary>
        /// <returns>The number of instances disposed.</returns>
        internal int SweepIdleInstances()
        {
            var now = TimeProvider.GetUtcNow();
            List<(string ds, string team)> toEvict = new();
            lock (_dictionaryLock)
            {
                foreach (var inst in _instances.Values)
                {
                    int hrs = inst.KeepAliveTimeHrs;
                    if (hrs == 0 || hrs == int.MaxValue)
                        continue;
                    if (inst.theInstance?.Status.SystemState != SystemState.Ready)
                        continue;
                    if (now - inst.LastUsedUtc > TimeSpan.FromHours(hrs))
                        toEvict.Add((inst.DataSetName, inst.TeamId));
                }
            }

            foreach (var (ds, team) in toEvict)
            {
                _logger.LogInformation(MakeLogPrefix(team, ds) + "idle-evicting (keep-alive countdown elapsed)");
                DisposeDataSetInstance(ds, team);
            }
            return toEvict.Count;
        }

        /// <summary>
        /// Returns all datasets across all teams, each with the owning team id.
        /// </summary>
        internal List<(string DataSetName, string TeamId)> GetAllDataSets()
        {
            var db = new SqLiteManager(SearchDbConnectionString);
            return db.GetAllDataSets()
                .Select(row => (row.DataSetName, row.UserName))
                .ToList();
        }

        /// <summary>Returns the names of all datasets owned by a team.</summary>
        internal List<string> GetTeamDataSets(string teamId)
        {
            var db = new SqLiteManager(SearchDbConnectionString);
            return db.GetUserDataSets(teamId);
        }

        /// <summary>
        /// Deletes a dataset: disposes the in-memory engine and removes the SQLite row.
        /// Single entry point so every caller (REST controller, Blazor UI, etc.) shares
        /// the same cleanup order and cannot accidentally skip the _instances eviction.
        /// </summary>
        /// <returns><c>true</c> if the dataset existed and was deleted; <c>false</c> if it did not exist.</returns>
        internal bool DeleteDataSet(string dataSetName, string teamId)
        {
            var persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId);
            if (!persistence.DataSetExists())
                return false;

            DisposeDataSetInstance(dataSetName, teamId);
            persistence.DeleteDataSet();
            _boostStore?.Delete(teamId, dataSetName);
            _metadataStore?.Delete(teamId, dataSetName);
            return true;
        }

        internal bool SetEmbeddableFields(string[] fieldNames, string dataSetName, string teamId)
        {
            var engine = FindInstance(dataSetName, teamId);
            if (engine?.DocumentFields == null)
                return false;
            foreach (var name in fieldNames)
            {
                var field = engine.DocumentFields.GetField(name);
                if (field == null)
                    return false;
                field.Embeddable = true;
            }
            return true;
        }

        internal EmbeddingResultEntry[] VectorSearch(VectorQueryProxy query, string dataSetName, string teamId)
        {
            try
            {
                var engine = ResolveEngine(dataSetName, teamId) as SearchEngine;
                if (engine == null)
                    return [];
                if (!engine.EmbeddingFields.TryGetValue(query.FieldName, out var index))
                    return [];
                Filter? filter = query.Filter != null
                    ? engine.GetFilterFromKey(query.Filter.HashString)
                    : null;
                var results = index.Search(query.Vector, query.MaxResults, filter);
                return results.Select(r => new EmbeddingResultEntry(r.documentKey, r.score)).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(MakeLogPrefix(teamId, dataSetName) + "IndxCloudInternalApi.VectorSearch exception " + ex);
                throw;
            }
        }

        internal EmbeddingResultEntry[] HybridSearch(HybridQueryProxy query, string dataSetName, string teamId)
        {
            try
            {
                var engine = ResolveEngine(dataSetName, teamId) as SearchEngine;
                if (engine == null)
                    return [];
                if (!engine.EmbeddingFields.TryGetValue(query.EmbeddingField, out var index))
                    return [];

                // Text search — fetch a larger pool to feed the merge
                int poolSize = query.MaxNumberOfRecordsToReturn * 2;
                var cloudQuery = new CloudQuery
                {
                    Text = query.Text,
                    MaxNumberOfRecordsToReturn = poolSize,
                    Filter = query.Filter,
                    TimeOutLimitMilliseconds = query.TimeOutLimitMilliseconds,
                    EnableCoverage = false,
                    RemoveDuplicates = true
                };
                Query textQuery = FromCloudQuery2Query(cloudQuery, engine, teamId, dataSetName);
                var textResult = engine.Search(textQuery);

                // Embedding search — also fetch a larger pool
                Filter? filter = query.Filter != null
                    ? engine.GetFilterFromKey(query.Filter.HashString)
                    : null;
                var embeddingResults = index.Search(query.Vector, poolSize, filter);

                // Merge and trim
                var merged = IEmbeddingIndex.MergeHybrid(textResult.Records, embeddingResults, query.Alpha);
                return merged
                    .Take(query.MaxNumberOfRecordsToReturn)
                    .Select(r => new EmbeddingResultEntry(r.documentKey, r.score))
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(MakeLogPrefix(teamId, dataSetName) + "IndxCloudInternalApi.HybridSearch exception " + ex);
                throw;
            }
        }

        /// <summary>
        /// Moves a dataset from one team to another (the team-ownership equivalent of the old
        /// per-user ownership transfer): evicts the old engine, updates SQLite atomically,
        /// then warms up the engine under the new owning team.
        /// </summary>
        /// <summary>
        /// The dataset's synonym list, or null when it has none. Read from the live engine, which
        /// was given the stored list when it was built.
        /// </summary>
        internal SynonymList? GetSynonyms(string dataSetName, string teamId)
        {
            return FindInstance(dataSetName, teamId)?.SynonymList;
        }

        /// <summary>
        /// Stores the dataset's synonym list and applies it to the live engine, or removes both when
        /// <paramref name="list"/> is null. Takes effect on the next search — synonyms are applied to
        /// the query text, so nothing has to be re-indexed. Returns false when the dataset does not
        /// exist in storage.
        /// </summary>
        internal bool SetSynonyms(string dataSetName, string teamId, SynonymList? list)
        {
            var engine = FindInstance(dataSetName, teamId);
            if (engine?.Persistence == null)
                return false;

            if (list == null)
            {
                engine.Persistence.DeleteSynonyms();
                engine.SynonymList = null;
                _logger.LogInformation(MakeLogPrefix(teamId, dataSetName) + "synonym list removed");
                return true;
            }

            list.UpdatedUtc = TimeProvider.GetUtcNow();
            if (!engine.Persistence.SaveSynonyms(list.GetSerialized()))
                return false;

            engine.SynonymList = list;
            _logger.LogInformation(MakeLogPrefix(teamId, dataSetName)
                + $"synonym list set ({list.Entries.Count} entries)");
            return true;
        }

        /// <summary>
        /// Copies the synonym list from one of the team's datasets onto another, replacing whatever
        /// list the target had. The two end up with independent copies: editing one afterwards does
        /// not touch the other.
        ///
        /// <para>Intended for the portal, which reaches this directly — the REST surface deliberately
        /// stays at GET/PUT, and a client there can copy by reading one and writing the other.</para>
        ///
        /// <para>Both datasets must belong to <paramref name="teamId"/>. A source with no list is a
        /// failure rather than a way to clear the target: "copy nothing onto it" is far more likely
        /// to be a mistake than an intention, and <see cref="SetSynonyms"/> with null already exists
        /// for clearing.</para>
        /// </summary>
        /// <returns>True on success; otherwise false with <paramref name="error"/> describing why.</returns>
        internal bool CopySynonyms(string fromDataSetName, string toDataSetName, string teamId, out string error)
        {
            error = string.Empty;

            if (string.Equals(fromDataSetName, toDataSetName, StringComparison.Ordinal))
            {
                error = "The source and the target are the same dataset.";
                return false;
            }

            var source = FindInstance(fromDataSetName, teamId);
            if (source == null)
            {
                error = $"The dataset '{fromDataSetName}' was not found.";
                return false;
            }

            // The engine holds the stored list from the moment it is built, so this reads what the
            // source's searches actually use, and works even on a dataset that is not loaded.
            var original = source.SynonymList;
            if (original == null)
            {
                error = $"The dataset '{fromDataSetName}' has no synonym list to copy.";
                return false;
            }

            if (FindInstance(toDataSetName, teamId) == null)
            {
                error = $"The dataset '{toDataSetName}' was not found.";
                return false;
            }

            // Round-trip through the serialized form for a genuine deep copy. Sharing the instance
            // would leave the two datasets holding the same object, so a later in-place edit or
            // Invalidate() on one would silently reach into the other.
            var copy = SynonymList.Deserialize(original.GetSerialized());

            if (!SetSynonyms(toDataSetName, teamId, copy))
            {
                error = $"The synonym list could not be stored on '{toDataSetName}'.";
                return false;
            }

            _logger.LogInformation(MakeLogPrefix(teamId, toDataSetName)
                + $"synonym list copied from '{fromDataSetName}' ({copy.Entries.Count} entries)");
            return true;
        }

        internal void TransferOwnership(string dataSetName, string currentTeamId, string newTeamId)
        {
            DisposeDataSetInstance(dataSetName, currentTeamId);
            var db = new SqLiteManager(SearchDbConnectionString);
            db.TransferOwnership(dataSetName, currentTeamId, newTeamId);
            _boostStore?.Transfer(currentTeamId, newTeamId, dataSetName);
            _metadataStore?.Transfer(currentTeamId, newTeamId, dataSetName);

            // Warm up the engine for the new owning team, same as InitializeSystem does on startup.
            var instance = FindInstance(dataSetName, newTeamId);
            if (instance?.Persistence != null && instance.Persistence.NumberOfJsonRecords() > 0)
            {
                var loadMonitor = new ProcessMonitor();
                instance.LoadFromDatabaseSync(loadMonitor);
                loadMonitor.WaitForCompletion();
                var indexMonitor = new ProcessMonitor();
                instance.Index(monitor: indexMonitor);
                indexMonitor.WaitForCompletion();
            }
        }

        internal LicenseInfo? GetLicenseInfo()
        {
            try
            {
                var licensePath = GetLicensePath();

                using var tempEngine = new SearchEngine(licensePath);

                var minimalJson = "[{\"field\":\"value\"}]";
                using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(minimalJson));

                tempEngine.Init(jsonStream);
                tempEngine.GetField("field")!.Searchable = true;
                jsonStream.Position = 0;
                tempEngine.Load(jsonStream);
                tempEngine.Index();

                if (tempEngine?.Status?.LicenseInfo == null)
                    return null;

                return tempEngine.Status.LicenseInfo;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "IndxCloudInternalAPI.GetLicenseInfo exception");
                throw;
            }
        }

        /// <summary>
        /// True when at least one live dataset engine is still running under a valid license.
        /// Running engines validate their license once at construction and keep that state for
        /// their lifetime, so this can be true even after every .license file has been removed
        /// from disk — the UI uses it to warn that a restart would drop those engines to the
        /// free-tier document limit.
        /// </summary>
        internal bool AnyRunningInstanceLicensed()
        {
            lock (_dictionaryLock)
            {
                foreach (var instance in _instances.Values)
                {
                    if (instance?.theInstance?.Status?.LicenseInfo?.Licensed == true)
                        return true;
                }
            }
            return false;
        }

        internal IReadOnlyList<LicenseFileInfo> GetLicenseFiles()
        {
            var dataDir = "./IndxData";
            if (!Directory.Exists(dataDir))
                return [];
            var activePath = GetLicensePath();
            return Directory.GetFiles(dataDir, "*.license")
                .Select(f => new LicenseFileInfo(
                    Path.GetFileName(f),
                    string.Equals(Path.GetFullPath(f), activePath, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Filename)
                .ToList();
        }

        internal async Task SaveLicenseFileAsync(string filename, Stream content)
        {
            var safeName = Path.GetFileName(filename);
            if (!safeName.EndsWith(".license", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only .license files are accepted.");
            var dataDir = "./IndxData";
            Directory.CreateDirectory(dataDir);
            using var fs = File.Create(Path.Combine(dataDir, safeName));
            await content.CopyToAsync(fs);
        }

        internal void DeleteLicenseFile(string filename)
        {
            var safeName = Path.GetFileName(filename);
            if (!safeName.EndsWith(".license", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only .license files can be deleted.");
            var dataDir = Path.GetFullPath("./IndxData");
            var fullPath = Path.GetFullPath(Path.Combine("./IndxData", safeName));
            if (!fullPath.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid file path.");
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        #endregion Internal Methods

        #region Private Fields
        private readonly object _dictionaryLock = new();
        private readonly Dictionary<string, SearchEngineInstance> _instances = [];
        private readonly ILogger<IndxCloudInternalApi> _logger;
        private static IndxCloudInternalApi? _manager;
        #endregion Private Fields

        #region Private Constructors
        private IndxCloudInternalApi(string searchDbConnectionString)
        {
            _logger = Indx.Utilities.ILoggerFactory.Create<IndxCloudInternalApi>(logFileName);
            SearchDbConnectionString = searchDbConnectionString;
        }
        #endregion Private Constructors

        #region Private Methods
        private void InitializeSystem()
        {
            const string tag = nameof(IndxCloudInternalApi) + "." + nameof(InitializeSystem);
            _logger.Log(LogLevel.Information, $"{tag} starting up");
            if (string.IsNullOrEmpty(SearchDbConnectionString))
            {
                _logger.LogError($"{tag} SearchDbConnectionString is null or empty");
                throw new InvalidOperationException("SearchDbConnectionString is null or empty");
            }

            var sqLiteManager = new SqLiteManager(SearchDbConnectionString);
            if (!sqLiteManager.DatabaseExists())
            {
                _logger.LogInformation($"{tag} no database found at {SearchDbConnectionString}");
                return;
            }
            // Add the KeepAliveTimeHrs column to databases that predate it (existing rows backfill
            // to int.MaxValue = warm-at-startup, never disposed). No-op once present. Stays on the
            // boot path: requests read the column as soon as the server is listening.
            sqLiteManager.EnsureKeepAliveColumn();
        }

        /// <summary>
        /// Loads and indexes every persisted dataset (except KeepAliveTimeHrs == 0, which is
        /// client-managed). Runs in the background after the server starts listening — pre-warming
        /// is an optimization, not a prerequisite: a request that arrives first auto-loads its
        /// dataset on demand in ResolveEngine under the same per-dataset DbLock, and both paths
        /// re-check the state inside the lock so the work happens exactly once. Startup must never
        /// block on this: a large persisted store on slow storage (Azure SMB content shares) takes
        /// minutes, and IIS/ANCM kills the process after its startup time limit (120 s by default)
        /// — the boot loop this caused when it ran inline before the server was up.
        /// </summary>
        internal void WarmUpPersistedDatasets()
        {
            const string tag = nameof(IndxCloudInternalApi) + "." + nameof(WarmUpPersistedDatasets);
            var sqLiteManager = new SqLiteManager(SearchDbConnectionString);
            if (!sqLiteManager.DatabaseExists())
                return;

            // Owner keys in the storage layer are team ids. Warm up every dataset under each.
            var owners = sqLiteManager.GetUsers();
            var work = owners
                .SelectMany(teamId => sqLiteManager.GetUserDataSets(teamId)
                    .Select(dataSet => (teamId, dataSet)))
                .ToList();
            var total = work.Count;
            _logger.LogInformation($"{tag} warming up {total} dataset(s) across {owners.Count} team(s), workingSet {WorkingSetMb()} MB");

            var overallSw = System.Diagnostics.Stopwatch.StartNew();
            int i = 0, loaded = 0, skipped = 0, failed = 0;
            foreach (var (teamId, dataSet) in work)
            {
                i++;
                // Per-dataset containment: one broken dataset (corrupt rows, OOM, a wedged build)
                // must not abort the warm-up of everything behind it in the list.
                try
                {
                    var wrapper = GetOrCreateInstance(dataSet, teamId);
                    var engine = wrapper?.theInstance;
                    if (wrapper == null || engine == null)
                    {
                        _logger.LogWarning($"{tag} [{i}/{total}] no engine instance for team {teamId} dataset '{dataSet}', skipping");
                        skipped++;
                        continue;
                    }
                    if (engine.Persistence == null)
                    {
                        _logger.LogWarning($"{tag} [{i}/{total}] instance.Persistence is null for team {teamId} dataset '{dataSet}', skipping");
                        skipped++;
                        continue;
                    }
                    // KeepAliveTimeHrs == 0 means "do not autoload at startup" — the client manages
                    // loading itself. The engine shell stays registered but unloaded.
                    if (engine.Persistence.ReadKeepAliveHrs() == 0)
                    {
                        _logger.LogInformation($"{tag} [{i}/{total}] skipping '{dataSet}' team {teamId}: KeepAliveTimeHrs=0 (client-managed)");
                        skipped++;
                        continue;
                    }
                    var records = engine.Persistence.NumberOfJsonRecords();
                    if (records == 0)
                    {
                        _logger.LogInformation($"{tag} [{i}/{total}] skipping '{dataSet}' team {teamId}: 0 records");
                        skipped++;
                        continue;
                    }

                    var beforeMb = WorkingSetMb();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    lock (wrapper.DbLock)
                    {
                        // A request may have auto-loaded (or a client begun loading) this dataset
                        // while we worked through the list — same double-check as ResolveEngine.
                        if (engine.Status.SystemState != SystemState.Created)
                        {
                            _logger.LogInformation($"{tag} [{i}/{total}] '{dataSet}' team {teamId} already {engine.Status.SystemState}, skipping");
                            skipped++;
                            continue;
                        }

                        _logger.LogInformation($"{tag} [{i}/{total}] loading '{dataSet}' team {teamId}: {records} records, workingSet {beforeMb} MB");
                        var monitor = new ProcessMonitor { TimeoutSeconds = 600 };
                        engine.LoadFromDatabaseSync(monitor);
                        if (!monitor.WaitForCompletion())
                        {
                            _logger.LogError($"{tag} [{i}/{total}] load of '{dataSet}' team {teamId} did not complete; dataset stays non-Ready");
                            failed++;
                            continue;
                        }
                        monitor = new ProcessMonitor { TimeoutSeconds = 600 };
                        engine.Index(monitor: monitor);
                        if (!monitor.WaitForCompletion())
                        {
                            _logger.LogError($"{tag} [{i}/{total}] index of '{dataSet}' team {teamId} did not complete; dataset stays non-Ready");
                            failed++;
                            continue;
                        }
                    }

                    sw.Stop();
                    var afterMb = WorkingSetMb();
                    loaded++;
                    _logger.LogInformation($"{tag} [{i}/{total}] loaded '{dataSet}' in {sw.ElapsedMilliseconds} ms, workingSet now {afterMb} MB (delta {afterMb - beforeMb} MB)");
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError($"{tag} [{i}/{total}] failed warming '{dataSet}' team {teamId}: {ex}");
                }
            }

            overallSw.Stop();
            _logger.LogInformation($"{tag} completed: {loaded} loaded, {skipped} skipped, {failed} failed, {overallSw.ElapsedMilliseconds} ms, workingSet {WorkingSetMb()} MB");
        }

        /// <summary>
        /// Current process working set in whole megabytes. Reflects total resident memory,
        /// including the unmanaged native allocations made by the search engine (which managed
        /// GC counters do not see), so it is the right gauge for startup memory pressure.
        /// </summary>
        private static long WorkingSetMb()
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            return proc.WorkingSet64 / (1024 * 1024);
        }

        // Per-dataset boost rules (cloud-owned). Wired in once at startup; null until then.
        private Services.BoostRuleStore? _boostStore;
        private int _boostCeiling = 6;

        /// <summary>Attaches the boost-rule store + saturation ceiling to the search path (startup-only).</summary>
        internal void AttachBoostStore(Services.BoostRuleStore store, int saturationCeiling)
        {
            _boostStore = store;
            _boostCeiling = saturationCeiling;
        }

        // Per-dataset metadata incl. the declared key field (cloud-owned). Wired in once at startup.
        private Services.DatasetMetadataStore? _metadataStore;

        // Stored when the user explicitly chooses auto-generated keys even though a real key field is
        // available. Distinct from "" (undeclared) — which falls back to the engine default (the "id"
        // field when present, else auto). Never a real JSON field name.
        internal const string KeyFieldAutoSentinel = "__indx_auto_key__";

        /// <summary>Attaches the metadata store (description + declared key field) (startup-only).</summary>
        internal void AttachMetadataStore(Services.DatasetMetadataStore store) => _metadataStore = store;

        /// <summary>
        /// Applies the dataset's cloud-declared key field to the engine's <see cref="DocumentFields"/>
        /// just before an external Load assigns and persists document keys. The lib does not persist the
        /// key-field name, so this re-establishes it on every fresh load (incl. replace).
        /// <list type="bullet">
        ///   <item>undeclared ("") → no-op: the engine keeps its default — the "id" field if present, else auto.</item>
        ///   <item>explicit auto (sentinel) → clears the key field so the engine auto-generates keys.</item>
        ///   <item>a field name → that field becomes the key.</item>
        /// </list>
        /// </summary>
        private void ApplyDeclaredKeyField(ICloudSearchEngine instance, string dataSetName, string teamId)
        {
            var declared = _metadataStore?.LoadKeyField(teamId, dataSetName);
            if (string.IsNullOrEmpty(declared)) return; // undeclared → engine default (id if present, else auto)
            var df = instance.DocumentFields;
            if (df == null) return;
            df.NameOfDocumentKeyField = declared == KeyFieldAutoSentinel ? "" : declared;
        }

        /// <summary>
        /// True when the dataset declares a named key field that <paramref name="instance"/>'s analyzed
        /// schema does not contain. The engine then falls back to its default key (an "id" field if
        /// present, else auto-generated) — the same thing it does for an undeclared key — so this is
        /// a warning for the caller to surface, not a failure.
        /// </summary>
        private bool DeclaredKeyFieldIsMissing(ICloudSearchEngine instance, string dataSetName, string teamId, out string declared)
        {
            declared = _metadataStore?.LoadKeyField(teamId, dataSetName) ?? "";
            if (string.IsNullOrEmpty(declared) || declared == KeyFieldAutoSentinel) return false;
            var df = instance.DocumentFields;
            var name = declared;
            return df != null && df.GetFieldList().All(f => f.Name != name);
        }

        /// <summary>
        /// The dataset's declared key field for API consumers: a field name, or "" meaning the default
        /// (the "id" field when present, otherwise auto-generated). Explicit auto-generated maps to "".
        /// </summary>
        internal string GetDeclaredKeyField(string dataSetName, string teamId)
        {
            var raw = _metadataStore?.LoadKeyField(teamId, dataSetName) ?? "";
            return raw == KeyFieldAutoSentinel ? "" : raw;
        }

        /// <summary>
        /// The fields eligible to be the document key: numeric AND non-optional (present + set on every
        /// analyzed document). A key field must identify every document, so optional fields — which the
        /// lib's Init flags when a field is null/empty/absent on some document — are excluded.
        /// </summary>
        internal string[] GetKeyFieldCandidates(string dataSetName, string teamId)
        {
            var df = FindInstance(dataSetName, teamId)?.DocumentFields;
            if (df == null) return Array.Empty<string>();
            return df.GetFieldList()
                .Where(f => f.Type == System.Text.Json.JsonValueKind.Number && !f.Optional)
                .Select(f => f.Name)
                .ToArray();
        }

        /// <summary>
        /// UX-only pre-check for the field-config UI: when the user picks a custom key field, dry-run the
        /// whole Load+Index in memory (NO persistence) against the buffered upload and return an error
        /// message if that key can't identify the documents (missing on some, or non-unique), else null.
        /// A no-op (null) for the default/auto key, or when fields aren't configured.
        ///
        /// This is purely to tell the user *at selection time* that a key won't work — it catches the one
        /// thing the lib can't surface cheaply yet (uniqueness; see indx_lib_fixes.md #3). It is NOT used
        /// on the actual load path: the lib's external Load is now atomic + state-resetting, so a bad key
        /// fails cleanly there on its own. Runs during setup (no live engine), so peak memory is ~1×.
        /// </summary>
        internal string? ValidateExternalLoadForCustomKey(string dataSetName, string teamId, Stream jsonStream)
        {
            var declared = _metadataStore?.LoadKeyField(teamId, dataSetName) ?? "";
            if (declared.Length == 0 || declared == KeyFieldAutoSentinel) return null; // no custom key → cheap path
            if (!jsonStream.CanSeek) return null; // can't re-read to dry-run; don't buffer a huge body

            var liveConfig = FindInstance(dataSetName, teamId)?.GetFieldConfiguration();
            if (liveConfig == null || liveConfig.Length == 0) return null;

            ConfigurationParameters configuration;
            using (var cfg = new Persistence(SearchDbConnectionString, dataSetName, teamId))
                configuration = ResolveConfiguration(cfg.ReadDataSetConfiguration(), dataSetName);

            // Persistence stays null → nothing this engine does can touch or lock the database.
            using var validate = new SearchEngine(
                MakeLogPrefix(teamId, dataSetName),
                Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
                configuration,
                GetLicensePath());
            try
            {
                jsonStream.Position = 0;
                validate.Init(jsonStream);
                ApplyDeclaredKeyField(validate, dataSetName, teamId);

                // Cheap guard: the declared key field must be present (as a numeric/string field) in
                // this data. If it is absent from every document, Haskey() is false and the engine would
                // silently auto-key — the Load below would "succeed" with surrogate keys and mask that
                // the chosen key isn't in this data. Catch it here, before paying for the full load.
                if (validate.DocumentFields?.Haskey() != true)
                    return DescribeKeyedLoadFailure(declared, $"field '{declared}' is not present in the data");

                var cfgErr = validate.SetFieldConfiguration(liveConfig);
                if (!string.IsNullOrEmpty(cfgErr)) return $"Field configuration error: {cfgErr}";

                jsonStream.Position = 0;
                var pm = new ProcessMonitor();
                validate.Load(jsonStream, pm);
                pm.WaitForCompletion();
                if (!pm.Succeeded) return DescribeKeyedLoadFailure(declared, pm.ErrorMessage);

                var im = new ProcessMonitor();
                validate.Index(im);
                im.WaitForCompletion();
                if (!im.Succeeded) return DescribeKeyedLoadFailure(declared, im.ErrorMessage);
                return null;
            }
            catch (Exception ex)
            {
                return DescribeKeyedLoadFailure(declared, ex.Message);
            }
        }

        private static string DescribeKeyedLoadFailure(string keyField, string? raw) =>
            $"The data can't be loaded with key field '{keyField}'. A key must be a whole number that is " +
            $"unique and present on every document (a price or rank won't work — values repeat). Pick a " +
            $"field that uniquely identifies each document, or choose Auto-generated." +
            (string.IsNullOrEmpty(raw) ? "" : $" (engine: {raw})");

        /// <summary>
        /// Turns a raw external-load failure message into something actionable when the dataset has a
        /// custom key field declared — e.g. a non-unique key throws a bare "An item with the same key
        /// has already been added", which becomes the "must be unique and present on every document"
        /// guidance. Returns the raw message unchanged when no custom key is declared.
        /// </summary>
        internal string DescribeLoadFailure(string dataSetName, string teamId, string? rawError)
        {
            var declared = GetDeclaredKeyField(dataSetName, teamId);
            return string.IsNullOrEmpty(declared)
                ? (rawError ?? "Load failed")
                : DescribeKeyedLoadFailure(declared, rawError);
        }

        /// <summary>
        /// Declares <paramref name="fieldName"/> (empty = auto-generated) as the dataset's key field:
        /// validates it exists and is numeric (the engine key is a long; a non-numeric key would silently
        /// collide via digit-stripping), persists the choice cloud-side, and applies it to the live engine.
        /// Returns null on success or an error message. <paramref name="needsReloadToReKey"/> is true when
        /// the dataset already holds loaded documents — their keys are frozen, so the new key field only
        /// takes effect on the next replace/reload.
        /// </summary>
        internal string? SetKeyField(string dataSetName, string teamId, string fieldName, out bool needsReloadToReKey)
        {
            needsReloadToReKey = false;
            if (_metadataStore == null)
                return "Key-field store is not available";

            var instance = FindInstance(dataSetName, teamId);
            var df = instance?.DocumentFields;
            if (df == null)
                return "Analyze the dataset (upload a sample) before declaring its key field";

            fieldName ??= "";
            string storeValue, applyValue;
            if (fieldName.Length == 0)
            {
                // Explicit auto-generated: force the engine off any default key field.
                storeValue = KeyFieldAutoSentinel;
                applyValue = "";
            }
            else
            {
                var field = df.GetField(fieldName);
                if (field == null)
                    return $"Field '{fieldName}' does not exist in this dataset";
                if (field.Type != System.Text.Json.JsonValueKind.Number)
                    return $"The key field must be numeric (the engine key is a whole number); " +
                           $"'{fieldName}' is {field.Type}. Pick a numeric id field, or choose auto-generated.";
                if (field.Optional)
                    return $"The key field must identify every document, but '{fieldName}' is missing or " +
                           $"empty on some documents. Pick a field that's always set, or choose auto-generated.";
                storeValue = applyValue = fieldName;
            }

            _metadataStore.SaveKeyField(teamId, dataSetName, storeValue);
            df.NameOfDocumentKeyField = applyValue; // apply to the live engine for the next load

            // If documents are already loaded, their JsonData.Id keys are persisted and won't change
            // until the data is reloaded (a replace).
            var state = instance!.Status.SystemState;
            needsReloadToReKey = state is SystemState.Ready or SystemState.Loaded or SystemState.Indexing;
            return null;
        }

        private Query FromCloudQuery2Query(CloudQuery cloudQuery, ICloudSearchEngine engine, string teamId, string dataSetName)
        {
            Query query = new Query(cloudQuery.Text, cloudQuery.MaxNumberOfRecordsToReturn)
            {
                CoverageSetup = cloudQuery.CoverageSetup,
                LogPrefix = cloudQuery.LogPrefix,
                CoverageDepth = cloudQuery.CoverageDepth,
                RemoveDuplicates = cloudQuery.RemoveDuplicates,
                EnableBoost = cloudQuery.EnableBoost,
                EnableCoverage = cloudQuery.EnableCoverage,
                EnableFacets = cloudQuery.EnableFacets,
                SortAscending = cloudQuery.SortAscending,
                SortBy = cloudQuery.SortBy != null ? engine.DocumentFields.GetField(cloudQuery.SortBy) : null,
                TimeOutLimitMilliseconds = cloudQuery.TimeOutLimitMilliseconds
            };
            if (cloudQuery.FieldBoosts != null)
                query.FieldBoosts = cloudQuery.FieldBoosts;
            if (cloudQuery.Filter != null)
                query.Filter = engine.GetFilterFromKey(cloudQuery.Filter.HashString);

            // Client-supplied boosts (kept verbatim for backward compat) merged with the dataset's
            // stored, currently-active boost rules. Rules only apply when the query opts in via
            // EnableBoost — same flag the client already uses.
            var merged = new List<Boost>();
            if (cloudQuery.Boosts != null)
                foreach (var b in cloudQuery.Boosts)
                    merged.Add(engine.CreateBoost(engine.GetFilterFromKey(b.FilterProxy.HashString), b.BoostStrength));

            if (cloudQuery.EnableBoost && _boostStore != null)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var ruleBoosts = _boostStore.BuildActiveBoosts(engine, teamId, dataSetName, today)
                                            .OrderByDescending(b => (int)b.BoostStrength);
                // Saturation cap: keep client boosts, then add rule boosts greedily while the total
                // summed strength stays within the ceiling. Additive boosts subtract MaxBoost*257
                // flatly, so an unbounded sum underflows the 16-bit score and flattens ranking.
                // Ceiling <= 0 disables the cap.
                var sum = merged.Sum(b => (int)b.BoostStrength);
                foreach (var rb in ruleBoosts)
                {
                    if (_boostCeiling > 0 && sum + (int)rb.BoostStrength > _boostCeiling) continue;
                    merged.Add(rb);
                    sum += (int)rb.BoostStrength;
                }
            }

            if (merged.Count > 0)
                query.Boosts = merged.ToArray();

            return query;
        }

        /// <summary>
        /// Returns the engine for a team-owned dataset, or null if the dataset doesn't exist.
        /// Authorization is the controller's responsibility (team membership) — this only resolves.
        /// </summary>
        internal ICloudSearchEngine? ResolveEngine(string dataSetName, string teamId)
        {
            var instance = GetOrCreateInstance(dataSetName, teamId);
            var engine = instance?.theInstance;
            if (instance == null || engine == null)
                return null;

            // Mark used so the idle sweeper sees activity (resets the keep-alive countdown).
            instance.Touch(TimeProvider.GetUtcNow());

            // Transparent auto-load for an evicted / not-yet-loaded dataset, EXCEPT when KeepAlive == 0
            // (those are client-managed: the client loads explicitly, the server never auto-loads).
            // Gate on Created + records-in-store: that combination only happens for a fresh shell whose
            // data is already persisted (restart or post-eviction) — never mid explicit client load
            // (which is Loading/Indexing) and never for a brand-new dataset (0 persisted records).
            if (instance.KeepAliveTimeHrs != 0
                && engine.Status.SystemState == SystemState.Created
                && engine.Persistence != null
                && engine.Persistence.NumberOfJsonRecords() > 0)
            {
                lock (instance.DbLock)
                {
                    if (engine.Status.SystemState == SystemState.Created)
                    {
                        // Bounded waits: DbLock is held for the duration, and every
                        // other request for this dataset queues behind it. An
                        // unbounded WaitForCompletion here turns a wedged build
                        // (e.g. a build thread that died under memory pressure and
                        // left the state at Indexing) into a permanently bricked
                        // dataset with a growing pile of blocked request threads.
                        var loadMonitor = new ProcessMonitor { TimeoutSeconds = 600 };
                        engine.LoadFromDatabaseSync(loadMonitor);
                        bool loaded = loadMonitor.WaitForCompletion();

                        var indexMonitor = new ProcessMonitor { TimeoutSeconds = 600 };
                        engine.Index(monitor: indexMonitor);
                        bool indexed = indexMonitor.WaitForCompletion();

                        if (loaded && indexed)
                            _logger.LogInformation(MakeLogPrefix(teamId, dataSetName)
                                + $"auto-loaded on demand (KeepAliveTimeHrs={instance.KeepAliveTimeHrs})");
                        else
                            _logger.LogError(MakeLogPrefix(teamId, dataSetName)
                                + $"auto-load timed out (load completed:{loaded}, index completed:{indexed}) — "
                                + "releasing the request; the dataset stays non-Ready until reloaded");
                    }
                }
                instance.Touch(TimeProvider.GetUtcNow());
            }

            return engine;
        }

        private static string MakeKey(string dataSetName, string teamId)
        {
            return teamId + dataSetName;
        }

        private static string MakeLogPrefix(string teamId, string dataSetName)
        {
            return "Team:" + teamId + " dataSet:" + dataSetName + " ";
        }

        private ICloudSearchEngine? FindInstance(string dataSetName, string teamId)
        {
            return GetOrCreateInstance(dataSetName, teamId)?.theInstance;
        }

        // Returns the instance wrapper (engine + keep-alive bookkeeping), creating an unloaded engine
        // shell on first touch. The actual data load happens at startup (InitializeSystem) or lazily
        // (ResolveEngine) — not here. Returns null if the dataset doesn't exist in the store.
        private SearchEngineInstance? GetOrCreateInstance(string dataSetName, string teamId)
        {
            string key = MakeKey(dataSetName, teamId);

            lock (_dictionaryLock)
            {
                if (_instances.TryGetValue(key, out var existing))
                    return existing;

                var persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId);
                var configuration = persistence.ReadDataSetConfiguration();
                if (configuration == null)
                    return null;

                var licensePath = GetLicensePath();
                var matcher = new SearchEngine(
                    MakeLogPrefix(teamId, dataSetName),
                    Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
                    ResolveConfiguration(configuration, dataSetName),
                    licensePath)
                {
                    Persistence = persistence
                };

                // The dataset's synonym list is engine configuration, so it is restored here — the
                // one place an engine is built — rather than looked up per search. A stored list
                // that will not parse is logged and skipped: a bad list must not brick the dataset.
                try
                {
                    var storedSynonyms = persistence.ReadSynonyms();
                    if (!string.IsNullOrWhiteSpace(storedSynonyms))
                        matcher.SynonymList = SynonymList.Deserialize(storedSynonyms);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, MakeLogPrefix(teamId, dataSetName)
                        + "stored synonym list could not be parsed — continuing without it");
                }

                var instance = new SearchEngineInstance
                {
                    theInstance = matcher,
                    DataSetName = dataSetName,
                    TeamId = teamId,
                    KeepAliveTimeHrs = persistence.ReadKeepAliveHrs()
                };
                instance.Touch(TimeProvider.GetUtcNow());
                _instances.Add(key, instance);
                return instance;
            }
        }
        #endregion Private Methods

        #region Private Classes
        internal record LicenseFileInfo(string Filename, bool IsActive);

        /// <summary>
        /// Snapshot of a dataset's keep-alive policy and live runtime state, for the website to show
        /// the setting and the countdown.
        /// <para><paramref name="Ready"/> is true only when a live, non-disposed engine has reached
        /// <see cref="SystemState.Ready"/> (fully loaded and indexed) — i.e. it can serve queries.</para>
        /// <para><paramref name="Disposed"/> is true when the engine reference still exists but has
        /// already been disposed (a stale instance). In the normal eviction/delete path the instance is
        /// removed from the registry before it is disposed, so this is rarely observed; it exists as a
        /// guard so a disposed engine is never reported as <paramref name="Ready"/>.</para>
        /// <para><paramref name="Remaining"/> is null when not Ready or for the non-counting policies
        /// (0 / int.MaxValue).</para>
        /// </summary>
        internal sealed record KeepAliveInfo(int KeepAliveTimeHrs, bool Ready, DateTimeOffset? LastUsedUtc, TimeSpan? Remaining, int RecordCount, bool Disposed);

        private sealed class SearchEngineInstance
        {
            #region Internal Fields
            // Serializes lazy auto-load (ResolveEngine) for this one dataset so two concurrent cold
            // requests don't both LoadFromDatabaseSync + Index the same engine.
            internal readonly object DbLock = new();
            /// <summary>
        /// The live engine. <b>volatile is load-bearing:</b> replace publishes a
        /// fully built shadow here from inside <c>_dictionaryLock</c>, but every
        /// reader — <c>ResolveEngine</c>, <c>FindInstance</c> — reads it without
        /// taking that lock. Without the acquire barrier a reader can observe the
        /// new reference before the writes that built the object behind it are
        /// visible, and answer a search from an engine that looks empty: HTTP 200,
        /// no timeout, no records. x86 hides this; ARM64 does not.
        /// </summary>
        internal volatile ICloudSearchEngine? theInstance;
            // Identity, kept so the idle sweeper can call DisposeDataSetInstance (the dictionary key
            // teamId+dataSetName is a non-reversible concatenation).
            internal string DataSetName = string.Empty;
            internal string TeamId = string.Empty;
            // Persisted keep-alive policy (hours), cached in memory: int.MaxValue = autoload + never
            // dispose, 0 = no autoload (client-managed), other = idle-eviction countdown.
            internal int KeepAliveTimeHrs = int.MaxValue;
            // Wall-clock of the last resolve (search/status/fields/...). In-memory only — the idle
            // sweeper compares (now - LastUsed) against KeepAliveTimeHrs. Written/read atomically.
            private long _lastUsedUtcTicks;
            #endregion Internal Fields

            #region Internal Methods
            internal void Touch(DateTimeOffset now) => Volatile.Write(ref _lastUsedUtcTicks, now.UtcTicks);
            internal DateTimeOffset LastUsedUtc => new(Volatile.Read(ref _lastUsedUtcTicks), TimeSpan.Zero);
            #endregion Internal Methods
        }
        #endregion Private Classes
    }
}
