using Indx.Api;
using IndxServer.Services;

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
        /// one, and whether it is the one counted. The preview puts the levels after "Records with
        /// every word", so the names finish that phrase (Nov 2026: "As typed 0" beside a result whose
        /// first word was whole read as a count that had missed it).</summary>
        public sealed record Level(string Name, int Records, bool Counted)
        {
            /// <summary>What the level counts, on hover: a record is on the level of its weakest word.</summary>
            public string Hint => Counted ? "These are the results. A record counts on the level of its weakest word."
                : Records == 0 ? "No record holds every word this way."
                : "Records where each word is met on this level or a nearer one.";
        }

        /// <summary>The words before the levels, which say what they count.</summary>
        public const string LevelsLabel = "Records with every word";

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
            if (PatternOnly(result))
            {
                // Every record listed came in by pieces of words alone: the word lookup confirmed
                // none of the query's words in any of them. Indx is only pattern searching.
                headline = "Pattern matches";
                notes.Add("None of the words was found in these records, only parts of them: BM25F's pieces, not whole words or typos of them.");
            }
            else if (result.ToleranceCounts is null)
            {
                // The Bm25f mode: any of the words, or parts of them, is a candidate. Reranked unless
                // the search says otherwise; the report the preview asks for is there only when it is.
                headline = "Broad matches";
                notes.Add($"{result.Count:N0} records hold any of the words or parts of them: the Bm25f mode, with no typo levels.");
                if (result.RerankReport == null && result.Records.Length > 0)
                    notes.Add("BM25F's order, not reranked.");
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
                levels.Add(new Level("as typed", c.Exact, result.CountedTolerance == Tolerance.Exact && !result.IsFallback));
                if (c.OneEdit is { } one) levels.Add(new Level("within one typo", one, result.CountedTolerance == Tolerance.OneEdit && !result.IsFallback));
                if (c.TwoEdits is { } two) levels.Add(new Level("within two typos", two, result.CountedTolerance == Tolerance.TwoEdits && !result.IsFallback));
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
                notes.Add($"{result.Ranked:N0} of the {result.Count:N0} were ranked: the candidate bound, taken by BM25F, the records holding a word itself before those met only by its start. The others are counted, and in the facets, but cannot come into the list. Candidates under Query parameters ranks more.");
            return new Summary(headline, levels, notes);
        }

        /// <summary>What one listed record is, by its place in the list: counted (it holds every word on
        /// the counted level), related (BM25F's best below them, Rest), partial (the fallback), or broad
        /// (the Bm25f mode). The label and what it means, on hover. Until Nov 2026 the preview said
        /// "close match" or "pattern match", 5.x's words for either side of the truncation.</summary>
        public static (string Label, string Title) KindOf(Result result, int index)
        {
            if (index < result.Records.Length && For(result, result.Records[index].DocumentKey) is { Count: > 0 } words && words.All(w => !w.Found))
                return ("pattern match", "Found only by parts of words: none of the words was confirmed in this record");
            if (result.ToleranceCounts is null)
                return ("broad match", "Holds some of the words or parts of them: the Bm25f mode, with no typo levels");
            if (result.IsFallback)
                return ("partial", "Holds some of the words: no record holds them all, so the fallback lists those with the most");
            if (index < result.CountedInList())
                return ("counted", "Holds every word on the level counted");
            return ("related", "Below the counted records: BM25F's best among the rest (Rest)");
        }

        /// <summary>
        /// The kind of match as one word, for Search or ask's judge (Nov 2026): strong (every word
        /// as typed), fuzzy (every word within typos), partial (the fallback), pattern (only parts of
        /// words in every listed record), broad (no word lookup: the Bm25f mode, or no field with
        /// WordMatching). The preview's headlines, in the words the judge reads.
        /// </summary>
        public static string MatchKind(Result result) =>
            PatternOnly(result) ? "pattern"
            : result.ToleranceCounts is null ? "broad"
            : result.IsFallback ? "partial"
            : result.CountedTolerance == Tolerance.Exact ? "strong"
            : "fuzzy";

        /// <summary>
        /// Whether every listed record matched by pieces of words alone: the rerank report confirms
        /// none of the query's words in any of them. Only known when the search was reranked, which
        /// the preview asks for; false otherwise, and with nothing listed.
        /// </summary>
        public static bool PatternOnly(Result result) =>
            result.Records.Length > 0 && result.RerankReport?.Matches is { Length: > 0 } matches
            && matches.All(m => m.Words.All(w => w.How == WordMatch.NotFound));

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
