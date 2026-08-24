package io.behaviordiff.agent;

import java.lang.reflect.Array;
import java.lang.reflect.Field;
import java.lang.reflect.Modifier;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.temporal.Temporal;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.atomic.LongAdder;

public final class StructuralDigest {
    private static final char[] HEX = "0123456789abcdef".toCharArray();
    private static final int MAX_DEPTH = 6;
    private static final int MAX_ELEMENTS = 16;
    private static final int MAX_VALUES = 1024;
    private static final int RENDERED_CAP = 2000;
    private static final LongAdder VALUES_DIGESTED = new LongAdder();
    private static final LongAdder DEPTH_LIMITED = new LongAdder();
    private static final LongAdder BLOCKLISTED = new LongAdder();
    private static final LongAdder ERRORED = new LongAdder();
    private static final LongAdder RENDERED_TRUNCATED = new LongAdder();
    private static final RedactionPolicy REDACTION = RedactionPolicy.fromEnvironment();

    private StructuralDigest() {
    }

    public static DigestResult compute(Object value) {
        return compute(value, null);
    }

    public static DigestResult compute(Object value, String filePath) {
        return computeArguments(value, filePath, new String[0]);
    }

    public static DigestResult computeArguments(Object value, String filePath, String[] parameterNames) {
        CollectionInternals.requireAccess();
        StringBuilder canonical = new StringBuilder();
        write(value, canonical, new Context(false, new String[0]), 0);
        String text = canonical.toString();
        StringBuilder safe = new StringBuilder();
        if (REDACTION.digestOnlyPath(filePath)) safe.append("<redacted>");
        else write(value, safe, new Context(true, parameterNames), 0);
        String safeText = safe.toString();
        String rendered = safeText.length() <= RENDERED_CAP
            ? safeText
            : safeText.substring(0, RENDERED_CAP) + "<truncated>";
        if (safeText.length() > RENDERED_CAP) {
            RENDERED_TRUNCATED.increment();
        }
        return new DigestResult(hash(text), rendered);
    }

    private static void write(Object value, StringBuilder output, Context context, int depth) {
        if (!context.takeValue()) {
            if (!context.limitWritten) {
                if (!context.redact) DEPTH_LIMITED.increment();
                output.append("<depth:budget>");
                context.limitWritten = true;
            }
            return;
        }
        if (!context.redact) VALUES_DIGESTED.increment();
        if (value == null) {
            output.append("null");
            return;
        }

        Class<?> type = value.getClass();
        if (context.redact && (REDACTION.digestOnlyType(type)
            || value instanceof String && RedactionPolicy.credentialContent((String) value))) {
            output.append("<redacted>");
            return;
        }
        if (writeScalar(value, type, output)) {
            return;
        }

        if (isBlocklisted(type)) {
            if (!context.redact) BLOCKLISTED.increment();
            output.append("<skipped:").append(type.getName()).append('>');
            return;
        }

        if (depth >= MAX_DEPTH) {
            if (!context.redact) DEPTH_LIMITED.increment();
            output.append("<depth:").append(type.getName()).append('>');
            return;
        }

        Integer existing = context.references.get(value);
        if (existing != null) {
            output.append("<ref:").append(existing).append('>');
            return;
        }
        int reference = context.references.size() + 1;
        context.references.put(value, reference);

        if (type.isArray()) {
            writeArray(value, output, context, depth, reference);
        } else if (type == ArrayList.class) {
            writeArrayList((ArrayList<?>) value, output, context, depth, reference);
        } else if (type == HashMap.class) {
            writeHashMap((HashMap<?, ?>) value, output, context, depth, reference, false);
        } else if (type == HashSet.class) {
            writeHashSet((HashSet<?>) value, output, context, depth, reference);
        } else {
            writeFields(value, output, context, depth, reference);
        }
    }

    private static boolean writeScalar(Object value, Class<?> type, StringBuilder output) {
        if (type == String.class) {
            output.append("String:").append(escape((String) value));
            return true;
        }
        if (type == Boolean.class || type == Byte.class || type == Short.class
            || type == Integer.class || type == Long.class || type == Float.class
            || type == Double.class || type == Character.class) {
            output.append(type.getSimpleName()).append(':').append(value);
            return true;
        }
        if (type.isEnum()) {
            output.append("Enum:").append(type.getName()).append(':').append(((Enum<?>) value).name());
            return true;
        }
        if (value instanceof UUID) {
            output.append("<uuid>");
            return true;
        }
        if (value instanceof Temporal || value instanceof java.util.Date) {
            output.append("<time:").append(type.getName()).append('>');
            return true;
        }
        return false;
    }

