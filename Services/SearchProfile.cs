namespace IndxServer.Services
{
    /// <summary>
    /// The shape of a window's search, six shares on one scale for the Statistics overview's radar:
    /// how often a search is clicked, finds something, is confirmed by coverage, comes with a
    /// filter, is one word, and how often the first result is the one chosen. Every axis is a share
    /// of the searches (or of the selects, for the last), so a change in any of them is the shape
    /// moving, and two windows lie on the same rings. Built from the reads the overview already
    /// makes; a window with no searches has no shape.
    /// </summary>
    public sealed record SearchProfile(double Clicked, double WithHits, double Covered, double Filtered, double OneWord, double FirstResult)
    {
        public static readonly IReadOnlyList<string> AxisLabels = ["Clicked", "With hits", "Covered", "Filtered", "One word", "First result"];

        public IReadOnlyList<double> Values => [Clicked, WithHits, Covered, Filtered, OneWord, FirstResult];

        /// <summary>Null when the window has no searches. Coverage counts only when the engine
        /// confirmed or denied it: a window whose searches ran without coverage is on the axis as
        /// nothing rather than as zero.</summary>
        public static SearchProfile? Build(OverviewStat overview, TypingStat typing, PositionStat positions)
        {
            if (overview.Searches <= 0) return null;
            double Share(long n, long of) => of > 0 ? Math.Clamp((double)n / of, 0, 1) : 0;
            return new SearchProfile(
                Share(overview.ClickedSearches, overview.Searches),
                Share(overview.Searches - overview.ZeroHits, overview.Searches),
                Share(overview.Searches - overview.Uncovered, overview.Searches),
                Share(typing.WithFilter, typing.Total),
                Share(typing.OneWord, typing.Total),
                Share(positions.First, positions.Total));
        }
    }
}
