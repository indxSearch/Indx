using Indx.Storage;
using Indx.Utilities;

using IndxCloudApi.Models;

namespace IndxCloudApi.Services
{
    /// <summary>Creates an empty dataset for a team — shared by the team page's form and the
    /// breadcrumb's "New dataset…" so both validate and persist the same way.</summary>
    public static class DatasetCreation
    {
        /// <summary>Returns null on success (the dataset now exists, empty) or the message to show.</summary>
        public static string? TryCreate(string rawName, string teamId, out string name)
        {
            name = rawName.Trim();
            if (name.Length == 0) return "Enter a dataset name.";
            if (!FileNameValidity.IsValid(name)) return "Invalid dataset name.";
            try
            {
                var persistence = new Persistence(IndxCloudInternalApi.SearchDbConnectionString, name, teamId);
                if (persistence.DataSetExists()) return $"Dataset '{name}' already exists.";
                persistence.CreateOrOpenDataSet(IndxCloudInternalApi.DefaultConfigurationNumber);
                return null;
            }
            catch (Exception ex)
            {
                return $"Failed to create dataset: {ex.Message}";
            }
        }
    }
}