    private static boolean isBlocklisted(Class<?> type) {
        return ClassLoader.class.isAssignableFrom(type)
            || Thread.class.isAssignableFrom(type)
            || java.io.InputStream.class.isAssignableFrom(type)
            || java.io.OutputStream.class.isAssignableFrom(type)
            || java.util.concurrent.Future.class.isAssignableFrom(type)
            || java.util.concurrent.Executor.class.isAssignableFrom(type)
            || Class.class.isAssignableFrom(type)
            || java.lang.reflect.Member.class.isAssignableFrom(type)
            || java.lang.reflect.Proxy.class.isAssignableFrom(type);
    }

    private static void writeArray(Object value, StringBuilder output, Context context, int depth, int reference) {
        int length = Array.getLength(value);
        output.append("Array#").append(reference).append('[').append(length).append("){ ");
        int count = Math.min(length, MAX_ELEMENTS);
        for (int index = 0; index < count; index++) {
            if (index > 0) {
                output.append(", ");
            }
            if (context.redact && depth == 0 && index < context.parameterNames.length
                && REDACTION.sensitiveName(context.parameterNames[index])) {
                output.append("<redacted>");
            } else {
                write(Array.get(value, index), output, context, depth + 1);
            }
        }
        if (length > count) {
            appendEntryLimit(output, length - count, context);
        }
        output.append(" }");
    }

    private static void writeArrayList(
        ArrayList<?> value,
        StringBuilder output,
        Context context,
        int depth,
        int reference) {
        try {
            Object[] elements = (Object[]) CollectionInternals.ARRAY_LIST_ELEMENTS.get(value);
            int size = CollectionInternals.ARRAY_LIST_SIZE.getInt(value);
            output.append("ShapeRule:ArrayList#").append(reference).append('[').append(size).append("){ ");
            int count = Math.min(size, MAX_ELEMENTS);
            for (int index = 0; index < count; index++) {
                if (index > 0) {
                    output.append(", ");
                }
                write(elements[index], output, context, depth + 1);
            }
            if (size > count) {
                appendEntryLimit(output, size - count, context);
            }
            output.append(" }");
        } catch (ReflectiveOperationException exception) {
            appendError(output, "ArrayList", exception, context);
        }
    }

    private static void writeHashMap(
        HashMap<?, ?> value,
        StringBuilder output,
        Context context,
        int depth,
        int reference,
        boolean keysOnly) {
        try {
            List<Object> nodes = hashNodes(value);
            nodes.sort(Comparator.comparing(StructuralDigest::sortableKey));
            int size = CollectionInternals.HASH_MAP_SIZE.getInt(value);
            output.append(keysOnly ? "ShapeRule:HashSet#" : "ShapeRule:HashMap#")
                .append(reference).append('[').append(size).append("){ ");
            for (int index = 0; index < nodes.size(); index++) {
                if (index > 0) {
                    output.append(", ");
                }
                Object node = nodes.get(index);
                write(CollectionInternals.HASH_NODE_KEY.get(node), output, context, depth + 1);
                if (!keysOnly) {
                    output.append(" => ");
                    write(CollectionInternals.HASH_NODE_VALUE.get(node), output, context, depth + 1);
                }
            }
            output.append(" }");
        } catch (ReflectiveOperationException exception) {
            appendError(output, keysOnly ? "HashSet" : "HashMap", exception, context);
        }
    }

    private static void writeHashSet(
        HashSet<?> value,
        StringBuilder output,
        Context context,
        int depth,
        int reference) {
        try {
            @SuppressWarnings("unchecked")
            HashMap<Object, Object> map = (HashMap<Object, Object>) CollectionInternals.HASH_SET_MAP.get(value);
            writeHashMap(map, output, context, depth, reference, true);
        } catch (ReflectiveOperationException exception) {
            appendError(output, "HashSet", exception, context);
        }
    }

