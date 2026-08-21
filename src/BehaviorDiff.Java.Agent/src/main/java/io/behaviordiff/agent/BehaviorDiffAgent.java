package io.behaviordiff.agent;

import java.lang.instrument.Instrumentation;

public final class BehaviorDiffAgent {
    private BehaviorDiffAgent() {
    }

    public static void premain(String agentArguments, Instrumentation instrumentation) {
        AgentOptions options = AgentOptions.fromProcess(agentArguments);
        instrumentation.addTransformer(
            new ScopeSelectingTransformer(new PackageScope(options)),
            false);
    }
}