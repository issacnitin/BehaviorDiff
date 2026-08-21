package io.behaviordiff.agent;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;

final class ScopeSelectingTransformer implements ClassFileTransformer {
    private final PackageScope scope;

    ScopeSelectingTransformer(PackageScope scope) {
        this.scope = scope;
    }

    @Override
    public byte[] transform(
        ClassLoader loader,
        String className,
        Class<?> classBeingRedefined,
        ProtectionDomain protectionDomain,
        byte[] classfileBuffer) {
        if (!scope.includes(className)) {
            return null;
        }

        return null;
    }
}