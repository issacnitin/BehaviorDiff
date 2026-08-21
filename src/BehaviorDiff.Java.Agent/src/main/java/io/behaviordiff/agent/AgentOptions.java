package io.behaviordiff.agent;

import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

final class AgentOptions {
    static final String INCLUDE_ENVIRONMENT = "BEHAVIORDIFF_NAMESPACES";
    static final String EXCLUDE_ENVIRONMENT = "BEHAVIORDIFF_EXCLUDE_NAMESPACES";
    static final String TRACE_ENVIRONMENT = "BEHAVIORDIFF_TRACE";

    private final List<String> includes;
    private final List<String> excludes;
    private final String tracePath;

    private AgentOptions(List<String> includes, List<String> excludes, String tracePath) {
        this.includes = includes;
        this.excludes = excludes;
        this.tracePath = tracePath;
    }

    static AgentOptions parse(String agentArguments, Map<String, String> environment) {
        Map<String, String> arguments = parseArguments(agentArguments);
        String includeText = arguments.getOrDefault("include", environment.get(INCLUDE_ENVIRONMENT));
        String excludeText = arguments.getOrDefault("exclude", environment.get(EXCLUDE_ENVIRONMENT));
        List<String> includes = parsePrefixes(includeText);
        if (includes.isEmpty()) {
            throw new IllegalArgumentException(
                "BehaviorDiff Java agent requires include=<package prefixes> or " + INCLUDE_ENVIRONMENT);
        }

        return new AgentOptions(
            includes,
            parsePrefixes(excludeText),
            arguments.getOrDefault("trace", environment.get(TRACE_ENVIRONMENT)));
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

    private static Map<String, String> parseArguments(String text) {
        if (text == null || text.trim().isEmpty()) {
            return Collections.emptyMap();
        }

        Map<String, String> values = new LinkedHashMap<>();
        for (String part : text.split(";")) {
            int separator = part.indexOf('=');
            if (separator <= 0) {
                throw new IllegalArgumentException("Invalid BehaviorDiff agent option: " + part);
            }

            String name = part.substring(0, separator).trim();
            String value = part.substring(separator + 1).trim();
            if (!name.equals("include") && !name.equals("exclude") && !name.equals("trace")) {
                throw new IllegalArgumentException("Unknown BehaviorDiff agent option: " + name);
            }

            if (values.put(name, value) != null) {
                throw new IllegalArgumentException("Duplicate BehaviorDiff agent option: " + name);
            }
        }

        return values;
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

    private static String normalize(String value) {
        String normalized = value.trim().replace('/', '.');
        while (normalized.endsWith(".")) {
            normalized = normalized.substring(0, normalized.length() - 1);
        }

        return normalized;
    }
}