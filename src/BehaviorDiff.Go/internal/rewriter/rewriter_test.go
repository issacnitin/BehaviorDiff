package rewriter

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"go/ast"
	"go/format"
	"go/parser"
	"go/token"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"testing"
)

func TestRewriteFixture(t *testing.T) {
	source := filepath.Join("..", "..", "testdata", "rewrite")
	before := hashTree(t, source)
	out := filepath.Join(t.TempDir(), "rewrite-cache")
	report, err := Rewrite(Options{Source: source, Out: out})
	if err != nil {
		t.Fatalf("Rewrite failed: %v", err)
	}
	after := hashTree(t, source)
	if !reflect.DeepEqual(before, after) {
		t.Fatal("source hashes changed")
	}

	expectedMetrics := Metrics{
		Packages: 2, Files: 2, Functions: 22, Methods: 4, Companions: 26,
		DirectCalls: 15, GoStatements: 8, Boundaries: 4,
	}
	if report.Metrics != expectedMetrics {
		t.Fatalf("metrics mismatch: got %+v, want %+v", report.Metrics, expectedMetrics)
	}
	expectedBoundaryKinds := []string{"cross-package-call", "function-value-call", "function-value-go", "interface-call"}
	actualBoundaryKinds := make([]string, 0, len(report.Boundaries))
	for _, boundary := range report.Boundaries {
		actualBoundaryKinds = append(actualBoundaryKinds, boundary.Kind)
	}
	sort.Strings(actualBoundaryKinds)
	if !reflect.DeepEqual(actualBoundaryKinds, expectedBoundaryKinds) {
		t.Fatalf("boundary kinds mismatch: got %v, want %v", actualBoundaryKinds, expectedBoundaryKinds)
	}

	parseGoTree(t, out)
	assertPublicSignaturesPreserved(t, filepath.Join(source, "subject.go"), filepath.Join(out, "subject.go"))
	assertGoldenFragments(t, filepath.Join(out, "subject.go"))
	assertFilesEqual(t, filepath.Join(source, "fixture.txt"), filepath.Join(out, "fixture.txt"))
	assertFileExists(t, filepath.Join(out, "internal", "behaviordiffrt", "runtime.go"))
	assertFileExists(t, filepath.Join(out, ReportFileName))
	runGoTest(t, out)

	secondOut := filepath.Join(t.TempDir(), "rewrite-cache")
	if _, err := Rewrite(Options{Source: source, Out: secondOut}); err != nil {
		t.Fatalf("second Rewrite failed: %v", err)
	}
	firstOutput, err := os.ReadFile(filepath.Join(out, "subject.go"))
	if err != nil {
		t.Fatal(err)
	}
	secondOutput, err := os.ReadFile(filepath.Join(secondOut, "subject.go"))
	if err != nil {
		t.Fatal(err)
	}
	if string(firstOutput) != string(secondOutput) {
		t.Fatal("transformed Go output is not deterministic")
	}

	reportData, err := os.ReadFile(filepath.Join(out, ReportFileName))
	if err != nil {
		t.Fatal(err)
	}
	var decoded Report
	if err := json.Unmarshal(reportData, &decoded); err != nil {
		t.Fatalf("parse report: %v", err)
	}
	if decoded.Metrics != report.Metrics {
		t.Fatalf("persisted metrics mismatch: got %+v, want %+v", decoded.Metrics, report.Metrics)
	}
}

func TestRejectsOutputInsideSource(t *testing.T) {
	source := filepath.Join("..", "..", "testdata", "rewrite")
	_, err := Rewrite(Options{Source: source, Out: filepath.Join(source, "cache")})
	if err == nil || !strings.Contains(err.Error(), "must not be inside source") {
		t.Fatalf("expected inside-source rejection, got %v", err)
	}
}

