using Indx.Api;
using IndxServer.Engine;

namespace IndxServer.Services.Observr
{
    /// <summary>One query over the period: how often, how often it found nothing, how often a result
    /// was chosen, where (1 is the top), and what was chosen most, by name.</summary>
    public sealed record ObservrSummaryQuery(string Text, long Searches, long ZeroHits, long Clicked, double? AvgPosition, string? MostChosen);
    public sealed record ObservrSummaryDocument(string Name, long Selects);
    public sealed record ObservrSummaryChange(DateTime AtUtc, string Kind, string? Summary);
    public sealed record ObservrSummaryTotals(long Searches, long ZeroHits, long ClickedSearches, long Uncovered);
    public sealed record ObservrSummaryAsk(long Judged, long Offers, long Asks, long Answered, long NothingFound, long Unanswered, List<string> UnansweredQuestions,
        List<string>? LeadIns = null, List<ObservrSummaryLeadIn>? LeadInCandidates = null, List<ObservrSummaryRefocusMiss>? RefocusedNothing = null,
        long AskedAgain = 0, List<ObservrSummaryRephrasing>? AskedAgainAs = null, long Failed = 0, ObservrSummaryProvider? Provider = null);
    /// <summary>
    /// How the AI provider behind Search or ask is doing on the instance (Nov 2026), so the digest can
    /// say when answers are being refused and why, and warn before a key's limit runs out. The
    /// refusal is the latest that has not been followed by a call of its kind that worked: which call
    /// (judge, answer, filters), the status, its kind (credit, keyLimit, inFlight, key, refused,
    /// failed), the provider's own words, when, and the server's advice. Offers are paused while
    /// answers are refused for good. The key's limit and what is left are OpenRouter's, the only
    /// provider that reports them; null elsewhere.
    /// </summary>
    public sealed record ObservrSummaryProvider(string Name, string? RefusedCall = null, int? Status = null, string? Kind = null,
        string? Reason = null, DateTime? RefusedAtUtc = null, string? Advice = null, DateTime? OffersPausedUntilUtc = null,
        double? KeyLimit = null, double? KeyRemaining = null, string? KeyReset = null);
    /// <summary>A question asked again in other words soon after, in the same visit: the first
    /// wording, the second, how often, and how often the second answer had a source opened.</summary>
    public sealed record ObservrSummaryRephrasing(string First, string Again, long Times, long AgainOpened);
    /// <summary>How questions began that no Ask phrase caught: the first words, how many, one of them.</summary>
    public sealed record ObservrSummaryLeadIn(string Words, long Searches, string Example);
    /// <summary>An Ask phrase taken off a question, and what was left, which found nothing.</summary>
    public sealed record ObservrSummaryRefocusMiss(string Phrase, string Searched, long Times);
    public sealed record ObservrSummaryField(string Name, string Type, bool Searchable, bool Filterable, bool Facetable, bool Sortable, float Weight);

    /// <summary>
    /// The bounded summary of a dataset that the digest is made from (Notes/Anders/observr-plan.md,
    /// "The Observr summary"): aggregates over the last seven days against the seven before, the
    /// same size at 2,000 or 2 million searches a day. No raw events, no subject, no record: queries
    /// and the names of chosen records are the only text from the site. <see cref="Version"/> is
    /// the format's; IndxObservr reads by it. Open source, so anyone can read exactly what is sent.
    /// </summary>
    public sealed record ObservrSummary(int Version, string Kind, string Team, string Dataset, string Site, string TimeZone,
        DateOnly PeriodStart, DateOnly PeriodEnd, long Records, bool Loaded,
        ObservrSummaryTotals Totals, ObservrSummaryTotals Previous,
        List<ObservrSummaryQuery> TopQueries, List<ObservrSummaryQuery> ZeroHitQueries, List<ObservrSummaryQuery> LowClickThrough,
        List<ObservrSummaryQuery> FuzzyFinds, List<ObservrSummaryDocument> TopDocuments, List<ObservrSummaryChange> Changes,
        ObservrSummaryAsk? SearchOrAsk, List<ObservrSummaryField> Fields, List<string> RecentFindings,
        List<string>? OwnerNotes = null)
    {
        public const int CurrentVersion = 1;
        public const int Days = 7;
    }

    /// <summary>Builds the summary from the statistics store, the dataset's metadata and, when the
    /// dataset is loaded, its engine. Never loads a dataset: a summary of a sleeping one has no field
    /// list, which is also a fact about it.</summary>
    internal sealed class ObservrSummaryBuilder(StatisticsService statistics, DatasetMetadataStore metadata, IDatasetEngines engines,
        ObservrFindingStore findings, SearchOrAsk.SearchOrAskStore searchOrAskStore, SearchOrAsk.SearchOrAskService searchOrAsk)
    {
        private const int TopQueries = 20;
        private const int ZeroHits = 15;
        private const int LowClickThrough = 10;
        private const int Fuzzy = 10;
        private const int Documents = 10;
        private const int ChangesMax = 20;
        private const int Unanswered = 10;
        private const int Rephrased = 10;
        private const int Findings = 15;

        /// <summary>The provider's state for the summary: the refusal standing now, the offer pause, and
        /// the key's limit as last read (never read here: a summary makes no call to the provider).</summary>
        private ObservrSummaryProvider Provider()
        {
            var setup = searchOrAsk.Setup;
            var refusal = searchOrAsk.CallHealth().Select(h => h.Refusal).Where(r => r != null).OrderByDescending(r => r!.At).FirstOrDefault();
            var limit = searchOrAsk.CachedKeyLimit;
            return new ObservrSummaryProvider(SearchOrAsk.SearchOrAskProviders.Label(setup.Provider), refusal?.Call, refusal?.Status,
                refusal == null ? null : SearchOrAsk.SearchOrAskService.RefusalKind(refusal.Status, refusal.LimitSource),
                refusal?.Reason, refusal?.At, refusal?.Advice, searchOrAsk.OffersPaused ? searchOrAsk.OffersPausedUntil : null,
                limit?.Limit, limit?.Remaining, limit?.Reset);
        }

        /// <summary>Null when the instance keeps no statistics: there is nothing to summarise.</summary>
        public ObservrSummary? Build(string teamId, string dataSetName)
        {
            var store = statistics.Store;
            if (store == null || !statistics.Enabled) return null;
            // Up to and including today: the daily job runs early, so its week ends on yesterday's full
            // day plus a little of this one; the Options tab's button then also shows what happened today.
            var today = store.Today(teamId, dataSetName);
            long to = today, from = to - (ObservrSummary.Days - 1);
            long previousTo = from - 1, previousFrom = previousTo - (ObservrSummary.Days - 1);
            var tz = store.ZoneOf(teamId, dataSetName);
            DateOnly Date(long day) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(StatisticsDays.Start(tz, day)), tz).DateTime);

