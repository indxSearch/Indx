using System.Text.Json.Serialization;
using Indx.Api;

namespace IndxServer.Models
{
    /// <summary>
    /// What a boost rule and a bury rule share (25 Oct 2026): everything but the strength's type.
    /// The console edits both through this, the strength as a number 1 to 3; the stores and the
    /// routes keep each its own type.
    /// </summary>
    public interface IRankingRule
    {
        string Name { get; set; }
        bool Enabled { get; set; }
        List<BoostCondition> Conditions { get; set; }
        BoostJoin Join { get; set; }
        /// <summary>The strength as its number: Low 1, Med 2, High 3.</summary>
        int StrengthValue { get; set; }
        DateOnly? ActiveFrom { get; set; }
        DateOnly? ActiveUntil { get; set; }
        bool IsActiveOn(DateOnly today);
    }

    /// <summary>
    /// A saved bury rule, a boost rule's twin (25 Oct 2026): a name, conditions on filterable fields
    /// joined by And or Or, a strength, an optional active period. Applied to a search that runs
    /// with <c>enableBury</c>: the documents the conditions match are scored lower.
    /// </summary>
    public class BuryRule : IRankingRule
    {
        public string Name { get; set; } = "";

        public bool Enabled { get; set; } = true;

        public List<BoostCondition> Conditions { get; set; } = [];

        public BoostJoin Join { get; set; } = BoostJoin.And;

        public BuryStrength Strength { get; set; } = BuryStrength.Med;

        [JsonIgnore]
        public int StrengthValue { get => (int)Strength; set => Strength = (BuryStrength)value; }

        public DateOnly? ActiveFrom { get; set; }

        public DateOnly? ActiveUntil { get; set; }

        public bool IsActiveOn(DateOnly today) =>
            Enabled
            && (!ActiveFrom.HasValue || today >= ActiveFrom.Value)
            && (!ActiveUntil.HasValue || today <= ActiveUntil.Value);
    }

    /// <summary>The checks a PUT of rules makes, the same for both kinds; null when the rules pass.</summary>
    public static class RankingRuleValidation
    {
        public static string? Objection(IEnumerable<IRankingRule> rules, DocumentFields? fields, Func<IRankingRule, bool> strengthDefined)
        {
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Name))
                    return "each rule needs a name";
                if (rule.Conditions == null || rule.Conditions.Count == 0)
                    return $"rule '{rule.Name}' needs at least one condition";
                if (!strengthDefined(rule))
                    return $"rule '{rule.Name}': strength {rule.StrengthValue} is not one of Low (1), Med (2) or High (3)";
                foreach (var c in rule.Conditions)
                {
                    var hasValue = !string.IsNullOrEmpty(c.Value);
                    var hasRange = c.Min.HasValue || c.Max.HasValue;
                    if (hasValue == hasRange)
                        return $"condition on '{c.Field}' must be either a value or a range";
                    if (fields != null && fields.GetField(c.Field) is not { Filterable: true })
                        return $"field '{c.Field}' is not filterable";
                }
            }
            return null;
        }
    }
}
