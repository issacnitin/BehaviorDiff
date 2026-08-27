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
		Packages: 2, Files: 3, Functions: 29, Methods: 5, Companions: 34,
		TestRoots: 5, Patched: 31, Skipped: 7, GenericTemplates: 3,
		DirectCalls: 38, GoStatements: 8, Boundaries: 4,
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
	if len(report.GenericTemplates) != 3 || report.Metrics.Skipped != report.Metrics.Boundaries+len(report.GenericTemplates) {
		t.Fatalf("generic template reconciliation failed: templates=%+v metrics=%+v", report.GenericTemplates, report.Metrics)
	}
	for _, template := range report.GenericTemplates {
		if template.SkipReason != "Unobservable" || template.Detail != "Go: GenericTemplate" {
			t.Fatalf("generic template classification mismatch: %+v", template)
		}
	}

	parseGoTree(t, out)
	assertPublicSignaturesPreserved(t, filepath.Join(source, "subject.go"), filepath.Join(out, "subject.go"))
	assertGoldenFragments(t, filepath.Join(out, "subject.go"))
	assertFilesEqual(t, filepath.Join(source, "fixture.txt"), filepath.Join(out, "fixture.txt"))
	assertFileExists(t, filepath.Join(out, "internal", "realdiffrt", "runtime.go"))
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

func TestExcludedFileRegistersMembersWithoutInstrumentation(t *testing.T) {
	source := filepath.Join("..", "..", "testdata", "rewrite")
	out := filepath.Join(t.TempDir(), "rewrite-cache")
	report, err := Rewrite(Options{Source: source, Out: out, Exclude: []string{"subject.go"}})
	if err != nil {
		t.Fatalf("Rewrite failed: %v", err)
	}
	transformed, err := os.ReadFile(filepath.Join(out, "subject.go"))
	if err != nil {
		t.Fatal(err)
	}
	text := string(transformed)
	if !strings.Contains(text, "func __bd_") {
		t.Fatal("excluded source omitted passthrough companions required by transformed callers")
	}
	if strings.Contains(text, ".Enter(") || strings.Contains(text, ".Exit(") {
		t.Fatal("excluded source contains entry or exit instrumentation")
	}
	if !strings.Contains(text, `"ExcludedByScope"`) || !strings.Contains(text, `"Go: ExcludedByScope"`) {
		t.Fatal("excluded source did not register explicit skipped-member metadata")
	}
	if report.Metrics.Skipped == 0 {
		t.Fatal("excluded members were not included in skipped metrics")
	}
	parseGoTree(t, out)
	runGoTest(t, out)
}

func TestRejectsOutputInsideSource(t *testing.T) {
	source := filepath.Join("..", "..", "testdata", "rewrite")
	_, err := Rewrite(Options{Source: source, Out: filepath.Join(source, "cache")})
	if err == nil || !strings.Contains(err.Error(), "must not be inside source") {
		t.Fatalf("expected inside-source rejection, got %v", err)
	}
}

type emittedEvent struct {
	TestID           string  `json:"testId"`
	Method           string  `json:"methodFullName"`
	File             string  `json:"filePath"`
	SourceResolution string  `json:"filePathResolution"`
	Line             int     `json:"line"`
	Depth            int     `json:"callDepth"`
	ParentCallID     *uint64 `json:"parentCallId"`
	CallID           uint64  `json:"callId"`
	Ordinal          int     `json:"ordinal"`
	ArgsDigest       string  `json:"argsDigest"`
	ReturnDigest     *string `json:"returnDigest"`
	ExceptionType    *string `json:"exceptionType"`
	Harness          bool    `json:"isHarness"`
}

type emittedManifestRecord struct {
	Kind                string `json:"kind"`
	Schema              string `json:"schema"`
	Language            string `json:"language"`
	Assembly            string `json:"assembly"`
	Discovery           string `json:"discovery"`
	Method              string `json:"method"`
	Status              string `json:"status"`
	SkipReason          string `json:"skipReason"`
	Detail              string `json:"detail"`
	IsTestRoot          bool   `json:"isTestRoot"`
	File                string `json:"filePath"`
	Line                int    `json:"line"`
	PatchedMembers      int    `json:"patchedMembers"`
	DiscoveredMembers   int    `json:"discoveredMembers"`
	SkippedMembers      int    `json:"skippedMembers"`
	PatchFailedMembers  int    `json:"patchFailedMembers"`
	TracedCalls         int    `json:"tracedCalls"`
	ValuesDigested      int    `json:"valuesDigested"`
	UnreadableFields    int    `json:"unreadableFields"`
	AmbiguousMapEntries int    `json:"ambiguousMapEntries"`
	Enqueued            int    `json:"enqueued"`
	Written             int    `json:"written"`
	Dropped             int    `json:"dropped"`
	Capacity            int    `json:"capacity"`
}

