using Indx.Api;
using Indx.Http;
using IndxServer.Models;

namespace IndxServer.Services
{
    /// <summary>
    /// The slice of the engine registry the dataset console uses, as an injectable service.
    /// The production implementation forwards to the static <c>IndxServerInternalApi.Manager</c>;
    /// tests can substitute a fake so component behaviour can be exercised without an engine.
    /// Method names and semantics mirror the manager one-to-one.
    /// </summary>
    /// <summary>
    /// The public slice of the registry a team needs when it goes away: which datasets it owns
    /// and how to delete one. Kept separate from <see cref="IDatasetEngines"/> (internal, and
    /// full of internal types) so the public <see cref="TeamService"/> can take it.
    /// </summary>
    public interface ITeamDatasets
    {
        List<string> GetTeamDataSets(string teamId);
        bool DeleteDataSet(string dataSetName, string teamId);
    }

    internal interface IDatasetEngines : ITeamDatasets
    {
        // Registry / lifecycle (GetTeamDataSets / DeleteDataSet come from ITeamDatasets)
        SystemStatus? GetState(string dataSetName, string teamId);
        IndxServerInternalApi.KeepAliveInfo GetKeepAliveInfo(string dataSetName, string teamId);
        bool SetKeepAliveHrs(string dataSetName, string teamId, int keepAliveHrs);
        IServerSearchEngine? FindSearchEngine(string dataSetName, string teamId);
        void DisposeDataSetInstance(string dataSetName, string teamId);
        void TransferOwnership(string dataSetName, string currentTeamId, string newTeamId);
        /// <summary>Renames within the team. Null on success, else the message to show.</summary>
        string? RenameDataSet(string dataSetName, string teamId, string newName);

        // Ingest
        Task<string?> InitFromStreamAsync(string dataSetName, string teamId, Stream jsonStream);
        (Task loadTask, ProcessMonitor monitor)? StartLoadAsync(string dataSetName, string teamId, Stream jsonData);
        string DescribeLoadFailure(string dataSetName, string teamId, string? rawError);
        ReplaceSchemaChange RunReplaceFromJson(string dataSetName, string teamId, Stream jsonStream);
        /// <summary>Step and per-step percent of a replace in progress, or null when none is running.</summary>
        ReplaceProgress? GetReplaceProgress(string dataSetName, string teamId);

        // Field configuration
        void RunFieldConfigurationOnShadow(string dataSetName, string teamId, FieldProxy[] fields);
        int GetShadowBuildPercent(string dataSetName, string teamId);

        /// <summary>Progress of a running load, index build or shadow rebuild, 0-100, or null when
        /// nothing is running for this dataset.</summary>
        int? GetProgressPercent(string dataSetName, string teamId);

        /// <summary>Publishes a monitor the console drives itself (the index build after an upload)
        /// as this dataset's progress until the returned scope is disposed, so a page reopened
        /// mid-build shows the same percentage.</summary>
        IDisposable TrackProgress(string dataSetName, string teamId, ProcessMonitor monitor);

        /// <summary>True while something is loading or indexing this dataset.</summary>
        bool IsWorkInProgress(string dataSetName, string teamId);

        // Wake from hibernation. The server owns the operation, so every page visit sees the
        // same progress and any of them can cancel it.
        /// <summary>Starts (or joins) the wake of a hibernated dataset. False when it does not exist.</summary>
        bool StartWake(string dataSetName, string teamId);
        /// <summary>Progress of the running wake, or null when none is running.</summary>
        WakeProgress? GetWakeProgress(string dataSetName, string teamId);
        /// <summary>Why the last wake failed, or null.</summary>
        string? GetWakeError(string dataSetName, string teamId);
        /// <summary>Stops the running wake and returns the dataset to hibernation.</summary>
        Task CancelWakeAsync(string dataSetName, string teamId);