            var engine = engines.FindSearchEngine(dataSetName, teamId);
            var loaded = engine != null && engine.Status.SystemState == SystemState.Ready;
            var label = loaded ? statistics.LabelsFor(engine!, teamId, dataSetName) : (Func<long, string?>)(_ => null);

            ObservrSummaryQuery Query(QueryStat q) => new(q.QueryText, q.Searches, q.ZeroHits, q.ClickedSearches,
                q.Selects > 0 ? Math.Round((double)q.PositionSum / q.Selects, 1) : null, q.MostChosenLabel ?? (q.MostChosenDocument is { } k ? label(k) : null));
            ObservrSummaryTotals Totals(OverviewStat o) => new(o.Searches, o.ZeroHits, o.ClickedSearches, o.Uncovered);

            var ask = store.AskOverview(teamId, dataSetName, from, to);
            var askSettings = searchOrAskStore.Load(teamId, dataSetName);
            var (again, rephrased) = store.AskedAgain(teamId, dataSetName, from, to, Rephrased);
            var searchOrAsk = ask.Judged == 0 && ask.Asks == 0 ? null : new ObservrSummaryAsk(ask.Judged, ask.Offers, ask.Asks, ask.Answered, ask.NothingFound,
                ask.Unanswered, store.TopQuestions(teamId, dataSetName, from, to, Unanswered).Where(q => q.Unanswered > 0 || q.NothingFound > 0).Select(q => q.Question).ToList(),
                [.. (askSettings?.AskPhrases ?? SearchOrAsk.SearchOrAskPhrases.Default).LeadIns],
                store.LeadInCandidates(teamId, dataSetName, from, to).Select(c => new ObservrSummaryLeadIn(c.Words, c.Searches, c.Example)).ToList(),
                store.RefocusMisses(teamId, dataSetName, from, to).Select(m => new ObservrSummaryRefocusMiss(m.Phrase, m.Searched, m.Times)).ToList(),
                again.AskedAgain, rephrased.Select(r => new ObservrSummaryRephrasing(r.First, r.Again, r.Times, r.AgainOpened)).ToList(),
                ask.Failed, Provider());

            var site = metadata.Load(teamId, dataSetName);
            return new ObservrSummary(ObservrSummary.CurrentVersion, ObservrClient.DigestKind, teamId, dataSetName,
                string.IsNullOrWhiteSpace(site) ? "" : site.Trim(), StatisticsDays.NameOf(tz), Date(from), Date(to),
                loaded ? engine!.Status.DocumentCount : 0, loaded,
                Totals(store.Overview(teamId, dataSetName, from, to)), Totals(store.Overview(teamId, dataSetName, previousFrom, previousTo)),
                store.TopQueries(teamId, dataSetName, from, to, TopQueries).Select(Query).ToList(),
                store.TopQueries(teamId, dataSetName, from, to, ZeroHits, zeroHitsOnly: true).Select(Query).ToList(),
                store.TopQueries(teamId, dataSetName, from, to, LowClickThrough, order: QueryOrder.LowestClickThrough).Select(Query).ToList(),
                store.TopQueries(teamId, dataSetName, from, to, Fuzzy, uncoveredOnly: true).Select(Query).ToList(),
                store.TopDocuments(teamId, dataSetName, from, to, Documents).Select(d => new ObservrSummaryDocument(d.Label ?? label(d.DocumentKey) ?? d.DocumentKey.ToString(), d.Selects)).ToList(),
                store.Changes(teamId, dataSetName, from, to).Take(ChangesMax)
                    .Select(c => new ObservrSummaryChange(DateTimeOffset.FromUnixTimeMilliseconds(c.Timestamp).UtcDateTime, c.Kind, c.Summary)).ToList(),
                searchOrAsk,
                loaded ? engine!.GetFieldList().Select(f => new ObservrSummaryField(f.Name, ObservrClient.TypeName(f.Type), f.Searchable, f.Filterable, f.Facetable, f.Sortable, f.Weight)).ToList() : [],
                // What Observr has said about this dataset lately, so a digest does not say it again.
                findings.ListForDataset(teamId, dataSetName, Findings).Select(f => $"{f.Kind}: {(f.Query.Length > 0 ? f.Query + ": " : "")}{f.Answer} ({f.Status})").ToList(),
                // What the owner told Observr and declined (Notes/observr-agent-plan.md): a digest
                // does not propose again what they turned down.
                findings.NotesFor(teamId, dataSetName).Select(n => n.Text).ToList());
        }
    }
}