func TestEmitterFixture(t *testing.T) {
	source := filepath.Join("..", "..", "testdata", "rewrite")
	out := filepath.Join(t.TempDir(), "rewrite-cache")
	if _, err := Rewrite(Options{Source: source, Out: out}); err != nil {
		t.Fatalf("Rewrite failed: %v", err)
	}
	traceDirectory := os.Getenv("REALDIFF_GO_EMITTER_OUTPUT")
	if traceDirectory == "" {
		traceDirectory = t.TempDir()
	} else if err := os.MkdirAll(traceDirectory, 0o755); err != nil {
		t.Fatal(err)
	}
	traceBase := filepath.Join(traceDirectory, "run.ndjson")
	command := exec.Command("go", "test", "-count=1", "./...")
	command.Dir = out
	command.Env = append(os.Environ(), "REALDIFF_TRACE="+traceBase)
	output, err := command.CombinedOutput()
	if err != nil {
		t.Fatalf("traced go test failed: %v\n%s", err, output)
	}

	tracePaths, err := filepath.Glob(strings.TrimSuffix(traceBase, ".ndjson") + ".*.ndjson")
	if err != nil {
		t.Fatal(err)
	}
	manifestPaths, err := filepath.Glob(strings.TrimSuffix(traceBase, ".ndjson") + ".*.manifest.ndjson")
	if err != nil {
		t.Fatal(err)
	}
	tracePaths = filterManifestPaths(tracePaths)
	if len(tracePaths) != 1 || len(manifestPaths) != 1 {
		t.Fatalf("process-decorated output mismatch: traces=%v manifests=%v", tracePaths, manifestPaths)
	}
	events := readNDJSON[emittedEvent](t, tracePaths[0])
	records := readNDJSON[emittedManifestRecord](t, manifestPaths[0])
	verifyEmittedEvents(t, events)
	verifyEmittedManifest(t, records, events)
}

func verifyEmittedEvents(t *testing.T, events []emittedEvent) {
	t.Helper()
	byCallID := make(map[uint64]emittedEvent, len(events))
	ordinals := make(map[string][]int)
	rootCount := 0
	returnCaptured := false
	panicCaptured := false
	recoveredTestPassed := false
	goroutineParent := false
	genericCounts := make(map[string]int)
	for _, event := range events {
		if event.CallID == 0 || byCallID[event.CallID].CallID != 0 {
			t.Fatalf("invalid or duplicate call id %d", event.CallID)
		}
		byCallID[event.CallID] = event
		if event.File == "" || strings.Contains(event.File, "\\") || !strings.HasSuffix(event.File, ".go") || event.Line <= 0 || event.SourceResolution != "debugInfo" {
			t.Fatalf("invalid source metadata: %+v", event)
		}
		if event.ArgsDigest == "" {
			t.Fatalf("missing args digest: %+v", event)
		}
		ordinals[event.TestID+"\x00"+event.Method] = append(ordinals[event.TestID+"\x00"+event.Method], event.Ordinal)
		if event.Harness {
			rootCount++
			if event.Depth != 0 || event.ParentCallID != nil || !event.IsTestMethod() {
				t.Fatalf("invalid test root: %+v", event)
			}
			if strings.Contains(event.Method, ".TestPanicRecover(") && event.ExceptionType == nil {
				recoveredTestPassed = true
			}
		}
		if strings.Contains(event.Method, ".One(") && event.ReturnDigest != nil {
			returnCaptured = true
		}
		if strings.Contains(event.Method, ".panicNow(") {
			panicCaptured = event.ExceptionType != nil && event.ReturnDigest == nil
		}
		if strings.Contains(event.Method, ".sendValue(") && event.TestID == "TestGoroutines" && event.ParentCallID != nil && event.Depth > 1 {
			goroutineParent = true
		}
		if strings.Contains(event.Method, ".Identity[") || strings.Contains(event.Method, ".PairValues[") || strings.Contains(event.Method, ".Box[") {
			genericCounts[event.Method]++
		}
	}
	for _, event := range events {
		if event.ParentCallID != nil {
			if _, found := byCallID[*event.ParentCallID]; !found {
				t.Fatalf("orphan parent %d for call %d", *event.ParentCallID, event.CallID)
			}
		} else if event.Depth != 0 {
			t.Fatalf("non-root call has no parent: %+v", event)
		}
	}
	for key, values := range ordinals {
		sort.Ints(values)
		for expected, actual := range values {
			if actual != expected {
				t.Fatalf("ordinal gap for %q: %v", key, values)
			}
		}
	}
	expectedGenericCounts := map[string]int{
		"example.com/rewritefixture.Identity[int](T) T":                                                     2,
		"example.com/rewritefixture.Identity[string](T) T":                                                  1,
		"example.com/rewritefixture.Identity[*example.com/rewritefixture.Counter](T) T":                     1,
		"example.com/rewritefixture.Identity[[]example.com/rewritefixture.Counter](T) T":                    1,
		"example.com/rewritefixture.PairValues[int,string](T,U) example.com/rewritefixture.PairValue[T, U]": 1,
		"example.com/rewritefixture.Box[int].Get() T":                                                       1,
		"example.com/rewritefixture.Box[string].Get() T":                                                    1,
	}
	if !reflect.DeepEqual(genericCounts, expectedGenericCounts) {
		t.Fatalf("generic event identities mismatch: got %v, want %v", genericCounts, expectedGenericCounts)
	}
	if rootCount != 5 || !returnCaptured || !panicCaptured || !recoveredTestPassed || !goroutineParent {
		t.Fatalf("event proof failed: roots=%d return=%t panic=%t recovered=%t goroutineParent=%t", rootCount, returnCaptured, panicCaptured, recoveredTestPassed, goroutineParent)
	}
}

