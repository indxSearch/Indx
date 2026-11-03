using Indx.Api;
using Indx.Systm.Blazor;
using Microsoft.AspNetCore.Components;

namespace IndxServer.Components.Shared
{
    /// <summary>
    /// Where the command palette can go, for one user at one moment: the active team's datasets and
    /// each one's sections, the user's other teams, the team's own pages, the account pages and,
    /// for an instance admin, the admin area. A section an editor alone sees is left out for a
    /// viewer, as the dataset's side menu leaves it out. The id of each item is the address it goes
    /// to; one that starts with http opens in a new tab.
    /// </summary>
    public static class ConsoleCommands
    {
        public sealed record Context(
            IReadOnlyList<(string Name, string Role)> Teams,
            string? ActiveTeam,
            string? ActiveRole,
            IReadOnlyList<string> Datasets,
            bool InstanceAdmin,
            bool Statistics,
            bool Observr);

        // The dataset sections the palette offers, as the side menu names them; null for any role.
        private static readonly (string Tab, string Label, RenderFragment<int> Icon, string? Role, string Keywords)[] Sections =
        [
            ("status", "Status", Components.Datasets.DatasetIcons.Status, null, "state overview"),
            ("fields", "Field configuration", Components.Datasets.DatasetIcons.Fields, null, "fields searchable filterable facetable schema"),
            ("categories", "Categories", Components.Datasets.DatasetIcons.Categories, null, "tree hierarchy taxonomy"),
            ("webhooks", "Webhooks", Components.Datasets.DatasetIcons.Webhooks, Services.TeamRoles.Admin, "webhook sync sanity optimizely cms"),
            ("query-parameters", "Query parameters", Components.Datasets.DatasetIcons.QueryParameters, Services.TeamRoles.Editor, "coverage depth relevance"),
            ("search", "Search preview", Components.Datasets.DatasetIcons.Search, null, "try test query"),
            ("statistics", "Statistics", Components.Datasets.DatasetIcons.Statistics, null, "analytics searches clicks"),
            ("boosts", "Boost rules", Components.Datasets.DatasetIcons.Boost, Services.TeamRoles.Editor, "boost ranking campaign"),
            ("synonyms", "Synonyms", Components.Datasets.DatasetIcons.Synonym, Services.TeamRoles.Editor, "synonym terms"),
            ("options", "Options", Components.Datasets.DatasetIcons.Sliders, Services.TeamRoles.Admin, "settings rename delete"),
        ];

        private static readonly RenderFragment<int> Dataset = size => b => { b.OpenComponent<Indx.Systm.Blazor.Icons.Database>(0); b.AddAttribute(1, "Size", size); b.AddAttribute(2, "Color", "currentColor"); b.CloseComponent(); };
        private static RenderFragment<int> Icon<T>() where T : IComponent => size => b => { b.OpenComponent<T>(0); b.AddAttribute(1, "Size", size); b.AddAttribute(2, "Color", "currentColor"); b.CloseComponent(); };

