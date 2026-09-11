namespace IndxCloudApi.Services
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
    }
}
