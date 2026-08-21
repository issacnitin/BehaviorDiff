package io.behaviordiff.reference.tests;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import io.behaviordiff.reference.Subject;
import java.net.URI;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

final class ReferenceTests {
    @ParameterizedTest
    @ValueSource(ints = {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39,
        40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
        50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
        60, 61, 62, 63, 64, 65, 66, 67, 68, 69,
        70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
        80, 81, 82, 83, 84, 85, 86, 87, 88, 89,
        90, 91, 92, 93, 94, 95, 96, 97, 98, 99,
        100, 101, 102, 103, 104, 105, 106, 107, 108, 109
    })
    void volume(int value) {
        assertEquals(value * 2 + 1, Subject.observe(value));
    }

    @Test
    void digestProofs() throws Exception {
        Subject.SideEffect.calls.clear();
        assertEquals(1, Subject.inspect(new Subject.SideEffect(42)));
        assertEquals("", Subject.observedCalls());

        assertEquals(1, Subject.cycle(Subject.Node.cycle("same")));
        assertEquals(1, Subject.cycle(Subject.Node.cycle("same")));
        Subject.Node shared = new Subject.Node("same");
        assertEquals(1, Subject.topology(new Subject.Pair(shared, shared)));
        assertEquals(1, Subject.topology(new Subject.Pair(new Subject.Node("same"), new Subject.Node("same"))));

        assertEquals(Subject.map(false), Subject.map(true));
        assertEquals(Subject.set(false), Subject.set(true));
        assertEquals(1, Subject.stamp(new Subject.Stamped(UUID.randomUUID(), Instant.now(), "fixed")));
        assertEquals(1, Subject.stamp(new Subject.Stamped(UUID.randomUUID(), Instant.now().minusSeconds(600), "fixed")));
        assertEquals(1, Subject.block(new Subject.RuntimeHolder()));
        assertEquals(1, Subject.deep(Subject.Node.deep(9)));

        String prefix = "a".repeat(3000);
        assertEquals(3001, Subject.longText(prefix + "x"));
        assertEquals(3001, Subject.longText(prefix + "y"));
        assertEquals(1, Subject.unreadable(new URI("https://example.test/path")));
    }

    @Test
    void exceptionalExitHasNoReturn() {
        assertThrows(IllegalStateException.class, Subject::throwsNow);
    }

    @Test
    void futureEmitsAtSettlement() throws Exception {
        CompletableFuture<String> future = new CompletableFuture<>();
        assertEquals(future, Subject.future(future));
        future.complete("settled");
        assertEquals("settled", future.get(5, TimeUnit.SECONDS));
        Thread.sleep(100);
    }
}