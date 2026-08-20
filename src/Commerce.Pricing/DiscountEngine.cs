using System.Collections.Generic;
using System.Linq;
using Infrastructure.Collections;

namespace Commerce.Pricing
{
    public sealed class DiscountEngine
    {
        private readonly IReadOnlyList<DiscountRule> _rules = new[]
        {
            new DiscountRule("INELIGIBLE_00", 10, 1000m),
            new DiscountRule("SEASONAL_15", 10, 50m),
            new DiscountRule("CLEARANCE_40", 10, 50m),
            new DiscountRule("INELIGIBLE_03", 10, 1000m),
            new DiscountRule("INELIGIBLE_04", 10, 1000m),
            new DiscountRule("INELIGIBLE_05", 10, 1000m),
            new DiscountRule("INELIGIBLE_06", 10, 1000m),
            new DiscountRule("INELIGIBLE_07", 10, 1000m),
            new DiscountRule("INELIGIBLE_08", 10, 1000m),
            new DiscountRule("INELIGIBLE_09", 10, 1000m),
            new DiscountRule("INELIGIBLE_10", 10, 1000m),
            new DiscountRule("INELIGIBLE_11", 10, 1000m),
            new DiscountRule("INELIGIBLE_12", 10, 1000m),
            new DiscountRule("INELIGIBLE_13", 10, 1000m),
            new DiscountRule("INELIGIBLE_14", 10, 1000m),
            new DiscountRule("INELIGIBLE_15", 10, 1000m),
            new DiscountRule("INELIGIBLE_16", 10, 1000m),
        };

        public string SelectDiscount(decimal listPrice)
        {
            return _rules
                .ByPriority(rule => rule.Priority)
                .First(rule => listPrice >= rule.MinimumTotal)
                .Code;
        }
    }

    internal sealed class DiscountRule
    {
        internal DiscountRule(string code, int priority, decimal minimumTotal)
        {
            Code = code;
            Priority = priority;
            MinimumTotal = minimumTotal;
        }

        internal string Code { get; }

        internal int Priority { get; }

        internal decimal MinimumTotal { get; }
    }
}