using Microsoft.AspNetCore.Components;

namespace Indx.Systm.Blazor
{
    /// <summary>
    /// Brief notices for a thing done ("Settings saved.", "Key created.") or failed, shown by a
    /// <see cref="ToastHost"/> in the layout and gone by themselves. Scoped: one per circuit, so a
    /// page and the host, separate interactive islands, share it. A notice is plain ink, with an
    /// icon when it has one; a failure is in the signal colour and stays longer. No motion: a
    /// notice is simply there, and then not. The React indx-systm has the same as toast() and
    /// &lt;Toaster /&gt;.
    /// </summary>
    public sealed class ToastService
    {
        /// <summary>A notice: its text, "neutral" or "error", an icon, how long it stays, and an
        /// optional action ("Undo") that closes it when pressed.</summary>
        public sealed record Toast(int Id, string Message, string Kind, RenderFragment<int>? Icon, TimeSpan Duration, string? ActionLabel, Func<Task>? OnAction);

        /// <summary>How long a notice stays, and a failure.</summary>
        public static readonly TimeSpan NeutralDuration = TimeSpan.FromSeconds(4), ErrorDuration = TimeSpan.FromSeconds(8);

        /// <summary>The most shown at once; an older one gives way to a new one.</summary>
        public const int MaxShown = 4;

        private readonly List<Toast> _toasts = new();
        private int _next;

        public IReadOnlyList<Toast> Toasts => _toasts;
        public event Action? Changed;

        /// <summary>Shows a notice and returns its id.</summary>
        public int Show(string message, RenderFragment<int>? icon = null, TimeSpan? duration = null, string? actionLabel = null, Func<Task>? onAction = null) =>
            Add(new Toast(++_next, message, "neutral", icon, duration ?? NeutralDuration, actionLabel, onAction));

        /// <summary>Shows a failure, in the signal colour, for longer.</summary>
        public int Error(string message, TimeSpan? duration = null, string? actionLabel = null, Func<Task>? onAction = null) =>
            Add(new Toast(++_next, message, "error", null, duration ?? ErrorDuration, actionLabel, onAction));

        public void Dismiss(int id)
        {
            if (_toasts.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
        }

        private int Add(Toast toast)
        {
            _toasts.Add(toast);
            while (_toasts.Count > MaxShown) _toasts.RemoveAt(0);
            Changed?.Invoke();
            return toast.Id;
        }
    }
}
