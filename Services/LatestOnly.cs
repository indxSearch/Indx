namespace IndxServer.Services
{
    /// <summary>
    /// "The newest answer wins", for work started once per keystroke. Take a ticket before the
    /// work, and when it comes back apply the result only if the ticket is still current. The
    /// same rule indx-intrface uses for its searches (<c>latestRequestId</c> in
    /// useSearchExecution.ts): every search runs, a superseded one just never reaches the screen.
    ///
    /// In Blazor it buys something more. An event handler that runs its search inline holds the
    /// circuit until it returns, so under latency the keystrokes queue up and the list walks
    /// through "h", "ha", "ham" long after the box says "hamburger". With the search awaited off
    /// the circuit, the next keystroke is handled at once and simply outdates the ticket.
    /// </summary>
    public sealed class LatestOnly
    {
        private int _current;

        /// <summary>Starts a new piece of work, outdating every ticket handed out before.</summary>
        public int Begin() => Interlocked.Increment(ref _current);

        /// <summary>True while no newer work has begun since this ticket was taken.</summary>
        public bool IsCurrent(int ticket) => Volatile.Read(ref _current) == ticket;
    }
}
