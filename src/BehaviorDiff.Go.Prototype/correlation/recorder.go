package correlation

import "sync"

const NoTest = "(no-test)"

type Event struct {
	CallID       uint64
	ParentCallID uint64
	Depth        int
	TestID       string
	Method       string
	Harness      bool
	Ordinal      uint64
}

// Frame is an immutable correlation token. Its fields are intentionally private.
type Frame struct {
	callID       uint64
	parentCallID uint64
	depth        int
	testID       string
	method       string
	harness      bool
	ordinal      uint64
	exit         *frameExit
}

type frameExit struct {
	once sync.Once
}

type ordinalKey struct {
	testID string
	method string
}

type Recorder struct {
	mu         sync.Mutex
	nextCallID uint64
	ordinals   map[ordinalKey]uint64
	events     []Event
}

func NewRecorder() *Recorder {
	return &Recorder{
		nextCallID: 1,
		ordinals:   make(map[ordinalKey]uint64),
	}
}

func (r *Recorder) Enter(parent *Frame, testID, method string, harness bool) *Frame {
	resolvedTestID := testID
	parentCallID := uint64(0)
	depth := 0
	if parent != nil {
		parentCallID = parent.callID
		depth = parent.depth + 1
		if resolvedTestID == "" {
			resolvedTestID = parent.testID
		}
	}
	if resolvedTestID == "" {
		resolvedTestID = NoTest
	}

	r.mu.Lock()
	callID := r.nextCallID
	r.nextCallID++
	key := ordinalKey{testID: resolvedTestID, method: method}
	r.ordinals[key]++
	ordinal := r.ordinals[key]
	r.mu.Unlock()

	return &Frame{
		callID:       callID,
		parentCallID: parentCallID,
		depth:        depth,
		testID:       resolvedTestID,
		method:       method,
		harness:      harness,
		ordinal:      ordinal,
		exit:         &frameExit{},
	}
}

func (r *Recorder) Exit(frame *Frame) {
	if frame == nil {
		return
	}
	frame.exit.once.Do(func() {
		event := Event{
			CallID:       frame.callID,
			ParentCallID: frame.parentCallID,
			Depth:        frame.depth,
			TestID:       frame.testID,
			Method:       frame.method,
			Harness:      frame.harness,
			Ordinal:      frame.ordinal,
		}
		r.mu.Lock()
		r.events = append(r.events, event)
		r.mu.Unlock()
	})
}

func (r *Recorder) Call(parent *Frame, method string, fn func(*Frame)) {
	frame := r.Enter(parent, "", method, false)
	defer func() {
		r.Exit(frame)
		if recovered := recover(); recovered != nil {
			panic(recovered)
		}
	}()
	fn(frame)
}

func (r *Recorder) Go(parent *Frame, method string, fn func(*Frame)) <-chan struct{} {
	var capturedParent *Frame
	if parent != nil {
		captured := *parent
		capturedParent = &captured
	}

	done := make(chan struct{})
	go func() {
		defer close(done)
		r.Call(capturedParent, method, fn)
	}()
	return done
}

func (r *Recorder) Events() []Event {
	r.mu.Lock()
	defer r.mu.Unlock()
	events := make([]Event, len(r.events))
	copy(events, r.events)
	return events
}
