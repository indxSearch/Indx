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

        /// <summary>Claim the header for <paramref name="owner"/>. Note the asymmetry with
        /// <see cref="Clear"/>: this takes ownership unconditionally, so a page that is being torn
        /// down and calls Set from a late callback will take the trail back from the page that
        /// replaced it. A caller that can Set asynchronously has to check it is still alive first.</summary>
        public void Set(object owner, RenderFragment? breadcrumb)
        {
            _owner = owner;
            Breadcrumb = breadcrumb;
            Changed?.Invoke();
        }

        /// <summary>Drop the owner's trail. <paramref name="keepIf"/> lets a console page being
        /// torn down leave its trail up while the URL is still a console URL, so the header does
        /// not flash the fallback while the next page resolves; the next Set replaces it.</summary>
        public void Clear(object owner, bool keepIf = false)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _owner = null;
            if (keepIf) return;
            Breadcrumb = null;
            Changed?.Invoke();
        }
    }
}
