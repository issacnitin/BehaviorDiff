package io.realdiff.agent;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertTimeoutPreemptively;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Arrays;
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
    void emitsLowercaseSha256Digest() {
        assertEquals(
            "sha256:af9476e2d2e9766ffa8aa350ccd6504c42cfb33bf3329b91ba7c2001ff8486d3",
            StructuralDigest.compute("value").digest());
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
        String first = "a-".repeat(1500) + "x";
        String second = "a-".repeat(1500) + "y";
        DigestResult left = StructuralDigest.compute(first);
        DigestResult right = StructuralDigest.compute(second);

        assertEquals(left.rendered(), right.rendered());
        assertTrue(left.rendered().endsWith("<truncated>"));
        assertNotEquals(left.digest(), right.digest());
    }

    @Test
    void boundsLargeNestedPrimitiveArrays() {
        char[][] value = new char[10_000][];
        Arrays.fill(value, new char[10_000]);

        DigestResult result = assertTimeoutPreemptively(
            Duration.ofSeconds(2),
            () -> StructuralDigest.compute(value));

        assertTrue(result.rendered().contains("<depth:entries:9984>"));
    }

    @Test
    void boundsTotalDigestWork() {
        long limitsBefore = StructuralDigest.depthLimited();

        assertTimeoutPreemptively(
            Duration.ofSeconds(2),
            () -> StructuralDigest.compute(new char[16][16][16]));

        assertTrue(StructuralDigest.depthLimited() > limitsBefore);
    }

    @Test
    void redactsSensitiveFieldsAndCredentialContentButKeepsRealDigest() {
        Credential first = new Credential("first-password", "AKIA1234567890ABCDEF");
        Credential second = new Credential("second-password", "AKIAFEDCBA0987654321");

        DigestResult left = StructuralDigest.compute(first);
        DigestResult right = StructuralDigest.compute(second);

        assertNotEquals(left.digest(), right.digest());
        assertFalse(left.rendered().contains("first-password"));
        assertFalse(left.rendered().contains("AKIA1234567890ABCDEF"));
        assertTrue(left.rendered().contains("<redacted>"));
    }

    private static final class Credential {
        private final String password;
        private final String result;

        Credential(String password, String result) {
            this.password = password;
            this.result = result;
        }
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