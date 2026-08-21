package io.behaviordiff.agent;

import java.util.Arrays;

public final class CallFrame {
    private final long callId;
    private final String methodFullName;
    private final Object[] arguments;
    private final String filePath;
    private final String filePathResolution;
    private final int line;

    CallFrame(
        long callId,
        String methodFullName,
        Object[] arguments,
        String filePath,
        String filePathResolution,
        int line) {
        this.callId = callId;
        this.methodFullName = methodFullName;
        this.arguments = arguments.clone();
        this.filePath = filePath;
        this.filePathResolution = filePathResolution;
        this.line = line;
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

    public String filePath() {
        return filePath;
    }

    public String filePathResolution() {
        return filePathResolution;
    }

    public int line() {
        return line;
    }

    @Override
    public String toString() {
        return methodFullName + "#" + callId + Arrays.toString(arguments);
    }
}