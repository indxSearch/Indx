using Indx.Systm.Blazor;
using IndxServer.Services;

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
        private List<AskQuestionStat> _askQuestions = [];
        private List<AskOfferStat> _askOffers = [];
        private string _askView = "questions";

        private bool HasSearchOrAsk => _ask.Judged > 0 || _ask.Asks > 0;

        private IEnumerable<Tabs.TabItem> SearchOrAskSection =>
            HasSearchOrAsk ? [new("Search or ask", "search-or-ask", DatasetIcons.SearchOrAsk)] : [];

        private static readonly List<Tabs.TabItem> AskViewItems =
        [
            new("Questions", "questions"), new("Offers", "offers"),
        ];

        private void LoadSearchOrAsk(StatisticsStore store, string team, string ds, long fromDay, long toDay, string? source)
        {
            _ask = store.AskOverview(team, ds, fromDay, toDay, source);
            _askQuestions = HasSearchOrAsk ? store.TopQuestions(team, ds, fromDay, toDay, 25, source) : [];
            _askOffers = HasSearchOrAsk ? store.TopOffers(team, ds, fromDay, toDay, 25, source) : [];
            if (!HasSearchOrAsk && _section == "search-or-ask") _section = "queries";
        }

        private static string Usd(double usd) => usd == 0 ? "–" : usd < 0.01 ? "< $0.01" : usd.ToString("$#,##0.00");

        private static string Seconds(long? ms) => ms is long m ? (m / 1000.0).ToString("0.0 s") : "–";

        private static string Score(double? p) => p is double v ? v.ToString("0.00") : "–";

        /// <summary>How a question's asks ended, in words: the worst of them leads.</summary>
        private static string QuestionOutcome(AskQuestionStat q) =>
            q.NothingFound > 0 ? (q.NothingFound == q.Asks ? "Nothing found" : $"Nothing found {q.NothingFound} of {q.Asks}")
            : q.Unanswered > 0 ? "Sources may not answer it"
            : q.Answered == q.Asks ? "Answered"
            : $"Answered {q.Answered} of {q.Asks}";
    }
}
