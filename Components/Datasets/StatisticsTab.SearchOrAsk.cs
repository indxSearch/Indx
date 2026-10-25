using Indx.Systm.Blazor;
using IndxServer.Services;
using IndxServer.Services.SearchOrAsk;
using Microsoft.AspNetCore.Components;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// The Statistics tab's Search or ask section (Notes/search-or-ask-server.md): what was offered
    /// and taken, the questions asked with the ones the content did not answer first, and the texts
    /// offers were shown beside. Its own file so the tab's shared code changes by a few calls; the
    /// markup is in the tab, whose scoped styles it uses. The section shows only once there are
    /// judgements or asks in the window.
    /// </summary>
    public partial class StatisticsTab
    {
        private AskOverviewStat _ask;
        private AskBudgetStat _askBudget;
        private List<AskQuestionStat> _askQuestions = [];
        private List<AskOfferStat> _askOffers = [];
        private string _askView = "questions";
        // The section's pictures: the window day by day, the budget day by day and today's, and
        // when people ask.
        private List<AskDayStat> _askDays = [];
        private List<AskBudgetDayStat> _askBudgetDays = [];
        private long _askCallsToday;
        private int _askBudgetToday;
        private WeekHourStat? _askWeekHour;
        private long _askFromDay, _askToDay;

        [Inject] private SearchOrAskService SearchOrAsk { get; set; } = default!;

        /// <summary>Only while the instance shows the feature, and once there is something to show.</summary>
        /// <summary>Whether the answers cite sources to open: documentation's do. A catalogue's answer
        /// puts its list on the page instead, so a count of sources opened would always read 0.</summary>
        private bool CitesSources => _citesSources;
        private bool _citesSources = true;

        private bool HasSearchOrAsk => SearchOrAsk.ShownFor(Ctx.TeamId, Ctx.Name) && (_ask.Judged > 0 || _ask.Asks > 0);

        private IEnumerable<Tabs.TabItem> SearchOrAskSection =>
            HasSearchOrAsk ? [new("Search or ask", "search-or-ask", DatasetIcons.SearchOrAsk)] : [];

        private static readonly List<Tabs.TabItem> AskViewItems =
        [
            new("Questions", "questions"), new("Offers", "offers"),
        ];

        private void LoadSearchOrAsk(StatisticsStore store, string team, string ds, long fromDay, long toDay, string? source)
        {
            _citesSources = SearchOrAsk.Settings(team, ds).Kind != SearchOrAskRecipes.Catalogue;
            _ask = store.AskOverview(team, ds, fromDay, toDay, source);
            // The budget belongs to the dataset, not to a surface: the whole window whatever the source.
            _askBudget = store.AskBudget(team, ds, fromDay, toDay);
            _askQuestions = HasSearchOrAsk ? store.TopQuestions(team, ds, fromDay, toDay, 25, source) : [];
            _askOffers = HasSearchOrAsk ? store.TopOffers(team, ds, fromDay, toDay, 25, source) : [];
            (_askFromDay, _askToDay) = (fromDay, toDay);
            _askDays = HasSearchOrAsk ? store.AsksByDay(team, ds, fromDay, toDay, source) : [];
            _askBudgetDays = HasSearchOrAsk ? store.BudgetByDay(team, ds, fromDay, toDay) : [];
            _askWeekHour = HasSearchOrAsk ? store.AsksByWeekHour(team, ds, Math.Max(fromDay, toDay - Stats.RetentionDays + 1), toDay, source) : null;
            // Today against its budget: the budget's day is UTC, as the service counts it.
            long todayUtc = StatisticsStore.DayOf(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _askCallsToday = HasSearchOrAsk ? store.BudgetCalls(team, ds, todayUtc) : 0;
            _askBudgetToday = SearchOrAsk.Settings(team, ds).DailyCallBudget;
            if (!HasSearchOrAsk && _section == "search-or-ask") _section = "queries";
        }

        /// <summary>How the asks ended, as shares of one bar: answered in the ink, the rest down
        /// the ladder, failed in the signal red. "Sources could not answer" is the unanswered less
        /// the ones that found nothing, which are their own segment.</summary>
        private IReadOnlyList<BarMeter.Segment> AskOutcomeSegments()
        {
            var segments = new List<BarMeter.Segment>();
            void Add(string label, long n, string? color = null) { if (n > 0) segments.Add(new(label, n, color)); }
            Add("Answered", _ask.Answered);
            Add("Nothing found", _ask.NothingFound);
            Add("Sources could not answer", Math.Max(0, _ask.Unanswered - _ask.NothingFound));
            Add("Stopped", _ask.Stopped);
            Add("Failed", _ask.Failed, "var(--CSignal)");
            return segments;
        }

        /// <summary>Offers and asks, one value a day across the window, days with nothing as zero.</summary>
        private (double[] Offers, double[] Asks, double[] Cost, string[] Labels) AskDaySeries()
        {
            int days = (int)(_askToDay - _askFromDay + 1);
            var byDay = _askDays.ToDictionary(d => d.Day);
            var offers = new double[days]; var asks = new double[days]; var cost = new double[days]; var labels = new string[days];
            int epoch = DateOnly.Parse("1970-01-01").DayNumber;
            for (int i = 0; i < days; i++)
            {
                var d = byDay.GetValueOrDefault(_askFromDay + i);
                offers[i] = d.Offers; asks[i] = d.Asks; cost[i] = d.Cost;
                labels[i] = DateOnly.FromDayNumber((int)(_askFromDay + i) + epoch).ToString("d MMM");
            }
            return (offers, asks, cost, labels);
        }

        /// <summary>The calls per day against the budget, the window's days in order; the budget
        /// shown is the largest any day in the window had, since it can be changed under Options.</summary>
        private (double[] Calls, int Budget, string[] Labels) AskBudgetSeries()
        {
            int days = (int)(_askToDay - _askFromDay + 1);
            var byDay = _askBudgetDays.ToDictionary(d => d.Day);
            var calls = new double[days]; var labels = new string[days];
            int epoch = DateOnly.Parse("1970-01-01").DayNumber;
            for (int i = 0; i < days; i++)
            {
                calls[i] = byDay.GetValueOrDefault(_askFromDay + i).Calls;
                labels[i] = DateOnly.FromDayNumber((int)(_askFromDay + i) + epoch).ToString("d MMM");
            }
            int budget = _askBudgetDays.Count > 0 ? _askBudgetDays.Max(d => d.Budget) : _askBudgetToday;
            return (calls, budget, labels);
        }

        private static string Usd(double usd) => usd == 0 ? "–" : usd < 0.01 ? "< $0.01" : usd.ToString("$#,##0.00");

        private static string Seconds(long? ms) => ms is long m ? (m / 1000.0).ToString("0.0 s") : "–";

        /// <summary>The budget tile's line: the days it ran out on, or the busiest day against its budget.</summary>
        private string BudgetLine =>
            _askBudget.DaysRunOut > 0 ? $"ran out on {_askBudget.DaysRunOut} {(_askBudget.DaysRunOut == 1 ? "day" : "days")}, {N(_askBudget.Refused)} turned away"
            : _askBudget.PeakBudget > 0 ? $"busiest day {N(_askBudget.PeakCalls)} of {N(_askBudget.PeakBudget)}"
            : "";

        private static string Score(double? p) => p is double v ? v.ToString("0.00") : "–";

        /// <summary>How a question's asks ended, in words: the worst of them leads.</summary>
        private static string QuestionOutcome(AskQuestionStat q) =>
            q.NothingFound > 0 ? (q.NothingFound == q.Asks ? "Nothing found" : $"Nothing found {q.NothingFound} of {q.Asks}")
            : q.Unanswered > 0 ? "Sources may not answer it"
            : q.Answered == q.Asks ? "Answered"
            : $"Answered {q.Answered} of {q.Asks}";
    }
}
