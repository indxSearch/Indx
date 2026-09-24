namespace IndxServer.Monitor
{
    /// <summary>
    /// Presents a snapshot. The seam that lets one collector serve the append-only text block, the
    /// Terminal.Gui screen and (later) a Blazor page without any of them knowing about each other.
    /// </summary>
    internal interface IMonitorRenderer
    {
        void Render(MonitorSnapshot snapshot);
        void Stop();
    }
}
