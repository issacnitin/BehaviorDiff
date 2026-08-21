package io.behaviordiff.reference;

import java.net.URI;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

public final class Subject {
    private Subject() { }

    public static int observe(int value) { return value * 2 + 1; }
    public static int inspect(SideEffect value) { return value == null ? 0 : 1; }
    public static String observedCalls() { return String.join(",", SideEffect.calls); }
    public static int cycle(Node value) { return value == null ? 0 : 1; }
    public static int topology(Pair value) { return value == null ? 0 : 1; }
    public static int stamp(Stamped value) { return value == null ? 0 : 1; }
    public static int block(RuntimeHolder value) { return value == null ? 0 : 1; }
    public static int deep(Node value) { return value == null ? 0 : 1; }
    public static int longText(String value) { return value.length(); }
    public static int unreadable(URI value) { return value == null ? 0 : 1; }
    public static void throwsNow() { throw new IllegalStateException("reference throw"); }
    public static CompletableFuture<String> future(CompletableFuture<String> value) { return value; }

    public static HashMap<String, Integer> map(boolean reverse) {
        HashMap<String, Integer> value = new HashMap<>();
        value.put(reverse ? "b" : "a", reverse ? 2 : 1);
        value.put(reverse ? "a" : "b", reverse ? 1 : 2);
        return value;
    }

    public static HashSet<String> set(boolean reverse) {
        HashSet<String> value = new HashSet<>();
        value.add(reverse ? "b" : "a");
        value.add(reverse ? "a" : "b");
        return value;
    }

    public static final class SideEffect implements Iterable<Integer> {
        public static final List<String> calls = new ArrayList<>();
        private final int value;

        public SideEffect(int value) { this.value = value; }
        public int getValue() { calls.add("getter"); return value; }
        @Override public String toString() { calls.add("toString"); return "side-effect"; }
        @Override public int hashCode() { calls.add("hashCode"); return value; }
        @Override public Iterator<Integer> iterator() { calls.add("iterator"); return List.of(value).iterator(); }
    }

    public static final class Node {
        private final String name;
        private Node next;

        public Node(String name) { this.name = name; }
        public static Node cycle(String name) { Node value = new Node(name); value.next = value; return value; }
        public static Node deep(int remaining) {
            Node value = new Node(Integer.toString(remaining));
            if (remaining > 0) { value.next = deep(remaining - 1); }
            return value;
        }
    }

    public static final class Pair {
        private final Object left;
        private final Object right;
        public Pair(Object left, Object right) { this.left = left; this.right = right; }
    }

    public static final class Stamped {
        private final UUID id;
        private final Instant at;
        private final String name;
        public Stamped(UUID id, Instant at, String name) { this.id = id; this.at = at; this.name = name; }
    }

    public static final class RuntimeHolder {
        private final Thread thread = Thread.currentThread();
        private final CompletableFuture<String> future = new CompletableFuture<>();
    }
}