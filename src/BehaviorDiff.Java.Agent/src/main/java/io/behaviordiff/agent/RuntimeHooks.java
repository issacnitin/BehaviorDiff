package io.behaviordiff.agent;

import java.util.concurrent.atomic.AtomicLong;

public final class RuntimeHooks {
    interface Sink {
        void completed(CallFrame frame, Object returnValue, Throwable throwable);
    }

    private static final AtomicLong NEXT_CALL_ID = new AtomicLong();
    private static volatile Sink sink = (frame, returnValue, throwable) -> { };

    private RuntimeHooks() {
    }

    public static CallFrame enter(String methodFullName, Object[] arguments) {
        return new CallFrame(NEXT_CALL_ID.incrementAndGet(), methodFullName, arguments);
    }

    public static void exit(CallFrame frame, Object returnValue, Throwable throwable) {
        sink.completed(frame, returnValue, throwable);
    }

    static void setSink(Sink replacement) {
        sink = replacement == null ? (frame, returnValue, throwable) -> { } : replacement;
    }
}