using Indx.Api;
using IndxServer.Models;

namespace IndxServer.Services
{
    /// <summary>The boost rules, in <c>DatasetBoostRules</c>. The store itself is
    /// <see cref="RankingRuleStore{TRule}"/> since 25 Oct 2026, shared with the bury rules.</summary>
    public class BoostRuleStore(ILogger<BoostRuleStore> logger)
        : RankingRuleStore<BoostRule>("DatasetBoostRules", DatasetChangeKind.BoostRules, "boost", logger)
    {
        public List<Boost> BuildActiveBoosts(ISearchEngine engine, string teamId, string dataSetName, DateOnly today)
        {
            var result = new List<Boost>();
            foreach (var rule in Load(teamId, dataSetName))
            {
                if (!rule.IsActiveOn(today) || rule.Conditions.Count == 0) continue;
                // A strength SetBoostRules refuses can still be in a row written before it did.
                if (CombinedFilter(engine, rule) is { } combined && engine.CreateBoost(combined, rule.Strength, out _) is { } boost)
                    result.Add(boost);
            }
            return result;
        }
    }
}
