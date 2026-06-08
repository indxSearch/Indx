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
               (int)configuration, licensePath)
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
        internal async Task<string?> InitFromStreamAsync(string dataSetName, string teamId, Stream jsonStream)
        {
            var engine = FindSearchEngineForInit(dataSetName, teamId);
            if (engine == null)
                return "Dataset not found";
            var (df, error) = await DocumentFields.AnalyzeAsync(jsonStream);
            if (!string.IsNullOrEmpty(error) || df == null)
                return string.IsNullOrEmpty(error) ? "Analyze returned no fields" : error;
            engine.SetDocumentFieldsInternal(df);
            engine.Persistence?.SaveDocumentFields(df.GetSerialized());
            return null;
        }

        internal bool Load(string dataSetName, string teamId, Stream jsonData, ProcessMonitor pm)
        {
            var instance = FindInstance(dataSetName, teamId);
            if (instance == null)
                return false;
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
                if (engine == null)
                    return Result.MakeEmptyResult();
                Query query = FromCloudQuery2Query(cloudQuery, engine);
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
                Query textQuery = FromCloudQuery2Query(cloudQuery, engine);
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
        internal void TransferOwnership(string dataSetName, string currentTeamId, string newTeamId)
        {
            DisposeDataSetInstance(dataSetName, currentTeamId);
            var db = new SqLiteManager(SearchDbConnectionString);
            db.TransferOwnership(dataSetName, currentTeamId, newTeamId);

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
            _logger.Log(LogLevel.Information, $"{nameof(IndxCloudInternalApi)}.{nameof(InitializeSystem)} starting up");
            if (string.IsNullOrEmpty(SearchDbConnectionString))
            {
                _logger.LogError($"{nameof(IndxCloudInternalApi)}.{nameof(InitializeSystem)} SearchDbConnectionString is null or empty");
                throw new InvalidOperationException("SearchDbConnectionString is null or empty");
            }
            try
            {
                var sqLiteManager = new SqLiteManager(SearchDbConnectionString);
                if (!sqLiteManager.DatabaseExists())
                {
                    _logger.LogInformation($"{nameof(IndxCloudInternalApi)}.{nameof(InitializeSystem)} no database found at {SearchDbConnectionString}");
                    return;
                }
                // Owner keys in the storage layer are team ids. Warm up every dataset under each.
                var owners = sqLiteManager.GetUsers();
                foreach (var teamId in owners)
                {
                    var dataSets = sqLiteManager.GetUserDataSets(teamId);
                    foreach (var dataSet in dataSets)
                    {
                        var instance = FindInstance(dataSet, teamId);
                        if (instance == null)
                            continue;
                        var monitor = new ProcessMonitor();
                        if (instance.Persistence == null)
                        {
                            _logger.LogWarning($"{nameof(IndxCloudInternalApi)}.{nameof(InitializeSystem)} instance.Persistence is null for team {teamId} dataset {dataSet}");
                            continue;
                        }
                        if (instance.Persistence.NumberOfJsonRecords() == 0)
                            continue;
                        instance.LoadFromDatabaseSync(monitor);
                        monitor.WaitForCompletion();
                        monitor = new ProcessMonitor();
                        instance.Index(monitor: monitor);
                        monitor.WaitForCompletion();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"{nameof(IndxCloudInternalApi)}.{nameof(InitializeSystem)} {ex.ToString()}");
                throw;
            }
        }

        private static Query FromCloudQuery2Query(CloudQuery cloudQuery, ICloudSearchEngine engine)
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
            if (cloudQuery.Boosts != null)
            {
                Boost[] boosts = new Boost[cloudQuery.Boosts.Length];
                for (int i = 0; i < cloudQuery.Boosts.Length; i++)
                {
                    var f = engine.GetFilterFromKey(cloudQuery.Boosts[i].FilterProxy.HashString);
                    boosts[i] = engine.CreateBoost(f, cloudQuery.Boosts[i].BoostStrength);
                }
                query.Boosts = boosts;
            }
            return query;
        }

        /// <summary>
        /// Returns the engine for a team-owned dataset, or null if the dataset doesn't exist.
        /// Authorization is the controller's responsibility (team membership) — this only resolves.
        /// </summary>
        internal ICloudSearchEngine? ResolveEngine(string dataSetName, string teamId)
        {
            return FindInstance(dataSetName, teamId);
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
            string key = MakeKey(dataSetName, teamId);

            lock (_dictionaryLock)
            {
                if (_instances.TryGetValue(key, out var instance))
                    return instance?.theInstance;

                var persistence = new Persistence(SearchDbConnectionString, dataSetName, teamId);
                var configuration = persistence.ReadDataSetConfiguration();
                if (configuration == null)
                    return null;

                var licensePath = GetLicensePath();
                var matcher = new SearchEngine(
                    MakeLogPrefix(teamId, dataSetName),
                    Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
                    (int)configuration,
                    licensePath)
                {
                    Persistence = persistence
                };

                _instances.Add(key, new SearchEngineInstance { theInstance = matcher });
                return matcher;
            }
        }
        #endregion Private Methods

        #region Private Classes
        internal record LicenseFileInfo(string Filename, bool IsActive);

        private sealed class SearchEngineInstance
        {
            #region Internal Fields
            internal readonly object DbLock = new();
            internal ICloudSearchEngine? theInstance;
            #endregion Internal Fields
        }
        #endregion Private Classes
    }
}
