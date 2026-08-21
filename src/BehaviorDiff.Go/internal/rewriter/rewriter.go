package rewriter

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const ReportFileName = "behaviordiff-rewrite-report.json"

type Options struct {
	Source string
	Out    string
}

type Metrics struct {
	Packages     int `json:"packages"`
	Files        int `json:"files"`
	Functions    int `json:"functions"`
	Methods      int `json:"methods"`
	Companions   int `json:"companions"`
	TestRoots    int `json:"testRoots"`
	Patched      int `json:"patched"`
	Skipped      int `json:"skipped"`
	DirectCalls  int `json:"directCalls"`
	GoStatements int `json:"goStatements"`
	Boundaries   int `json:"boundaries"`
}

type Boundary struct {
	File       string `json:"file"`
	Line       int    `json:"line"`
	Column     int    `json:"column"`
	Kind       string `json:"kind"`
	Expression string `json:"expression"`
}

type Report struct {
	Source        string     `json:"source"`
	Output        string     `json:"output"`
	Module        string     `json:"module"`
	RuntimeImport string     `json:"runtimeImport"`
	Metrics       Metrics    `json:"metrics"`
	Boundaries    []Boundary `json:"boundaries"`
}

func Rewrite(options Options) (report Report, err error) {
	source, out, err := validatePaths(options)
	if err != nil {
		return Report{}, err
	}
	modulePath, err := readModulePath(filepath.Join(source, "go.mod"))
	if err != nil {
		return Report{}, err
	}
	runtimeImport := modulePath + "/internal/behaviordiffrt"
	if _, err := os.Stat(filepath.Join(source, "internal", "behaviordiffrt")); err == nil {
		return Report{}, fmt.Errorf("source already contains internal/behaviordiffrt")
	} else if !os.IsNotExist(err) {
		return Report{}, fmt.Errorf("inspect runtime package path: %w", err)
	}
	if err := copyModuleTree(source, out); err != nil {
		return Report{}, err
	}
	completed := false
	defer func() {
		if !completed {
			_ = os.RemoveAll(out)
		}
	}()
	model, err := analyzeModule(source, modulePath)
	if err != nil {
		return Report{}, err
	}
	report = Report{
		Source:        source,
		Output:        out,
		Module:        modulePath,
		RuntimeImport: runtimeImport,
		Boundaries:    make([]Boundary, 0),
	}
	if err := transformModule(model, out, runtimeImport, &report); err != nil {
		return Report{}, err
	}
	if err := writeRuntime(out); err != nil {
		return Report{}, err
	}
	sort.Slice(report.Boundaries, func(i, j int) bool {
		left, right := report.Boundaries[i], report.Boundaries[j]
		if left.File != right.File {
			return left.File < right.File
		}
		if left.Line != right.Line {
			return left.Line < right.Line
		}
		if left.Column != right.Column {
			return left.Column < right.Column
		}
		return left.Kind < right.Kind
	})
	report.Metrics.Boundaries = len(report.Boundaries)
	if err := writeReport(out, report); err != nil {
		return Report{}, err
	}
	completed = true
	return report, nil
}

func validatePaths(options Options) (string, string, error) {
	if strings.TrimSpace(options.Source) == "" || strings.TrimSpace(options.Out) == "" {
		return "", "", fmt.Errorf("source and output are required")
	}
	source, err := filepath.Abs(options.Source)
	if err != nil {
		return "", "", fmt.Errorf("resolve source: %w", err)
	}
	out, err := filepath.Abs(options.Out)
	if err != nil {
		return "", "", fmt.Errorf("resolve output: %w", err)
	}
	source = filepath.Clean(source)
	out = filepath.Clean(out)
	info, err := os.Stat(source)
	if err != nil {
		return "", "", fmt.Errorf("inspect source: %w", err)
	}
	if !info.IsDir() {
		return "", "", fmt.Errorf("source is not a directory: %s", source)
	}
	rel, err := filepath.Rel(source, out)
	if err != nil {
		return "", "", fmt.Errorf("compare source and output: %w", err)
	}
	if rel == "." || (rel != ".." && !strings.HasPrefix(rel, ".."+string(filepath.Separator))) {
		return "", "", fmt.Errorf("output must not be inside source")
	}
	if _, err := os.Stat(out); err == nil {
		return "", "", fmt.Errorf("output already exists: %s", out)
	} else if !os.IsNotExist(err) {
		return "", "", fmt.Errorf("inspect output: %w", err)
	}
	return source, out, nil
}

func writeReport(out string, report Report) error {
	data, err := json.MarshalIndent(report, "", "  ")
	if err != nil {
		return fmt.Errorf("encode rewrite report: %w", err)
	}
	data = append(data, '\n')
	if err := os.WriteFile(filepath.Join(out, ReportFileName), data, 0o644); err != nil {
		return fmt.Errorf("write rewrite report: %w", err)
	}
	return nil
}
