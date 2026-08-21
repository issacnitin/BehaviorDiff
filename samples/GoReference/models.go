package goreference

type Number interface {
	~int | ~int32 | ~int64 | ~float64
}

type Audit struct {
	Label    string
	revision int
}

type Record struct {
	Audit
	Name   string
	Values []int
	secret string
}

type Counter struct {
	Base int
}

type Incrementer interface {
	Add(int) int
}

type Box[T any] struct {
	Value T
}

type Pair[T, U any] struct {
	First  T
	Second U
}

func NewRecord(name string, values []int) Record {
	return Record{
		Audit:  Audit{Label: "reference", revision: 3},
		Name:   name,
		Values: append([]int(nil), values...),
		secret: "private",
	}
}

func (record Record) Total() int {
	total := 0
	for _, value := range record.Values {
		total += value
	}
	return total
}

func (record Record) Renamed(name string) Record {
	record.Name = name
	return record
}

func (counter Counter) Add(value int) int {
	return counter.Base + value
}

func (counter Counter) Multiply(value int) int {
	return counter.Base * value
}

func ThroughCounter(counter Counter, value int) int {
	return counter.Add(value)
}

func ApplyIncrementer(incrementer Incrementer, value int) int {
	return incrementer.Add(value)
}

func Identity[T any](value T) T {
	return value
}

func Convert[T, U any](value T, converter func(T) U) U {
	return converter(value)
}

func SumNumbers[T Number](values []T) T {
	var total T
	for _, value := range values {
		total += value
	}
	return total
}

func PairValues[T, U any](first T, second U) Pair[T, U] {
	return Pair[T, U]{First: first, Second: second}
}

func (box Box[T]) Get() T {
	return box.Value
}