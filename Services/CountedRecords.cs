using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// How many of a result's records are matches the search counted (6.0): the documents holding
    /// every query word on the rung counted, which come first in the list. What follows them is the
    /// rest (<c>Candidates.Rest</c>), and a fallback or a Bm25f search counted none. Until 6.0 this
    /// was read from <c>TruncationIndex</c>, the last record above the cut.
    /// </summary>
    public static class CountedRecords
    {
        /// <summary>The records at the head of <paramref name="result"/> that were counted, at most
        /// its records' length; 0 for a fallback, a Bm25f search or an empty result.</summary>
        public static int CountedInList(this Result result)
        {
            int shown = result.Records?.Length ?? 0;
            if (result.IsFallback || result.CountedTolerance is not { } rung || result.ToleranceCounts is not { } counts)
                return 0;
            int counted = rung switch
            {
                Tolerance.Exact => counts.Exact,
                Tolerance.OneEdit => counts.OneEdit ?? 0,
                _ => counts.TwoEdits ?? 0,
            };
            return Math.Min(shown, counted);
        }
    }
}
