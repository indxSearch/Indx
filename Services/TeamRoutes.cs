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
            if (string.IsNullOrEmpty(relativePath))
                return null;

            // A query or fragment is not part of the name.
            var path = relativePath;
            int cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0) path = path[..cut];
            path = path.Trim('/');

            const string marker = "datasets/";
            int at = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return null;

            var name = path[(at + marker.Length)..];
            // The console has no pages below a dataset, so a further segment is not this scheme.
            if (name.Length == 0 || name.Contains('/'))
                return null;

            return Uri.UnescapeDataString(name);
        }
    }
}
