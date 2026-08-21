package io.behaviordiff.agent;

import java.util.Arrays;

public final class CallFrame {
    private final long callId;
    private final String methodFullName;
    private final Object[] arguments;

    CallFrame(long callId, String methodFullName, Object[] arguments) {
        this.callId = callId;
        this.methodFullName = methodFullName;
        this.arguments = arguments.clone();
    }

    public long callId() {
        return callId;
    }

    public String methodFullName() {
        return methodFullName;
    }

    public Object[] arguments() {
        return arguments.clone();
    }

    @Override
    public String toString() {
        return methodFullName + "#" + callId + Arrays.toString(arguments);
    }
}