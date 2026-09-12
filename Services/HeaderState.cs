using Microsoft.AspNetCore.Components;

namespace IndxServer.Services
{
    /// <summary>
    /// Per-circuit slot for what the header's breadcrumb area shows. The console pages put their
    /// breadcrumb (team › dataset, with switchers and state) here; when nothing is set, the nav
    /// derives a page label from the URL. Owner-tagged so a page that is being torn down cannot
    /// clear what its successor just set. The nav and the pages are separate interactive islands,
    /// but they share the circuit, so a scoped service reaches both.
    /// </summary>
    public sealed class HeaderState
    {
        public RenderFragment? Breadcrumb { get; private set; }
        private object? _owner;

        public event Action? Changed;

        public void Set(object owner, RenderFragment? breadcrumb)
        {
            _owner = owner;
            Breadcrumb = breadcrumb;
            Changed?.Invoke();
        }

        public void Clear(object owner)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _owner = null;
            Breadcrumb = null;
            Changed?.Invoke();
        }
    }
}
