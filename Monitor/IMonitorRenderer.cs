namespace IndxServer.Monitor
{
    /// <summary>
    /// Presents a snapshot. The seam that lets one collector serve the append-only text block, the
    /// Terminal.Gui screen and (later) a Blazor page without any of them knowing about each other.
    /// </summary>
    internal interface IMonitorRenderer
    {
        /// <summary>Called once before the first <see cref="Render"/>. A renderer that owns the
        /// screen starts its own thread here; a renderer that only writes lines does nothing.
        /// <paramref name="webUrl"/> is where the console is served, or null when that cannot be
        /// determined.</summary>
        void Start(string? webUrl);

        void Render(MonitorSnapshot snapshot);

        void Stop();
    }
}
