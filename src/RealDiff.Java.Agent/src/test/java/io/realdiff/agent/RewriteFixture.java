package io.realdiff.agent;

final class RewriteFixture {
    int normal(int value) {
        return value + 1;
    }

    long wide(long value) {
        return value + 1;
    }

    String reference(String value) {
        return value + "!";
    }

    void noValue() {
    }

    int explicitThrow() {
        throw new IllegalArgumentException("explicit");
    }

    int implicitThrow(String value) {
        return value.length();
    }
}