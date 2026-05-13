using Indx.Api;
using Indx.CloudApi;
using Indx.Embeddings;
using Indx.Storage;
namespace IndxCloudApi.Models
{
    internal sealed partial class IndxCloudInternalApi
    {
        #region Public Methods
        public ICloudSearchEngine? FindSearchEngineForInit(string dataSetName, string userId)
        {
            var matcher = FindInstance(dataSetName, userId);
            if (matcher == null)
                return null;
            if (matcher.Status.SystemState == SystemState.Created)
                return matcher;
            matcher.Dispose();
            _instances.Remove(MakeKey(dataSetName, userId));
            var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, dataSetName, userId);
            // invariant; DataSetExists() == true   
            int? configuration = persistence.ReadDataSetConfiguration();
            if (configuration == null)
                return null;
            persistence.CreateOrOpenDataSet((int)configuration);

            var licensePath = GetLicensePath();
            var newMatcher = new SearchEngine(MakeLogPrefix(userId, dataSetName), Indx.Utilities.ILoggerFactory.GetFactory(logFileName),
               (int)configuration, licensePath)
            {
                Persistence = persistence
            };
            _instances.Add(MakeKey(dataSetName, userId), new SearchEngineInstance() { theInstance = newMatcher });
            return newMatcher;
        }
        public ICloudSearchEngine? FindSearchEngine(string dataSetName, string userId)
        {
            return FindInstance(dataSetName, userId);
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
        /// <param name="dataSetName"></param>
        /// <param name="userId"></param>
        /// <returns></returns>
        internal bool DoIndex(string dataSetName, string userId)
        {
            try
            {
                var pm = new ProcessMonitor();
                var engine = FindInstance(dataSetName, userId);
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
                _logger.LogError(MakeLogPrefix(userId, dataSetName) + "IndxCloudInternalApi.DoIndexAsync exception" + ex.ToString());
                throw;
            }
        }

        internal string[] GetFields(string dataSetName, string userId, bool all, bool indexable, bool sortable, bool filterable, bool facetable, bool wordIndexing)
        {
            try
            {
                var engine = FindInstance(dataSetName, userId);
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
                _logger.LogError(MakeLogPrefix(userId, dataSetName) + "IndxCloudInternalAPI.GetFields exception" + ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Returns status of the system see the model
        /// class for details.
        /// </summary>
        /// <param name="dataSetName"></param>
        /// <param name="userId"></param>
        /// <returns></returns>
        internal SystemStatus? GetState(string dataSetName, string userId)
        {
            try
            {
                var engine = FindInstance(dataSetName, userId);
                if (engine == null)
                    return null;
                return engine.Status;
            }
            catch (System.Exception ex)
            {
                _logger.LogError(MakeLogPrefix(userId, dataSetName) + "IndxCloudInternalAPI.GetState exception" + ex.ToString());
                throw;
            }
        }

        internal bool Load(string dataSetName, string userId, Stream jsonData, ProcessMonitor pm)
        {
            var instance = FindInstance(dataSetName, userId);
            if (instance == null)
                return false;
            instance.Load(jsonData, pm);
            return true;
        }

        internal bool LoadFromDatabase(string dataSetName, string userId, ProcessMonitor monitor)
        {
            var instance = FindInstance(dataSetName, userId);
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

        internal async Task<(bool success, string errorMessage)> LoadJsonStreamAsync(string dataSetName, string userId, Stream jsonData)
        {
            var instance = FindInstance(dataSetName, userId);
            if (instance == null)
                return (false, $"{nameof(LoadJsonStreamAsync)} SearchEngine not found");
            var pm = new ProcessMonitor();
            await instance.LoadAsync(jsonData, pm);
            return (pm.Succeeded, pm.ErrorMessage);
        }

        /// <summary>
        /// Performs a search. See the model class for details.
        /// Make sure to check for search readiness after a call
        /// to DoIndexAsync.
        /// </summary>
        /// <param name="cloudQuery"></param>
        /// <param name="dataSetName"></param>
        /// <param name="userId"></param>
        /// <returns></returns>
        internal Result Search(Indx.CloudApi.CloudQuery cloudQuery, string dataSetName, string userId)
        {
            try
            {
                var engine = FindInstance(dataSetName, userId);
                if (engine == null)
                    return Result.MakeEmptyResult();
                Query query = FromCloudQuery2Query(cloudQuery, engine);
                return engine.Search(query);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(MakeLogPrefix(userId, dataSetName) + "IndxCloudInternalAPI.Search exception" + ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Removes and disposes all SearchEngine instances for a specific user.
        /// This should be called before deleting a user from the database.
        /// </summary>
        /// <param name="userId">The user ID to clean up</param>
        internal void DisposeUserInstances(string userId)
        {
            lock (_dictionaryLock)
            {
                var keysToRemove = _instances.Keys
                    .Where(k => k.StartsWith(userId))
                    .ToList();

                _logger.LogInformation($"Disposing {keysToRemove.Count} SearchEngine instances for user {userId}");

                foreach (var key in keysToRemove)
                {
                    if (_instances.TryGetValue(key, out var instance))
                    {
                        try
                        {
                            instance?.theInstance?.Dispose();
                            _instances.Remove(key);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error disposing SearchEngine instance {key}: {ex.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Removes and disposes a specific SearchEngine instance for a dataset.
        /// This should be called before deleting a dataset from the database.
        /// </summary>
        /// <param name="dataSetName">The dataset name to clean up</param>
        /// <param name="userId">The user ID that owns the dataset</param>
        internal void DisposeDataSetInstance(string dataSetName, string userId)
        {
            lock (_dictionaryLock)
            {
                var key = MakeKey(dataSetName, userId);

                if (_instances.TryGetValue(key, out var instance))
                {
                    try
                    {
                        instance?.theInstance?.Dispose();
                        _logger.LogInformation($"Disposed SearchEngine instance for user {userId}, dataset {dataSetName}");
                    }
                    catch (Exception ex)
                    {
                        // A stale entry surviving here re-serves "deleted" data on the next FindInstance.
                        _logger.LogError($"Error disposing SearchEngine instance {key}: {ex.Message}");
                    }
                    finally
                    {
                        _instances.Remove(key);
                    }
                }
            }
        }

        /// <summary>
        /// Deletes a dataset: disposes the in-memory engine and removes the SQLite row.
        /// Single entry point so every caller (REST controller, Blazor UI, etc.) shares
        /// the same cleanup order and cannot accidentally skip the _instances eviction.
        /// </summary>
        /// <returns><c>true</c> if the dataset existed and was deleted; <c>false</c> if it did not exist.</returns>
        internal bool DeleteDataSet(string dataSetName, string userId)
        {
            var persistence = new Persistence(SearchDbConnectionString, dataSetName, userId);
            if (!persistence.DataSetExists())
                return false;

            DisposeDataSetInstance(dataSetName, userId);
            persistence.DeleteDataSet();
            return true;
        }

        internal bool SetEmbeddableFields(string[] fieldNames, string dataSetName, string userId)
        {
            var engine = FindInstance(dataSetName, userId);
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

        internal EmbeddingResultEntry[] VectorSearch(VectorQueryProxy query, string dataSetName, string userId)
        {
            try
            {
                var engine = FindInstance(dataSetName, userId) as SearchEngine;
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
                _logger.LogError(MakeLogPrefix(userId, dataSetName) + "IndxCloudInternalApi.VectorSearch exception " + ex);
                throw;
            }
        }

        internal EmbeddingResultEntry[] HybridSearch(HybridQueryProxy query, string dataSetName, string userId)
        {
            try
            {
                var engine = FindInstance(dataSetName, userId) as SearchEngine;
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
                _logger.LogError(MakeLogPrefix(userId, dataSetName) + "IndxCloudInternalApi.HybridSearch exception " + ex);
                throw;
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
                var users = sqLiteManager.GetUsers();
                foreach (var user in users)
                {
                    var dataSets = sqLiteManager.GetUserDataSets(user);
                    foreach (var dataSet in dataSets)
                    {
                        var instance = FindInstance(dataSet, user);
                        if (instance == null)
                            continue;
                        var monitor = new ProcessMonitor();
                        if (instance.Persistence == null)
                        {
                            _logger.LogWarning($"{nameof(IndxCloudInternalApi)}.{nameof(InitializeSystem)} instance.Persistence is null for user {user} dataset {dataSet}");
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

        private static string MakeKey(string dataSetName, string userId)
        {
            return userId + dataSetName;
        }

        private static string MakeLogPrefix(string userId, string dataSetName)
        {
            return "User:" + userId + " dataSet:" + dataSetName + " ";
        }

        private ICloudSearchEngine? FindInstance(string dataSetName, string userId)
        {
            string key = MakeKey(dataSetName, userId);

            lock (_dictionaryLock)
            {
                if (_instances.TryGetValue(key, out var instance))
                    return instance?.theInstance;

                var persistence = new Persistence(SearchDbConnectionString, dataSetName, userId);
                var configuration = persistence.ReadDataSetConfiguration();
                if (configuration == null)
                    return null;

                var licensePath = GetLicensePath();
                var matcher = new SearchEngine(
                    MakeLogPrefix(userId, dataSetName),
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