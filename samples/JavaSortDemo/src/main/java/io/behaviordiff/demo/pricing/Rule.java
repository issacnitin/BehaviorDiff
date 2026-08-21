package io.behaviordiff.demo.pricing;

public final class Rule {
    public final String code;
    public final int priority;
    public final double minimumTotal;

    public Rule(String code, int priority, double minimumTotal) {
        this.code = code;
        this.priority = priority;
        this.minimumTotal = minimumTotal;
    }
}