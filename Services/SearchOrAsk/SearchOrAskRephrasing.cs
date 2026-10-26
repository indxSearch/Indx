namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// A question asked again in other words: the visitor's quiet way of saying an answer missed
    /// (26 Oct 2026). Whether two questions in a row are the same request is the judge's call
    /// (<see cref="SearchOrAskRecipes.Rephrased"/>); word and record matching were tried first and
    /// were wrong more often than right. This holds what the comparison needs around the judge.
    /// </summary>
    public static class SearchOrAskRephrasing
    {
        /// <summary>A rewording comes soon or not at all: later it is a new visit to the question.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

        /// <summary>From this probability up, the judge's word counts as "asked again". Measured on
        /// the labelled pairs (JevProbe/data/rephrased.tsv).</summary>
        public const double Threshold = 0.5;

        /// <summary>The same text, lowercased with its spaces tidied: the same question again is a
        /// double submit, never judged and never counted.</summary>
        public static string Normalise(string text) =>
            string.Join(' ', text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
