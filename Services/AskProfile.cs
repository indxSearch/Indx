namespace IndxServer.Services
{
    /// <summary>
    /// The shape of a window's Search or ask, shares on one scale for the section's radar: how often
    /// a judged pause got an offer, an offer a question, a question an answer, an offer the strong
    /// kind, an answer a source opened (where the recipe cites sources), and how much of the budget
    /// the window used. As <see cref="SearchProfile"/>: built from the reads the overview makes, two
    /// windows on the same rings, no shape without a judged pause.
    /// </summary>
    public sealed record AskProfile(double Offered, double Asked, double Answered, double Strong, double? SourcesOpened, double BudgetUsed)
    {
        public static IReadOnlyList<string> AxisLabels(bool citesSources) =>
            citesSources ? ["Offered", "Asked", "Answered", "Strong offers", "Sources opened", "Budget used"]
                         : ["Offered", "Asked", "Answered", "Strong offers", "Budget used"];

        public IReadOnlyList<double> Values(bool citesSources) =>
            citesSources ? [Offered, Asked, Answered, Strong, SourcesOpened ?? 0, BudgetUsed]
                         : [Offered, Asked, Answered, Strong, BudgetUsed];

        /// <summary>Null when nothing was judged. Budget used is the calls over the budgets of the
        /// window's days that had a row, 1 at most.</summary>
        public static AskProfile? Build(AskOverviewStat ask, IReadOnlyList<AskBudgetDayStat> budgetDays)
        {
            if (ask.Judged <= 0 && ask.Asks <= 0) return null;
            double Share(long n, long of) => of > 0 ? Math.Clamp((double)n / of, 0, 1) : 0;
            long calls = 0, budget = 0;
            foreach (var d in budgetDays) { calls += d.Calls; budget += d.Budget; }
            return new AskProfile(
                Share(ask.Offers, ask.Judged),
                Share(ask.Asks, ask.Offers),
                Share(ask.Answered, ask.Asks),
                Share(ask.StrongOffers, ask.Offers),
                ask.Answered > 0 ? Share(ask.SourcesOpened, ask.Answered) : null,
                Share(calls, budget));
        }
    }
}
