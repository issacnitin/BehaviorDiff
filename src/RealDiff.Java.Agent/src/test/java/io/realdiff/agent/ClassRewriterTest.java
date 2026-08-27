package io.realdiff.agent;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

final class ClassRewriterTest {
    @TempDir
    Path repositoryRoot;

    @AfterEach
    void resetSink() {
        RuntimeHooks.setSink(null);
    }

    @Test
    void emitsOnceForNormalAndExceptionalExits() throws Exception {
        List<Completion> completions = new ArrayList<>();
        RuntimeHooks.setSink((frame, returnValue, throwable) ->
            completions.add(new Completion(frame, returnValue, throwable)));
        Object fixture = loadRewrittenFixture();
        assertEquals(1, completions.size());
        assertNull(completions.get(0).returnValue);
        assertNull(completions.get(0).throwable);
        completions.clear();

        assertEquals(4, invoke(fixture, "normal", new Class<?>[] { int.class }, 3));
        assertEquals(6L, invoke(fixture, "wide", new Class<?>[] { long.class }, 5L));
        assertEquals("x!", invoke(fixture, "reference", new Class<?>[] { String.class }, "x"));
        assertNull(invoke(fixture, "noValue", new Class<?>[0]));
        InvocationTargetException explicit = assertThrows(
            InvocationTargetException.class,
            () -> invoke(fixture, "explicitThrow", new Class<?>[0]));
        InvocationTargetException implicit = assertThrows(
            InvocationTargetException.class,
            () -> invoke(fixture, "implicitThrow", new Class<?>[] { String.class }, new Object[] { null }));

        assertInstanceOf(IllegalArgumentException.class, explicit.getCause());
        assertInstanceOf(NullPointerException.class, implicit.getCause());
        assertEquals(6, completions.size());
        assertEquals(4, completions.get(0).returnValue);
        assertEquals(6L, completions.get(1).returnValue);
        assertEquals("x!", completions.get(2).returnValue);
        assertNull(completions.get(3).returnValue);
        assertNull(completions.get(4).returnValue);
        assertInstanceOf(IllegalArgumentException.class, completions.get(4).throwable);
        assertNull(completions.get(5).returnValue);
        assertInstanceOf(NullPointerException.class, completions.get(5).throwable);
        assertEquals(3, completions.get(0).frame.arguments()[0]);
        assertEquals("io/realdiff/agent/RewriteFixture.java", completions.get(0).frame.filePath());
        assertEquals("debugInfo", completions.get(0).frame.filePathResolution());
        assertEquals(true, completions.get(0).frame.line() > 0);
    }

    @Test
    void derivesTestCorrelationFromRootSubtrees() throws Exception {
        List<Completion> completions = new ArrayList<>();
        RuntimeHooks.setSink((frame, returnValue, throwable) ->
            completions.add(new Completion(frame, returnValue, throwable)));
        Object fixture = loadRewritten(CorrelationFixture.class);
        assertEquals(0, completions.size());
        completions.clear();

        assertEquals(5, invoke(fixture, "first", new Class<?>[0]));
        assertEquals(7, invoke(fixture, "second", new Class<?>[0]));

        long runnerCount = java.util.Arrays.stream(CorrelationFixture.class.getDeclaredMethods())
            .filter(method -> method.isAnnotationPresent(Test.class))
            .count();
        List<CallFrame> roots = completions.stream()
            .map(completion -> completion.frame)
            .filter(CallFrame::isTestRoot)
            .collect(java.util.stream.Collectors.toList());
        assertEquals(runnerCount, roots.size());
        assertEquals(2, roots.stream().map(CallFrame::testId).distinct().count());

        for (Completion completion : completions) {
            CallFrame frame = completion.frame;
            assertEquals(true, frame.isHarness());
            assertEquals(true, frame.testId().contains("first") || frame.testId().contains("second"));
            if (frame.isTestRoot()) {
                assertEquals(0, frame.callDepth());
                assertEquals(0, frame.parentCallId());
            } else {
                assertEquals(true, frame.callDepth() > 0);
                assertEquals(true, frame.parentCallId() > 0);
            }
        }
    }

    @Test
    void configuredRepositoryRootEmitsResolvedTestSource() throws Exception {
        Path output = repositoryRoot.resolve("module/target/test-classes");
        Path source = repositoryRoot.resolve(
            "module/src/test/java/io/realdiff/agent/RewriteFixture.java");
        Files.createDirectories(output);
        Files.createDirectories(source.getParent());
        Files.writeString(source, "class RewriteFixture {}");

        List<Completion> completions = new ArrayList<>();
        RuntimeHooks.setSink((frame, returnValue, throwable) ->
            completions.add(new Completion(frame, returnValue, throwable)));
        Object fixture = loadRewritten(
            RewriteFixture.class, output, new JavaSourceResolver(repositoryRoot));
        completions.clear();

        assertEquals(4, invoke(fixture, "normal", new Class<?>[] { int.class }, 3));
        assertEquals(
            "module/src/test/java/io/realdiff/agent/RewriteFixture.java",
            completions.get(0).frame.filePath());
        assertEquals("debugInfo", completions.get(0).frame.filePathResolution());
    }

