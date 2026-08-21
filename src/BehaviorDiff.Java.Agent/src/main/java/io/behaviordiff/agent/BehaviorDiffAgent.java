package io.behaviordiff.agent;

import java.lang.instrument.Instrumentation;

public final class BehaviorDiffAgent {
    private BehaviorDiffAgent() {
    }

    public static void premain(String agentArguments, Instrumentation instrumentation) {
        CollectionInternals.requireAccess();
        AgentOptions options = AgentOptions.fromProcess(agentArguments);
        TraceSession traceSession = TraceSession.start(options.tracePath());
        RuntimeHooks.initialize(traceSession);
        instrumentation.addTransformer(
            new ScopeSelectingTransformer(
                new PackageScope(options),
                traceSession,
                new JavaSourceResolver(options.repositoryRoot())),
            false);
    }
}