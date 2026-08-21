package io.behaviordiff.agent;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

final class ClassRewriterTest {
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
        assertEquals("io/behaviordiff/agent/RewriteFixture.java", completions.get(0).frame.filePath());
        assertEquals("debugInfo", completions.get(0).frame.filePathResolution());
        assertEquals(true, completions.get(0).frame.line() > 0);
    }

    private static Object loadRewrittenFixture() throws Exception {
        String name = RewriteFixture.class.getName();
        String resource = "/" + name.replace('.', '/') + ".class";
        byte[] original;
        try (InputStream stream = RewriteFixture.class.getResourceAsStream(resource)) {
            if (stream == null) {
                throw new IOException("missing fixture class bytes");
            }
            original = stream.readAllBytes();
        }

        byte[] rewritten = new ClassRewriter().rewrite(
            name.replace('.', '/'), original, RewriteFixture.class.getClassLoader());
        ClassLoader loader = new ClassLoader(RewriteFixture.class.getClassLoader()) {
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