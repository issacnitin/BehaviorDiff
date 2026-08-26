package io.realdiff.agent;

final class CorrelationFixture {
    @org.junit.jupiter.api.Test
    int first() {
        return nested(2);
    }

    @org.junit.jupiter.api.Test
    int second() {
        return nested(3);
    }

    private int nested(int value) {
        return leaf(value) + 1;
    }

    private int leaf(int value) {
        return value * 2;
    }
}