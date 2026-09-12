using System;
using Indx.Api;

namespace IndxServer.Models
{
    /// <summary>
    /// Thrown by <see cref="IndxServerInternalApi"/> when a heavy mutation arrives for a
    /// dataset that is busy — either a shadow build is already in progress, or the engine
    /// itself is mid-lifecycle. The controller maps this to HTTP 409 Conflict so the client
    /// can retry once the prior work completes.
    /// </summary>
    public sealed class ShadowBusyException : Exception
    {
        /// <summary>Creates the exception with a message naming the affected dataset.</summary>
        public ShadowBusyException(string dataSetName)
            : base($"A shadow build is already in progress for dataset '{dataSetName}'. Retry after it completes.")
        { }

        /// <summary>
        /// Creates the exception for a dataset whose engine is in a transient state
        /// (<see cref="SystemState.Loading"/> / <see cref="SystemState.Indexing"/>) rather than
        /// one with a registered shadow build. Same 409, but the message must not claim a shadow
        /// build is running when none is.
        /// </summary>
        public ShadowBusyException(string dataSetName, SystemState state)
            : base($"Dataset '{dataSetName}' is {state} and cannot accept mutations yet. " +
                   "Retry once it is Ready.")
        { }
    }
}
