package io.behaviordiff.agent;

import java.util.ArrayDeque;
import java.util.Deque;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicLong;

public final class RuntimeHooks {
    interface Sink {
        void completed(CallFrame frame, Object returnValue, Throwable throwable);
    }

    private static final AtomicLong NEXT_CALL_ID = new AtomicLong();
    private static final ThreadLocal<Deque<CallFrame>> CALL_STACK = ThreadLocal.withInitial(ArrayDeque::new);
    private static volatile Sink sink = (frame, returnValue, throwable) -> { };

    private RuntimeHooks() {
    }

    public static CallFrame enter(
        String methodFullName,
        Object[] arguments,
        String filePath,
        String filePathResolution,
        int line,
        boolean harness,
        boolean testRoot) {
        Deque<CallFrame> stack = CALL_STACK.get();
        CallFrame parent = stack.peek();
        long callId = NEXT_CALL_ID.incrementAndGet();
        String testId = testRoot
            ? methodFullName
            : parent == null ? "(no-test)" : parent.testId();
        CallFrame frame = new CallFrame(
            callId,
            methodFullName,
            arguments,
            filePath,
            filePathResolution,
            line,
            testId,
            parent == null ? 0 : parent.callId(),
            stack.size(),
            harness,
            testRoot);
        stack.push(frame);
        return frame;
    }

    public static void exit(CallFrame frame, Object returnValue, Throwable throwable) {
        pop(frame);
        sink.completed(frame, returnValue, throwable);
    }

    public static void exitFuture(CallFrame frame, CompletableFuture<?> future) {
        pop(frame);
        future.whenCompleteAsync((returnValue, throwable) ->
            sink.completed(frame, returnValue, unwrapCompletion(throwable)));
    }

    private static void pop(CallFrame frame) {
        Deque<CallFrame> stack = CALL_STACK.get();
        CallFrame current = stack.poll();
        if (current != frame) {
            stack.clear();
            throw new IllegalStateException("BehaviorDiff call stack is unbalanced at " + frame.methodFullName());
        }
        if (stack.isEmpty()) {
            CALL_STACK.remove();
        }
    }

    private static Throwable unwrapCompletion(Throwable throwable) {
        if (throwable instanceof java.util.concurrent.CompletionException && throwable.getCause() != null) {
            return throwable.getCause();
        }
        return throwable;
    }

    static void setSink(Sink replacement) {
        CALL_STACK.remove();
        sink = replacement == null ? (frame, returnValue, throwable) -> { } : replacement;
    }
}