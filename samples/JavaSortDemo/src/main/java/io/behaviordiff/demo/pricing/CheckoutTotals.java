package io.behaviordiff.demo.pricing;

import io.behaviordiff.demo.pipeline.DecisionPipeline;

public final class CheckoutTotals {
    private final DiscountEngine discounts = new DiscountEngine();
    private final DecisionPipeline pipeline = new DecisionPipeline();
    public String selectedCode;

    public double compute(double listPrice) {
        selectedCode = pipeline.execute(listPrice, discounts);
        return selectedCode.equals("A_SEASONAL") ? listPrice * 0.85 : listPrice * 0.60;
    }
}