namespace IndxServer.Monitor
{
    /// <summary>
    /// The latest snapshot and the recent events, held for whoever wants to look.
    ///
    /// <para>The collector runs whether or not anything is displaying it, so that a page opened in
    /// the console has something to show without a terminal having been attached first. This is
    /// where its output waits: the Terminal.Gui screen, the piped block and the admin page are all
    /// readers of the same thing.</para>
    ///
    /// <para>Events are a fixed ring in memory and nothing more. They are gone on restart, and an
    /// instance that scaled out has only its own. That is the right trade for "what is happening
    /// now"; anything that has to answer "what changed last Tuesday" needs a table, and that is a
    /// bigger decision than this.</para>
    /// </summary>
    internal sealed class MonitorState
    {
        private const int MaxEvents = 500;

        private readonly Lock _gate = new();
        private readonly List<MonitorEvent> _events = [];
        private MonitorSnapshot? _latest;

        /// <summary>Raised after each publish, for a page that wants to re-render.</summary>
        internal event Action? Changed;

        internal MonitorSnapshot? Latest => Volatile.Read(ref _latest);

        internal void Publish(MonitorSnapshot snapshot)
        {
            if (snapshot.NewEvents.Count > 0)
                Add(snapshot.NewEvents);

            Volatile.Write(ref _latest, snapshot);
            Changed?.Invoke();
        }

        /// <summary>A log line, from <see cref="MonitorLoggerProvider"/>.</summary>
        internal void Add(MonitorEvent entry) => Add([entry]);

        private void Add(IReadOnlyList<MonitorEvent> entries)
        {
            lock (_gate)
            {
                _events.AddRange(entries);
                if (_events.Count > MaxEvents)
                    _events.RemoveRange(0, _events.Count - MaxEvents);
            }
        }

        /// <summary>Oldest first. The screen reverses it; the page does its own thing.</summary>
        internal IReadOnlyList<MonitorEvent> Events()
        {
            lock (_gate)
                return _events.ToArray();
        }
    }
}
