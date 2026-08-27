package io.realdiff.agent;

import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.nio.file.Path;
import java.nio.file.Paths;

final class AgentOptions {
    static final String INCLUDE_ENVIRONMENT = "REALDIFF_NAMESPACES";
    static final String EXCLUDE_ENVIRONMENT = "REALDIFF_EXCLUDE_NAMESPACES";
    static final String TRACE_ENVIRONMENT = "REALDIFF_TRACE";
    static final String REPOSITORY_ROOT_ENVIRONMENT = "REALDIFF_REPOSITORY_ROOT";
    static final String SOURCE_ROOTS_ENVIRONMENT = "REALDIFF_JAVA_SOURCE_ROOTS";

    private final List<String> includes;
    private final List<String> excludes;
    private final String tracePath;
    private final Path repositoryRoot;
    private final List<String> sourceRoots;

    private AgentOptions(List<String> includes, List<String> excludes, String tracePath, Path repositoryRoot, List<String> sourceRoots) {
        this.includes = includes;
        this.excludes = excludes;
        this.tracePath = tracePath;
        this.repositoryRoot = repositoryRoot;
        this.sourceRoots = sourceRoots;
    }

    static AgentOptions parse(String agentArguments, Map<String, String> environment) {
        Map<String, String> arguments = parseArguments(agentArguments);
        String includeText = arguments.getOrDefault("include", environment.get(INCLUDE_ENVIRONMENT));
        String excludeText = arguments.getOrDefault("exclude", environment.get(EXCLUDE_ENVIRONMENT));
        List<String> includes = parsePrefixes(includeText);
        if (includes.isEmpty()) {
            throw new IllegalArgumentException(
                "RealDiff Java agent requires include=<package prefixes> or " + INCLUDE_ENVIRONMENT);
        }

        return new AgentOptions(
            includes,
            parsePrefixes(excludeText),
            arguments.getOrDefault("trace", environment.get(TRACE_ENVIRONMENT)),
            parsePath(arguments.getOrDefault("repositoryRoot", environment.get(REPOSITORY_ROOT_ENVIRONMENT))),
            parsePaths(arguments.getOrDefault("sourceRoots", environment.get(SOURCE_ROOTS_ENVIRONMENT))));
    }

    static AgentOptions fromProcess(String agentArguments) {
        return parse(agentArguments, System.getenv());
    }

    List<String> includes() {
        return includes;
    }

    List<String> excludes() {
        return excludes;
    }

    String tracePath() {
        return tracePath;
    }

    Path repositoryRoot() {
        return repositoryRoot;
    }

    List<String> sourceRoots() {
        return sourceRoots;
    }

    private static Map<String, String> parseArguments(String text) {
        if (text == null || text.trim().isEmpty()) {
            return Collections.emptyMap();
        }

        Map<String, String> values = new LinkedHashMap<>();
        for (String part : text.split(";")) {
            int separator = part.indexOf('=');
            if (separator <= 0) {
                throw new IllegalArgumentException("Invalid RealDiff agent option: " + part);
            }

            String name = part.substring(0, separator).trim();
            String value = part.substring(separator + 1).trim();
            if (!name.equals("include") && !name.equals("exclude") && !name.equals("trace")
                && !name.equals("repositoryRoot") && !name.equals("sourceRoots")) {
                throw new IllegalArgumentException("Unknown RealDiff agent option: " + name);
            }

            if (values.put(name, value) != null) {
                throw new IllegalArgumentException("Duplicate RealDiff agent option: " + name);
            }
        }

        return values;
    }

    private static Path parsePath(String text) {
        return text == null || text.trim().isEmpty()
            ? null
            : Paths.get(text.trim()).toAbsolutePath().normalize();
    }

    private static List<String> parsePrefixes(String text) {
        if (text == null || text.trim().isEmpty()) {
            return Collections.emptyList();
        }

        List<String> prefixes = new ArrayList<>();
        for (String value : text.split("[,]")) {
            String prefix = normalize(value);
            if (!prefix.isEmpty() && !prefixes.contains(prefix)) {
                prefixes.add(prefix);
            }
        }

        return Collections.unmodifiableList(prefixes);
    }

    private static List<String> parsePaths(String text) {
        if (text == null || text.trim().isEmpty()) {
            return List.of("src/main/java", "src/test/java");
        }
        List<String> roots = new ArrayList<>();
        for (String value : text.split("[,;]")) {
            String root = value.trim().replace('\\', '/');
            if (!root.isEmpty() && !roots.contains(root)) {
                roots.add(root);
            }
        }
        return Collections.unmodifiableList(roots);
    }

    private static String normalize(String value) {
        String normalized = value.trim().replace('/', '.');
        while (normalized.endsWith(".")) {
            normalized = normalized.substring(0, normalized.length() - 1);
        }

        return normalized;
    }
}