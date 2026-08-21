package io.behaviordiff.demo.tests;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import io.behaviordiff.demo.pricing.CheckoutService;
import io.behaviordiff.demo.pricing.CheckoutTotals;
import org.junit.jupiter.api.Test;

final class SortStabilityTests {
    @Test
    void discountIsApplied() {
        CheckoutTotals checkout = (CheckoutTotals) new CheckoutService().headline();
        double total = checkout.compute(100.0);

        assertTrue(total < 100.0);
    }

    @Test
    void totalIsNeverAboveListPrice() {
        CheckoutTotals checkout = (CheckoutTotals) new CheckoutService().headline();
        double total = checkout.compute(100.0);

        assertTrue(total <= 100.0);
    }

    @Test
    void clearanceDiscountWinsCurrentTies() {
        CheckoutTotals checkout = (CheckoutTotals) new CheckoutService().headline();
        checkout.compute(100.0);

        assertEquals("Z_CLEARANCE", checkout.selectedCode);
    }
}