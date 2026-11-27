using Indx.Api;
using IndxServer.Engine;

namespace IndxServer.Services.Observr
{
    /// <summary>One query over the period: how often, how often it found nothing, how often a result
    /// was chosen, where (1 is the top), and what was chosen most, by name. For the queries the
    /// summary names as a problem (found nothing, rarely chosen, closest matches only), also the
    /// filter it was searched with most (<see cref="Filter"/>) and what it finds now
    /// (<see cref="Now"/>), so a finding is checked before it is reported (20 Nov 2026). On a dataset
    /// with Search or ask, <see cref="Ask"/> says how its searches were offered to be asked and how
    /// often they were (27 Nov 2026), so a question the field offered to answer is not reported as a
    /// question the search failed.</summary>
    public sealed record ObservrSummaryQuery(string Text, long Searches, long ZeroHits, long Clicked, double? AvgPosition, string? MostChosen,
        ObservrSummaryFilter? Filter = null, ObservrSummaryCheck? Now = null, ObservrSummaryQueryAsk? Ask = null);
    /// <summary>Search or ask beside one query's searches: how many had an offer to ask, how many of
    /// those the full offer (the judge sure it was a question or a description), and how many asks
    /// followed. Null when none of its searches had an offer.</summary>
    public sealed record ObservrSummaryQueryAsk(long Offered, long FullOffer, long Asked);
    /// <summary>The filter a query was searched with most, in plain words ("type = Bok"), in how many
    /// of its searches, of how many. A page searching one section of the dataset (a multi search's
    /// books beside its authors) sends it; a record outside it is then not found by that search, and
    /// its misses are the page's filter, not the dataset's setup.</summary>
    public sealed record ObservrSummaryFilter(string Words, long WithIt, long Searches);
    /// <summary>The query searched again when the summary is made: as the visitors searched it (with
    /// <see cref="ObservrSummaryFilter"/>'s filter), and, when that had a filter, with none.</summary>
    public sealed record ObservrSummaryCheck(ObservrSummarySearch AsSearched, ObservrSummarySearch? WithoutFilter);
    /// <summary>One search now: how many records hold every word (or the closest, when none does),
    /// whether any holds every word (<see cref="Hit"/>), and the first by name.</summary>
    public sealed record ObservrSummarySearch(int Records, bool Hit, List<string> First);
    public sealed record ObservrSummaryDocument(string Name, long Selects);
    public sealed record ObservrSummaryChange(DateTime AtUtc, string Kind, string? Summary);
    /// <summary>The period's totals. <see cref="AgentSearches"/>: how many of the searches AI agents
    /// made, as their callers marked them (25 Nov 2026); 0 where nothing is marked, which is most.</summary>
    public sealed record ObservrSummaryTotals(long Searches, long ZeroHits, long ClickedSearches, long Uncovered, long AgentSearches = 0);
    /// <summary>Search or ask over the period. <see cref="Enabled"/>, <see cref="Kind"/> (catalogue or
    /// documentation) and <see cref="Mode"/> are the dataset's setup when the summary is made
    /// (27 Nov 2026): a digest that does not know the site offers asking proposes it.</summary>
    public sealed record ObservrSummaryAsk(long Judged, long Offers, long Asks, long Answered, long NothingFound, long Unanswered, List<string> UnansweredQuestions,
        long AskedAgain = 0, List<ObservrSummaryRephrasing>? AskedAgainAs = null, long Failed = 0, ObservrSummaryProvider? Provider = null,
        bool Enabled = false, string? Kind = null, string? Mode = null);
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
    public sealed record ObservrSummaryField(string Name, string Type, bool WordMatching, bool PatternMatching, bool Filterable, bool Facetable,
        bool Sortable, float Weight, string Tolerance);

    /// <summary>
    /// The bounded summary of a dataset that the digest is made from: aggregates over the last seven days against the seven before, the
    /// same size at 2,000 or 2 million searches a day. No raw events, no subject, no record: queries
    /// and the names of chosen records are the only text from the site. <see cref="Version"/> is
    /// the format's; IndxObservr reads by it. Its source is public, so anyone can read exactly what is sent.
    /// </summary>
    public sealed record ObservrSummary(int Version, string Kind, string Team, string Dataset, string Site, string TimeZone,
        DateOnly PeriodStart, DateOnly PeriodEnd, long Records, bool Loaded,
        ObservrSummaryTotals Totals, ObservrSummaryTotals Previous,
        List<ObservrSummaryQuery> TopQueries, List<ObservrSummaryQuery> ZeroHitQueries, List<ObservrSummaryQuery> LowClickThrough,
        List<ObservrSummaryQuery> FuzzyFinds, List<ObservrSummaryDocument> TopDocuments, List<ObservrSummaryChange> Changes,
        ObservrSummaryAsk? SearchOrAsk, List<ObservrSummaryField> Fields, List<string> RecentFindings,
        List<string>? OwnerNotes = null, ObservrRecordsReport? RecordsReport = null)
    {
        // Still 1 with the queries' filter and check (Nov 2026): they only add, and an Observr that
        // does not know them reads the rest as before; a new version number would be refused by one.
        public const int CurrentVersion = 1;
        public const int Days = 7;
    }

