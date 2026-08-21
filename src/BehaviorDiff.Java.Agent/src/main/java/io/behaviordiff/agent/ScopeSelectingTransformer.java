package io.behaviordiff.agent;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.IllegalClassFormatException;
import java.security.ProtectionDomain;

final class ScopeSelectingTransformer implements ClassFileTransformer {
    private final PackageScope scope;
    private final ClassRewriter rewriter;

    ScopeSelectingTransformer(PackageScope scope) {
        this.scope = scope;
        rewriter = new ClassRewriter();
    }

    @Override
    public byte[] transform(
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) throws IllegalClassFormatException {
        if (!scope.includes(className)) {
            return null;
        }

        return rewriter.rewrite(className, classfileBuffer, loader);
    }
}