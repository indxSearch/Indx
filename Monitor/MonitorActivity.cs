namespace IndxServer.Monitor
{
    /// <summary>One thing that happened to a dataset, waiting to reach the event stream.</summary>
    internal readonly record struct ActivityEntry(
        DateTimeOffset At, string DataSetName, string TeamId, string Text);

    /// <summary>
    /// Where the rest of the server reports something worth seeing, for the monitor to pick up on
    /// its next tick.
    ///
    /// <para>The collector derives what it can from polling, but a field configuration change is
    /// not a state the engine holds afterwards: by the next tick the dataset is Ready again and
    /// looks exactly as it did. Only the code making the change knows it happened, so it says so.</para>
    ///
    /// <para>Static and bounded, like the diagnostics counters in the library. Off until the
    /// monitor starts, so a server nobody is watching does no work for it, and capped so a burst
    /// between ticks costs a fixed amount rather than growing. The team is reported as an id and
    /// named later: the collector already resolves names and this is not the place to read a
    /// second database.</para>
    /// </summary>
    internal static class MonitorActivity
    {
        private const int MaxPending = 200;

        private static readonly Lock Gate = new();
        private static readonly Queue<ActivityEntry> Pending = new();

        /// <summary>Set by the monitor when it starts. Reporting is a no-op until then.</summary>
        internal static bool Enabled { get; set; }

        internal static void Report(string dataSetName, string teamId, string text)
        {
            if (!Enabled || string.IsNullOrEmpty(text))
                return;

            lock (Gate)
            {
                Pending.Enqueue(new ActivityEntry(DateTimeOffset.UtcNow, dataSetName, teamId, text));
                while (Pending.Count > MaxPending)
                    Pending.Dequeue();
            }
        }

        /// <summary>Takes everything reported since the last call.</summary>
        internal static List<ActivityEntry> Drain()
        {
            lock (Gate)
            {
                if (Pending.Count == 0)
                    return [];
                var taken = Pending.ToList();
                Pending.Clear();
                return taken;
            }
        }
    }
}