    @Test
    void configuredRepositoryRootLeavesMissingSourceUnresolved() throws Exception {
        Path output = repositoryRoot.resolve("module/target/test-classes");
        Files.createDirectories(output);
        List<Completion> completions = new ArrayList<>();
        RuntimeHooks.setSink((frame, returnValue, throwable) ->
            completions.add(new Completion(frame, returnValue, throwable)));
        Object fixture = loadRewritten(
            RewriteFixture.class, output, new JavaSourceResolver(repositoryRoot));
        completions.clear();

        assertEquals(4, invoke(fixture, "normal", new Class<?>[] { int.class }, 3));
        assertNull(completions.get(0).frame.filePath());
        assertEquals("unresolved", completions.get(0).frame.filePathResolution());
    }

    @Test
    void emitsCompletableFutureOnlyAfterSettlement() throws Exception {
        List<Completion> completions = new java.util.concurrent.CopyOnWriteArrayList<>();
        CountDownLatch settled = new CountDownLatch(2);
        RuntimeHooks.setSink((frame, returnValue, throwable) -> {
            if (frame.methodFullName().contains("passThrough")) {
                completions.add(new Completion(frame, returnValue, throwable));
                settled.countDown();
            }
        });
        Object fixture = loadRewritten(AsyncFixture.class);

        CompletableFuture<String> success = new CompletableFuture<>();
        CompletableFuture<String> failure = new CompletableFuture<>();
        assertEquals(success, invoke(
            fixture,
            "passThrough",
            new Class<?>[] { CompletableFuture.class },
            success));
        assertEquals(failure, invoke(
            fixture,
            "passThrough",
            new Class<?>[] { CompletableFuture.class },
            failure));
        assertEquals(0, completions.size());

        success.complete("done");
        failure.completeExceptionally(new IllegalStateException("failed"));
        assertEquals(true, settled.await(5, TimeUnit.SECONDS));
        assertEquals(2, completions.size());
        Completion successful = completions.stream()
            .filter(completion -> completion.throwable == null)
            .findFirst()
            .orElseThrow();
        Completion failed = completions.stream()
            .filter(completion -> completion.throwable != null)
            .findFirst()
            .orElseThrow();
        assertEquals("done", successful.returnValue);
        assertInstanceOf(IllegalStateException.class, failed.throwable);
        assertNull(failed.returnValue);
    }

    @Test
    void tracerFailureCannotCorruptTheApplicationCallStack() throws Exception {
        RuntimeHooks.setSink((frame, returnValue, throwable) -> {
            throw new IllegalStateException("simulated trace write failure");
        });
        Object fixture = loadRewrittenFixture();

        assertEquals(4, invoke(fixture, "normal", new Class<?>[] { int.class }, 3));
        assertEquals(5, invoke(fixture, "normal", new Class<?>[] { int.class }, 4));
    }

    @Test
    void skippedInnerCompletionCannotCorruptTheApplicationCallStack() {
        CallFrame outer = enter("outer");
        enter("inner");

        RuntimeHooks.exit(outer, null, null);

        CallFrame recovered = enter("recovered");
        assertEquals(0, recovered.callDepth());
        assertEquals(0, recovered.parentCallId());
        RuntimeHooks.exit(recovered, null, null);
    }

    @Test
    void traceOverflowWritesOnlyCompleteEventsAndAccountsForDrops() throws Exception {
        Path configured = repositoryRoot.resolve("run.ndjson");
        TraceSession session = TraceSession.start(configured.toString(), 1);
        RuntimeHooks.initialize(session);

        CallFrame retained = enter("retained");
        RuntimeHooks.exit(retained, null, null);
        CallFrame dropped = enter("dropped");
        assertNull(dropped.argumentsDigest());
        RuntimeHooks.exit(dropped, null, null);
        session.close();

        Path trace = Files.list(repositoryRoot)
            .filter(path -> path.getFileName().toString().matches("run\\.\\d+\\.ndjson"))
            .findFirst()
            .orElseThrow();
        Path manifest = Files.list(repositoryRoot)
            .filter(path -> path.getFileName().toString().matches("run\\.\\d+\\.manifest\\.ndjson"))
            .findFirst()
            .orElseThrow();
        List<String> events = Files.readAllLines(trace);
        assertEquals(1, events.size());
        assertTrue(events.get(0).startsWith("{\"testId\":"));
        String manifestText = Files.readString(manifest);
        assertTrue(manifestText.contains(
            "\"kind\":\"writer\",\"enqueued\":2,\"written\":1,\"dropped\":1,\"capacity\":1"));
    }

