package io.behaviordiff.agent;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;

import java.nio.file.Files;
import java.nio.file.Path;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

final class JavaSourceResolverTest {
    @TempDir
    Path repositoryRoot;

    @Test
    void resolvesMainClassesAgainstRepositoryRoot() throws Exception {
        Path output = repositoryRoot.resolve("samples/JavaReference/target/classes");
        Path source = repositoryRoot.resolve(
            "samples/JavaReference/src/main/java/io/behaviordiff/reference/Subject.java");
        Files.createDirectories(output);
        Files.createDirectories(source.getParent());
        Files.writeString(source, "class Subject {}");

        String resolved = new JavaSourceResolver(repositoryRoot).resolve(
            output, "io/behaviordiff/reference/Subject", "Subject.java");

        assertEquals("samples/JavaReference/src/main/java/io/behaviordiff/reference/Subject.java", resolved);
        assertFalse(resolved.contains("\\"));
    }

    @Test
    void resolvesTestClassesAgainstRepositoryRoot() throws Exception {
        Path output = repositoryRoot.resolve("samples/JavaReference/target/test-classes");
        Path source = repositoryRoot.resolve(
            "samples/JavaReference/src/test/java/io/behaviordiff/reference/SubjectTest.java");
        Files.createDirectories(output);
        Files.createDirectories(source.getParent());
        Files.writeString(source, "class SubjectTest {}");

        assertEquals(
            "samples/JavaReference/src/test/java/io/behaviordiff/reference/SubjectTest.java",
            new JavaSourceResolver(repositoryRoot).resolve(
                output, "io/behaviordiff/reference/SubjectTest", "SubjectTest.java"));
    }

    @Test
    void leavesMissingCandidateUnresolved() throws Exception {
        Path output = repositoryRoot.resolve("samples/JavaReference/target/classes");
        Files.createDirectories(output);

        assertNull(new JavaSourceResolver(repositoryRoot).resolve(
            output, "io/behaviordiff/reference/Subject", "Subject.java"));
    }
}