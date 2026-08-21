package io.behaviordiff.agent;

import java.util.Arrays;

public final class CallFrame {
    private final long callId;
    private final String methodFullName;
    private final Object[] arguments;
    private final String filePath;
    private final String filePathResolution;
    private final int line;
    private final String testId;
    private final long parentCallId;
    private final int callDepth;
    private final boolean harness;
    private final boolean testRoot;

    CallFrame(
        long callId,
        String methodFullName,
        Object[] arguments,
        String filePath,
        String filePathResolution,
        int line,
        String testId,
        long parentCallId,
        int callDepth,
        boolean harness,
        boolean testRoot) {
        this.callId = callId;
        this.methodFullName = methodFullName;
        this.arguments = arguments.clone();
        this.filePath = filePath;
        this.filePathResolution = filePathResolution;
        this.line = line;
        this.testId = testId;
        this.parentCallId = parentCallId;
        this.callDepth = callDepth;
        this.harness = harness;
        this.testRoot = testRoot;
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

    public String testId() {
        return testId;
    }

    public long parentCallId() {
        return parentCallId;
    }

    public int callDepth() {
        return callDepth;
    }

    public boolean isHarness() {
        return harness;
    }

    public boolean isTestRoot() {
        return testRoot;
    }

    @Override
    public String toString() {
        return methodFullName + "#" + callId + Arrays.toString(arguments);
    }
}