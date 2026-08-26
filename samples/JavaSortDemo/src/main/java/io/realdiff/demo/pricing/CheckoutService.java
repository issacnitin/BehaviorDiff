package io.realdiff.demo.pricing;

public final class CheckoutService {
    public Object headline() {
        try {
            return Class.forName("io.realdiff.demo.pricing.CheckoutTotals")
                .getDeclaredConstructor()
                .newInstance();
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Checkout totals are unavailable", exception);
        }
    }
}