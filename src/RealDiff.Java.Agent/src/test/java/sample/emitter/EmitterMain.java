package sample.emitter;

import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

public final class EmitterMain {
    public static void main(String[] arguments) throws Exception {
        EmitterMain fixture = new EmitterMain();
        if (fixture.root(3) != 7) {
            throw new AssertionError("unexpected result");
        }
        try {
            fixture.throwsNow();
        } catch (IllegalStateException expected) {
        }
        if (!"settled".equals(fixture.future(new CompletableFuture<>()).completeAsync(() -> "settled").get(5, TimeUnit.SECONDS))) {
            throw new AssertionError("unexpected future result");
        }
    }

    @org.junit.jupiter.api.Test
    int root(int value) {
        return nested(value) + 1;
    }

    private int nested(int value) {
        return value * 2;
    }

    private void throwsNow() {
        throw new IllegalStateException("expected");
    }

    private CompletableFuture<String> future(CompletableFuture<String> future) {
        return future;
    }
}