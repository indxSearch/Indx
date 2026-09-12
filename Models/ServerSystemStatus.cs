using Indx.Api;

namespace IndxServer.Models
{
    /// <summary>
    /// SystemStatus extended with server-layer fields that the core library has no knowledge of.
    /// </summary>
    public class ServerSystemStatus : SystemStatus
    {
        /// <summary>For serialization.</summary>
        public ServerSystemStatus() { }

        /// <summary>Copy from a core SystemStatus, then set server fields separately.</summary>
        public ServerSystemStatus(SystemStatus source) : base(source) { }

        /// <summary>True while a shadow-instance rebuild is in progress for this dataset.</summary>
        public bool ShadowBuildInProgress { get; set; }

        /// <summary>UTC timestamp at which the in-progress shadow build started, or null if none is active.</summary>
        public DateTime? ShadowBuildStartedUtc { get; set; }
    }
}
