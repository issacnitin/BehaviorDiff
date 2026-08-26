package io.realdiff.demo.sorting;

import io.realdiff.demo.pricing.Rule;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

public final class RuleOrdering {
    private RuleOrdering() {
    }

    public static List<Rule> byPriority(List<Rule> rules) {
        List<Rule> ordered = new ArrayList<>(rules);
        ordered.sort(Comparator.comparingInt(RuleOrdering::priority));
        return ordered;
    }

    private static int priority(Rule rule) {
        return rule.priority;
    }

    private static String code(Rule rule) {
        return rule.code;
    }
}