    @Test
    void classInitializerIsAnExplicitSkippedManifestMember() throws Exception {
        Path output = repositoryRoot.resolve("module/target/test-classes");
        Path source = repositoryRoot.resolve("module/src/test/java/io/realdiff/agent/RewriteFixture.java");
        Files.createDirectories(output);
        Files.createDirectories(source.getParent());
        Files.writeString(source, "class RewriteFixture {}\n");
        String name = RewriteFixture.class.getName();
        String resource = "/" + name.replace('.', '/') + ".class";
        byte[] bytes;
        try (InputStream stream = RewriteFixture.class.getResourceAsStream(resource)) {
            bytes = stream.readAllBytes();
        }
        TraceSession session = TraceSession.start(repositoryRoot.resolve("run.ndjson").toString());
        session.registerClass(
            "test-classes",
            name.replace('.', '/'),
            bytes,
            false,
            output,
            new JavaSourceResolver(repositoryRoot));
        session.close();

        Path manifest = Files.list(repositoryRoot)
            .filter(path -> path.getFileName().toString().matches("run\\.\\d+\\.manifest\\.ndjson"))
            .findFirst()
            .orElseThrow();
        String initializer = Files.readAllLines(manifest).stream()
            .filter(line -> line.contains(".<clinit>"))
            .findFirst()
            .orElseThrow();
        assertTrue(initializer.contains("\"skipReason\":\"Unobservable\""));
        assertTrue(initializer.contains("\"detail\":\"Java: ClassInitializer\""));
        assertTrue(initializer.contains("\"filePath\":\"module/src/test/java/io/realdiff/agent/RewriteFixture.java\""));
        assertTrue(initializer.matches(".*\\\"line\\\":[1-9][0-9]*.*"));
    }

    private static CallFrame enter(String methodFullName) {
        return RuntimeHooks.enter(
            methodFullName,
            new Object[0],
            new String[0],
            null,
            "unresolved",
            0,
            false,
            false,
            true,
            "test");
    }

    private static Object loadRewrittenFixture() throws Exception {
        return loadRewritten(RewriteFixture.class);
    }

    private static Object loadRewritten(Class<?> fixtureClass) throws Exception {
        return loadRewritten(fixtureClass, null, null);
    }

    private static Object loadRewritten(
        Class<?> fixtureClass,
        Path outputLocation,
        JavaSourceResolver sourceResolver) throws Exception {
        String name = fixtureClass.getName();
        String resource = "/" + name.replace('.', '/') + ".class";
        byte[] original;
        try (InputStream stream = fixtureClass.getResourceAsStream(resource)) {
            if (stream == null) {
                throw new IOException("missing fixture class bytes");
            }
            original = stream.readAllBytes();
        }

        byte[] rewritten = sourceResolver == null
            ? new ClassRewriter().rewrite(name.replace('.', '/'), original, fixtureClass.getClassLoader())
            : new ClassRewriter().rewrite(
                name.replace('.', '/'),
                original,
                fixtureClass.getClassLoader(),
                "test-classes",
                outputLocation,
                sourceResolver);
        ClassLoader loader = new ClassLoader(fixtureClass.getClassLoader()) {
            @Override
            protected Class<?> loadClass(String requestedName, boolean resolve) throws ClassNotFoundException {
                if (!requestedName.equals(name)) {
                    return super.loadClass(requestedName, resolve);
                }
                Class<?> defined = findLoadedClass(requestedName);
                if (defined == null) {
                    defined = defineClass(requestedName, rewritten, 0, rewritten.length);
                }
                if (resolve) {
                    resolveClass(defined);
                }
                return defined;
            }
        };
        Class<?> type = Class.forName(name, true, loader);
        var constructor = type.getDeclaredConstructor();
        constructor.setAccessible(true);
        return constructor.newInstance();
    }

    private static Object invoke(Object target, String name, Class<?>[] parameterTypes, Object... arguments)
        throws Exception {
        Method method = target.getClass().getDeclaredMethod(name, parameterTypes);
        method.setAccessible(true);
        return method.invoke(target, arguments);
    }

    private static final class Completion {
        private final CallFrame frame;
        private final Object returnValue;
        private final Throwable throwable;

        Completion(CallFrame frame, Object returnValue, Throwable throwable) {
            this.frame = frame;
            this.returnValue = returnValue;
            this.throwable = throwable;
        }
    }
}