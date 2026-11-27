using System;

namespace IndxServer.Models
{
    /// <summary>
    /// Thrown when documents are written to a dataset that sleeps with its records in the store:
    /// unloaded (<c>Created</c>) with records on disk, which is a hibernated dataset the server does
    /// not wake by itself (keep-alive Off) or one whose wake failed. The engine takes an insert in
    /// that state as a load, and a load replaces what the store holds, so the write is refused
    /// (409 invalidState over HTTP) until the dataset is awake.
    /// </summary>
    public sealed class DatasetAsleepException : Exception
    {
        /// <summary>Creates the exception with a message naming the dataset and what to do.</summary>
        public DatasetAsleepException(string dataSetName, int recordsOnDisk)
            : base($"Dataset '{dataSetName}' is hibernated, with {recordsOnDisk:N0} records in the store. " +
                   "Wake it (POST wakeup) and write again once it is Ready; a write now would replace the stored records.")
        { }
    }
}
