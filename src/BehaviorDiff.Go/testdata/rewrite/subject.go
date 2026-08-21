package rewritefixture

import (
	"strconv"

	"example.com/rewritefixture/helper"
)

var _ = strconv.IntSize

type Counter struct {
	Base int
}

type Incrementer interface {
	Add(int) int
}

type eventLog chan string

func (log eventLog) add(event string) {
	log <- event
}

func (log eventLog) snapshot(count int) []string {
	events := make([]string, 0, count)
	for len(events) < count {
		events = append(events, <-log)
	}
	return events
}

func (log eventLog) recordChild(value int, done chan<- struct{}) {
	log.add("child:" + strconv.Itoa(value))
	close(done)
}

func NoReturn(target *int) {
	*target = *target + 1
}

func One(value int) int {
	return value + 1
}

func Pair(value int) (int, int) {
	return value, value * 2
}

func Named(value int) (double int) {
	double = value * 2
	return
}

func (counter Counter) Add(value int) int {
	return counter.Base + value
}

func Recursive(value int) int {
	if value <= 1 {
		return 1
	}
	return value * Recursive(value-1)
}

func Direct(counter Counter, value int) (int, int, int) {
	one := One(value)
	left, right := Pair(value)
	return one, left + right, counter.Add(value)
}

func sendValue(ch chan<- int, value int) {
	ch <- value
}

func GoDirect(ch chan<- int, value int) {
	go sendValue(ch, value)
}

func GoNested(ch chan<- int, value int) {
	go func() {
		go sendValue(ch, One(value))
	}()
}

func evaluatedArgument(log eventLog) int {
	log.add("arg")
	return 7
}

func evaluatedReceiver(log eventLog) eventLog {
	log.add("receiver")
	return log
}

func GoEvaluationOrder() []string {
	log := make(eventLog, 6)
	argumentDone := make(chan struct{})
	go func(value int) {
		log.recordChild(value, argumentDone)
	}(evaluatedArgument(log))
	log.add("after-spawn")
	<-argumentDone

	receiverDone := make(chan struct{})
	go evaluatedReceiver(log).recordChild(11, receiverDone)
	log.add("after-receiver-spawn")
	<-receiverDone
	return log.snapshot(6)
}

func sendVariadic(ch chan<- int, values ...int) {
	total := 0
	for _, value := range values {
		total += value
	}
	ch <- total
}

func GoVariadic(ch chan<- int, values []int) {
	go sendVariadic(ch, values...)
}

func GoStatementContexts(ch chan<- int, value int) {
	switch value {
	case 1:
		go sendValue(ch, value)
	}
	goto spawn
spawn:
	go sendValue(ch, value+1)
}

func panicNow() {
	panic("fixture panic")
}

func RecoverPanic() (message string) {
	defer func() {
		if recovered := recover(); recovered != nil {
			message = recovered.(string)
		}
	}()
	panicNow()
	return "unreachable"
}

func Dynamic(fn func(int) int, value int) int {
	return fn(value)
}

func ThroughInterface(incrementer Incrementer, value int) int {
	return incrementer.Add(value)
}

func AcrossPackage(value int) int {
	return helper.Triple(value)
}

func GoDynamic(fn func()) {
	go fn()
}
