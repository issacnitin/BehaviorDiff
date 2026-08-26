package io.realdiff.agent;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.IllegalClassFormatException;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.ProtectionDomain;

final class ScopeSelectingTransformer implements ClassFileTransformer {
    private final PackageScope scope;
    private final ClassRewriter rewriter;
    private final TraceSession traceSession;
    private final JavaSourceResolver sourceResolver;

    ScopeSelectingTransformer(PackageScope scope) {
        this(scope, TraceSession.disabled(), new JavaSourceResolver(null));
    }

    ScopeSelectingTransformer(PackageScope scope, TraceSession traceSession) {
        this(scope, traceSession, new JavaSourceResolver(null));
    }

    ScopeSelectingTransformer(PackageScope scope, TraceSession traceSession, JavaSourceResolver sourceResolver) {
        this.scope = scope;
        rewriter = new ClassRewriter();
        this.traceSession = traceSession;
        this.sourceResolver = sourceResolver;
    }

    @Override
    public byte[] transform(
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) throws IllegalClassFormatException {
        if (!scope.isIncluded(className)) {
            return null;
        }

        Path outputLocation = outputLocation(protectionDomain);
        String module = moduleName(outputLocation);
        if (scope.isExcluded(className)) {
            traceSession.registerClass(
                module, className, classfileBuffer, true, outputLocation, sourceResolver);
            return null;
        }

        byte[] rewritten = rewriter.rewrite(
            className, classfileBuffer, loader, module, outputLocation, sourceResolver);
        traceSession.registerClass(
            module, className, classfileBuffer, false, outputLocation, sourceResolver);
        return rewritten;
    }

    private static Path outputLocation(ProtectionDomain protectionDomain) {
        if (protectionDomain == null || protectionDomain.getCodeSource() == null
            || protectionDomain.getCodeSource().getLocation() == null) {
            return null;
        }
        try {
            return Paths.get(protectionDomain.getCodeSource().getLocation().toURI());
        } catch (java.net.URISyntaxException | IllegalArgumentException exception) {
            return null;
        }
    }

    private static String moduleName(Path outputLocation) {
        Path name = outputLocation == null ? null : outputLocation.getFileName();
        return name == null ? "unnamed" : name.toString();
    }
}