func hashTree(t *testing.T, root string) map[string]string {
	t.Helper()
	hashes := make(map[string]string)
	err := filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		rel, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		sum := sha256.Sum256(data)
		hashes[filepath.ToSlash(rel)] = hex.EncodeToString(sum[:])
		return nil
	})
	if err != nil {
		t.Fatalf("hash %s: %v", root, err)
	}
	return hashes
}

func parseGoTree(t *testing.T, root string) {
	t.Helper()
	err := filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
		if err != nil || entry.IsDir() || filepath.Ext(path) != ".go" {
			return err
		}
		data, readErr := os.ReadFile(path)
		if readErr != nil {
			return readErr
		}
		if _, parseErr := parser.ParseFile(token.NewFileSet(), path, data, parser.AllErrors); parseErr != nil {
			return parseErr
		}
		formatted, formatErr := format.Source(data)
		if formatErr != nil {
			return formatErr
		}
		if !bytes.Equal(data, formatted) {
			t.Errorf("transformed Go file is not gofmt-clean: %s", path)
		}
		return nil
	})
	if err != nil {
		t.Fatalf("parse transformed tree: %v", err)
	}
}

func assertPublicSignaturesPreserved(t *testing.T, sourcePath, transformedPath string) {
	t.Helper()
	source := functionSignatures(t, sourcePath, false)
	transformed := functionSignatures(t, transformedPath, true)
	if !reflect.DeepEqual(source, transformed) {
		t.Fatalf("public signatures changed: got %v, want %v", transformed, source)
	}
}

func functionSignatures(t *testing.T, path string, skipCompanions bool) map[string]string {
	t.Helper()
	fset := token.NewFileSet()
	file, err := parser.ParseFile(fset, path, nil, parser.AllErrors)
	if err != nil {
		t.Fatal(err)
	}
	signatures := make(map[string]string)
	for _, declaration := range file.Decls {
		function, ok := declaration.(*ast.FuncDecl)
		if !ok || !ast.IsExported(function.Name.Name) {
			continue
		}
		if skipCompanions && strings.HasPrefix(function.Name.Name, "__bd_") {
			continue
		}
		var signature bytes.Buffer
		if function.Recv != nil {
			if err := format.Node(&signature, fset, function.Recv.List[0].Type); err != nil {
				t.Fatal(err)
			}
			signature.WriteByte(' ')
		}
		if err := format.Node(&signature, fset, function.Type); err != nil {
			t.Fatal(err)
		}
		signatures[function.Name.Name] = signature.String()
	}
	return signatures
}

func assertGoldenFragments(t *testing.T, transformedPath string) {
	t.Helper()
	transformed, err := os.ReadFile(transformedPath)
	if err != nil {
		t.Fatal(err)
	}
	goldenPath := filepath.Join("..", "..", "testdata", "rewrite.golden")
	golden, err := os.Open(goldenPath)
	if err != nil {
		t.Fatal(err)
	}
	defer golden.Close()
	scanner := bufio.NewScanner(golden)
	for scanner.Scan() {
		fragment := scanner.Text()
		if strings.TrimSpace(fragment) == "" {
			continue
		}
		if !strings.Contains(string(transformed), fragment) {
			t.Errorf("transformed output missing golden fragment %q", fragment)
		}
	}
	if err := scanner.Err(); err != nil {
		t.Fatal(err)
	}
}

func runGoTest(t *testing.T, directory string) {
	t.Helper()
	command := exec.Command("go", "test", "./...")
	command.Dir = directory
	output, err := command.CombinedOutput()
	if err != nil {
		t.Fatalf("go test transformed cache failed: %v\n%s", err, output)
	}
}

func assertFileExists(t *testing.T, path string) {
	t.Helper()
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("expected file %s: %v", path, err)
	}
}

func assertFilesEqual(t *testing.T, expectedPath, actualPath string) {
	t.Helper()
	expected, err := os.ReadFile(expectedPath)
	if err != nil {
		t.Fatal(err)
	}
	actual, err := os.ReadFile(actualPath)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(actual, expected) {
		t.Fatalf("content mismatch between %s and %s", expectedPath, actualPath)
	}
}
