namespace IndxServer.Services.Observr
{
    /// <summary>
    /// The questions a new conversation offers, drawn from where the owner is:
    /// the tab they are on decides the kind of question, and on the
    /// pages about searching, this week's most searched query that found nothing becomes one.
    /// Three at most; read from the statistics without loading the dataset.
    /// </summary>
    internal static class ObservrSuggestions
    {
        public static List<string> For(ObservrChatContext ctx, StatisticsService statistics)
        {
            if (!ctx.HasDataset)
                return ["Which dataset needs attention?", "What did you find this week?", "Which datasets are hibernated?"];
            var ds = ctx.DataSetName!;
            var missed = ZeroHit(ctx, statistics);
            var why = missed != null ? $"Why does \"{missed}\" find nothing?" : null;
            List<string> list = ctx.Tab switch
            {
                "statistics" => [why ?? "Which searches found nothing this week?", "Which searches end without a click?", "What did visitors search for most?"],
                "fields" => ["Is my field setup sensible?", "Which fields should be facets?", "Which fields should weigh more?"],
                "categories" => ["Are my categories set up well?", "Which category field should I use?"],
                "filters" => ["Which filters do people use?", "Which filters should come first?"],
                "webhooks" => ["Is the webhook keeping the dataset up to date?", "Why was a batch held?"],
                "query-parameters" => ["How many typos should a search allow here?", "Should a search with no match show the closest records?"],
                "boosts" => ["Which boost rules still do something?", "What would be worth boosting?"],
                "synonyms" => [why != null ? $"Would a synonym help \"{missed}\"?" : "Which synonyms would help?", "Are any of my synonyms doing harm?"],
                "search" => [why ?? "Why does a search rank as it does?", $"How do visitors search {ds}?"],
                "search-or-ask" => ["How should Search or ask be set up here?", "Which questions went unanswered?"],
                "options" => ["Is anything here worth changing?"],
                _ => [$"What happened on {ds} this week?", why ?? "Which searches found nothing?", "Is my field setup sensible?"],
            };
            return list.Where(q => q.Length > 0).Distinct().Take(3).ToList();
        }

        /// <summary>This week's most searched query that found nothing, cut short; null when there is none.</summary>
        private static string? ZeroHit(ObservrChatContext ctx, StatisticsService statistics)
        {
            if (statistics.Store is not { } store || !statistics.Enabled || !ctx.HasDataset) return null;
            try
            {
                var today = store.Today(ctx.TeamId, ctx.DataSetName!);
                var q = store.TopQueries(ctx.TeamId, ctx.DataSetName!, today - 6, today, 1, zeroHitsOnly: true).Select(x => x.QueryText).FirstOrDefault()?.Trim();
                return string.IsNullOrEmpty(q) ? null : q.Length > 40 ? q[..40] + "…" : q;
            }
            catch (Exception) { return null; }
        }
    }
}