        /// <summary>Every item, with the keywords each is also found by.</summary>
        public static List<(CommandPalette.Item Item, string Keywords)> Build(Context c)
        {
            var list = new List<(CommandPalette.Item, string)>();
            // An address once: the palette keys its rows by it.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string href, string label, string group, RenderFragment<int>? icon, string? hint = null, string keywords = "")
            {
                if (seen.Add(href)) list.Add((new CommandPalette.Item(href, label, group, icon, hint), keywords));
            }

            if (c.ActiveTeam is { } team)
            {
                foreach (var ds in c.Datasets)
                    Add(Services.TeamRoutes.Dataset(team, ds), ds, "Datasets", Dataset, team, "dataset");
                foreach (var ds in c.Datasets)
                    foreach (var s in Sections)
                    {
                        if (s.Role != null && !RoleAtLeast(c.ActiveRole, s.Role)) continue;
                        if (s.Tab == "statistics" && !c.Statistics) continue;
                        // Status too names its tab, so it is not the same address as the dataset itself.
                        var href = Services.TeamRoutes.Dataset(team, ds) + $"?tab={s.Tab}";
                        Add(href, $"{ds} › {s.Label}", "Dataset sections", s.Icon, null, s.Keywords);
                    }
                Add(Services.TeamRoutes.Team(team), $"{team}: datasets", "Team", Icon<Indx.Systm.Blazor.Icons.Database>(), null, "team home all datasets");
                Add(Services.TeamRoutes.TeamSettings(team), "Manage team", "Team", Icon<Indx.Systm.Blazor.Icons.Users>(), team, "members invite roles settings");
                if (RoleAtLeast(c.ActiveRole, Services.TeamRoles.Admin))
                    Add(Services.TeamRoutes.TeamKeys(team), "Team API keys", "Team", Icon<Indx.Systm.Blazor.Icons.Key>(), team, "keys tokens");
                if (c.Observr)
                    Add(Services.TeamRoutes.TeamObservr(team), "Observr", "Team", Components.Datasets.DatasetIcons.Observr, team, "findings suggestions agent");
            }
            foreach (var (name, _) in c.Teams)
                if (name != c.ActiveTeam)
                    Add(Services.TeamRoutes.Team(name), name, "Switch team", Icon<Indx.Systm.Blazor.Icons.Users>(), null, "team");

            Add("/account/manage", "Account", "Account", Icon<Indx.Systm.Blazor.Icons.User>(), null, "profile password email");
            Add("/account/api-key", "API keys", "Account", Icon<Indx.Systm.Blazor.Icons.Key>(), null, "personal keys tokens");
            Add("/account/notifications", "Notifications", "Account", Icon<Indx.Systm.Blazor.Icons.Bell>(), null, "alerts");
            Add("https://docs.indx.co", "Docs", "Account", Icon<Indx.Systm.Blazor.Icons.Book>(), "opens in a new tab", "documentation help guide");
            Add("/swagger", "Swagger", "Account", Icon<Indx.Systm.Blazor.Icons.Api>(), "opens in a new tab", "api reference openapi");

            if (c.InstanceAdmin)
            {
                Add("/admin/users", "Users", "Admin", Icon<Indx.Systm.Blazor.Icons.Users>(), null, "accounts people");
                Add("/admin/teams", "Teams", "Admin", Icon<Indx.Systm.Blazor.Icons.Users>(), null, "all teams");
                Add("/admin/datasets", "All datasets", "Admin", Icon<Indx.Systm.Blazor.Icons.Database>(), null, "overview documents");
                Add("/admin/monitor", "Monitor", "Admin", Icon<Indx.Systm.Blazor.Icons.Cpu>(), null, "cpu memory health");
                Add("/admin/settings", "Settings: General", "Admin", Components.Datasets.DatasetIcons.Sliders, null, "instance");
                Add("/admin/settings/access", "Settings: Access", "Admin", Components.Datasets.DatasetIcons.Sliders, null, "login sso");
                Add("/admin/settings/registration", "Settings: Registration", "Admin", Components.Datasets.DatasetIcons.Sliders, null, "sign up invite");
                Add("/admin/settings/websites", "Settings: Websites", "Admin", Components.Datasets.DatasetIcons.Sliders, null, "cors origins allowed");
                Add("/admin/settings/mcp", "Settings: AI agents (MCP)", "Admin", Components.Datasets.DatasetIcons.Sliders, null, "mcp agents");
                Add("/admin/observr", "Observr", "Admin", Components.Datasets.DatasetIcons.Observr, null, "subscription");
                Add("/admin/license", "License", "Admin", Icon<Indx.Systm.Blazor.Icons.Key>(), null, "edition");
            }
            return list;
        }

        private static bool RoleAtLeast(string? role, string needed) => needed switch
        {
            Services.TeamRoles.Admin => Services.TeamRoles.CanAdmin(role),
            Services.TeamRoles.Editor => Services.TeamRoles.CanWrite(role),
            _ => Services.TeamRoles.CanRead(role),
        };
    }

    /// <summary>
    /// The palette's search, with Indx itself: a small engine over the items' labels, groups and
    /// keywords, so a typo or a part of a word still finds the page ("synonim", "furn syn"). Before
    /// the engine's own order come the labels that start with what was typed, then those that hold
    /// it, so an exact fragment is never ranked below a guess. Rebuilt when the items change; a few
    /// hundred index in milliseconds.
    /// </summary>
    internal sealed class CommandSearch : IDisposable
    {
        private SearchEngine? _engine;
        private List<(CommandPalette.Item Item, string Keywords)> _items = new();

        public const int MaxResults = 40;

        public void Use(List<(CommandPalette.Item Item, string Keywords)> items)
        {
            if (_items.Select(i => i.Item.Id).SequenceEqual(items.Select(i => i.Item.Id))) { _items = items; return; }
            _engine?.Dispose();
            _engine = null;
            _items = items;
        }

        /// <summary>The items for what was typed, best first; with nothing typed, the first in the
        /// catalog's own order (the team's datasets, then its pages).</summary>
        public IReadOnlyList<CommandPalette.Item> Find(string text, int emptyCount = 12)
        {
            var needle = text.Trim();
            if (needle.Length == 0)
                return _items.Where(i => i.Item.Group != "Dataset sections").Take(emptyCount).Select(i => i.Item).ToList();

            var order = new List<int>();
            var seen = new HashSet<int>();
            void Take(int i) { if (seen.Add(i)) order.Add(i); }
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Item.Label.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) Take(i);
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Item.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)) Take(i);
            try
            {
                var engine = Engine();
                if (engine != null)
                    foreach (var i in MicroIndex.Find(engine, needle, _items.Count, MaxResults)) Take(i);
            }
            catch
            {
                // The fuzzy match is a convenience: the label matches above still stand.
            }
            return order.Take(MaxResults).Select(i => _items[i].Item).ToList();
        }

        private SearchEngine? Engine()
        {
            if (_engine != null || _items.Count == 0) return _engine;
            return _engine = MicroIndex.Build(_items.Select(x => $"{x.Item.Label} {x.Item.Group} {x.Keywords}".Replace('›', ' ').Replace(':', ' ')));
        }

        public void Dispose()
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
