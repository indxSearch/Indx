using System;

namespace IndxCloudApi.Models
{
    /// <summary>
    /// Thrown by internal operations that address a dataset which does not exist
    /// (neither live in memory nor persisted). The controller maps this to
    /// HTTP 404 with error code <c>datasetNotFound</c>.
    /// </summary>
    public sealed class DataSetNotFoundException : Exception
    {
        /// <summary>Creates the exception with a message naming the missing dataset.</summary>
        public DataSetNotFoundException(string dataSetName)
            : base($"Dataset '{dataSetName}' does not exist")
        { }
    }
}