    /// <summary>Builds the summary from the statistics store, the dataset's metadata and, when the
    /// dataset is loaded, its engine. Never loads a dataset: a summary of a sleeping one has no field
    /// list, which is also a fact about it.</summary>
    internal sealed class ObservrSummaryBuilder(StatisticsService statistics, DatasetMetadataStore metadata, IDatasetEngines engines,
        ObservrFindingStore findings, SearchOrAsk.SearchOrAskService searchOrAsk,
        DataCheckStore dataChecks)
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
        /// <summary>The texts read for offers beside the named queries: the most offered of the week.</summary>
        private const int Offered = 500;
        /// <summary>The names a check returns of what a query finds now.</summary>
        private const int CheckNames = 3;

        /// <summary>A filter key in plain words: each operand as field and value, ranges by their
        /// limits, joined with commas. Read, not validated (FilterKeyReader).</summary>
        internal static string FilterWords(string key)
        {
            static string Number(double? d) => d?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            var words = FilterKeyReader.Operands(key).Select(o => (o.Negated ? "not " : "") + o.Kind switch
            {
                FilterOperandKind.Range => o.Min == o.Max && o.Min != null ? $"{o.Field} = {Number(o.Min)}"
                    : o.Min != null && o.Max != null ? $"{o.Field} from {Number(o.Min)} to {Number(o.Max)}"
                    : o.Min != null ? $"{o.Field} from {Number(o.Min)}" : $"{o.Field} up to {Number(o.Max)}",
                FilterOperandKind.Position or FilterOperandKind.Custom => $"a filter on {o.Field}",
                _ => $"{o.Field} = {o.Value}",
            }).Distinct().ToList();
            return words.Count == 0 ? "a filter" : string.Join(", ", words);
        }

        /// <summary>The query as a visitor's search now, never recorded: the first names, the count,
        /// and whether a record holds every word. Null when the search fails (a filter the dataset no
        /// longer takes): a check that cannot be made is left out, not reported as a miss.</summary>
        private ObservrSummarySearch? SearchNow(string teamId, string dataSetName, string text, string? filterKey, Func<long, string?> label)
        {
            try
            {
                var query = new Indx.Http.QueryProxy { Text = text, MaxNumberOfRecordsToReturn = CheckNames, Match = new Indx.Http.MatchProxy { CursorAt = 0 } };
                if (filterKey != null) query.Filter = new Indx.Http.FilterProxy(filterKey);
                var result = engines.Search(query, dataSetName, teamId);
                var records = result.Records ?? [];
                return new ObservrSummarySearch(Math.Max(result.Count, records.Length), result.CountedTolerance != null,
                    records.Take(CheckNames).Select(r => label(r.DocumentKey) ?? r.DocumentKey.ToString()).ToList());
            }
            catch (Exception) { return null; }
        }

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
            ObservrSummaryTotals Totals(OverviewStat o, long agents) => new(o.Searches, o.ZeroHits, o.ClickedSearches, o.Uncovered, agents);

            var ask = store.AskOverview(teamId, dataSetName, from, to);
            var (again, rephrased) = store.AskedAgain(teamId, dataSetName, from, to, Rephrased);
            var askSettings = searchOrAsk.Settings(teamId, dataSetName);
            var askOn = askSettings.Enabled && askSettings.Shown;
            var askSummary = !askOn && ask.Judged == 0 && ask.Asks == 0 ? null : new ObservrSummaryAsk(ask.Judged, ask.Offers, ask.Asks, ask.Answered, ask.NothingFound,
                ask.Unanswered, store.TopQuestions(teamId, dataSetName, from, to, Unanswered).Where(q => q.Unanswered > 0 || q.NothingFound > 0).Select(q => q.Question).ToList(),
                again.AskedAgain, rephrased.Select(r => new ObservrSummaryRephrasing(r.First, r.Again, r.Times, r.AgainOpened)).ToList(),
                ask.Failed, Provider(), askOn, askSettings.Kind, askSettings.Mode);
            // The offers beside each query's searches, by its text as the statistics fold it: a search
            // that was a question still counts as a search, and this is what says it was offered to be
            // asked (27 Nov 2026). "comedies from the 90s" was reported as a search that could not
            // filter, on a catalogue whose Search or ask turns a description into the filters.
            var offers = askSummary == null ? [] : store.TopOffers(teamId, dataSetName, from, to, Offered)
                .ToDictionary(o => o.Text, o => new ObservrSummaryQueryAsk(o.Offers, o.Strong, o.Asked));
            ObservrSummaryQuery Asked(ObservrSummaryQuery q) => offers.TryGetValue(q.Text.Trim().ToLowerInvariant(), out var a) ? q with { Ask = a } : q;

