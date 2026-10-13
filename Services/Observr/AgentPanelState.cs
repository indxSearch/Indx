namespace IndxServer.Services.Observr
{
    /// <summary>An action a tab hands to the Agent: a label for the thread, the ask to run, and
    /// what to do after a Yes (reload the tab).</summary>
    public sealed record AgentAction(string Label, Func<CancellationToken, Task<ObservrOutcome>> Ask, Func<Task>? OnApplied = null);

    /// <summary>
    /// Per-circuit state of the Agent panel (Notes/Anders/console-shell-plan-2026-10-13.md, stage 5):
    /// whether it is open, and the action a tab's button handed it. The header's button and the
    /// tabs are separate islands from the panel; they share the circuit, so a scoped service
    /// reaches all three, the way HeaderState does for the trail.
    /// </summary>
    public sealed class AgentPanelState
    {
        public bool IsOpen { get; private set; }
        /// <summary>A question to put in the composer when the panel opens.</summary>
        public string? Prefill { get; private set; }
        /// <summary>Handed by a tab's button, taken by the panel.</summary>
        public AgentAction? Pending { get; private set; }

        public event Action? Changed;

        public void Toggle() { IsOpen = !IsOpen; Changed?.Invoke(); }
        public void Close() { if (!IsOpen) return; IsOpen = false; Changed?.Invoke(); }

        public void Open(string? prefill = null)
        {
            IsOpen = true;
            Prefill = prefill;
            Changed?.Invoke();
        }

        /// <summary>Opens the panel and runs an action there.</summary>
        public void Run(AgentAction action)
        {
            Pending = action;
            IsOpen = true;
            Changed?.Invoke();
        }

        public AgentAction? TakePending()
        {
            var p = Pending;
            Pending = null;
            return p;
        }

        public string? TakePrefill()
        {
            var p = Prefill;
            Prefill = null;
            return p;
        }
    }
}
