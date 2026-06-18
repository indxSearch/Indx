using System.Globalization;
using System.Text.Json.Serialization;
using Indx.Api;

namespace IndxCloudApi.Models
{
    /// <summary>How the conditions of a single boost rule are joined.</summary>
    public enum BoostJoin
    {
        And,
        Or,
    }

    /// <summary>
    /// One condition of a boost rule: a filterable field matched either by an exact
    /// <see cref="Value"/> (string/keyword fields) or by a numeric <see cref="Min"/>/<see cref="Max"/>
    /// range. Exactly one of the two forms is used: if <see cref="Min"/> or <see cref="Max"/> is set
    /// it's a range, otherwise <see cref="Value"/> is an exact match.
    /// </summary>
    public class BoostCondition
    {
        public string Field { get; set; } = "";

        /// <summary>Exact value match (value form). Ignored when Min/Max are set.</summary>
        public string? Value { get; set; }

        /// <summary>Inclusive lower bound (range form). Null = open lower bound.</summary>
        public double? Min { get; set; }

        /// <summary>Inclusive upper bound (range form). Null = open upper bound.</summary>
        public double? Max { get; set; }

        public bool IsRange => Min.HasValue || Max.HasValue;

        // Text views for binding numeric inputs (the UI has only a text InputField). Not persisted.
        [JsonIgnore]
        public string MinText
        {
            get => Min?.ToString(CultureInfo.InvariantCulture) ?? "";
            set => Min = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        [JsonIgnore]
        public string MaxText
        {
            get => Max?.ToString(CultureInfo.InvariantCulture) ?? "";
            set => Max = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }

    /// <summary>
    /// A persisted, per-dataset ranking rule: when a search has <c>enableBoost</c> on, documents
    /// matching this rule's combined condition get their score lifted by <see cref="Strength"/>.
    /// Rules are managed in the UI and applied server-side; see <c>BoostRuleStore</c>.
    /// </summary>
    public class BoostRule
    {
        public string Name { get; set; } = "";

        public bool Enabled { get; set; } = true;

        public List<BoostCondition> Conditions { get; set; } = [];

        /// <summary>Operator joining all of this rule's conditions (one operator per rule, v1).</summary>
        public BoostJoin Join { get; set; } = BoostJoin.And;

        /// <summary>Boost magnitude. Reuses the engine enum (Low=1, Med=2, High=3).</summary>
        public BoostStrength Strength { get; set; } = BoostStrength.Med;

        /// <summary>Inclusive first day the rule is active (UTC date). Null = no lower bound.</summary>
        public DateOnly? ActiveFrom { get; set; }

        /// <summary>Inclusive last day the rule is active (UTC date). Null = no upper bound.</summary>
        public DateOnly? ActiveUntil { get; set; }

        /// <summary>True when the rule is enabled and <paramref name="today"/> is within its window.</summary>
        public bool IsActiveOn(DateOnly today) =>
            Enabled
            && (!ActiveFrom.HasValue || today >= ActiveFrom.Value)
            && (!ActiveUntil.HasValue || today <= ActiveUntil.Value);
    }
}
