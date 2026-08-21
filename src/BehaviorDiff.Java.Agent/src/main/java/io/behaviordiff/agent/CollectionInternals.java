package io.behaviordiff.agent;

import java.lang.reflect.Field;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;

final class CollectionInternals {
    static final Field ARRAY_LIST_ELEMENTS = field(ArrayList.class, "elementData");
    static final Field ARRAY_LIST_SIZE = field(ArrayList.class, "size");
    static final Field HASH_MAP_TABLE = field(HashMap.class, "table");
    static final Field HASH_MAP_SIZE = field(HashMap.class, "size");
    static final Field HASH_SET_MAP = field(HashSet.class, "map");
    static final Field HASH_NODE_KEY = field("java.util.HashMap$Node", "key");
    static final Field HASH_NODE_VALUE = field("java.util.HashMap$Node", "value");
    static final Field HASH_NODE_NEXT = field("java.util.HashMap$Node", "next");

    private CollectionInternals() {
    }

    static void requireAccess() {
        Field[] fields = {
            ARRAY_LIST_ELEMENTS,
            ARRAY_LIST_SIZE,
            HASH_MAP_TABLE,
            HASH_MAP_SIZE,
            HASH_SET_MAP,
            HASH_NODE_KEY,
            HASH_NODE_VALUE,
            HASH_NODE_NEXT
        };
        for (Field field : fields) {
            if (!field.trySetAccessible()) {
                throw new IllegalStateException(
                    "BehaviorDiff Java agent requires --add-opens java.base/java.util=ALL-UNNAMED; "
                        + "cannot access " + field.getDeclaringClass().getName() + "." + field.getName());
            }
        }
    }

    private static Field field(Class<?> owner, String name) {
        try {
            return owner.getDeclaredField(name);
        } catch (ReflectiveOperationException exception) {
            throw new IllegalStateException("Unsupported JDK collection layout: " + owner.getName() + "." + name, exception);
        }
    }

    private static Field field(String owner, String name) {
        try {
            return field(Class.forName(owner, false, null), name);
        } catch (ClassNotFoundException exception) {
            throw new IllegalStateException("Unsupported JDK collection layout: " + owner, exception);
        }
    }
}