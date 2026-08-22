package io.behaviordiff.agent;

import java.io.BufferedWriter;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.LongAdder;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.Label;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

final class TraceSession {
    private static final TraceSession DISABLED = new TraceSession();

    private final boolean enabled;
    private final Path tracePath;
    private final Path manifestPath;
    private final BufferedWriter traceWriter;
    private final Map<String, ModuleCoverage> modules = new ConcurrentHashMap<>();
    private final LongAdder written = new LongAdder();

    private TraceSession() {
        enabled = false;
        tracePath = null;
        manifestPath = null;
        traceWriter = null;
    }

    private TraceSession(String configuredPath) throws IOException {
        enabled = true;
        long processId = ProcessHandle.current().pid();
        tracePath = decorate(configuredPath, processId, "");
        manifestPath = decorate(configuredPath, processId, ".manifest");
        Path parent = tracePath.toAbsolutePath().getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }
        traceWriter = Files.newBufferedWriter(
            tracePath,
            StandardCharsets.UTF_8,
            StandardOpenOption.CREATE,
            StandardOpenOption.TRUNCATE_EXISTING,
            StandardOpenOption.WRITE);
        Runtime.getRuntime().addShutdownHook(new Thread(this::close, "behaviordiff-java-shutdown"));
    }

    static TraceSession disabled() {
        return DISABLED;
    }

    static TraceSession start(String configuredPath) {
        if (configuredPath == null || configuredPath.trim().isEmpty()) {
            return disabled();
        }
        try {
            return new TraceSession(configuredPath);
        } catch (IOException exception) {
            throw new IllegalStateException("BehaviorDiff cannot open Java trace output: " + configuredPath, exception);
        }
    }

    synchronized void writeEvent(CallFrame frame, Object returnValue, Throwable throwable) {
        if (!enabled) {
            return;
        }
        DigestResult returnDigest = throwable == null && !frame.returnsVoid()
            ? StructuralDigest.compute(returnValue, frame.filePath())
            : null;
        StringBuilder line = new StringBuilder(768)
            .append('{')
            .append("\"testId\":").append(Json.string(frame.testId()))
            .append(",\"methodFullName\":").append(Json.string(frame.methodFullName()));
        if (frame.filePath() != null) {
            line.append(",\"filePath\":").append(Json.string(frame.filePath()));
        }
        line.append(",\"filePathResolution\":").append(Json.string(frame.filePathResolution()))
            .append(",\"line\":").append(frame.line())
            .append(",\"callDepth\":").append(frame.callDepth());
        if (frame.parentCallId() != 0) {
            line.append(",\"parentCallId\":").append(frame.parentCallId());
        }
        line.append(",\"callId\":").append(frame.callId())
            .append(",\"ordinal\":").append(frame.ordinal())
            .append(",\"argsDigest\":").append(Json.string(frame.argumentsDigest().digest()))
            .append(",\"argsRendered\":").append(Json.string(frame.argumentsDigest().rendered()));
        if (returnDigest != null) {
            line.append(",\"returnDigest\":").append(Json.string(returnDigest.digest()))
                .append(",\"returnRendered\":").append(Json.string(returnDigest.rendered()));
        }
        if (throwable != null) {
            line.append(",\"exceptionType\":").append(Json.string(throwable.getClass().getName()));
        }
        line.append(",\"threadId\":").append(frame.threadId());
        if (frame.isHarness()) {
            line.append(",\"isHarness\":true");
        }
        line.append('}');
        try {
            traceWriter.write(line.toString());
            traceWriter.newLine();
            written.increment();
            modules.computeIfAbsent(frame.module(), ModuleCoverage::new).tracedCalls.increment();
        } catch (IOException exception) {
            throw new IllegalStateException("BehaviorDiff failed to write Java trace event", exception);
        }
    }

    void registerClass(
        String moduleName,
        String className,
        byte[] classBytes,
        boolean excluded,
        Path outputLocation,
        JavaSourceResolver sourceResolver) {
        if (!enabled) {
            return;
        }
        ModuleCoverage module = modules.computeIfAbsent(moduleName, ModuleCoverage::new);
        new ClassReader(classBytes).accept(new ClassVisitor(Opcodes.ASM9) {
            private String sourceFile;
            private final Map<String, Integer> lines = new LinkedHashMap<>();
            private final Map<String, Boolean> roots = new LinkedHashMap<>();

            @Override
            public void visitSource(String source, String debug) {
                sourceFile = source;
            }

            @Override
            public MethodVisitor visitMethod(
                int access,
                String name,
                String descriptor,
                String signature,
                String[] exceptions) {
                String key = name + descriptor;
                return new MethodVisitor(Opcodes.ASM9) {
                    @Override
                    public void visitLineNumber(int line, Label start) {
                        lines.merge(key, line, Math::min);
                    }

                    @Override
                    public org.objectweb.asm.AnnotationVisitor visitAnnotation(String annotation, boolean visible) {
                        if (annotation.equals("Lorg/junit/Test;")
                            || annotation.equals("Lorg/junit/jupiter/api/Test;")
                            || annotation.equals("Lorg/junit/jupiter/params/ParameterizedTest;")
                            || annotation.equals("Lorg/testng/annotations/Test;")) {
                            roots.put(key, true);
                            module.testFramework = annotation.contains("testng") ? "TestNG" : "JUnit";
                        }
                        return null;
                    }

                    @Override
                    public void visitEnd() {
                        boolean noBody = (access & (Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0;
                        boolean initializer = name.equals("<clinit>");
                        boolean skipped = excluded || noBody || initializer;
                        String resolvedSourcePath = sourceResolver.resolve(
                            outputLocation, className, sourceFile);
                        String resolution = sourceFile == null || lines.isEmpty()
                            ? "debugInfoMissing"
                            : resolvedSourcePath == null
                                ? "unresolved"
                                : lines.containsKey(key) ? "debugInfo" : "declaringType";
                        String returnKind = Type.getReturnType(descriptor).equals(Type.VOID_TYPE)
                            ? "Void"
                            : Type.getReturnType(descriptor).equals(Type.getType(java.util.concurrent.CompletableFuture.class))
                                ? "CompletableFuture" : "Sync";
                        module.members.add(new MemberCoverage(
                            className.replace('/', '.') + "." + name + descriptor,
                            skipped,
                            excluded ? "ExcludedByScope" : initializer ? "Unobservable" : noBody ? "DeclaredExternally" : null,
                            excluded ? "Java: ExcludedPackage" : initializer ? "Java: ClassInitializer" : noBody ? "Java: NoCode" : null,
                            returnKind,
                            roots.getOrDefault(key, false),
                            resolution));
                    }
                };
            }
        }, ClassReader.SKIP_FRAMES);
    }

    private synchronized void close() {
        if (!enabled) {
            return;
        }
        try {
            traceWriter.flush();
            traceWriter.close();
            try (BufferedWriter manifest = Files.newBufferedWriter(
                manifestPath,
                StandardCharsets.UTF_8,
                StandardOpenOption.CREATE,
                StandardOpenOption.TRUNCATE_EXISTING,
                StandardOpenOption.WRITE)) {
                writeLine(manifest, "{\"kind\":\"run\",\"schema\":\"behaviordiff.trace/1\",\"language\":\"java\"}");
                List<ModuleCoverage> orderedModules = new ArrayList<>(modules.values());
                orderedModules.sort(Comparator.comparing(module -> module.name));
                for (ModuleCoverage module : orderedModules) {
                    module.members.sort(Comparator.comparing(member -> member.method));
                    int patched = (int) module.members.stream().filter(member -> !member.skipped).count();
                    int exact = (int) module.members.stream().filter(member -> !member.skipped && member.resolution.equals("debugInfo")).count();
                    int percent = patched == 0 ? 100 : exact * 100 / patched;
                    writeLine(manifest, "{\"kind\":\"assembly\",\"assembly\":" + Json.string(module.name)
                        + ",\"discovery\":\"JavaAgentTransform\",\"scanned\":true,\"instrumented\":" + (patched > 0)
                        + ",\"patchedMembers\":" + patched + ",\"discoveredMembers\":" + module.members.size()
                        + ",\"skippedMembers\":" + (module.members.size() - patched) + ",\"patchFailedMembers\":0"
                        + ",\"queuedAtMs\":0,\"patchedAtMs\":0,\"tracedCalls\":" + module.tracedCalls.sum()
                        + ",\"membersWithExactSource\":" + exact + ",\"exactSourcePercent\":" + percent
                        + ",\"sourceRule\":\"" + (patched == 0 ? "notApplicable" : "ratio") + "\""
                        + (exact == 0 && patched > 0 ? ",\"sourceUnavailable\":true" : "")
                        + (exact > 0 && exact < patched ? ",\"sourcePartial\":true" : "")
                        + (module.testFramework == null ? "" : ",\"isTestAssembly\":true,\"testFrameworkReference\":" + Json.string(module.testFramework))
                        + "}");
                    for (MemberCoverage member : module.members) {
                        StringBuilder record = new StringBuilder("{\"kind\":\"member\",\"assembly\":")
                            .append(Json.string(module.name)).append(",\"method\":").append(Json.string(member.method))
                            .append(",\"status\":\"").append(member.skipped ? "Skipped" : "Patched").append('"');
                        if (member.reason != null) {
                            record.append(",\"skipReason\":").append(Json.string(member.reason))
                                .append(",\"detail\":").append(Json.string(member.detail));
                        }
                        record.append(",\"returnKind\":").append(Json.string(member.returnKind));
                        if (member.testRoot) {
                            record.append(",\"isTestRoot\":true");
                        }
                        record.append(",\"sourceResolution\":").append(Json.string(member.resolution)).append('}');
                        writeLine(manifest, record.toString());
                    }
                }
                writeLine(manifest, "{\"kind\":\"digest\",\"valuesDigested\":" + StructuralDigest.valuesDigested()
                    + ",\"depthLimited\":" + StructuralDigest.depthLimited() + ",\"blocklisted\":"
                    + StructuralDigest.blocklisted() + ",\"errored\":"
                    + StructuralDigest.errored() + ",\"renderedTruncated\":" + StructuralDigest.renderedTruncated() + "}");
                long count = written.sum();
                writeLine(manifest, "{\"kind\":\"writer\",\"enqueued\":" + count + ",\"written\":" + count
                    + ",\"dropped\":0,\"capacity\":0}");
            }
        } catch (IOException exception) {
            throw new IllegalStateException("BehaviorDiff failed to finalize Java trace", exception);
        }
    }

    private static void writeLine(BufferedWriter writer, String line) throws IOException {
        writer.write(line);
        writer.newLine();
    }

    private static Path decorate(String configuredPath, long processId, String suffix) {
        Path path = Paths.get(configuredPath).toAbsolutePath();
        String fileName = path.getFileName().toString();
        int dot = fileName.lastIndexOf('.');
        String stem = dot < 0 ? fileName : fileName.substring(0, dot);
        String extension = dot < 0 ? "" : fileName.substring(dot);
        return path.resolveSibling(stem + "." + processId + suffix + extension);
    }

    private static final class ModuleCoverage {
        private final String name;
        private final List<MemberCoverage> members = java.util.Collections.synchronizedList(new ArrayList<>());
        private final LongAdder tracedCalls = new LongAdder();
        private volatile String testFramework;

        ModuleCoverage(String name) {
            this.name = name;
        }
    }

    private static final class MemberCoverage {
        private final String method;
        private final boolean skipped;
        private final String reason;
        private final String detail;
        private final String returnKind;
        private final boolean testRoot;
        private final String resolution;

        MemberCoverage(
            String method,
            boolean skipped,
            String reason,
            String detail,
            String returnKind,
            boolean testRoot,
            String resolution) {
            this.method = method;
            this.skipped = skipped;
            this.reason = reason;
            this.detail = detail;
            this.returnKind = returnKind;
            this.testRoot = testRoot;
            this.resolution = resolution;
        }
    }
}