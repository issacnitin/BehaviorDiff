package io.behaviordiff.agent;

import java.util.List;

final class PackageScope {
    private final List<String> includes;
    private final List<String> excludes;

    PackageScope(AgentOptions options) {
        includes = options.includes();
        excludes = options.excludes();
    }

    boolean includes(String internalClassName) {
        if (internalClassName == null) {
            return false;
        }

        String className = internalClassName.replace('/', '.');
        String packageName = packageName(className);
        return matchesAny(packageName, includes) && !matchesAny(packageName, excludes);
    }

    private static String packageName(String className) {
        int separator = className.lastIndexOf('.');
        return separator < 0 ? "" : className.substring(0, separator);
    }

    private static boolean matchesAny(String packageName, List<String> prefixes) {
        for (String prefix : prefixes) {
            if (packageName.equals(prefix) || packageName.startsWith(prefix + ".")) {
                return true;
            }
        }

        return false;
    }
}