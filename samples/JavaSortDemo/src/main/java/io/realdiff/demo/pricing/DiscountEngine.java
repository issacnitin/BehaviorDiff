package io.realdiff.demo.pricing;

import io.realdiff.demo.sorting.RuleOrdering;
import java.util.List;

public final class DiscountEngine {
    private final List<Rule> rules = List.of(
        new Rule("Z_CLEARANCE", 10, 50.0),
        new Rule("A_SEASONAL", 10, 50.0),
        new Rule("INELIGIBLE", 10, 1000.0));

    public String selectDiscount(double listPrice) {
        for (Rule rule : RuleOrdering.byPriority(rules)) {
            if (listPrice >= rule.minimumTotal) {
                return rule.code;
            }
        }
        throw new IllegalStateException("No eligible discount rule");
    }
}