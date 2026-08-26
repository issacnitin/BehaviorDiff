package io.realdiff.agent;

final class Json {
    private static final char[] HEX = "0123456789abcdef".toCharArray();

    private Json() {
    }

    static String string(String value) {
        if (value == null) {
            return "null";
        }
        StringBuilder output = new StringBuilder(value.length() + 2).append('"');
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            switch (character) {
                case '"': output.append("\\\""); break;
                case '\\': output.append("\\\\"); break;
                case '\b': output.append("\\b"); break;
                case '\f': output.append("\\f"); break;
                case '\n': output.append("\\n"); break;
                case '\r': output.append("\\r"); break;
                case '\t': output.append("\\t"); break;
                default:
                    if (character < 0x20) {
                        output.append("\\u")
                            .append(HEX[(character >>> 12) & 0xf])
                            .append(HEX[(character >>> 8) & 0xf])
                            .append(HEX[(character >>> 4) & 0xf])
                            .append(HEX[character & 0xf]);
                    } else {
                        output.append(character);
                    }
            }
        }
        return output.append('"').toString();
    }
}