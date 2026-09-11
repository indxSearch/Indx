using Indx.Api;
using Indx.CloudApi;
using IndxCloudApi.Models;

namespace IndxCloudApi.Services
{
    /// <summary>
    /// The slice of the engine registry the dataset console uses, as an injectable service.
    /// The production implementation forwards to the static <c>IndxCloudInternalApi.Manager</c>;
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
        IndxCloudInternalApi.KeepAliveInfo GetKeepAliveInfo(string dataSetName, string teamId);
        bool SetKeepAliveHrs(string dataSetName, string teamId, int keepAliveHrs);
        ICloudSearchEngine? FindSearchEngine(string dataSetName, string teamId);
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
        string[] GetKeyFieldCandidates(string dataSetName, string teamId);
        string? SetKeyField(string dataSetName, string teamId, string fieldName, out bool needsReloadToReKey);
        string? ValidateExternalLoadForCustomKey(string dataSetName, string teamId, Stream jsonStream);

        // Search / synonyms
        Result Search(CloudQuery query, string dataSetName, string teamId);
        SynonymList? GetSynonyms(string dataSetName, string teamId);
        bool SetSynonyms(string dataSetName, string teamId, SynonymList? list);
    }

    /// <summary>Forwards to the process-wide engine registry.</summary>
    internal sealed class ManagerDatasetEngines : IDatasetEngines
    {
        private static IndxCloudInternalApi M => IndxCloudInternalApi.Manager;

        public List<string> GetTeamDataSets(string teamId) => M.GetTeamDataSets(teamId);
        public SystemStatus? GetState(string d, string t) => M.GetState(d, t);
        public IndxCloudInternalApi.KeepAliveInfo GetKeepAliveInfo(string d, string t) => M.GetKeepAliveInfo(d, t);
        public bool SetKeepAliveHrs(string d, string t, int hrs) => M.SetKeepAliveHrs(d, t, hrs);
        public ICloudSearchEngine? FindSearchEngine(string d, string t) => M.FindSearchEngine(d, t);
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
        public string[] GetKeyFieldCandidates(string d, string t) => M.GetKeyFieldCandidates(d, t);
        public string? SetKeyField(string d, string t, string field, out bool needsReload) => M.SetKeyField(d, t, field, out needsReload);
        public string? ValidateExternalLoadForCustomKey(string d, string t, Stream s) => M.ValidateExternalLoadForCustomKey(d, t, s);

        public Result Search(CloudQuery q, string d, string t) => M.Search(q, d, t);
        public SynonymList? GetSynonyms(string d, string t) => M.GetSynonyms(d, t);
        public bool SetSynonyms(string d, string t, SynonymList? l) => M.SetSynonyms(d, t, l);
    }
}
