package subject

import "github.com/realdiff/realdiff-go-prototype/correlation"

type Subject struct {
	recorder *correlation.Recorder
}

func New(recorder *correlation.Recorder) *Subject {
	return &Subject{recorder: recorder}
}

func (s *Subject) Run() {
	s.run(nil)
}

// RunRewritten is the prototype bridge for a rewritten in-scope call site.
func RunRewritten(s *Subject, parent *correlation.Frame) {
	s.run(parent)
}

func (s *Subject) run(parent *correlation.Frame) {
	s.recorder.Call(parent, "subject.Run", func(runFrame *correlation.Frame) {
		s.syncStep(runFrame)

		completions := make([]<-chan struct{}, 0, 4)
		for range 4 {
			captured := runFrame
			completions = append(completions, s.recorder.Go(captured, "subject.worker.goroutine", func(goroutineFrame *correlation.Frame) {
				s.worker(goroutineFrame)
			}))
		}
		for _, completion := range completions {
			<-completion
		}
	})
}

func (s *Subject) syncStep(parent *correlation.Frame) {
	s.recorder.Call(parent, "subject.syncStep", func(syncFrame *correlation.Frame) {
		s.leaf(syncFrame)
	})
}

func (s *Subject) worker(parent *correlation.Frame) {
	s.recorder.Call(parent, "subject.worker", func(workerFrame *correlation.Frame) {
		s.leaf(workerFrame)

		captured := workerFrame
		completion := s.recorder.Go(captured, "subject.nested.goroutine", func(goroutineFrame *correlation.Frame) {
			s.nestedWork(goroutineFrame)
		})
		<-completion
	})
}

func (s *Subject) nestedWork(parent *correlation.Frame) {
	s.recorder.Call(parent, "subject.nestedWork", func(nestedFrame *correlation.Frame) {
		s.leaf(nestedFrame)
	})
}

func (s *Subject) leaf(parent *correlation.Frame) {
	s.recorder.Call(parent, "subject.leaf", func(*correlation.Frame) {})
}

func (s *Subject) DirectGoroutineWorkWithoutToken() <-chan struct{} {
	done := make(chan struct{})
	go func() {
		defer close(done)
		s.leaf(nil)
	}()
	return done
}
