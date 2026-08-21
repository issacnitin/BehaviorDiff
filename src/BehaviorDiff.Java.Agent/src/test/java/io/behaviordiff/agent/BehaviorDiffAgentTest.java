package io.behaviordiff.agent;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotNull;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Proxy;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.Test;

final class BehaviorDiffAgentTest {
    @Test
    void premainRegistersNonRetransformingTransformer() {
        AtomicReference<ClassFileTransformer> transformer = new AtomicReference<>();
        AtomicBoolean canRetransform = new AtomicBoolean(true);
        Instrumentation instrumentation = (Instrumentation) Proxy.newProxyInstance(
            getClass().getClassLoader(),
            new Class<?>[] { Instrumentation.class },
            (proxy, method, arguments) -> {
                if (method.getName().equals("addTransformer") && arguments.length == 2) {
                    transformer.set((ClassFileTransformer) arguments[0]);
                    canRetransform.set((boolean) arguments[1]);
                }
                return defaultValue(method.getReturnType());
            });

        BehaviorDiffAgent.premain("include=com.example", instrumentation);

        assertNotNull(transformer.get());
        assertInstanceOf(ScopeSelectingTransformer.class, transformer.get());
        assertFalse(canRetransform.get());
    }

    private static Object defaultValue(Class<?> type) {
        if (!type.isPrimitive()) {
            return null;
        }

        if (type == boolean.class) {
            return false;
        }

        if (type == char.class) {
            return '\0';
        }

        return 0;
    }
}