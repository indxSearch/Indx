namespace IndxCloudApi.Services
{
#pragma warning disable 1591
    /// <summary>
    /// The roles a user can hold on a team's datasets. Distinct from the platform-wide
    /// Identity roles ("Admin"/"Member") that gate the /Admin/* pages.
    /// </summary>
    public static class TeamRoles
    {
        public const string Admin  = "Admin";   // manage members + grants, delete dataset, + all Editor rights
        public const string Editor = "Editor";  // insert/update/delete docs, index, hibernate, field-config
        public const string Viewer = "Viewer";  // search, read field info, status

        public static readonly string[] All = { Admin, Editor, Viewer };

        public static bool IsValid(string? role) => role is Admin or Editor or Viewer;

        /// <summary>Higher rank = more access. Used to pick the strongest of several roles.</summary>
        public static int Rank(string? role) => role switch
        {
            Admin  => 3,
            Editor => 2,
            Viewer => 1,
            _      => 0,
        };

        /// <summary>Returns the stronger of two roles (null = no access).</summary>
        public static string? Max(string? a, string? b) => Rank(a) >= Rank(b) ? a : b;

        // Permission gates — what each role may do on the team's datasets.
        public static bool CanRead(string? role)  => Rank(role) >= Rank(Viewer);
        public static bool CanWrite(string? role) => Rank(role) >= Rank(Editor);
        public static bool CanAdmin(string? role) => Rank(role) >= Rank(Admin);
    }
#pragma warning restore 1591
}
