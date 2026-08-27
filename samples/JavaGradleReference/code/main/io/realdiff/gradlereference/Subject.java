package io.realdiff.gradlereference;

import java.util.ArrayList;
import java.util.List;

public final class Subject {
    public int sum(int left, int right) {
        List<Integer> values = new ArrayList<>();
        values.add(left);
        values.add(right);
        return values.stream().mapToInt(Integer::intValue).sum();
    }
}
