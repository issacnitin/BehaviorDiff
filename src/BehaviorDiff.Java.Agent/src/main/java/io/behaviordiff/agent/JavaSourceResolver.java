package io.behaviordiff.agent;

import java.nio.file.Files;
import java.nio.file.Path;

final class JavaSourceResolver {
    private final Path repositoryRoot;

    JavaSourceResolver(Path repositoryRoot) {
        this.repositoryRoot = repositoryRoot == null ? null : repositoryRoot.toAbsolutePath().normalize();
    }

    String resolve(Path outputLocation, String owner, String sourceFile) {
        if (repositoryRoot == null || outputLocation == null || sourceFile == null) {
            return null;
        }

        Path normalizedOutput = outputLocation.toAbsolutePath().normalize();
        Path sourceDirectory;
        if (endsWith(normalizedOutput, "target", "classes")) {
            sourceDirectory = normalizedOutput.getParent().getParent().resolve("src/main/java");
        } else if (endsWith(normalizedOutput, "target", "test-classes")) {
            sourceDirectory = normalizedOutput.getParent().getParent().resolve("src/test/java");
        } else {
            return null;
        }

        int separator = owner.lastIndexOf('/');
        String packageName = separator < 0 ? "" : owner.substring(0, separator);
        Path candidate = packageName.isEmpty()
            ? sourceDirectory.resolve(sourceFile)
            : sourceDirectory.resolve(packageName).resolve(sourceFile);
        candidate = candidate.toAbsolutePath().normalize();
        if (!candidate.startsWith(repositoryRoot) || !Files.isRegularFile(candidate)) {
            return null;
        }

        return repositoryRoot.relativize(candidate).toString().replace('\\', '/');
    }

    private static boolean endsWith(Path path, String parent, String child) {
        int count = path.getNameCount();
        return count >= 2
            && path.getName(count - 2).toString().equals(parent)
            && path.getName(count - 1).toString().equals(child);
    }
}