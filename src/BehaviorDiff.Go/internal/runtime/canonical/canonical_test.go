package canonical

import (
	"encoding/json"
	"go/parser"
	"go/token"
	"math"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

type privateRecord struct {
	Readable string
	private  string
}

type privateOnly struct {
	private string
}

type cycleNode struct {
	Value int
	Next  *cycleNode
}

type pair struct {
	Left  *cycleNode
	Right *cycleNode
}

type tiedMapTopology struct {
	Entries map[*cycleNode]*cycleNode
	Outside [4]*cycleNode
}

type slicePair struct {
	Left  []int
	Right []int
}

type zeroSized struct{}

func TestRedactionKeepsRealDigest(t *testing.T) {
	type credential struct {
		Password string
		Result   string
	}
	first := credential{Password: "first-password", Result: "AKIA1234567890ABCDEF"}
	second := credential{Password: "second-password", Result: "AKIAFEDCBA0987654321"}
	options := Options{Redact: true, SensitiveNames: []string{"password"}}

	left := DigestWithOptions(first, options)
	right := DigestWithOptions(second, options)

	if left.SHA256 == right.SHA256 {
		t.Fatal("real secret values did not affect digest")
	}
	if strings.Contains(left.Canonical, "first-password") || strings.Contains(left.Canonical, "AKIA1234567890ABCDEF") {
		t.Fatalf("rendered secret leaked: %s", left.Canonical)
	}
	if !strings.Contains(left.Canonical, "<redacted>") {
		t.Fatalf("redaction marker missing: %s", left.Canonical)
	}
}

type namedFloat32 float32

type float32Holder struct {
	Value namedFloat32
}

type Embedded struct {
	Name string
}

type embedding struct {
	Embedded
	Count int
}

type interfaceHolder struct {
	Value any
}

type Box[T any] struct {
	Value T
}

var (
	stringCalls  atomic.Int32
	marshalCalls atomic.Int32
)

type methodTrap struct {
	Value int
}

func (value methodTrap) String() string {
	stringCalls.Add(1)
	return "invoked"
}

func (value methodTrap) MarshalJSON() ([]byte, error) {
	marshalCalls.Add(1)
	return json.Marshal(value.Value)
}

func TestScalarsPreserveKindsAndExceptionalValues(t *testing.T) {
	values := []any{
		true,
		int(1), int8(1), int16(1), int32(1), int64(1),
		uint(1), uint8(1), uint16(1), uint32(1), uint64(1),
		float32(1), float64(1),
		float64(0), math.Copysign(0, -1),
		math.Inf(1), math.Inf(-1), math.Float64frombits(0x7ff8000000000001),
		complex64(complex(1, -2)), complex128(complex(1, -2)),
		"1",
	}

	digests := make(map[string]struct{}, len(values))
	for _, value := range values {
		result := Digest(value)
		if result.Partial {
			t.Fatalf("scalar %T unexpectedly partial: %s", value, result.Canonical)
		}
		if _, exists := digests[result.SHA256]; exists {
			t.Fatalf("scalar canonical collision for %T: %s", value, result.Canonical)
		}
		digests[result.SHA256] = struct{}{}
	}

	var pointer *int
	if result := Digest(pointer); result.Partial || !strings.Contains(result.Canonical, "(nil)") {
		t.Fatalf("nil pointer was not preserved: %+v", result)
	}
}

func TestUnexportedFieldIsExplicitAndCounted(t *testing.T) {
	result := Digest(privateRecord{Readable: "visible", private: "secret"})
	if !result.Partial {
		t.Fatal("skipping an unexported field must make the digest partial")
	}
	if result.Counters.Unexported != 1 || result.Counters.Blocklisted != 1 {
		t.Fatalf("unexpected counters: %+v", result.Counters)
	}
	if len(result.SkippedUnexported) != 1 || result.SkippedUnexported[0].Field != "private" || result.SkippedUnexported[0].Count != 1 {
		t.Fatalf("unexpected skipped details: %+v", result.SkippedUnexported)
	}
	marker := "<skipped:unexported:" + typeLabel(reflect.TypeOf(privateRecord{})) + ".private>"
	if !strings.Contains(result.Canonical, marker) {
		t.Fatalf("missing explicit marker %q in %s", marker, result.Canonical)
	}
}

func TestReadableDifferenceChangesDigest(t *testing.T) {
	left := Digest(privateRecord{Readable: "left", private: "same"})
	right := Digest(privateRecord{Readable: "right", private: "same"})
	if left.SHA256 == right.SHA256 {
		t.Fatal("readable field difference must change the digest")
	}
}

func TestPrivateOnlyDifferenceIntentionallyCollidesAsPartial(t *testing.T) {
	left := Digest(privateOnly{private: "left"})
	right := Digest(privateOnly{private: "right"})
	if left.SHA256 != right.SHA256 {
		t.Fatal("private-only differences should intentionally canonicalize to the same skipped marker")
	}
	if !left.Partial || !right.Partial {
		t.Fatal("an identical partial digest does not prove value equality")
	}
}

func TestCyclesTerminateWithReferences(t *testing.T) {
	node := &cycleNode{Value: 7}
	node.Next = node
	result := Digest(node)
	if result.Partial || !strings.Contains(result.Canonical, "@ref:1") {
		t.Fatalf("cycle was not represented as a reference: %+v", result)
	}
}

func TestSharedReferencesDifferFromCopies(t *testing.T) {
	sharedNode := &cycleNode{Value: 7}
	shared := Digest(pair{Left: sharedNode, Right: sharedNode})
	copies := Digest(pair{Left: &cycleNode{Value: 7}, Right: &cycleNode{Value: 7}})
	if shared.SHA256 == copies.SHA256 {
		t.Fatal("shared references and equal copies must have different topology")
	}
	if !strings.Contains(shared.Canonical, "@ref:1") {
		t.Fatalf("shared reference missing from %s", shared.Canonical)
	}
}

func TestZeroCapacitySlicesRenderStructurallyWithoutReferenceIdentity(t *testing.T) {
	sharedSlice := make([]int, 0)
	shared := Digest(slicePair{Left: sharedSlice, Right: sharedSlice})
	independent := Digest(slicePair{Left: make([]int, 0), Right: make([]int, 0)})
	if shared.Partial || independent.Partial {
		t.Fatalf("zero-capacity slices unexpectedly partial: %+v, %+v", shared, independent)
	}
	if shared.SHA256 != independent.SHA256 || shared.Canonical != independent.Canonical {
		t.Fatalf("unobservable zero-capacity storage identity affected output:\n%s\n%s", shared.Canonical, independent.Canonical)
	}
	if strings.Contains(shared.Canonical, "@ref:") || strings.Contains(shared.Canonical, "slice:[]int#") {
		t.Fatalf("zero-capacity slice received reference identity: %s", shared.Canonical)
	}
	for iteration := 0; iteration < 100; iteration++ {
		if result := Digest(slicePair{Left: make([]int, 0), Right: make([]int, 0)}); result.SHA256 != independent.SHA256 {
			t.Fatalf("zero-capacity slice output changed at iteration %d: %+v", iteration, result)
		}
	}
}

func TestZeroSizeElementSlicesRenderStructurallyWithoutReferenceIdentity(t *testing.T) {
	sharedSlice := make([]zeroSized, 0, 4)
	shared := Digest(struct {
		Left  []zeroSized
		Right []zeroSized
	}{Left: sharedSlice, Right: sharedSlice})
	independent := Digest(struct {
		Left  []zeroSized
		Right []zeroSized
	}{Left: make([]zeroSized, 0, 4), Right: make([]zeroSized, 0, 4)})
	if shared.Partial || independent.Partial || shared.SHA256 != independent.SHA256 {
		t.Fatalf("zero-size element storage identity affected output: %+v, %+v", shared, independent)
	}
	if strings.Contains(shared.Canonical, "@ref:") || strings.Contains(shared.Canonical, "#") {
		t.Fatalf("zero-size element slice received reference identity: %s", shared.Canonical)
	}
}

func TestZeroSizePointersAreExplicitlyPartial(t *testing.T) {
	values := []struct {
		value  any
		marker string
	}{
		{value: new(zeroSized), marker: "<skipped:zero-size-pointer:" + typeLabel(reflect.TypeOf(new(zeroSized))) + ">"},
		{value: new([0]byte), marker: "<skipped:zero-size-pointer:" + typeLabel(reflect.TypeOf(new([0]byte))) + ">"},
	}
	for _, test := range values {
		result := Digest(test.value)
		if !result.Partial || result.Counters.Blocklisted != 1 || !strings.Contains(result.Canonical, test.marker) {
			t.Errorf("zero-size pointer %T was not explicitly blocked: %+v", test.value, result)
		}
		if strings.Contains(result.Canonical, "#") || strings.Contains(result.Canonical, "@ref:") {
			t.Errorf("zero-size pointer %T received reference identity: %s", test.value, result.Canonical)
		}
	}
}

func TestFloat32NaNPayloadBitsArePreserved(t *testing.T) {
	firstBits := uint32(0x7fc00001)
	secondBits := uint32(0x7fc00002)
	first := math.Float32frombits(firstBits)
	second := math.Float32frombits(secondBits)

	firstResult := Digest(first)
	secondResult := Digest(second)
	if firstResult.Partial || secondResult.Partial || firstResult.SHA256 == secondResult.SHA256 {
		t.Fatalf("distinct float32 NaN payloads were lost: %+v, %+v", firstResult, secondResult)
	}
	if !strings.Contains(firstResult.Canonical, "nan:0x7fc00001") || !strings.Contains(secondResult.Canonical, "nan:0x7fc00002") {
		t.Fatalf("float32 NaN bits missing from canonical output: %s, %s", firstResult.Canonical, secondResult.Canonical)
	}

	named := namedFloat32(first)
	for _, value := range []any{
		named,
		float32Holder{Value: named},
		map[string]namedFloat32{"value": named},
	} {
		result := Digest(value)
		if result.Partial || !strings.Contains(result.Canonical, "nan:0x7fc00001") {
			t.Errorf("named float32 NaN payload was not safely preserved for %T: %+v", value, result)
		}
	}
}

func TestMapInsertionOrderIsStable(t *testing.T) {
	left := map[string][]int{}
	left["third"] = []int{3}
	left["first"] = []int{1}
	left["second"] = []int{2}
	right := map[string][]int{}
	right["second"] = []int{2}
	right["first"] = []int{1}
	right["third"] = []int{3}

	leftResult := Digest(left)
	rightResult := Digest(right)
	if leftResult.SHA256 != rightResult.SHA256 || leftResult.Canonical != rightResult.Canonical {
		t.Fatalf("map order changed canonical output:\n%s\n%s", leftResult.Canonical, rightResult.Canonical)
	}
}

func TestMapCanonicalTiesDoNotMutateSharedReferenceState(t *testing.T) {
	firstKey := &cycleNode{Value: 1}
	secondKey := &cycleNode{Value: 1}
	firstValue := &cycleNode{Value: 2}
	secondValue := &cycleNode{Value: 2}
	value := tiedMapTopology{
		Entries: map[*cycleNode]*cycleNode{
			firstKey:  firstValue,
			secondKey: secondValue,
		},
		Outside: [4]*cycleNode{firstKey, secondKey, firstValue, secondValue},
	}

	first := Digest(value)
	if !first.Partial || first.Counters.MapTies != 2 || first.Counters.Blocklisted != 1 {
		t.Fatalf("canonical tie was not explicitly partial and counted: %+v", first)
	}
	if !strings.Contains(first.Canonical, "<skipped:map-tie:count=2:probe-sha256=") {
		t.Fatalf("canonical tie marker missing count and stable probe digest: %s", first.Canonical)
	}
	for iteration := 0; iteration < 100; iteration++ {
		result := Digest(value)
		if result.SHA256 != first.SHA256 || result.Canonical != first.Canonical || !reflect.DeepEqual(result.Counters, first.Counters) {
			t.Fatalf("canonical tie changed at iteration %d:\n%+v\n%+v", iteration, first, result)
		}
	}
}

func TestMapStableAcrossProcesses(t *testing.T) {
	first := runMapHelper(t, "first")
	second := runMapHelper(t, "second")
	if first != second {
		t.Fatalf("map digest changed across processes: %s != %s", first, second)
	}
}

func TestMapCanonicalTieStableAcrossProcesses(t *testing.T) {
	first := runCanonicalHelperProof(t, "first", "CANONICAL_MAP_TIE_PROOF")
	second := runCanonicalHelperProof(t, "second", "CANONICAL_MAP_TIE_PROOF")
	if first != second {
		t.Fatalf("map tie proof changed across processes:\n%s\n%s", first, second)
	}
}

func TestCanonicalProcessHelper(t *testing.T) {
	order := os.Getenv("BEHAVIORDIFF_CANONICAL_HELPER")
	if order == "" {
		t.Skip("cross-process helper")
	}
	value := make(map[string]any)
	keys := []string{"alpha", "beta", "gamma", "delta"}
	if order == "second" {
		for left, right := 0, len(keys)-1; left < right; left, right = left+1, right-1 {
			keys[left], keys[right] = keys[right], keys[left]
		}
	}
	for index, key := range keys {
		value[key] = Box[int]{Value: indexForKey(key)}
		_ = index
	}
	result := Digest(value)
	t.Logf("CANONICAL_MAP_PROOF sha256=%s values=%d partial=%t", result.SHA256, result.Counters.Values, result.Partial)

	tied := newTiedMapTopology(order == "second")
	tiedResult := Digest(tied)
	t.Logf(
		"CANONICAL_MAP_TIE_PROOF sha256=%s values=%d blocklisted=%d mapTies=%d partial=%t",
		tiedResult.SHA256,
		tiedResult.Counters.Values,
		tiedResult.Counters.Blocklisted,
		tiedResult.Counters.MapTies,
		tiedResult.Partial,
	)
}

func TestCanonicalCounterProof(t *testing.T) {
	if os.Getenv("BEHAVIORDIFF_CANONICAL_PROOF") == "" {
		t.Skip("verifier counter proof")
	}
	result := Digest(privateRecord{Readable: "visible", private: "secret"})
	t.Logf(
		"CANONICAL_COUNTER_PROOF values=%d depthLimited=%d entryLimited=%d blocklisted=%d mapTies=%d errored=%d renderedTruncated=%d unexported=%d partial=%t",
		result.Counters.Values,
		result.Counters.DepthLimited,
		result.Counters.EntryLimited,
		result.Counters.Blocklisted,
		result.Counters.MapTies,
		result.Counters.Errored,
		result.Counters.RenderedTruncated,
		result.Counters.Unexported,
		result.Partial,
	)
}

func TestCollectionsEmbeddingInterfacesAndGenerics(t *testing.T) {
	array := Digest([3]int{1, 2, 3})
	slice := Digest([]int{1, 2, 3})
	if array.SHA256 == slice.SHA256 {
		t.Fatal("arrays and slices must remain distinct")
	}
	if result := Digest(embedding{Embedded: Embedded{Name: "inside"}, Count: 2}); result.Partial || !strings.Contains(result.Canonical, "Name=") {
		t.Fatalf("exported embedded struct was not traversed: %+v", result)
	}
	integer := Digest(interfaceHolder{Value: Box[int]{Value: 3}})
	text := Digest(interfaceHolder{Value: Box[string]{Value: "3"}})
	if integer.SHA256 == text.SHA256 || !strings.Contains(integer.Canonical, "interface:") {
		t.Fatalf("interface dynamic types or generic instantiations were lost:\n%s\n%s", integer.Canonical, text.Canonical)
	}
}

func TestTimeNormalizesWallClockInstant(t *testing.T) {
	instant := time.Date(2026, time.August, 22, 12, 34, 56, 789, time.FixedZone("offset", 5*60*60))
	utc := instant.UTC()
	if Digest(instant).SHA256 != Digest(utc).SHA256 {
		t.Fatal("the same wall-clock instant in different locations must normalize to UTC")
	}

	now := time.Now()
	withoutMonotonic, err := time.Parse(time.RFC3339Nano, now.UTC().Format(time.RFC3339Nano))
	if err != nil {
		t.Fatal(err)
	}
	if Digest(now).SHA256 != Digest(withoutMonotonic).SHA256 {
		t.Fatal("monotonic clock metadata must not affect the digest")
	}
}

func TestBlockedRuntimeIdentityShapes(t *testing.T) {
	unsafePointerType := reflect.TypeOf(reflect.Value.UnsafePointer).Out(0)
	values := []struct {
		value  any
		marker string
	}{
		{value: uintptr(42), marker: "<skipped:uintptr:"},
		{value: func() {}, marker: "<skipped:func:"},
		{value: make(chan int), marker: "<skipped:chan:"},
		{value: reflect.Zero(unsafePointerType).Interface(), marker: "<skipped:unsafe-pointer:"},
	}
	for _, test := range values {
		result := Digest(test.value)
		if !result.Partial || result.Counters.Blocklisted != 1 || !strings.Contains(result.Canonical, test.marker) {
			t.Errorf("blocked value %T was not marked: %+v", test.value, result)
		}
	}
}

func TestDepthAndEntryLimitsArePartial(t *testing.T) {
	deep := &cycleNode{Value: 1, Next: &cycleNode{Value: 2}}
	depth := DigestWithOptions(deep, Options{MaxDepth: 2})
	if !depth.Partial || depth.Counters.DepthLimited == 0 || !strings.Contains(depth.Canonical, "<depth:") {
		t.Fatalf("depth limit not reported: %+v", depth)
	}

	entries := DigestWithOptions([]int{1, 2, 3}, Options{MaxEntries: 2})
	if !entries.Partial || entries.Counters.EntryLimited != 1 || !strings.Contains(entries.Canonical, "max-entries:1") {
		t.Fatalf("entry limit not reported: %+v", entries)
	}
}

func TestRenderedCapRetainsHiddenPartialMarkers(t *testing.T) {
	deep := &cycleNode{Value: 0}
	cursor := deep
	for index := 1; index < DefaultMaxDepth+4; index++ {
		cursor.Next = &cycleNode{Value: index}
		cursor = cursor.Next
	}
	result := Digest(deep)
	if !result.Partial || result.Counters.DepthLimited == 0 || result.Counters.RenderedTruncated != 1 {
		t.Fatalf("deep capped value did not exercise both limits: %+v", result)
	}
	if !strings.Contains(result.Canonical, "<depth:") || !strings.HasSuffix(result.Canonical, "<truncated>") {
		t.Fatalf("capped diagnostic lost its depth marker: %s", result.Canonical)
	}
}

func TestRenderedCapHashesFullCanonicalText(t *testing.T) {
	prefix := strings.Repeat("x", DefaultRenderedCap+200)
	left := Digest(prefix + "left")
	right := Digest(prefix + "right")
	if len(left.Canonical) != DefaultRenderedCap || len(right.Canonical) != DefaultRenderedCap {
		t.Fatalf("rendered cap not enforced: %d, %d", len(left.Canonical), len(right.Canonical))
	}
	if left.Counters.RenderedTruncated != 1 || right.Counters.RenderedTruncated != 1 {
		t.Fatalf("rendered truncation not counted: %+v, %+v", left.Counters, right.Counters)
	}
	if left.Partial || right.Partial {
		t.Fatal("render truncation must not make a complete full-text digest partial")
	}
	if left.SHA256 == right.SHA256 {
		t.Fatal("differences beyond the rendered cap must affect the full canonical digest")
	}
	if !strings.HasSuffix(left.Canonical, "<truncated>") || left.Canonical != right.Canonical {
		t.Fatalf("rendered truncation marker did not preserve the shared visible prefix: %s", left.Canonical)
	}
}

func TestDoesNotInvokeStringerOrMarshaler(t *testing.T) {
	stringCalls.Store(0)
	marshalCalls.Store(0)
	result := Digest(methodTrap{Value: 9})
	if result.Partial {
		t.Fatalf("ordinary exported fields should be readable: %+v", result)
	}
	if stringCalls.Load() != 0 || marshalCalls.Load() != 0 {
		t.Fatalf("user methods were invoked: String=%d MarshalJSON=%d", stringCalls.Load(), marshalCalls.Load())
	}
}

func TestImplementationDoesNotImportUnsafe(t *testing.T) {
	entries, err := os.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".go" || strings.HasSuffix(entry.Name(), "_test.go") {
			continue
		}
		file, parseErr := parser.ParseFile(token.NewFileSet(), entry.Name(), nil, parser.ImportsOnly)
		if parseErr != nil {
			t.Fatal(parseErr)
		}
		for _, imported := range file.Imports {
			path, unquoteErr := strconv.Unquote(imported.Path.Value)
			if unquoteErr != nil {
				t.Fatal(unquoteErr)
			}
			if path == "unsafe" {
				t.Fatalf("implementation imports unsafe in %s", entry.Name())
			}
		}
	}
}

