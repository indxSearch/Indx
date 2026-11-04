using Indx.Api;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// What the search preview says about how a search found what it found (Nov 2026), in the words
    /// IndxWorkbench's summary line and record pane use: the typo level whose records were counted,
    /// how many of them were ranked, the fallback and the related records below, and per record how
    /// each query word was met (Result.RerankReport). Kept apart from the tab so the wording can be
    /// tested without rendering it.
    /// </summary>
    public static class SearchExplanation
    {
        /// <summary>One typo level: its name, how many records hold every word on it or a nearer
        /// one, and whether it is the one counted.</summary>
        public sealed record Level(string Name, int Records, bool Counted);

        /// <summary>The search as a whole: what kind of matches the results are, the typo levels with
        /// their counts, and what else was said. The headline names the kind and nothing more; the
        /// numbers are the levels' (Nov 2026: a sentence with the count repeated the level under it).</summary>
        public sealed record Summary(string Headline, IReadOnlyList<Level> Levels, IReadOnlyList<string> Notes);

        /// <summary>One query word in one record: the field that confirmed it, and how.</summary>
        public sealed record Word(string Text, string? Field, string How, bool Found);

        public static Summary? Of(Result result)
        {
            if (result.Reason != null) return null;
            var notes = new List<string>();
            var levels = new List<Level>();
            string headline;
            if (result.ToleranceCounts is null)
            {
                headline = "Ranked by relevance";
                notes.Add($"{result.Count:N0} records score above 0 by BM25F: the Bm25f mode, with no typo levels.");
            }
            else if (result.IsFallback)
            {
                headline = "Partial matches";
                notes.Add("No record holds every word: the records holding the most of them, then BM25F's best.");
            }
            else
            {
                headline = result.CountedTolerance == Tolerance.Exact ? "Strong matches" : "Fuzzy matches";
            }
            if (result.ToleranceCounts is { } c)
            {
                levels.Add(new Level("As typed", c.Exact, result.CountedTolerance == Tolerance.Exact && !result.IsFallback));
                if (c.OneEdit is { } one) levels.Add(new Level("Within one typo", one, result.CountedTolerance == Tolerance.OneEdit && !result.IsFallback));
                if (c.TwoEdits is { } two) levels.Add(new Level("Within two typos", two, result.CountedTolerance == Tolerance.TwoEdits && !result.IsFallback));
                if (c.Rest is { } rest && !result.IsFallback)
                {
                    int counted = result.CountedTolerance switch
                    {
                        Tolerance.OneEdit => c.OneEdit ?? 0,
                        Tolerance.TwoEdits => c.TwoEdits ?? 0,
                        _ => c.Exact,
                    };
                    if (rest > counted)
                        notes.Add($"{rest - counted:N0} related records below them, holding some of the words (Rest).");
                }
            }
            if (result.Count > result.Ranked)
                notes.Add($"The best {result.Ranked:N0} of the {result.Count:N0} were ranked: the candidate bound. The rest are counted, and in the facets, but not ordered.");
            return new Summary(headline, levels, notes);
        }

        /// <summary>How the reranker met each query word in one record, in the query's order; null
        /// when the search was not reranked or the record is not in the report.</summary>
        public static IReadOnlyList<Word>? For(Result result, long documentKey)
        {
            var match = result.RerankReport?.Matches?.FirstOrDefault(m => m.DocumentKey == documentKey);
            return match?.Words.Select(w => new Word(w.Word, w.Field, How(w.How), w.How != WordMatch.NotFound)).ToList();
        }

        public static string How(WordMatch how) => how switch
        {
            WordMatch.Whole => "whole word",
            WordMatch.Prefix => "start of a longer word",
            WordMatch.Suffix => "end of a longer word",
            WordMatch.Joined => "joined or split",
            WordMatch.RunTogether => "several words typed as one",
            WordMatch.OneEdit => "within one typo",
            WordMatch.TwoEdits => "within two typos",
            _ => "not confirmed",
        };
    }
}