func (event emittedEvent) IsTestMethod() bool {
	return strings.Contains(event.Method, ".Test")
}

func verifyEmittedManifest(t *testing.T, records []emittedManifestRecord, events []emittedEvent) {
	t.Helper()
	modules := make(map[string]emittedManifestRecord)
	members := make(map[string]emittedManifestRecord)
	var run, digest, writer emittedManifestRecord
	for _, record := range records {
		switch record.Kind {
		case "run":
			run = record
		case "assembly":
			modules[record.Assembly] = record
		case "member":
			members[record.Method] = record
		case "digest":
			digest = record
		case "writer":
			writer = record
		default:
			t.Fatalf("unknown manifest kind %q", record.Kind)
		}
	}
	if run.Schema != "realdiff.trace/1" || run.Language != "go" {
		t.Fatalf("run metadata mismatch: %+v", run)
	}
	patched, skipped, traced := 0, 0, 0
	for _, module := range modules {
		if module.Discovery != "GoAstRewrite" || module.PatchFailedMembers != 0 || module.DiscoveredMembers != module.PatchedMembers+module.SkippedMembers {
			t.Fatalf("module reconciliation failed: %+v", module)
		}
		patched += module.PatchedMembers
		skipped += module.SkippedMembers
		traced += module.TracedCalls
	}
	if len(modules) != 2 || len(members) != 45 || patched != 38 || skipped != 7 || traced != len(events) {
		t.Fatalf("manifest totals mismatch: modules=%d members=%d patched=%d skipped=%d traced=%d events=%d", len(modules), len(members), patched, skipped, traced, len(events))
	}
	testRoots := 0
	templateCount := 0
	for _, member := range members {
		if member.File == "" || member.Line <= 0 {
			t.Fatalf("member source identity missing: %+v", member)
		}
		if member.IsTestRoot {
			testRoots++
		}
		if member.Status == "Skipped" {
			if member.SkipReason == "Unobservable" && member.Detail == "Go: GenericTemplate" {
				templateCount++
			} else if member.SkipReason != "UnsupportedShape" && member.SkipReason != "DeclaredExternally" {
				t.Fatalf("non-neutral skip: %+v", member)
			}
		}
	}
	for _, event := range events {
		member, found := members[event.Method]
		if !found || member.Status != "Patched" {
			t.Fatalf("event/member join failed for %q", event.Method)
		}
	}
	if testRoots != 5 || templateCount != 3 || digest.ValuesDigested <= 0 || digest.UnreadableFields <= 0 || writer.Enqueued != len(events) || writer.Written != len(events) || writer.Dropped != 0 || writer.Capacity <= 0 {
		t.Fatalf("counter proof failed: roots=%d values=%d unreadable=%d writer=%+v events=%d", testRoots, digest.ValuesDigested, digest.UnreadableFields, writer, len(events))
	}
	t.Logf("GO_EMITTER_SUMMARY events=%d members=%d modules=%d patched=%d skipped=%d roots=%d values=%d unreadableFields=%d ambiguousMapEntries=%d enqueued=%d written=%d dropped=%d",
		len(events), len(members), len(modules), patched, skipped, testRoots, digest.ValuesDigested, digest.UnreadableFields, digest.AmbiguousMapEntries, writer.Enqueued, writer.Written, writer.Dropped)
}

func filterManifestPaths(paths []string) []string {
	filtered := make([]string, 0, len(paths))
	for _, path := range paths {
		if !strings.HasSuffix(path, ".manifest.ndjson") {
			filtered = append(filtered, path)
		}
	}
	return filtered
}

func readNDJSON[T any](t *testing.T, path string) []T {
	t.Helper()
	file, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer file.Close()
	values := make([]T, 0)
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		if scanner.Text() == "" {
			continue
		}
		var value T
		if err := json.Unmarshal(scanner.Bytes(), &value); err != nil {
			t.Fatalf("parse %s: %v", path, err)
		}
		values = append(values, value)
	}
	if err := scanner.Err(); err != nil {
		t.Fatal(err)
	}
	return values
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
