package io.behaviordiff.reference.tests;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import io.behaviordiff.reference.Subject;
import java.net.URI;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
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

        String prefix = "a-".repeat(1500);
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

    @Test
    void virtualDispatchUsesBothImplementations() {
        Subject.Operation increment = new Subject.IncrementOperation();
        Subject.Operation triple = new Subject.TripleOperation();

        assertEquals(8, Subject.dispatch(increment, 7));
        assertEquals(21, Subject.dispatch(triple, 7));
    }

    @Test
    void abstractInheritanceCallsOverrideThroughBaseType() {
        Subject.TextDecorator decorator = new Subject.BracketDecorator();

        assertEquals("[mixed case]", Subject.decorate(decorator, "  mixed case  "));
    }

    @Test
    void genericMethodAndBoxPreserveValues() {
        assertEquals(9, Subject.greater(4, 9));

        Subject.Box<Integer> box = new Subject.Box<>(42);
        Subject.Box<String> mapped = box.map(new Subject.IntegerTextMapper());
        assertEquals(42, box.get());
        assertEquals("42", mapped.get());
    }

    @Test
    void overloadsAndChainedConstructorsRemainDistinct() {
        assertEquals("int:12", Subject.describe(12));
        assertEquals("text:twelve", Subject.describe("twelve"));
        assertEquals("3 item", new Subject.ChainedValue(3).render());
        assertEquals("5 crates", new Subject.ChainedValue(5, "crates").render());
    }

    @Test
    void namedFunctionalImplementationDispatches() {
        Subject.NamedTextFunction function = new Subject.SuffixFunction("!");

        assertEquals("ready!", Subject.dispatchNamed(function, "ready"));
    }

    @Test
    void arraysListsAndRecoveredExceptionsAreObservable() {
        assertEquals(10, Subject.sumArray(new int[] { 1, 2, 3, 4 }));
        ArrayList<String> normalized = Subject.normalizedList(" first ", "second ");
        assertEquals(List.of("first", "second"), normalized);
        assertEquals(17, Subject.parseOrDefault("17", -1));
        assertEquals(-1, Subject.parseOrDefault("not-a-number", -1));
    }

    @Test
    void futureChainEmitsAtSettlement() throws Exception {
        CompletableFuture<String> source = new CompletableFuture<>();
        CompletableFuture<String> chained = Subject.futureChain(source);
        assertEquals(false, chained.isDone());

        source.complete(" settled ");
        assertEquals("SETTLED", chained.get(5, TimeUnit.SECONDS));
        Thread.sleep(100);
    }
}