package goreference

import (
	"os"
	"strings"
	"testing"
	"time"
)

func requireCore(t *testing.T, seed int) {
	t.Helper()
	if result := ExerciseCore(seed); result <= 0 {
		t.Fatalf("core workflow failed: %d", result)
	}
}

func TestArithmeticAndReturns(t *testing.T) {
	t.Parallel()
	requireCore(t, 2)
	if Factorial(5) != 120 || Fibonacci(8) != 21 {
		t.Fatal("recursive arithmetic failed")
	}
	if quotient, ok := DivideSafe(12, 0); quotient != 0 || ok {
		t.Fatal("zero denominator was not reported")
	}
}

func TestRecordsEmbeddingAndFields(t *testing.T) {
	t.Parallel()
	requireCore(t, 3)
	record := NewRecord("record", []int{2, 3, 4})
	if record.Label != "reference" || record.Total() != 9 || record.secret != "private" {
		t.Fatal("embedded or private fields changed")
	}
}

func TestGenericFunctionsAndTypes(t *testing.T) {
	t.Parallel()
	requireCore(t, 4)
	if Identity("generic") != "generic" || SumNumbers([]int64{2, 3, 5}) != 10 {
		t.Fatal("generic operation failed")
	}
	converted := Convert(7, func(value int) string { return Prefix("", string(rune('0'+value))) })
	if converted != "7" || (Box[string]{Value: converted}).Get() != "7" {
		t.Fatal("generic conversion failed")
	}
}

func TestCollectionsAndMapInsertionOrder(t *testing.T) {
	t.Parallel()
	requireCore(t, 5)
	left := map[string][]int{}
	left["third"] = []int{3}
	left["first"] = []int{1}
	left["second"] = []int{2}
	right := map[string][]int{}
	right["second"] = []int{2}
	right["first"] = []int{1}
	right["third"] = []int{3}
	if len(EchoMap(left)) != len(EchoMap(right)) {
		t.Fatal("map behavior changed")
	}
}

func TestGoroutinePropagation(t *testing.T) {
	t.Parallel()
	requireCore(t, 6)
	doubled, tripled := ConcurrentPair(4, 5)
	if doubled != 8 || tripled != 15 {
		t.Fatal("concurrent pair failed")
	}
}

func TestNestedGoroutinePropagation(t *testing.T) {
	t.Parallel()
	requireCore(t, 7)
	if NestedConcurrent(5) != 120 {
		t.Fatal("nested goroutine failed")
	}
}

func TestPanicRecoverAndNamedReturn(t *testing.T) {
	t.Parallel()
	requireCore(t, 8)
	if RecoverPanic("expected panic") != "expected panic" {
		t.Fatal("panic recovery failed")
	}
}

func TestInterfacesAndDynamicBoundaries(t *testing.T) {
	t.Parallel()
	requireCore(t, 9)
	counter := Counter{Base: 10}
	if ThroughCounter(counter, 5) != 15 || counter.Multiply(3) != 30 {
		t.Fatal("concrete wrapper failed")
	}
	if os.Getenv("REALDIFF_TRACE") == "" {
		if ApplyIncrementer(counter, 5) != 15 || ApplyFunction(func(value int) int { return value * 4 }, 3) != 12 {
			t.Fatal("dynamic boundary behavior failed")
		}
	}
}

func TestDigestProofs(t *testing.T) {
	requireCore(t, 10)
	ResetTrapCalls()
	if EchoTrap(MethodTrap{Value: 7}) != 7 || ObservedTrapCalls() != 0 {
		t.Fatal("canonicalization invoked user code")
	}

	firstCycle := &CycleNode{Value: 7}
	firstCycle.Next = firstCycle
	secondCycle := &CycleNode{Value: 7}
	secondCycle.Next = secondCycle
	EchoCycle(firstCycle)
	EchoCycle(secondCycle)

	sharedNode := &CycleNode{Value: 8}
	EchoTopology(Topology{Left: sharedNode, Right: sharedNode})
	EchoTopology(Topology{Left: &CycleNode{Value: 8}, Right: &CycleNode{Value: 8}})

	firstMap := map[string][]int{"third": {3}, "first": {1}, "second": {2}}
	secondMap := map[string][]int{"second": {2}, "first": {1}, "third": {3}}
	EchoMap(firstMap)
	EchoMap(secondMap)

	instant := time.Date(2026, time.August, 22, 12, 34, 56, 789, time.FixedZone("offset", 5*60*60))
	EchoTime(instant)
	EchoTime(instant.UTC())

	EchoBlocked(BlockedEnvelope{Callback: func() int { return 1 }, Channel: make(chan int)})
	deep := &DepthNode{Value: 0}
	cursor := deep
	for index := 1; index < 70; index++ {
		cursor.Next = &DepthNode{Value: index}
		cursor = cursor.Next
	}
	EchoDepth(deep)

	prefix := strings.Repeat("x-", 1200)
	EchoLong(prefix + "left")
	EchoLong(prefix + "right")
	EchoPrivate(PrivateEnvelope{Visible: "visible", hidden: "secret"})

	firstKey := &CycleNode{Value: 1}
	secondKey := &CycleNode{Value: 1}
	firstValue := &CycleNode{Value: 2}
	secondValue := &CycleNode{Value: 2}
	EchoTiedMap(TiedMap{
		Entries: map[*CycleNode]*CycleNode{firstKey: firstValue, secondKey: secondValue},
		Outside: [4]*CycleNode{firstKey, secondKey, firstValue, secondValue},
	})
}

func TestParallelWorkflowIsolation(t *testing.T) {
	t.Parallel()
	requireCore(t, 11)
	doubled, tripled := ConcurrentPair(6, 7)
	if doubled+tripled != 33 {
		t.Fatal("parallel workflow failed")
	}
}