    private static List<Object> hashNodes(HashMap<?, ?> value) throws IllegalAccessException {
        Object[] table = (Object[]) CollectionInternals.HASH_MAP_TABLE.get(value);
        List<Object> nodes = new ArrayList<>();
        if (table == null) {
            return nodes;
        }
        for (Object bucket : table) {
            Object node = bucket;
            while (node != null) {
                nodes.add(node);
                node = CollectionInternals.HASH_NODE_NEXT.get(node);
            }
        }
        return nodes;
    }

    private static String sortableKey(Object node) {
        try {
            StringBuilder key = new StringBuilder();
            write(CollectionInternals.HASH_NODE_KEY.get(node), key, new Context(false, new String[0]), 0);
            return key.toString();
        } catch (IllegalAccessException exception) {
            return "<error:key:" + exception.getClass().getSimpleName() + ">";
        }
    }

    private static void writeFields(Object value, StringBuilder output, Context context, int depth, int reference) {
        Class<?> type = value.getClass();
        output.append("Fields:").append(type.getName()).append('#').append(reference).append("{ ");
        List<Field> fields = instanceFields(type);
        for (int index = 0; index < fields.size(); index++) {
            if (index > 0) {
                output.append(", ");
            }
            Field field = fields.get(index);
            output.append(field.getDeclaringClass().getName()).append('.').append(field.getName()).append('=');
            if (context.redact && REDACTION.sensitiveName(field.getName())) {
                output.append("<redacted>");
                continue;
            }
            try {
                if (!field.trySetAccessible()) {
                    if (!context.redact) ERRORED.increment();
                    output.append("<error:").append(field.getName()).append(":Inaccessible>");
                } else {
                    write(field.get(value), output, context, depth + 1);
                }
            } catch (RuntimeException | IllegalAccessException exception) {
                appendError(output, field.getName(), exception, context);
            }
        }
        output.append(" }");
    }

    private static List<Field> instanceFields(Class<?> type) {
        List<Field> fields = new ArrayList<>();
        for (Class<?> current = type; current != null && current != Object.class; current = current.getSuperclass()) {
            fields.addAll(Arrays.asList(current.getDeclaredFields()));
        }
        fields.removeIf(field -> Modifier.isStatic(field.getModifiers()));
        fields.sort(Comparator.comparing(field -> field.getDeclaringClass().getName() + "." + field.getName()));
        return fields;
    }

    private static void appendError(StringBuilder output, String location, Exception exception, Context context) {
        if (!context.redact) ERRORED.increment();
        output.append("<error:").append(location).append(':')
            .append(exception.getClass().getSimpleName()).append('>');
    }

    private static void appendEntryLimit(StringBuilder output, int omitted, Context context) {
        if (!context.redact) DEPTH_LIMITED.increment();
        output.append(", <depth:entries:").append(omitted).append('>');
    }

    private static String escape(String value) {
        return '"' + value.replace("\\", "\\\\").replace("\"", "\\\"") + '"';
    }

    private static String hash(String value) {
        try {
            byte[] bytes = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
            StringBuilder output = new StringBuilder("sha256:");
            for (byte item : bytes) {
                int valueByte = item & 0xff;
                output.append(HEX[valueByte >>> 4]).append(HEX[valueByte & 0xf]);
            }
            return output.toString();
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("SHA-256 unavailable", exception);
        }
    }

    private static final class Context {
        private final IdentityHashMap<Object, Integer> references = new IdentityHashMap<>();
        private final boolean redact;
        private final String[] parameterNames;
        private int remainingValues = MAX_VALUES;
        private boolean limitWritten;

        private Context(boolean redact, String[] parameterNames) {
            this.redact = redact;
            this.parameterNames = parameterNames;
        }

        private boolean takeValue() {
            return remainingValues-- > 0;
        }
    }

    static long valuesDigested() {
        return VALUES_DIGESTED.sum();
    }

    static long depthLimited() {
        return DEPTH_LIMITED.sum();
    }

    static long blocklisted() {
        return BLOCKLISTED.sum();
    }

    static long errored() {
        return ERRORED.sum();
    }

    static long renderedTruncated() {
        return RENDERED_TRUNCATED.sum();
    }
}