namespace IndxServer.Monitor
{
    /// <summary>
    /// Where this instance can be reached, for the screen to show. Resolved once, after the server
    /// has started: an admin can switch MCP off later and the line would then be stale, which is
    /// a rare deliberate action and cheaper to live with than re-reading settings.json every tick.
    /// </summary>
    internal sealed record MonitorEndpoints(string? Web, string? Mcp);

    /// <summary>
    /// Presents a snapshot. The seam that lets one collector serve the append-only text block, the
    /// Terminal.Gui screen and (later) a Blazor page without any of them knowing about each other.
    /// </summary>
    internal interface IMonitorRenderer
    {
        /// <summary>Called once before the first <see cref="Render"/>. A renderer that owns the
        /// screen starts its own thread here; a renderer that only writes lines does nothing.
        /// <paramref name="endpoints"/> is where this instance can be reached.</summary>
        void Start(MonitorEndpoints endpoints);

        void Render(MonitorSnapshot snapshot);

        void Stop();
    }
}