            var zeroHit = store.TopQueries(teamId, dataSetName, from, to, ZeroHits, zeroHitsOnly: true).Select(Query).ToList();
            var lowClick = store.TopQueries(teamId, dataSetName, from, to, LowClickThrough, order: QueryOrder.LowestClickThrough).Select(Query).ToList();
            var fuzzy = store.TopQueries(teamId, dataSetName, from, to, Fuzzy, uncoveredOnly: true).Select(Query).ToList();
            // The queries named as a problem, checked (20 Nov 2026): the filter their searches had,
            // and what they find now with it and without. A finding about a query is then made from
            // what the dataset does, not from aggregates alone: a multi search's books section
            // missing an author made Observr report her as never found, while a search of the
            // whole dataset put her first.
            var named = zeroHit.Concat(lowClick).Concat(fuzzy).Select(q => q.Text).Distinct().ToList();
            var filters = store.CommonFilters(teamId, dataSetName, from, to, named);
            var checks = new Dictionary<string, ObservrSummaryCheck>();
            if (loaded)
                foreach (var text in named)
                {
                    var key = filters.TryGetValue(text, out var f) ? f.FilterKey : null;
                    if (SearchNow(teamId, dataSetName, text, key, label) is not { } asSearched) continue;
                    checks[text] = new ObservrSummaryCheck(asSearched, key == null ? null : SearchNow(teamId, dataSetName, text, null, label));
                }
            List<ObservrSummaryQuery> Checked(List<ObservrSummaryQuery> queries) => queries.Select(q => Asked(q with
            {
                Filter = filters.TryGetValue(q.Text, out var f) && f.FilterKey != null ? new ObservrSummaryFilter(FilterWords(f.FilterKey), f.WithIt, f.Searches) : null,
                Now = checks.GetValueOrDefault(q.Text),
            })).ToList();

            var site = metadata.Load(teamId, dataSetName);
            return new ObservrSummary(ObservrSummary.CurrentVersion, ObservrClient.DigestKind, teamId, dataSetName,
                string.IsNullOrWhiteSpace(site) ? "" : site.Trim(), StatisticsDays.NameOf(tz), Date(from), Date(to),
                loaded ? engine!.Status.DocumentCount : 0, loaded,
                Totals(store.Overview(teamId, dataSetName, from, to), store.AgentSearches(teamId, dataSetName, from, to)),
                Totals(store.Overview(teamId, dataSetName, previousFrom, previousTo), store.AgentSearches(teamId, dataSetName, previousFrom, previousTo)),
                store.TopQueries(teamId, dataSetName, from, to, TopQueries).Select(Query).Select(Asked).ToList(),
                Checked(zeroHit), Checked(lowClick), Checked(fuzzy),
                store.TopDocuments(teamId, dataSetName, from, to, Documents).Select(d => new ObservrSummaryDocument(d.Label ?? label(d.DocumentKey) ?? d.DocumentKey.ToString(), d.Selects)).ToList(),
                store.Changes(teamId, dataSetName, from, to).Take(ChangesMax)
                    .Select(c => new ObservrSummaryChange(DateTimeOffset.FromUnixTimeMilliseconds(c.Timestamp).UtcDateTime, c.Kind, c.Summary)).ToList(),
                askSummary,
                loaded ? engine!.GetFieldList().Select(f => new ObservrSummaryField(f.Name, ObservrClient.TypeName(f.Type), f.WordMatching, f.PatternMatching != null, f.Filterable, f.Facetable, f.Sortable, f.Weight, f.Tolerance.ToString())).ToList() : [],
                // What Observr has said about this dataset lately, so a digest does not say it again.
                findings.ListForDataset(teamId, dataSetName, Findings).Select(f => $"{f.Kind}: {(f.Query.Length > 0 ? f.Query + ": " : "")}{f.Answer} ({f.Status})").ToList(),
                // What the owner told Observr and declined: a digest
                // does not propose again what they turned down.
                findings.NotesFor(teamId, dataSetName).Select(n => n.Text).ToList(),
                // The records report the owner ran last, if any: what the records lack (Nov 2026).
                ObservrRecordsReport.From(dataChecks.Load(teamId, dataSetName), loaded ? engine!.GetFieldConfiguration() : null));
        }
    }
}
