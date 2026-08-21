package io.behaviordiff.agent;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.IllegalClassFormatException;
import java.security.ProtectionDomain;

final class ScopeSelectingTransformer implements ClassFileTransformer {
    private final PackageScope scope;
    private final ClassRewriter rewriter;
    private final TraceSession traceSession;

    ScopeSelectingTransformer(PackageScope scope) {
        this(scope, TraceSession.disabled());
    }

    ScopeSelectingTransformer(PackageScope scope, TraceSession traceSession) {
        this.scope = scope;
        rewriter = new ClassRewriter();
        this.traceSession = traceSession;
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

        String module = moduleName(protectionDomain);
        if (scope.isExcluded(className)) {
            traceSession.registerClass(module, className, classfileBuffer, true);
            return null;
        }

        byte[] rewritten = rewriter.rewrite(className, classfileBuffer, loader, module);
        traceSession.registerClass(module, className, classfileBuffer, false);
        return rewritten;
    }

    private static String moduleName(ProtectionDomain protectionDomain) {
        if (protectionDomain == null || protectionDomain.getCodeSource() == null
            || protectionDomain.getCodeSource().getLocation() == null) {
            return "unnamed";
        }
        try {
            java.nio.file.Path path = java.nio.file.Paths.get(protectionDomain.getCodeSource().getLocation().toURI());
            java.nio.file.Path name = path.getFileName();
            return name == null ? "unnamed" : name.toString();
        } catch (java.net.URISyntaxException exception) {
            return "unnamed";
        }
    }
}