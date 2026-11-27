using System.Runtime.CompilerServices;
using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>How refocusing changed a search (<see cref="Refocusing"/>).</summary>
    public enum RefocusOutcome
    {
        /// <summary>Searched as asked: a record holds every word, or refocusing was not asked for.</summary>
        None,
        /// <summary>No record holds every word, so the list is in BM25F's order: the rare words the
        /// records hold weigh most, the common words of a question little. The count, the typo
        /// levels and the facets are still the search's own.</summary>
        Reordered,
        /// <summary>The word being typed is too short to be met by its start, and it alone left
        /// no record holding every word: the list is the text before it, until it is longer.</summary>
        Held,
    }

    /// <summary>
    /// Refocusing a search: a search whose words no record holds all of, a
    /// question ("hvem har skrevet den indre fredsprosessen") or a description of what to list,
    /// gets BM25F's order in place of the fallback's, which ranks by how many of the words a record
    /// holds and so lets the common words decide. Language independent, no model, one more search
    /// and only when the first falls back; the count, the levels and the facets stay the search's.
    /// <para>A request asks for it (<c>?refocus=true</c> on search, 10 Nov 2026): intrface does
    /// while Search or ask's judge reads the text as a question or a description
    /// (thresholds.refocus), and keeps doing so on every keystroke until a judgement says otherwise,
    /// so the list does not flip at each pause. Never a dataset setting: on 9 Nov it was one
    /// (focusing), and moved lookups and descriptions on every search that fell back.</para>
    /// <para>The library's fallback cannot be asked for another order, so the BM25F records are put
    /// into the first result through its setters.</para>
    /// </summary>
    public static class Refocusing
    {
        private static readonly ConditionalWeakTable<Result, StrongBox<RefocusOutcome>> Outcomes = new();
        // The tokenizer every server engine is built with; the property hands out a copy each read.
        private static readonly char[] Delimiters = TokenizerSetup.Default.Delimiters;

        /// <summary>How refocusing changed this result; None for one it did not touch.</summary>
        public static RefocusOutcome OutcomeOf(Result result) =>
            Outcomes.TryGetValue(result, out var box) ? box.Value : RefocusOutcome.None;

        /// <summary>The response header naming the outcome, when there is one.</summary>
        public const string ResponseHeader = "Indx-Refocus";

        public static Result Search(IServerSearchEngine engine, Query query)
        {
            var first = engine.Search(query);
            if (!first.IsFallback || first.Reason != null || string.IsNullOrWhiteSpace(query.Text))
                return first;

            // A word being typed shorter than the typing prefix is met only whole or within its
            // edits, so for a keystroke or two ("hvem har sk") no record holds it and the list
            // would drop to BM25F and back. When the text before it is held, that text's list stands.
            if (TextBeforeShortWord(query) is { } before)
            {
                var (text, cursor) = (query.Text, query.Match.CursorAt);
                query.Text = before;
                query.Match.CursorAt = null;
                var held = engine.Search(query);
                query.Text = text;
                query.Match.CursorAt = cursor;
                if (!held.IsFallback && held.Reason == null)
                    return Mark(held, RefocusOutcome.Held);
            }

            var mode = query.Mode;
            query.Mode = SearchMode.Bm25f;
            var ordered = engine.Search(query);
            query.Mode = mode;
            if (ordered.Reason != null || ordered.Records.Length == 0)
                return first;

            // BM25F's records and how it met their words, in the fallback's result: BM25F counts
            // nearly every record (any part of any word), so the count, the levels and the facets
            // stay the search's own.
            Set(first, nameof(Result.Records), ordered.Records);
            Set(first, nameof(Result.MatchReport), ordered.MatchReport);
            Set(first, nameof(Result.Reranked), ordered.Reranked);
            Set(first, nameof(Result.Fill), null);
            return Mark(first, RefocusOutcome.Reordered);
        }

        /// <summary>The text before the word being typed, when that word is shorter than the typing
        /// prefix (and at least one character); null otherwise. Only a cursor at the end, or none.</summary>
        internal static string? TextBeforeShortWord(Query query)
        {
            var text = query.Text;
            var prefixFrom = query.Match.TypingPrefixFrom;
            if (prefixFrom <= 1 || query.Match.CursorAt is { } at && at < text.Length) return null;
            if (text.Length == 0 || Array.IndexOf(Delimiters, text[^1]) >= 0) return null;
            var cut = text.LastIndexOfAny(Delimiters);
            if (cut <= 0 || text.Length - cut - 1 >= prefixFrom) return null;
            var before = text[..cut].TrimEnd(Delimiters);
            return before.Length == 0 ? null : before;
        }

        private static Result Mark(Result result, RefocusOutcome outcome)
        {
            Outcomes.AddOrUpdate(result, new StrongBox<RefocusOutcome>(outcome));
            return result;
        }

        private static void Set(Result result, string property, object? value) =>
            typeof(Result).GetProperty(property)!.SetValue(result, value);
    }
}
