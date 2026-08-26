package io.realdiff.agent;

import java.util.List;

final class PackageScope {
    private final List<String> includes;
    private final List<String> excludes;

    PackageScope(AgentOptions options) {
        includes = options.includes();
        excludes = options.excludes();
    }

    boolean includes(String internalClassName) {
        return isIncluded(internalClassName) && !isExcluded(internalClassName);
    }

    boolean isIncluded(String internalClassName) {
        if (internalClassName == null) {
            return false;
        }

        String className = internalClassName.replace('/', '.');
        String packageName = packageName(className);
        return matchesAny(packageName, includes);
    }

    boolean isExcluded(String internalClassName) {
        if (internalClassName == null) {
            return false;
        }
        String className = internalClassName.replace('/', '.');
        return matchesAny(packageName(className), excludes);
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