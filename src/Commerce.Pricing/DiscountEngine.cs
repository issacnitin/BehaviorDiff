using System;
using System.Collections.Generic;
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
            var candidates = new List<(int Priority, DiscountRule Value)>(_rules.Count);
            foreach (DiscountRule rule in _rules)
            {
                candidates.Add((rule.Priority, rule));
            }

            foreach ((int _, DiscountRule rule) in candidates.ByPriority())
            {
                if (listPrice >= rule.MinimumTotal)
                {
                    return rule.Code;
                }
            }

            throw new InvalidOperationException("No eligible discount rule.");
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

        internal readonly string Code;

        internal readonly int Priority;

        internal readonly decimal MinimumTotal;
    }
}