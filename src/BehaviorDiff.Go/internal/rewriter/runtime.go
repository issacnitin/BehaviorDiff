package rewriter

import (
	"fmt"
	"os"
	"path/filepath"
)

const runtimeSource = `package behaviordiffrt

type Frame struct {
	parent *Frame
	method string
}

func Enter(parent *Frame, method string) *Frame {
	return &Frame{parent: parent, method: method}
}

func Exit(_ *Frame, _ []any) {}

func Go(parent *Frame, fn func(*Frame)) {
	captured := parent
	go func() {
		fn(captured)
	}()
}
`

func writeRuntime(out string) error {
	directory := filepath.Join(out, "internal", "behaviordiffrt")
	if err := os.MkdirAll(directory, 0o755); err != nil {
		return fmt.Errorf("create runtime package: %w", err)
	}
	if err := os.WriteFile(filepath.Join(directory, "runtime.go"), []byte(runtimeSource), 0o644); err != nil {
		return fmt.Errorf("write runtime package: %w", err)
	}
	return nil
}
