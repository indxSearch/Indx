using IndxServer.Data;

namespace IndxServer.Services
{
    /// <summary>How the console looks for one user (Nov 2026): light, dark or the system's, and the
    /// indx cursors. App.razor renders it as attributes on &lt;html&gt; and the systm stylesheets read
    /// them: data-theme fixes light or dark (without it the system decides), data-cursors="indx"
    /// turns the cursors on. The account page applies a change at once through js/appearance.js,
    /// with the same attributes.</summary>
    public sealed record Appearance(string? Scheme, bool Cursors)
    {
        public static readonly IReadOnlyList<string> Schemes = ["light", "dark"];

        public static readonly Appearance Default = new(null, false);

        /// <summary>The user's, with a value this version does not know (a hand-edited row) read as
        /// the system's rather than written into the page.</summary>
        public static Appearance Of(ApplicationUser user) => From(user.ColorScheme, user.IndxCursors);

        public static Appearance From(string? scheme, bool cursors) =>
            new(scheme != null && Schemes.Contains(scheme) ? scheme : null, cursors);

        public string? DataTheme => Scheme;
        public string? DataCursors => Cursors ? "indx" : null;
    }
}
