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

        /// <summary>UTC timestamp at which the dataset's last shadow build ended, or null when
        /// none has ended since the server started or one is running now. A request that starts a
        /// rebuild answers 202 before it is done; the rebuild is over when
        /// <see cref="ShadowBuildInProgress"/> is false again and this is set.</summary>
        public DateTime? ShadowBuildFinishedUtc { get; set; }

        /// <summary>Why the dataset's last shadow build failed, or null: it succeeded, none has
        /// run, or one is running now. A failed build swaps nothing in, so the dataset keeps
        /// serving with the documents and field configuration it had. Cleared when the next build
        /// starts.</summary>
        public string? ShadowBuildError { get; set; }
    }
}
