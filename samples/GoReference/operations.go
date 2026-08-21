package goreference

import (
	"sort"
	"strings"
)

func Add(left, right int) int {
	return left + right
}

func Subtract(left, right int) int {
	return left - right
}

func Multiply(left, right int) int {
	return left * right
}

func DivideSafe(numerator, denominator int) (quotient int, ok bool) {
	if denominator == 0 {
		return 0, false
	}
	return numerator / denominator, true
}

func MinMax(values []int) (int, int) {
	minimum, maximum := values[0], values[0]
	for _, value := range values[1:] {
		if value < minimum {
			minimum = value
		}
		if value > maximum {
			maximum = value
		}
	}
	return minimum, maximum
}

func NamedStats(values []int) (sum int, count int) {
	for _, value := range values {
		sum += value
	}
	count = len(values)
	return
}

func MultipleReturns(value int) (int, string, bool) {
	return value * 2, "value", value%2 == 0
}

func Clamp(value, minimum, maximum int) int {
	if value < minimum {
		return minimum
	}
	if value > maximum {
		return maximum
	}
	return value
}

func Factorial(value int) int {
	if value <= 1 {
		return 1
	}
	return value * Factorial(value-1)
}

func Fibonacci(value int) int {
	if value <= 1 {
		return value
	}
	return Fibonacci(value-1) + Fibonacci(value-2)
}

func Concat(values ...string) string {
	return strings.Join(values, ":")
}

func ReverseText(value string) string {
	runes := []rune(value)
	for left, right := 0, len(runes)-1; left < right; left, right = left+1, right-1 {
		runes[left], runes[right] = runes[right], runes[left]
	}
	return string(runes)
}

func WordCount(value string) int {
	return len(strings.Fields(value))
}

func Prefix(value, prefix string) string {
	return prefix + value
}

func CopySlice(values []int) []int {
	return append([]int(nil), values...)
}

func RotateSlice(values []int) []int {
	if len(values) == 0 {
		return nil
	}
	result := append([]int(nil), values[1:]...)
	return append(result, values[0])
}

func ReverseArray(values [4]int) [4]int {
	for left, right := 0, len(values)-1; left < right; left, right = left+1, right-1 {
		values[left], values[right] = values[right], values[left]
	}
	return values
}

func ScaleMap(values map[string]int, factor int) map[string]int {
	result := make(map[string]int, len(values))
	for key, value := range values {
		result[key] = value * factor
	}
	return result
}

func MapKeys(values map[string]int) []string {
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

func MergeMaps(left, right map[string]int) map[string]int {
	result := make(map[string]int, len(left)+len(right))
	for key, value := range left {
		result[key] = value
	}
	for key, value := range right {
		result[key] = value
	}
	return result
}

func PanicNow(message string) {
	panic(message)
}

func RecoverPanic(message string) (recovered string) {
	defer func() {
		if value := recover(); value != nil {
			recovered = value.(string)
		}
	}()
	PanicNow(message)
	return "unreachable"
}

func ApplyFunction(operation func(int) int, value int) int {
	return operation(value)
}

func ExerciseCore(seed int) int {
	values := []int{seed, seed + 1, seed + 2}
	record := NewRecord("core", values)
	renamed := record.Renamed("renamed")
	minimum, maximum := MinMax(values)
	sum, count := NamedStats(values)
	doubled, label, even := MultipleReturns(seed)
	quotient, divided := DivideSafe(Multiply(seed+2, 6), seed+2)
	copyValues := CopySlice(values)
	rotated := RotateSlice(copyValues)
	reversed := ReverseArray([4]int{seed, seed + 1, seed + 2, seed + 3})
	scaled := ScaleMap(map[string]int{"b": seed + 1, "a": seed}, 2)
	keys := MapKeys(scaled)
	merged := MergeMaps(scaled, map[string]int{"c": seed + 2})
	pair := PairValues(Identity(seed), Concat("go", "reference"))
	box := Box[int]{Value: SumNumbers(values)}
	textScore := WordCount(Prefix(ReverseText("flow"), "trace "))
	result := Add(Subtract(maximum, minimum), Clamp(sum, 0, 1000))
	result += ThroughCounter(Counter{Base: seed}, box.Get())
	result += renamed.Total() + doubled + len(label) + len(keys) + len(merged) + len(rotated)
	result += reversed[0] + pair.First + len(pair.Second) + count + quotient + textScore
	if even {
		result++
	}
	if divided {
		result++
	}
	return result
}