namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// Whether a question asked again is the same question in other words: the visitor's quiet way
    /// of saying an answer missed (26 Oct 2026). Read from what is already stored, never asked of a
    /// model. Two questions are the same when the text is the same once lowercased, when they share
    /// most of their meaningful words (question words dropped, a word matched on its first five
    /// letters so "return" meets "returns" and "returning"), or when their answers drew on mostly the
    /// same records, which catches a rewording that shares no word at all.
    /// </summary>
    public static class SearchOrAskRephrasing
    {
        /// <summary>A rewording comes soon or not at all: later it is a new visit to the question.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

        public static bool Same(string first, string again, string firstSources, string againSources)
        {
            if (Normalise(first) == Normalise(again)) return true;
            var a = Stems(first);
            var b = Stems(again);
            if (a.Count > 0 && b.Count > 0)
            {
                var shared = a.Intersect(b).Count();
                if (shared >= 1 && shared * 2 >= Math.Min(a.Count, b.Count)) return true;
            }
            var sa = Keys(firstSources);
            var sb = Keys(againSources);
            if (sa.Count >= 2 && sb.Count >= 2 && sa.Intersect(sb).Count() * 2 >= Math.Min(sa.Count, sb.Count)) return true;
            return false;
        }

        public static string Normalise(string text) =>
            string.Join(' ', text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>The words that carry a question's subject, each cut to its first five letters.</summary>
        public static HashSet<string> Stems(string text)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            var letters = new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
            foreach (var raw in letters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.Length < 3 || SearchOrAskService.QuestionWords.Contains(raw)) continue;
                words.Add(raw.Length > 5 ? raw[..5] : raw);
            }
            return words;
        }

        private static HashSet<string> Keys(string sourceKeys) =>
            sourceKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    }
}
