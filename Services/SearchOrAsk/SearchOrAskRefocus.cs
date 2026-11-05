using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Indx.Api;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>One way of searching a question: its text and the mode it is searched in.</summary>
    public sealed record RefocusCandidate(string Name, string Text, SearchMode Mode);

    /// <summary>What the judge chose for a question: the search the list would show
    /// (<paramref name="Searched"/> in <paramref name="Mode"/>), its score, the kept BM25F search's
    /// score, whether it switched, and the keys the chosen search lists.</summary>
    public sealed record RefocusPick(string Searched, SearchMode Mode, double? P, double? KeptP, bool Switched, IReadOnlyList<long> Keys);

    /// <summary>
    /// Refocusing a question with the judge, an experiment in the console only (5 Nov 2026,
    /// Notes/search-or-ask.md "Refocusing a question"). Nothing a visitor gets uses it. The list
    /// searches the stripped text in BM25F, which ranks by the words that match instead of requiring
    /// all of them. On the pause a few more searches are made, the stripped text in EditDistance and
    /// with one or two of its informative words left out ("rat paris" for "rat living in paris"), and
    /// the judge is asked one yes/no per search. The list switches only when the judge reads the
    /// BM25F list as not holding what is asked and another as holding it: letting the highest score
    /// win took first places BM25F already had (the school register, 37 to 28). Replayed on 121
    /// questions this took the record to first place for 77 against today's 51, into the top five
    /// for 99 against 79.
    /// </summary>
    public static class SearchOrAskRefocus
    {
        /// <summary>From this score the judge is taken to have found what is asked in a list.</summary>
        public const double Sure = 0.5;
        public const int MaxCandidates = 10;
        /// <summary>The kept search: the stripped text in BM25F, what the list shows while typing.</summary>
        public const string Kept = "bm25f";
        /// <summary>The judge's questions for the searches are named with this, beside its own.</summary>
        public const string Prefix = "refocus_";

        /// <summary>The searches to choose between. A word is informative when it has three letters
        /// or more and is held by at least one record and at most a sixth of them (or 50, on a
        /// small dataset); <paramref name="count"/> is how many records hold every word of a text.</summary>
        public static List<RefocusCandidate> Candidates(string stripped, Func<string, int> count, int documents)
        {
            var limit = Math.Max(50, documents / 6);
            var words = Regex.Matches(stripped.ToLowerInvariant(), @"[\w']+").Select(m => m.Value).Distinct()
                .Where(w => w.Length >= 3 && count(w) is > 0 and var c && c <= limit).ToList();
            var list = new List<RefocusCandidate> { new(Kept, stripped, SearchMode.Bm25f), new("edit", stripped, SearchMode.EditDistance) };
            foreach (var size in new[] { words.Count - 1, words.Count - 2 })
            {
                if (size < 1) continue;
                foreach (var subset in Subsets(words, size))
                {
                    if (list.Count >= MaxCandidates) return list;
                    var text = string.Join(' ', subset);
                    if (count(text) > 0) list.Add(new($"w{list.Count}", text, SearchMode.EditDistance));
                }
            }
            return list;
        }

        /// <summary>Every choice of <paramref name="size"/> of the words, in their order.</summary>
        private static IEnumerable<IReadOnlyList<string>> Subsets(IReadOnlyList<string> words, int size)
        {
            if (size == 0) { yield return []; yield break; }
            for (int i = 0; i <= words.Count - size; i++)
                foreach (var rest in Subsets(words.Skip(i + 1).ToList(), size - 1))
                    yield return [words[i], .. rest];
        }

        /// <summary>The state's <c>candidates</c>: each search's text and its results as the judge
        /// reads results.</summary>
        public static JsonObject State(IReadOnlyList<RefocusCandidate> candidates, Func<RefocusCandidate, IReadOnlyList<(long Key, string Title, string Section, string Snippet)>> results) =>
            new(candidates.Select(c => new KeyValuePair<string, JsonNode?>(c.Name, new JsonObject
            {
                ["searched"] = c.Text,
                ["results"] = new JsonArray(results(c).Select(h => (JsonNode)new JsonObject
                {
                    ["title"] = h.Title,
                    ["section"] = h.Section,
                    ["snippet"] = h.Snippet,
                }).ToArray()),
            })));

        /// <summary>One question per search, as measured.</summary>
        public static Dictionary<string, JsonObject> Questions(IReadOnlyList<RefocusCandidate> candidates) =>
            candidates.ToDictionary(c => Prefix + c.Name, c => new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = $"`request` is what a visitor typed into the search field of `site`. `candidates.{c.Name}.results` " +
                    $"are what searching `candidates.{c.Name}.searched` found. Is what the visitor is asking about among these results?",
                ["criteria"] = new JsonObject
                {
                    ["true"] = "One of these results is what the visitor is asking about, or answers what they ask.",
                    ["false"] = "None of these results is it.",
                },
            });

        /// <summary>The search to show: the kept one, unless the judge reads it as not holding what is
        /// asked (below <see cref="Sure"/>) and another as holding it, the highest of those.</summary>
        public static RefocusCandidate Choose(IReadOnlyList<RefocusCandidate> candidates, Func<string, double?> score)
        {
            var kept = candidates.First(c => c.Name == Kept);
            if (score(Prefix + Kept) >= Sure) return kept;
            var best = candidates.Where(c => c.Name != Kept).OrderByDescending(c => score(Prefix + c.Name) ?? 0).FirstOrDefault();
            return best != null && score(Prefix + best.Name) >= Sure ? best : kept;
        }
    }
}
