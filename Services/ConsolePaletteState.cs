namespace IndxServer.Services
{
    /// <summary>
    /// Per-circuit state of the command palette: whether it is open. The palette is drawn in the
    /// layout, outside the header, since the header's account controls are positioned with a
    /// transform and a fixed overlay inside them is placed against them, not the window. The
    /// header's search step and the palette are separate islands; they share the circuit, so this
    /// scoped service reaches both, as AgentPanelState does for the Agent.
    /// </summary>
    public sealed class ConsolePaletteState
    {
        public bool IsOpen { get; private set; }
        public event Action? Changed;

        public void Set(bool open)
        {
            if (IsOpen == open) return;
            IsOpen = open;
            Changed?.Invoke();
        }
    }
}
