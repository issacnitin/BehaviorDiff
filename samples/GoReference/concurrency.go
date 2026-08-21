package goreference

func Double(value int) int {
	return value * 2
}

func Triple(value int) int {
	return value * 3
}

func doubleInto(output chan<- int, value int) {
	output <- Double(value)
}

func tripleInto(output chan<- int, value int) {
	output <- Triple(value)
}

func ConcurrentPair(left, right int) (int, int) {
	doubles := make(chan int, 1)
	triples := make(chan int, 1)
	go doubleInto(doubles, left)
	go tripleInto(triples, right)
	return <-doubles, <-triples
}

func nestedLeaf(output chan<- int, value int) {
	output <- Factorial(value)
}

func NestedConcurrent(value int) int {
	output := make(chan int, 1)
	done := make(chan struct{})
	go func() {
		go nestedLeaf(output, value)
		close(done)
	}()
	<-done
	return <-output
}