package io.behaviordiff.agent;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.Test;

final class StructuralDigestTest {
    @Test
    void readsFieldsWithoutInvokingUserCode() {
        SideEffectValue.calls.clear();
        SideEffectValue value = new SideEffectValue(42);

        DigestResult result = StructuralDigest.compute(value);

        assertTrue(result.rendered().contains("value=Integer:42"));
        assertEquals(List.of(), SideEffectValue.calls);
    }

    @Test
    void usesBackingStorageForSupportedCollections() {
        ArrayList<String> list = new ArrayList<>();
        list.add("b");
        list.add("a");
        HashMap<String, Integer> map = new HashMap<>();
        map.put("b", 2);
        map.put("a", 1);
        HashSet<String> set = new HashSet<>();
        set.add("b");
        set.add("a");

        assertTrue(StructuralDigest.compute(list).rendered().startsWith("ShapeRule:ArrayList"));
        assertTrue(StructuralDigest.compute(map).rendered().startsWith("ShapeRule:HashMap"));
        assertTrue(StructuralDigest.compute(set).rendered().startsWith("ShapeRule:HashSet"));
    }

    @Test
    void hashCollectionsAreStableAcrossInsertionOrder() {
        HashMap<String, Integer> first = new HashMap<>();
        first.put("a", 1);
        first.put("b", 2);
        HashMap<String, Integer> second = new HashMap<>();
        second.put("b", 2);
        second.put("a", 1);

        assertEquals(StructuralDigest.compute(first).digest(), StructuralDigest.compute(second).digest());
    }

    @Test
    void normalizesTimeAndIdentity() {
        Holder first = new Holder(UUID.randomUUID(), Instant.now());
        Holder second = new Holder(UUID.randomUUID(), Instant.now().minusSeconds(600));

        assertEquals(StructuralDigest.compute(first).digest(), StructuralDigest.compute(second).digest());
    }

    @Test
    void distinguishesReferenceTopologyAndStopsCycles() {
        Node shared = new Node("same");
        Pair first = new Pair(shared, shared);
        Pair second = new Pair(new Node("same"), new Node("same"));
        Node cyclic = new Node("cycle");
        cyclic.next = cyclic;

        assertNotEquals(StructuralDigest.compute(first).digest(), StructuralDigest.compute(second).digest());
        assertTrue(StructuralDigest.compute(cyclic).rendered().contains("<ref:"));
    }

    @Test
    void blocklistsRuntimeShapesBeforeRecursion() {
        RuntimeHolder holder = new RuntimeHolder();

        DigestResult result = StructuralDigest.compute(holder);

        assertTrue(result.rendered().contains("<skipped:java.lang.Thread>"));
        assertTrue(result.rendered().contains("<skipped:java.util.concurrent.CompletableFuture>"));
    }

    @Test
    void truncationDoesNotChangeDigestInput() {
        String first = "a".repeat(3000) + "x";
        String second = "a".repeat(3000) + "y";
        DigestResult left = StructuralDigest.compute(first);
        DigestResult right = StructuralDigest.compute(second);

        assertEquals(left.rendered(), right.rendered());
        assertTrue(left.rendered().endsWith("<truncated>"));
        assertNotEquals(left.digest(), right.digest());
    }

    private static final class SideEffectValue implements Iterable<Integer> {
        static final List<String> calls = new ArrayList<>();
        private final int value;

        SideEffectValue(int value) {
            this.value = value;
        }

        public int getValue() {
            calls.add("getter");
            return value;
        }

        @Override
        public String toString() {
            calls.add("toString");
            return "side-effect";
        }

        @Override
        public int hashCode() {
            calls.add("hashCode");
            return value;
        }

        @Override
        public Iterator<Integer> iterator() {
            calls.add("iterator");
            return List.of(value).iterator();
        }
    }

    private static final class Holder {
        private final UUID id;
        private final Instant at;

        Holder(UUID id, Instant at) {
            this.id = id;
            this.at = at;
        }
    }

    private static final class Node {
        private final String name;
        private Node next;

        Node(String name) {
            this.name = name;
        }
    }

    private static final class Pair {
        private final Object left;
        private final Object right;

        Pair(Object left, Object right) {
            this.left = left;
            this.right = right;
        }
    }

    private static final class RuntimeHolder {
        private final Thread thread = Thread.currentThread();
        private final java.util.concurrent.CompletableFuture<String> future = new java.util.concurrent.CompletableFuture<>();
    }
}