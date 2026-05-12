using System;

namespace IndxCloudApi.Models
{
    /// <summary>
    /// Thrown by <see cref="IndxCloudInternalApi"/> when a heavy mutation arrives for a
    /// dataset that already has a shadow build in progress. The controller maps this to
    /// HTTP 409 Conflict so the client can retry once the prior build completes.
    /// </summary>
    public sealed class ShadowBusyException : Exception
    {
        /// <summary>Creates the exception with a message naming the affected dataset.</summary>
        public ShadowBusyException(string dataSetName)
            : base($"A shadow build is already in progress for dataset '{dataSetName}'. Retry after it completes.")
        { }
    }
}