func runMapHelper(t *testing.T, order string) string {
	t.Helper()
	proof := runCanonicalHelperProof(t, order, "CANONICAL_MAP_PROOF")
	fields := strings.Fields(proof)
	return strings.TrimPrefix(fields[1], "sha256=")
}

func runCanonicalHelperProof(t *testing.T, order, prefix string) string {
	t.Helper()
	command := exec.Command(os.Args[0], "-test.run=^TestCanonicalProcessHelper$", "-test.v")
	command.Env = append(os.Environ(), "BEHAVIORDIFF_CANONICAL_HELPER="+order)
	output, err := command.CombinedOutput()
	if err != nil {
		t.Fatalf("cross-process helper failed: %v\n%s", err, output)
	}
	for _, line := range strings.Split(string(output), "\n") {
		if index := strings.Index(line, prefix+" "); index >= 0 {
			return strings.TrimSpace(line[index:])
		}
	}
	t.Fatalf("cross-process proof %q missing from output:\n%s", prefix, output)
	return ""
}

func newTiedMapTopology(reverseInsertion bool) tiedMapTopology {
	firstKey := &cycleNode{Value: 1}
	secondKey := &cycleNode{Value: 1}
	firstValue := &cycleNode{Value: 2}
	secondValue := &cycleNode{Value: 2}
	entries := make(map[*cycleNode]*cycleNode, 2)
	if reverseInsertion {
		entries[secondKey] = secondValue
		entries[firstKey] = firstValue
	} else {
		entries[firstKey] = firstValue
		entries[secondKey] = secondValue
	}
	return tiedMapTopology{
		Entries: entries,
		Outside: [4]*cycleNode{firstKey, secondKey, firstValue, secondValue},
	}
}

func indexForKey(key string) int {
	switch key {
	case "alpha":
		return 1
	case "beta":
		return 2
	case "gamma":
		return 3
	case "delta":
		return 4
	default:
		return 0
	}
}
