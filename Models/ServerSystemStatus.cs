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

        /// <summary>Number of records the store holds for this dataset, readable without waking
        /// the engine. Together with <see cref="SystemStatus.SystemState"/> this is what
        /// "hibernated" means on the server: Created with RecordsOnDisk &gt; 0. Zero on Created
        /// means genuinely empty. A persistence fact, so it lives here and not in the shared enum —
        /// it is meaningless in a library without a store.</summary>
        public int RecordsOnDisk { get; set; }

        /// <summary>Number of fields in the stored field configuration, 0 when the dataset has not
        /// been analyzed. Also readable without waking the engine.</summary>
        public int FieldsDiscovered { get; set; }

        /// <summary>True while a shadow-instance rebuild is in progress for this dataset.</summary>
        public bool ShadowBuildInProgress { get; set; }

        /// <summary>UTC timestamp at which the in-progress shadow build started, or null if none is active.</summary>
        public DateTime? ShadowBuildStartedUtc { get; set; }
    }
}
