package io.behaviordiff.agent;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;

final class RedactionPolicy {
    private static final List<String> DEFAULT_NAMES = Arrays.asList(
        "password", "token", "secret", "key", "ssn", "email", "auth", "credential");
    private static final Pattern JWT = Pattern.compile(
        "\\beyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b");
    private static final Pattern AWS = Pattern.compile("\\b(?:AKIA|ASIA)[0-9A-Z]{16}\\b");
    private static final Pattern PEM = Pattern.compile("-----BEGIN [A-Z0-9 ]*(?:PRIVATE KEY|CERTIFICATE)-----");
    private static final Pattern BASE64 = Pattern.compile(
        "(?:^|[^A-Za-z0-9+/])(?:[A-Za-z0-9+/]{40,}={0,2})(?:$|[^A-Za-z0-9+/=])");

    private final List<String> names;
    private final List<String> types;
    private final List<String> paths;

    private RedactionPolicy(List<String> names, List<String> types, List<String> paths) {
        this.names = names;
        this.types = types;
        this.paths = paths;
    }

    static RedactionPolicy fromEnvironment() {
        List<String> names = new ArrayList<>(DEFAULT_NAMES);
        names.addAll(readList(System.getenv("BEHAVIORDIFF_REDACT_NAMES")));
        return new RedactionPolicy(names, readList(System.getenv("BEHAVIORDIFF_REDACT_TYPES")),
            readList(System.getenv("BEHAVIORDIFF_REDACT_PATHS")));
    }

    boolean sensitiveName(String value) {
        String normalized = value.toLowerCase(Locale.ROOT);
        return names.stream().anyMatch(normalized::contains);
    }

    boolean digestOnlyType(Class<?> type) {
        String name = type.getName().toLowerCase(Locale.ROOT);
        return types.stream().anyMatch(prefix -> name.equals(prefix) || name.startsWith(prefix + "."));
    }

    boolean digestOnlyPath(String path) {
        if (path == null) return false;
        String normalized = normalizePath(path);
        return paths.stream().anyMatch(prefix -> normalized.equals(prefix)
            || normalized.startsWith(prefix + "/")
            || normalized.endsWith("/" + prefix)
            || normalized.contains("/" + prefix + "/"));
    }

    static boolean credentialContent(String value) {
        return JWT.matcher(value).find() || AWS.matcher(value).find() || PEM.matcher(value).find()
            || BASE64.matcher(value).find();
    }

    private static List<String> readList(String value) {
        List<String> result = new ArrayList<>();
        if (value == null) return result;
        for (String item : value.split("[;,]")) {
            String normalized = item.trim().toLowerCase(Locale.ROOT);
            if (!normalized.isEmpty() && !result.contains(normalized)) result.add(normalizePath(normalized));
        }
        return result;
    }

    private static String normalizePath(String value) {
        String normalized = value.replace('\\', '/');
        while (normalized.startsWith("/")) normalized = normalized.substring(1);
        while (normalized.endsWith("/")) normalized = normalized.substring(0, normalized.length() - 1);
        return normalized;
    }
}