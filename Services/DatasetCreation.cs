using IndxServer.Engine;
using Indx.Storage;
using Indx.Utilities;

using IndxServer.Models;
using Microsoft.Extensions.Logging;

namespace IndxServer.Services
{
    /// <summary>Creates an empty dataset for a team — shared by the team page's form and the
    /// breadcrumb's "New dataset…" so both validate and persist the same way.</summary>
    public static class DatasetCreation
    {
        /// <summary>Returns null on success (the dataset now exists, empty) or the message to show.</summary>
        public static string? TryCreate(string rawName, string teamId, ILogger logger, out string name)
        {
            name = rawName.Trim();
            if (name.Length == 0) return "Enter a dataset name.";
            if (!FileNameValidity.IsValid(name)) return "Invalid dataset name.";
            try
            {
                var persistence = new Persistence(IndxServerInternalApi.SearchDbConnectionString, name, teamId);
                if (persistence.DataSetExists()) return $"Dataset '{name}' already exists.";
                persistence.CreateOrOpenDataSet(IndxServerInternalApi.DefaultConfigurationNumber);
                return null;
            }
            catch (Exception ex)
            {
                // The person sees the message; the stack is only here.
                logger.LogError(ex, "Creating dataset '{DataSet}' for team {TeamId} failed", name, teamId);
                return $"Failed to create dataset: {ex.Message}";
            }
        }
    }
}
