package io.behaviordiff.agent;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.util.HashMap;
import java.util.Map;
import org.junit.jupiter.api.Test;

final class AgentOptionsTest {
    @Test
    void readsScopeFromEnvironment() {
        Map<String, String> environment = new HashMap<>();
        environment.put(AgentOptions.INCLUDE_ENVIRONMENT, "com.example, org.acme");
        environment.put(AgentOptions.EXCLUDE_ENVIRONMENT, "com.example.generated");

        AgentOptions options = AgentOptions.parse(null, environment);

        assertEquals(java.util.List.of("com.example", "org.acme"), options.includes());
        assertEquals(java.util.List.of("com.example.generated"), options.excludes());
    }

    @Test
    void agentArgumentsOverrideEnvironment() {
        Map<String, String> environment = Map.of(
            AgentOptions.INCLUDE_ENVIRONMENT, "ignored",
            AgentOptions.EXCLUDE_ENVIRONMENT, "ignored.internal");

        AgentOptions options = AgentOptions.parse(
            "include=com.example,org.acme;exclude=com.example.generated", environment);

        assertEquals(java.util.List.of("com.example", "org.acme"), options.includes());
        assertEquals(java.util.List.of("com.example.generated"), options.excludes());
    }

    @Test
    void includeScopeIsRequired() {
        assertThrows(IllegalArgumentException.class, () -> AgentOptions.parse(null, Map.of()));
    }

    @Test
    void rejectsUnknownAndDuplicateOptions() {
        assertThrows(IllegalArgumentException.class,
            () -> AgentOptions.parse("other=value", Map.of()));
        assertThrows(IllegalArgumentException.class,
            () -> AgentOptions.parse("include=a;include=b", Map.of()));
    }
}