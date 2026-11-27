namespace IndxServer.Services
{
    /// <summary>
    /// The console's URL scheme, in one place: a team page and a dataset page under it.
    /// Team names are URL-safe by construction (see <see cref="Data.Team.Name"/>); dataset
    /// names are not, so they are escaped.
    /// </summary>
    public static class TeamRoutes
    {
        public static string Team(string teamName) => $"/teams/{teamName}";

        public static string TeamSettings(string teamName) => $"/teams/{teamName}/settings";
        public static string TeamObservr(string teamName) => $"/teams/{teamName}/observr";
        public static string TeamObservrDatasets(string teamName) => $"/teams/{teamName}/observr/datasets";
        public static string TeamKeys(string teamName) => $"/teams/{teamName}/keys";

        public static string Dataset(string teamName, string dataset) =>
            $"/teams/{teamName}/datasets/{Uri.EscapeDataString(dataset)}";

        /// <summary>
        /// The dataset a console path points at, decoded, or null when the path is not a dataset
        /// page. The inverse of <see cref="Dataset"/>, and it lives beside it for a reason:
        /// <c>NavigationManager.Uri</c> is escaped while a route parameter arrives decoded, so
        /// comparing one against the other looks right and fails for every name that escapes to
        /// something else. A dataset called "norsk skoleregister" reached the page as
        /// <c>norsk%20skoleregister</c> in the URL and <c>norsk skoleregister</c> in the
        /// parameter, the two never matched, and the breadcrumb silently stopped updating.
        /// Spaces, <c>æøå</c>, <c>+</c> and <c>&amp;</c> all do this.
        /// </summary>
        public static string? DatasetFromPath(string? relativePath)
        {
            // teams/{team}/datasets/{name}, or the legacy datasets/{name}. The console has no pages
            // below a dataset, so a further segment is not this scheme.
            var name = Segments(relativePath) switch
            {
                ["teams", _, "datasets", var n] => n,
                ["datasets", var n] => n,
                _ => null,
            };
            return string.IsNullOrEmpty(name) ? null : Uri.UnescapeDataString(name);
        }

        /// <summary>
        /// Which console view a path names, read segment by segment. Until 1 Nov 2026 the console
        /// looked at how the path ended, so a dataset or a team called "settings", "keys" or
        /// "observr" opened that page instead of its own, and a query string
        /// (<c>/teams/x/settings?foo=1</c>) fell through to the team page.
        /// </summary>
        public static ConsoleView ViewFromPath(string? relativePath) => Segments(relativePath) switch
        {
            ["teams", _, "settings"] => ConsoleView.Settings,
            ["teams", _, "keys"] => ConsoleView.Keys,
            ["teams", _, "observr"] => ConsoleView.Observr,
            ["teams", _, "observr", "datasets"] => ConsoleView.ObservrDatasets,
            ["teams", _, "datasets", _] or ["datasets", _] => ConsoleView.Dataset,
            _ => ConsoleView.Team,
        };

        /// <summary>The path's segments, without the query or fragment, which are not part of it.
        /// The fixed segments are compared lower-case; a team or dataset name keeps its case.</summary>
        private static string[] Segments(string? relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return [];
            var path = relativePath;
            int cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0) path = path[..cut];
            var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            // The fixed words are the first and third segments, and the fourth under observr
            // (teams/{team}/observr/datasets); the others are names and keep their case.
            for (int i = 0; i < segments.Length; i++)
                if (i is 0 or 2 || (i == 3 && segments.Length == 4 && segments[2].Equals("observr", StringComparison.OrdinalIgnoreCase)))
                    segments[i] = segments[i].ToLowerInvariant();
            return segments;
        }
    }

    /// <summary>The views of the console's one routed page (ConsolePage).</summary>
    public enum ConsoleView { Team, Settings, Observr, ObservrDatasets, Keys, Dataset }
}
