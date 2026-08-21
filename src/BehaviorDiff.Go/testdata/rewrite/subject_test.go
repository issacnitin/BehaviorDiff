package rewritefixture

import "testing"

func TestBehavior(t *testing.T) {
	value := 4
	NoReturn(&value)
	if value != 5 {
		t.Fatalf("NoReturn changed behavior: %d", value)
	}
	if One(4) != 5 || Named(4) != 8 || Recursive(5) != 120 {
		t.Fatal("return behavior changed")
	}
	one, pairTotal, method := Direct(Counter{Base: 10}, 4)
	if one != 5 || pairTotal != 12 || method != 14 {
		t.Fatalf("direct behavior changed: %d %d %d", one, pairTotal, method)
	}
	if Dynamic(func(input int) int { return input * 3 }, 4) != 12 {
		t.Fatal("dynamic call behavior changed")
	}
	if ThroughInterface(Counter{Base: 2}, 4) != 6 {
		t.Fatal("interface call behavior changed")
	}
	if AcrossPackage(4) != 12 {
		t.Fatal("cross-package call behavior changed")
	}
}

func TestGoroutines(t *testing.T) {
	direct := make(chan int, 1)
	GoDirect(direct, 7)
	if value := <-direct; value != 7 {
		t.Fatalf("direct goroutine changed behavior: %d", value)
	}
	nested := make(chan int, 1)
	GoNested(nested, 7)
	if value := <-nested; value != 8 {
		t.Fatalf("nested goroutine changed behavior: %d", value)
	}
	dynamic := make(chan struct{})
	GoDynamic(func() { close(dynamic) })
	<-dynamic

	variadic := make(chan int, 1)
	GoVariadic(variadic, []int{2, 3, 4})
	if value := <-variadic; value != 9 {
		t.Fatalf("variadic goroutine changed behavior: %d", value)
	}

	contexts := make(chan int, 2)
	GoStatementContexts(contexts, 1)
	if total := <-contexts + <-contexts; total != 3 {
		t.Fatalf("case or labeled goroutine changed behavior: %d", total)
	}
}

func TestGoEvaluationOrder(t *testing.T) {
	events := GoEvaluationOrder()
	if len(events) != 6 {
		t.Fatalf("go evaluation event count changed: %v", events)
	}
	expected := map[string]bool{
		"arg": true, "after-spawn": true, "child:7": true,
		"receiver": true, "after-receiver-spawn": true, "child:11": true,
	}
	positions := make(map[string]int, len(events))
	for index, event := range events {
		if !expected[event] {
			t.Fatalf("unexpected go evaluation event %q: %v", event, events)
		}
		if _, duplicate := positions[event]; duplicate {
			t.Fatalf("duplicate go evaluation event %q: %v", event, events)
		}
		positions[event] = index
	}
	if positions["arg"] != 0 || positions["after-spawn"] <= positions["arg"] || positions["child:7"] <= positions["arg"] {
		t.Fatalf("go argument was not evaluated before spawn continuation: %v", events)
	}
	if positions["receiver"] <= positions["child:7"] || positions["after-receiver-spawn"] <= positions["receiver"] || positions["child:11"] <= positions["receiver"] {
		t.Fatalf("go receiver was not evaluated before spawn continuation: %v", events)
	}
}

func TestPanicRecover(t *testing.T) {
	if message := RecoverPanic(); message != "fixture panic" {
		t.Fatalf("panic/recover changed behavior: %q", message)
	}
}
