package goreference

import (
	"encoding/json"
	"sync/atomic"
	"time"
)

type CycleNode struct {
	Value int
	Next  *CycleNode
}

type Topology struct {
	Left  *CycleNode
	Right *CycleNode
}

type TiedMap struct {
	Entries map[*CycleNode]*CycleNode
	Outside [4]*CycleNode
}

type DepthNode struct {
	Value int
	Next  *DepthNode
}

type PrivateEnvelope struct {
	Visible string
	hidden  string
}

type BlockedEnvelope struct {
	Callback func() int
	Channel  chan int
}

type MethodTrap struct {
	Value int
}

var trapCalls atomic.Int32

func (trap MethodTrap) String() string {
	trapCalls.Add(1)
	return "method trap"
}

func (trap MethodTrap) MarshalJSON() ([]byte, error) {
	trapCalls.Add(1)
	return json.Marshal(trap.Value)
}

func ResetTrapCalls() {
	trapCalls.Store(0)
}

func ObservedTrapCalls() int32 {
	return trapCalls.Load()
}

func EchoTrap(value MethodTrap) int {
	return value.Value
}

func EchoCycle(value *CycleNode) *CycleNode {
	return value
}

func EchoTopology(value Topology) Topology {
	return value
}

func EchoMap(value map[string][]int) map[string][]int {
	return value
}

func EchoTime(value time.Time) time.Time {
	return value
}

func EchoBlocked(value BlockedEnvelope) bool {
	return value.Callback != nil && value.Channel != nil
}

func EchoDepth(value *DepthNode) *DepthNode {
	return value
}

func EchoLong(value string) int {
	return len(value)
}

func EchoPrivate(value PrivateEnvelope) string {
	return value.Visible
}

func EchoTiedMap(value TiedMap) int {
	return len(value.Entries)
}