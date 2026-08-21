package io.behaviordiff.agent;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Map;
import org.junit.jupiter.api.Test;

final class PackageScopeTest {
    private final PackageScope scope = new PackageScope(AgentOptions.parse(
        "include=com.example,org.acme;exclude=com.example.generated", Map.of()));

    @Test
    void includesExactPackageAndDescendants() {
        assertTrue(scope.includes("com/example/Subject"));
        assertTrue(scope.includes("com/example/service/Subject"));
        assertTrue(scope.includes("org/acme/Subject"));
    }

    @Test
    void excludeWinsForExactPackageAndDescendants() {
        assertFalse(scope.includes("com/example/generated/Subject"));
        assertFalse(scope.includes("com/example/generated/model/Subject"));
    }

    @Test
    void usesPackageSegmentBoundaries() {
        assertFalse(scope.includes("com/examples/Subject"));
        assertFalse(scope.includes("com/examplegenerated/Subject"));
        assertFalse(scope.includes("DefaultPackageSubject"));
        assertFalse(scope.includes(null));
    }
}