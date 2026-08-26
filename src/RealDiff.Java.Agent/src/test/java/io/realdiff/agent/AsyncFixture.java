package io.realdiff.agent;

import java.util.concurrent.CompletableFuture;

final class AsyncFixture {
    CompletableFuture<String> passThrough(CompletableFuture<String> future) {
        return future;
    }
}