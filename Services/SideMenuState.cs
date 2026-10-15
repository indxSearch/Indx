using Microsoft.AspNetCore.Components;

namespace IndxServer.Services
{
    /// <summary>One entry of the side column's menu. <see cref="Href"/> navigates; a page that owns
    /// the menu may instead handle the choice through <see cref="SideMenuState.OnSelect"/>.</summary>
    /// <summary>A section of the side column. <paramref name="Badge"/> is a count at the end of the
    /// row: a signal pill by default (something to act on, the boost rules' expired count), or
    /// with <paramref name="BadgeIcon"/> a quiet chip with that icon before the number (what
    /// Observr found, in Observr's look), which says whose count it is.</summary>
    public sealed record SideMenuItem(string Key, string Label, RenderFragment<int>? Icon = null, string? Href = null,
        bool Disabled = false, string? Title = null, string? Badge = null, string? BadgeTitle = null, bool Experimental = false,
        RenderFragment<int>? BadgeIcon = null);

    /// <summary>A group of items under a title; <see cref="Note"/> is one quiet line under them
    /// (the admin area's version).</summary>
    public sealed record SideMenuGroup(string? Title, IReadOnlyList<SideMenuItem> Items, string? Note = null);

    /// <summary>
    /// Per-circuit slot for what the side column's menu shows, the way <see cref="HeaderState"/>
    /// holds the trail. A page that has sections of its own (the dataset page) sets its groups and
    /// which is open; when nothing is set, the side menu derives the area's list from the URL (the
    /// team's pages, the admin sections, the account pages). Owner-tagged, as the header is, so a
    /// page being torn down cannot clear what its successor just set.
    /// </summary>
    public sealed class SideMenuState
    {
        public IReadOnlyList<SideMenuGroup>? Groups { get; private set; }
        public string? Active { get; private set; }
        /// <summary>When set, choosing an item calls this with its key instead of navigating.</summary>
        public Action<string>? OnSelect { get; private set; }
        private object? _owner;

        public event Action? Changed;

        public void Set(object owner, IReadOnlyList<SideMenuGroup> groups, string? active, Action<string>? onSelect = null)
        {
            _owner = owner;
            Groups = groups;
            Active = active;
            OnSelect = onSelect;
            Changed?.Invoke();
        }

        public void Clear(object owner)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _owner = null;
            Groups = null;
            Active = null;
            OnSelect = null;
            Changed?.Invoke();
        }
    }
}
