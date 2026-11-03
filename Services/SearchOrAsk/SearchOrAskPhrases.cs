using System.Text.RegularExpressions;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// Ask phrases: the words a question is built with, taken off its start so the list searches
    /// what the question is about while the visitor is still typing it. "Help me find the movie
    /// about the green ogre" searches "green ogre", which finds Shrek; the whole sentence does not,
    /// since "movie" and "find" match titles better than "ogre" matches a description (measured on
    /// tmdb23k, 20 Oct 2026). No model and no pause: the page applies the list on every keystroke,
    /// and the judge's search reads the same text, so it judges the results the question is about.
    ///
    /// <para>Four groups, matched in order from the start, each the longest phrase that fits: a
    /// lead-in ("help me find"), what is asked for ("the movie"), the word that links it to the rest
    /// ("about"), and an article. Nothing is taken unless the start has a question's shape (a
    /// lead-in of two words or more, a one-word lead-in with what is asked for, or what is asked
    /// for with its link) and a word of substance is left: "help" (a title), "help me find"
    /// (unfinished), "film noir" and "what we do in the shadows" search as typed.</para>
    ///
    /// <para>Then the end: what is asked for, typed after the subject, comes off too, so "show me green
    /// ogre movie" searches "green ogre" (Shrek first; with "movie" left on it came third). Only once
    /// the start had a question's shape, and only when two words are left, one of substance: a title
    /// ending in "Movie" keeps it when one word stands before it ("show me bee movie", "show me the
    /// lego movie", "scary movie"; 194 of tmdb23k's 23k titles end so), where a description seldom
    /// has fewer than two ("green ogre", "tom hanks island").</para>
    ///
    /// <para>A dataset's own, kept with its Search or ask settings; null there means
    /// <see cref="Default"/>. The lead-ins, links and articles are the language's; the frames are
    /// the dataset's nouns, which is where a catalogue adds its own ("the movie", "the product").
    /// Observr can suggest additions from the statistics: questions that did not start with a
    /// known phrase.</para>
    /// </summary>
    public sealed class SearchOrAskPhrases
    {
        public List<string> LeadIns { get; set; } = [];
        public List<string> Frames { get; set; } = [];
        public List<string> Links { get; set; } = [];
        public List<string> Articles { get; set; } = [];

        public const int MaxPerGroup = 200;
        public const int MaxLength = 60;

        /// <summary>English, for any dataset: the frames name nothing in particular, so a dataset
        /// whose records have a name of their own ("the movie") adds it.</summary>
        public static SearchOrAskPhrases Default { get; } = new()
        {
            LeadIns =
            [
                "help me find", "can you help me find", "can you find", "could you find", "find me", "show me",
                "i'm looking for", "i am looking for", "im looking for", "looking for", "searching for", "search for",
                "can you recommend", "could you recommend", "recommend me", "recommend",
                "i want", "i need", "i would like", "i'd like",
                "do you know", "do you have", "is there", "are there",
                "what was", "what's", "what is", "which is", "which", "what", "name",
            ],
            Frames =
            [
                "the one", "that one", "the ones", "something", "anything",
                "the product", "a product", "products", "the item", "an item", "items",
                "the page", "a page", "the article", "an article", "the guide", "a guide",
            ],
            Links =
            [
                "where there is", "where there's", "in which there is", "that has", "that is", "that was", "which has",
                "about", "where", "with", "in which", "that", "when", "has", "is", "was", "like", "for", "on", "called", "named",
            ],
            Articles = ["the", "a", "an"],
        };

        /// <summary>Null when the list can be saved, otherwise what is wrong, for a 400.</summary>
        public string? Validate()
        {
            foreach (var (name, list) in Groups())
            {
                if (list == null) return $"askPhrases.{name} is required (an empty list for none).";
                if (list.Count > MaxPerGroup) return $"askPhrases.{name} holds at most {MaxPerGroup} phrases.";
                foreach (var phrase in list)
                {
                    if (string.IsNullOrWhiteSpace(phrase)) return $"askPhrases.{name} has an empty phrase.";
                    if (phrase.Length > MaxLength) return $"askPhrases.{name}: a phrase is at most {MaxLength} characters.";
                }
            }
            return null;
        }

        private IEnumerable<(string Name, List<string> List)> Groups() =>
            [("leadIns", LeadIns), ("frames", Frames), ("links", Links), ("articles", Articles)];

        /// <summary>What to search for what is typed, and the phrases taken off it. The text as
        /// typed, and nothing taken, when it has no question's shape or nothing of substance would
        /// be left.</summary>
        public static (string Text, IReadOnlyList<string> Taken) Strip(string typed, SearchOrAskPhrases phrases)
        {
            var original = typed.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var words = Words(typed);
            int at = 0;
            var taken = new List<string>();
            bool Step(List<string> list)
            {
                int n = Take(words, at, list);
                if (n == 0) return false;
                taken.Add(string.Join(' ', original.Skip(at).Take(n)));
                at += n;
                return true;
            }
            int leadWords = Take(words, 0, phrases.LeadIns);
            bool lead = Step(phrases.LeadIns);
            bool frame = Step(phrases.Frames);
            bool link = Step(phrases.Links);
            bool shaped = (lead && (leadWords > 1 || frame)) || (frame && link);
            if (!shaped) return (typed, []);
            Step(phrases.Articles);
            var rest = TrailingPunctuation.Replace(string.Join(' ', original.Skip(at)), "").Trim();
            if (!Substance.IsMatch(rest)) return (typed, []);

            var restOriginal = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int end = TakeEnd(Words(rest), phrases.Frames);
            if (end > 0 && restOriginal.Length - end >= MinWordsBeforeEnd)
            {
                var front = string.Join(' ', restOriginal.Take(restOriginal.Length - end));
                if (Substance.IsMatch(front))
                {
                    taken.Add(string.Join(' ', restOriginal.Skip(restOriginal.Length - end)));
                    rest = front;
                }
            }
            return (rest, taken);
        }

        /// <summary>The words that must stand before what is asked for at the end for it to come off.</summary>
        public const int MinWordsBeforeEnd = 2;

        private static readonly Regex TrailingPunctuation = new(@"[?!.]+$", RegexOptions.Compiled);
        private static readonly Regex Substance = new(@"\p{L}{3,}", RegexOptions.Compiled);
        private static readonly Regex EndPunctuation = new(@"[?!.,]+$", RegexOptions.Compiled);

        private static string[] Words(string s) =>
            s.ToLowerInvariant().Replace('’', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>The longest phrase of the list that the words end with, as a word count.</summary>
        private static int TakeEnd(string[] words, List<string> list)
        {
            int best = 0;
            foreach (var phrase in list)
            {
                var p = Words(phrase);
                if (p.Length <= best || p.Length > words.Length) continue;
                bool all = true;
                for (int i = 0; i < p.Length && all; i++)
                    all = EndPunctuation.Replace(words[words.Length - p.Length + i], "") == p[i];
                if (all) best = p.Length;
            }
            return best;
        }

        /// <summary>The longest phrase of the list that the words start with at <paramref name="at"/>, as a word count.</summary>
        private static int Take(string[] words, int at, List<string> list)
        {
            int best = 0;
            foreach (var phrase in list)
            {
                var p = Words(phrase);
                if (p.Length <= best || at + p.Length > words.Length) continue;
                bool all = true;
                for (int i = 0; i < p.Length && all; i++)
                    all = EndPunctuation.Replace(words[at + i], "") == p[i];
                if (all) best = p.Length;
            }
            return best;
        }
    }
}