        string[] GetKeyFieldCandidates(string dataSetName, string teamId);
        string? SetKeyField(string dataSetName, string teamId, string fieldName, out bool needsReloadToReKey);
        string? ValidateExternalLoadForCustomKey(string dataSetName, string teamId, Stream jsonStream);

        // Search / synonyms
        Result Search(QueryProxy query, string dataSetName, string teamId);
        SynonymList? GetSynonyms(string dataSetName, string teamId);
        bool SetSynonyms(string dataSetName, string teamId, SynonymList? list);
    }

    /// <summary>Forwards to the process-wide engine registry.</summary>
    internal sealed class ManagerDatasetEngines : IDatasetEngines
    {
        private static IndxServerInternalApi M => IndxServerInternalApi.Manager;

        public List<string> GetTeamDataSets(string teamId) => M.GetTeamDataSets(teamId);
        public SystemStatus? GetState(string d, string t) => M.GetState(d, t);
        public IndxServerInternalApi.KeepAliveInfo GetKeepAliveInfo(string d, string t) => M.GetKeepAliveInfo(d, t);
        public bool SetKeepAliveHrs(string d, string t, int hrs) => M.SetKeepAliveHrs(d, t, hrs);
        public IServerSearchEngine? FindSearchEngine(string d, string t) => M.FindSearchEngine(d, t);
        public bool DeleteDataSet(string d, string t) => M.DeleteDataSet(d, t);
        public void DisposeDataSetInstance(string d, string t) => M.DisposeDataSetInstance(d, t);
        public void TransferOwnership(string d, string from, string to) => M.TransferOwnership(d, from, to);
        public string? RenameDataSet(string d, string t, string newName) => M.RenameDataSet(d, t, newName);

        public Task<string?> InitFromStreamAsync(string d, string t, Stream s) => M.InitFromStreamAsync(d, t, s);
        public (Task loadTask, ProcessMonitor monitor)? StartLoadAsync(string d, string t, Stream s) => M.StartLoadAsync(d, t, s);
        public string DescribeLoadFailure(string d, string t, string? raw) => M.DescribeLoadFailure(d, t, raw);
        public ReplaceSchemaChange RunReplaceFromJson(string d, string t, Stream s) => M.RunReplaceFromJson(d, t, s);
        public ReplaceProgress? GetReplaceProgress(string d, string t) => M.GetReplaceProgress(d, t);

        public void RunFieldConfigurationOnShadow(string d, string t, FieldProxy[] f) => M.RunFieldConfigurationOnShadow(d, t, f);
        public int GetShadowBuildPercent(string d, string t) => M.GetShadowBuildPercent(d, t);
        public int? GetProgressPercent(string d, string t) => M.GetProgressPercent(d, t);
        public IDisposable TrackProgress(string d, string t, ProcessMonitor m) => M.TrackMonitor(d, t, m);
        public bool IsWorkInProgress(string d, string t) => M.IsWorkInProgress(d, t);
        public bool StartWake(string d, string t) => M.StartWake(d, t);
        public WakeProgress? GetWakeProgress(string d, string t) => M.GetWakeProgress(d, t);
        public string? GetWakeError(string d, string t) => M.GetWakeError(d, t);
        public Task CancelWakeAsync(string d, string t) => M.CancelWakeAsync(d, t);
        public string[] GetKeyFieldCandidates(string d, string t) => M.GetKeyFieldCandidates(d, t);
        public string? SetKeyField(string d, string t, string field, out bool needsReload) => M.SetKeyField(d, t, field, out needsReload);
        public string? ValidateExternalLoadForCustomKey(string d, string t, Stream s) => M.ValidateExternalLoadForCustomKey(d, t, s);

        public Result Search(QueryProxy q, string d, string t) => M.Search(q, d, t);
        public SynonymList? GetSynonyms(string d, string t) => M.GetSynonyms(d, t);
        public bool SetSynonyms(string d, string t, SynonymList? l) => M.SetSynonyms(d, t, l);
    }
}
