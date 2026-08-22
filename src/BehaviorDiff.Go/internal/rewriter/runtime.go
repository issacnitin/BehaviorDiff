package rewriter

import (
	"fmt"
	"go/format"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

const runtimeSource = `package behaviordiffrt

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"sync"
)

const (
	schema = "behaviordiff.trace/1"
	language = "go"
	writerCapacity = 1
)

type Member struct {
	Module string
	Method string
	File string
	Line int
	ReturnKind string
	SourceResolution string
	Status string
	SkipReason string
	Detail string
	IsTestRoot bool
	IsHarness bool
	ParameterNames []string
}

type capture struct {
	digest string
	rendered string
}

type Frame struct {
	member Member
	testID string
	callID uint64
	parentCallID uint64
	depth int
	ordinal uint64
	args *capture
	exit sync.Once
}

type moduleState struct {
	members map[string]Member
	tracedCalls uint64
}

type ordinalKey struct {
	testID string
	method string
}

var state = struct {
	sync.Mutex
	nextCallID uint64
	ordinals map[ordinalKey]uint64
	modules map[string]*moduleState
	trace *os.File
	tracePath string
	manifestPath string
	enqueued uint64
	written uint64
	digest Counters
}{
	nextCallID: 1,
	ordinals: make(map[ordinalKey]uint64),
	modules: make(map[string]*moduleState),
}

var defaultSensitiveNames = []string{"password", "token", "secret", "key", "ssn", "email", "auth", "credential"}
var sensitiveNames = append(defaultSensitiveNames, configuredList("BEHAVIORDIFF_REDACT_NAMES")...)
var digestOnlyTypes = configuredList("BEHAVIORDIFF_REDACT_TYPES")
var digestOnlyPaths = configuredList("BEHAVIORDIFF_REDACT_PATHS")

func init() {
	configured := os.Getenv("BEHAVIORDIFF_TRACE")
	if configured == "" {
		return
	}
	state.tracePath = decorate(configured, "")
	state.manifestPath = decorate(configured, ".manifest")
	if err := os.MkdirAll(filepath.Dir(state.tracePath), 0755); err != nil {
		panic(fmt.Errorf("behaviordiff: create trace directory: %w", err))
	}
	trace, err := os.Create(state.tracePath)
	if err != nil {
		panic(fmt.Errorf("behaviordiff: create trace: %w", err))
	}
	state.trace = trace
	state.Lock()
	defer state.Unlock()
	writeManifestLocked()
}

func Register(members ...Member) bool {
	state.Lock()
	defer state.Unlock()
	changed := false
	for _, member := range members {
		changed = ensureMemberLocked(member) || changed
	}
	if changed {
		writeManifestLocked()
	}
	return true
}

func TypeName[T any]() string {
	return stableTypeName(reflect.TypeOf((*T)(nil)).Elem())
}

func stableTypeName(typeToken reflect.Type) string {
	if typeToken.Name() != "" {
		if typeToken.PkgPath() != "" {
			return typeToken.PkgPath() + "." + typeToken.Name()
		}
		return typeToken.Name()
	}
	switch typeToken.Kind() {
	case reflect.Pointer:
		return "*" + stableTypeName(typeToken.Elem())
	case reflect.Slice:
		return "[]" + stableTypeName(typeToken.Elem())
	case reflect.Array:
		return fmt.Sprintf("[%d]%s", typeToken.Len(), stableTypeName(typeToken.Elem()))
	case reflect.Map:
		return "map[" + stableTypeName(typeToken.Key()) + "]" + stableTypeName(typeToken.Elem())
	case reflect.Chan:
		return typeToken.ChanDir().String() + " " + stableTypeName(typeToken.Elem())
	case reflect.Func:
		var name strings.Builder
		name.WriteString("func(")
		for index := 0; index < typeToken.NumIn(); index++ {
			if index > 0 { name.WriteByte(',') }
			name.WriteString(stableTypeName(typeToken.In(index)))
		}
		name.WriteByte(')')
		if typeToken.NumOut() == 1 {
			name.WriteByte(' ')
			name.WriteString(stableTypeName(typeToken.Out(0)))
		} else if typeToken.NumOut() > 1 {
			name.WriteString(" (")
			for index := 0; index < typeToken.NumOut(); index++ {
				if index > 0 { name.WriteByte(',') }
				name.WriteString(stableTypeName(typeToken.Out(index)))
			}
			name.WriteByte(')')
		}
		return name.String()
	}
	return typeToken.String()
}

func Specialize(member Member, callable, receiver string, receiverTypeArgumentCount int, typeArguments ...string) Member {
	if receiver != "" {
		prefix := member.Module + "." + receiver
		receiverArguments := typeArguments[:receiverTypeArgumentCount]
		member.Method = prefix + "[" + strings.Join(receiverArguments, ",") + "]" + strings.TrimPrefix(member.Method, prefix)
	} else {
		prefix := member.Module + "." + callable
		member.Method = prefix + "[" + strings.Join(typeArguments, ",") + "]" + strings.TrimPrefix(member.Method, prefix)
	}
	member.Status = "Patched"
	member.SkipReason = ""
	member.Detail = ""
	return member
}

func ensureMemberLocked(member Member) bool {
	module := state.modules[member.Module]
	if module == nil {
		module = &moduleState{members: make(map[string]Member)}
		state.modules[member.Module] = module
	}
	if _, exists := module.members[member.Method]; exists {
		return false
	}
	module.members[member.Method] = member
	return true
}

func Enter(parent *Frame, member Member, args []any) *Frame {
	testID := "(no-test)"
	parentCallID := uint64(0)
	depth := 0
	if parent != nil {
		testID = parent.testID
		parentCallID = parent.callID
		depth = parent.depth + 1
	}
	return enter(parentCallID, depth, testID, member, args)
}

func OpenTest(testID string, member Member, args []any) *Frame {
	if testID == "" {
		testID = "(no-test)"
	}
	return enter(0, 0, testID, member, args)
}

func enter(parentCallID uint64, depth int, testID string, member Member, args []any) *Frame {
	state.Lock()
	if ensureMemberLocked(member) {
		writeManifestLocked()
	}
	callID := state.nextCallID
	state.nextCallID++
	key := ordinalKey{testID: testID, method: member.Method}
	ordinal := state.ordinals[key]
	state.ordinals[key] = ordinal + 1
	state.Unlock()
	argsCapture := captureValue(args, member, member.ParameterNames)
	return &Frame{
		member: member,
		testID: testID,
		callID: callID,
		parentCallID: parentCallID,
		depth: depth,
		ordinal: ordinal,
		args: argsCapture,
	}
}

func Exit(frame *Frame, results []any, recovered any) {
	if frame == nil {
		return
	}
	frame.exit.Do(func() {
		event := map[string]any{
			"testId": frame.testID,
			"methodFullName": frame.member.Method,
			"filePathResolution": frame.member.SourceResolution,
			"line": frame.member.Line,
			"callDepth": frame.depth,
			"callId": frame.callID,
			"ordinal": frame.ordinal,
			"threadId": 0,
		}
		if frame.member.File != "" {
			event["filePath"] = frame.member.File
		}
		if frame.parentCallID != 0 {
			event["parentCallId"] = frame.parentCallID
		}
		if frame.args != nil {
			event["argsDigest"] = frame.args.digest
			event["argsRendered"] = frame.args.rendered
		}
		if recovered != nil {
			event["exceptionType"] = fmt.Sprintf("%T", recovered)
		} else if len(results) > 0 {
			if captured := captureValue(resultValues(results), frame.member, nil); captured != nil {
				event["returnDigest"] = captured.digest
				event["returnRendered"] = captured.rendered
			}
		}
		if frame.member.IsHarness {
			event["isHarness"] = true
		}
		line, err := json.Marshal(event)
		if err != nil {
			panic(fmt.Errorf("behaviordiff: encode event: %w", err))
		}
		state.Lock()
		defer state.Unlock()
		if state.trace == nil {
			return
		}
		state.enqueued++
		if _, err := state.trace.Write(append(line, '\n')); err != nil {
			panic(fmt.Errorf("behaviordiff: write event: %w", err))
		}
		state.written++
		if module := state.modules[frame.member.Module]; module != nil {
			module.tracedCalls++
		}
		writeManifestLocked()
	})
}

func Go(parent *Frame, fn func(*Frame)) {
	captured := parent
	go func() {
		fn(captured)
	}()
}

func captureValue(value any, member Member, argumentNames []string) *capture {
	result := DigestWithOptions(value, Options{
		Redact: true,
		SensitiveNames: sensitiveNames,
		DigestOnlyTypes: digestOnlyTypes,
		ArgumentNames: argumentNames,
	})
	state.Lock()
	state.digest.Values += result.Counters.Values
	state.digest.DepthLimited += result.Counters.DepthLimited
	state.digest.EntryLimited += result.Counters.EntryLimited
	state.digest.Blocklisted += result.Counters.Blocklisted
	state.digest.MapTies += result.Counters.MapTies
	state.digest.Errored += result.Counters.Errored
	state.digest.RenderedTruncated += result.Counters.RenderedTruncated
	state.digest.Unexported += result.Counters.Unexported
	state.Unlock()
	rendered := result.Canonical
	if digestOnlyPath(member.File) {
		rendered = "<redacted>"
	}
	return &capture{digest: "sha256:" + result.SHA256, rendered: rendered}
}

func configuredList(name string) []string {
	var values []string
	for _, value := range strings.FieldsFunc(os.Getenv(name), func(character rune) bool { return character == ';' || character == ',' }) {
		if trimmed := strings.TrimSpace(value); trimmed != "" {
			values = append(values, trimmed)
		}
	}
	return values
}

func digestOnlyPath(path string) bool {
	normalized := strings.Trim(strings.ReplaceAll(path, "\\", "/"), "/")
	for _, configured := range digestOnlyPaths {
		prefix := strings.Trim(strings.ReplaceAll(configured, "\\", "/"), "/")
		if normalized == prefix || strings.HasPrefix(normalized, prefix+"/") ||
			strings.HasSuffix(normalized, "/"+prefix) || strings.Contains(normalized, "/"+prefix+"/") {
			return true
		}
	}
	return false
}

func resultValues(results []any) any {
	values := make([]any, len(results))
	for index, result := range results {
		value := reflect.ValueOf(result)
		if value.IsValid() && value.Kind() == reflect.Pointer && !value.IsNil() {
			values[index] = value.Elem().Interface()
		} else {
			values[index] = result
		}
	}
	if len(values) == 1 {
		return values[0]
	}
	return values
}

func decorate(configured, suffix string) string {
	absolute, err := filepath.Abs(configured)
	if err != nil {
		panic(fmt.Errorf("behaviordiff: resolve trace path: %w", err))
	}
	extension := filepath.Ext(absolute)
	base := strings.TrimSuffix(filepath.Base(absolute), extension)
	name := fmt.Sprintf("%s.%d%s%s", base, os.Getpid(), suffix, extension)
	return filepath.Join(filepath.Dir(absolute), name)
}

func writeManifestLocked() {
	if state.manifestPath == "" {
		return
	}
	records := make([]map[string]any, 0)
	records = append(records, map[string]any{"kind": "run", "schema": schema, "language": language})
	moduleNames := make([]string, 0, len(state.modules))
	for name := range state.modules {
		moduleNames = append(moduleNames, name)
	}
	sort.Strings(moduleNames)
	for _, name := range moduleNames {
		module := state.modules[name]
		members := make([]Member, 0, len(module.members))
		for _, member := range module.members {
			members = append(members, member)
		}
		sort.Slice(members, func(left, right int) bool { return members[left].Method < members[right].Method })
		patched := 0
		exact := 0
		for _, member := range members {
			if member.Status == "Patched" {
				patched++
				if member.SourceResolution == "debugInfo" {
					exact++
				}
			}
		}
		skipped := len(members) - patched
		exactPercent := 100
		sourceRule := "notApplicable"
		if patched > 0 {
			exactPercent = exact * 100 / patched
			sourceRule = "ratio"
		}
		records = append(records, map[string]any{
			"kind": "assembly", "assembly": name, "discovery": "GoAstRewrite",
			"scanned": true, "instrumented": patched > 0, "patchedMembers": patched,
			"discoveredMembers": len(members), "skippedMembers": skipped, "patchFailedMembers": 0,
			"queuedAtMs": 0, "patchedAtMs": 0, "tracedCalls": module.tracedCalls,
			"membersWithExactSource": exact, "exactSourcePercent": exactPercent, "sourceRule": sourceRule,
		})
		for _, member := range members {
			record := map[string]any{
				"kind": "member", "assembly": name, "method": member.Method,
				"status": member.Status, "returnKind": member.ReturnKind,
				"sourceResolution": member.SourceResolution,
			}
			if member.SkipReason != "" { record["skipReason"] = member.SkipReason }
			if member.Detail != "" { record["detail"] = member.Detail }
			if member.IsTestRoot { record["isTestRoot"] = true }
			records = append(records, record)
		}
	}
	records = append(records, map[string]any{
		"kind": "digest", "valuesDigested": state.digest.Values,
		"depthLimited": state.digest.DepthLimited, "blocklisted": state.digest.Blocklisted,
		"errored": state.digest.Errored, "renderedTruncated": state.digest.RenderedTruncated,
		"unreadableFields": state.digest.Unexported, "ambiguousMapEntries": state.digest.MapTies,
	})
	records = append(records, map[string]any{
		"kind": "writer", "enqueued": state.enqueued, "written": state.written,
		"dropped": 0, "capacity": writerCapacity,
	})
	var output strings.Builder
	for _, record := range records {
		line, err := json.Marshal(record)
		if err != nil { panic(fmt.Errorf("behaviordiff: encode manifest: %w", err)) }
		output.Write(line)
		output.WriteByte('\n')
	}
	temporary := state.manifestPath + ".tmp"
	if err := os.WriteFile(temporary, []byte(output.String()), 0644); err != nil {
		panic(fmt.Errorf("behaviordiff: write manifest: %w", err))
	}
	if err := os.Rename(temporary, state.manifestPath); err != nil {
		_ = os.Remove(state.manifestPath)
		if retryErr := os.Rename(temporary, state.manifestPath); retryErr != nil {
			panic(fmt.Errorf("behaviordiff: replace manifest: %w", retryErr))
		}
	}
}
`

func writeRuntime(out string) error {
	directory := filepath.Join(out, "internal", "behaviordiffrt")
	if err := os.MkdirAll(directory, 0o755); err != nil {
		return fmt.Errorf("create runtime package: %w", err)
	}
	formattedRuntime, err := format.Source([]byte(runtimeSource))
	if err != nil {
		return fmt.Errorf("format runtime source: %w", err)
	}
	if err := os.WriteFile(filepath.Join(directory, "runtime.go"), formattedRuntime, 0o644); err != nil {
		return fmt.Errorf("write runtime package: %w", err)
	}
	_, sourceFile, _, ok := runtime.Caller(0)
	if !ok {
		return fmt.Errorf("locate canonicalizer source")
	}
	canonicalPath := filepath.Join(filepath.Dir(sourceFile), "..", "runtime", "canonical", "canonical.go")
	canonical, err := os.ReadFile(canonicalPath)
	if err != nil {
		return fmt.Errorf("read canonicalizer source: %w", err)
	}
	generated := strings.NewReplacer(
		"package canonical", "package behaviordiffrt",
		"newState", "newCanonicalState",
		"type state struct", "type canonicalState struct",
		"*state", "*canonicalState",
		"&state{", "&canonicalState{",
	).Replace(string(canonical))
	formattedCanonical, err := format.Source([]byte(generated))
	if err != nil {
		return fmt.Errorf("format canonicalizer source: %w", err)
	}
	if err := os.WriteFile(filepath.Join(directory, "canonical.go"), formattedCanonical, 0o644); err != nil {
		return fmt.Errorf("write canonicalizer source: %w", err)
	}
	return nil
}
