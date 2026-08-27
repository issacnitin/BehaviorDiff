package io.realdiff.agent;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Collectors;

final class JavaSourceResolver {
    private final Path repositoryRoot;
    private final List<Path> sourceRoots;

    JavaSourceResolver(Path repositoryRoot) {
        this(repositoryRoot, List.of("src/main/java", "src/test/java"));
    }

    JavaSourceResolver(Path repositoryRoot, List<String> sourceRoots) {
        this.repositoryRoot = repositoryRoot == null ? null : repositoryRoot.toAbsolutePath().normalize();
        this.sourceRoots = this.repositoryRoot == null
            ? List.of()
            : sourceRoots.stream()
                .map(root -> this.repositoryRoot.resolve(root).toAbsolutePath().normalize())
                .filter(root -> root.startsWith(this.repositoryRoot))
                .collect(Collectors.toUnmodifiableList());
    }

    String resolve(Path outputLocation, String owner, String sourceFile) {
        if (repositoryRoot == null || outputLocation == null || sourceFile == null) {
            return null;
        }

        int separator = owner.lastIndexOf('/');
        String packageName = separator < 0 ? "" : owner.substring(0, separator);
        List<Path> candidates = new ArrayList<>(sourceRoots);
        Path normalizedOutput = outputLocation.toAbsolutePath().normalize();
        if (endsWith(normalizedOutput, "target", "classes")) {
            candidates.add(normalizedOutput.getParent().getParent().resolve("src/main/java"));
        } else if (endsWith(normalizedOutput, "target", "test-classes")) {
            candidates.add(normalizedOutput.getParent().getParent().resolve("src/test/java"));
        } else if (endsWith(normalizedOutput, "classes", "java", "main")) {
            candidates.add(normalizedOutput.getParent().getParent().getParent().getParent().resolve("src/main/java"));
        } else if (endsWith(normalizedOutput, "classes", "java", "test")) {
            candidates.add(normalizedOutput.getParent().getParent().getParent().getParent().resolve("src/test/java"));
        }
        for (Path sourceDirectory : candidates) {
            Path candidate = packageName.isEmpty()
                ? sourceDirectory.resolve(sourceFile)
                : sourceDirectory.resolve(packageName).resolve(sourceFile);
            candidate = candidate.toAbsolutePath().normalize();
            if (candidate.startsWith(repositoryRoot) && Files.isRegularFile(candidate)) {
                return repositoryRoot.relativize(candidate).toString().replace('\\', '/');
            }
        }
        return null;
    }

    private static boolean endsWith(Path path, String... parts) {
        int count = path.getNameCount();
        if (count < parts.length) {
            return false;
        }
        for (int index = 0; index < parts.length; index++) {
            if (!path.getName(count - parts.length + index).toString().equals(parts[index])) {
                return false;
            }
        }
        return true;
    }
}