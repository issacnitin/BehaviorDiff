package prototype_test

import (
	"fmt"
	"sort"
	"strings"
	"testing"

	"github.com/behaviordiff/behaviordiff-go-prototype/correlation"
	"github.com/behaviordiff/behaviordiff-go-prototype/subject"
)

const (
	parallelTests       = 8
	expectedEventsPerID = 28
	expectedEvents      = parallelTests * expectedEventsPerID
)

type audit struct {
	events          int
	tests           int
	wrongTestIDs    int
	parentOrphans   int
	ordinalGaps     int
	depthErrors     int
	duplicates      int
	loss            int
	unexpectedShape int
}

func TestExplicitTokenParallelCorrelation(t *testing.T) {
	recorder := correlation.NewRecorder()
	subjectUnderTest := subject.New(recorder)
	expectedTestIDs := make(map[string]struct{}, parallelTests)

	t.Cleanup(func() {
		result := auditEvents(recorder.Events(), expectedTestIDs)
		fmt.Printf("GO_CORRELATION_SUMMARY events=%d tests=%d wrong_test_ids=%d parent_orphans=%d ordinal_gaps=%d depth_errors=%d duplicates=%d loss=%d\n",
			result.events, result.tests, result.wrongTestIDs, result.parentOrphans, result.ordinalGaps, result.depthErrors, result.duplicates, result.loss)
		if result.events != expectedEvents || result.tests != parallelTests || result.wrongTestIDs != 0 ||
			result.parentOrphans != 0 || result.ordinalGaps != 0 || result.depthErrors != 0 ||
			result.duplicates != 0 || result.loss != 0 || result.unexpectedShape != 0 {
			t.Errorf("correlation audit failed: %+v", result)
		}
	})

	for index := range parallelTests {
		name := fmt.Sprintf("parallel-%02d", index)
		expectedTestIDs[t.Name()+"/"+name] = struct{}{}
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			root := recorder.Enter(nil, t.Name(), "test.root", true)
			defer recorder.Exit(root)
			subject.RunRewritten(subjectUnderTest, root)
		})
	}
}

func TestDirectGoroutineWithoutCapturedToken(t *testing.T) {
	recorder := correlation.NewRecorder()
	subjectUnderTest := subject.New(recorder)
	<-subjectUnderTest.DirectGoroutineWorkWithoutToken()

	events := recorder.Events()
	boundaries := 0
	for _, event := range events {
		if event.TestID == correlation.NoTest && event.ParentCallID == 0 {
			boundaries++
		}
	}
	if len(events) != 1 || boundaries != 1 {
		t.Fatalf("direct goroutine boundary mismatch: events=%d boundaries=%d", len(events), boundaries)
	}
	fmt.Printf("GO_CORRELATION_BOUNDARY uncorrelated=%d test_id=%s method=%s\n", boundaries, events[0].TestID, events[0].Method)
}

func TestCallExitAndRepanic(t *testing.T) {
	recorder := correlation.NewRecorder()
	root := recorder.Enter(nil, t.Name(), "test.root", true)

	func() {
		defer func() {
			if recovered := recover(); recovered != "expected panic" {
				t.Fatalf("panic mismatch: %v", recovered)
			}
		}()
		recorder.Call(root, "subject.panics", func(*correlation.Frame) {
			panic("expected panic")
		})
	}()
	recorder.Exit(root)
	recorder.Exit(root)

	events := recorder.Events()
	if len(events) != 2 {
		t.Fatalf("exit must record each frame exactly once: got %d events", len(events))
	}
}

func auditEvents(events []correlation.Event, expectedTestIDs map[string]struct{}) audit {
	result := audit{events: len(events), tests: len(expectedTestIDs)}
	byCallID := make(map[uint64]correlation.Event, len(events))
	perTestMethods := make(map[string]map[string]int, len(expectedTestIDs))
	ordinals := make(map[string][]uint64)

	for _, event := range events {
		if _, exists := byCallID[event.CallID]; exists {
			result.duplicates++
		} else {
			byCallID[event.CallID] = event
		}
		if _, expected := expectedTestIDs[event.TestID]; !expected {
			result.wrongTestIDs++
		}
		if perTestMethods[event.TestID] == nil {
			perTestMethods[event.TestID] = make(map[string]int)
		}
		perTestMethods[event.TestID][event.Method]++
		key := event.TestID + "\x00" + event.Method
		ordinals[key] = append(ordinals[key], event.Ordinal)
	}

	for _, event := range events {
		if event.ParentCallID == 0 {
			if event.Depth != 0 || event.Method != "test.root" || !event.Harness {
				result.depthErrors++
			}
			continue
		}
		parent, exists := byCallID[event.ParentCallID]
		if !exists {
			result.parentOrphans++
			continue
		}
		if event.Depth != parent.Depth+1 || event.TestID != parent.TestID {
			result.depthErrors++
		}
	}

	for _, values := range ordinals {
		sort.Slice(values, func(left, right int) bool { return values[left] < values[right] })
		for index, value := range values {
			if value != uint64(index+1) {
				result.ordinalGaps++
			}
		}
	}

	expectedMethods := map[string]int{
		"test.root":                1,
		"subject.Run":              1,
		"subject.syncStep":         1,
		"subject.worker.goroutine": 4,
		"subject.worker":           4,
		"subject.nested.goroutine": 4,
		"subject.nestedWork":       4,
		"subject.leaf":             9,
	}
	for testID := range expectedTestIDs {
		methods := perTestMethods[testID]
		actualTotal := 0
		for method, count := range methods {
			actualTotal += count
			if expectedMethods[method] != count {
				result.unexpectedShape++
			}
		}
		for method, expectedCount := range expectedMethods {
			if methods[method] < expectedCount {
				result.loss += expectedCount - methods[method]
			}
		}
		if actualTotal > expectedEventsPerID {
			result.unexpectedShape += actualTotal - expectedEventsPerID
		}
	}
	for testID := range perTestMethods {
		if _, expected := expectedTestIDs[testID]; !expected && !strings.HasPrefix(testID, "ignored:") {
			result.unexpectedShape++
		}
	}
	return result
}
