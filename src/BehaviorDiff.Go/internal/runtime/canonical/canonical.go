package canonical

import (
	"crypto/sha256"
	"encoding/hex"
	"math"
	"reflect"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	DefaultRenderedCap = 2000
	DefaultMaxDepth    = 64
	DefaultMaxEntries  = 10000
)

var (
	timeType           = reflect.TypeOf(time.Time{})
	builtinFloat32Type = reflect.TypeOf(float32(0))
)

type Options struct {
	RenderedCap int
	MaxDepth    int
	MaxEntries  int
}

type Counters struct {
	Values            int
	DepthLimited      int
	EntryLimited      int
	Blocklisted       int
	MapTies           int
	Errored           int
	RenderedTruncated int
	Unexported        int
}

type SkippedUnexported struct {
	Type  string
	Field string
	Count int
}

type Result struct {
	SHA256            string
	Canonical         string
	FullBytes         int
	Partial           bool
	Counters          Counters
	SkippedUnexported []SkippedUnexported
}

type reference struct {
	kind     reflect.Kind
	typeName string
	pointer  uintptr
	length   int
	capacity int
}

type skippedKey struct {
	typeName string
	field    string
}

type state struct {
	options       Options
	counters      Counters
	partial       bool
	seen          map[reference]int
	nextReference int
	skipped       map[skippedKey]int
}

type mapEntry struct {
	key   reflect.Value
	value reflect.Value
	probe string
}

func Digest(value any) Result {
	return DigestWithOptions(value, Options{})
}

func DigestWithOptions(value any, options Options) Result {
	options = normalizedOptions(options)
	current := newState(options)
	canonical := current.render(reflect.ValueOf(value), 0)
	sum := sha256.Sum256([]byte(canonical))
	digest := hex.EncodeToString(sum[:])
	rendered := canonical
	if len(rendered) > options.RenderedCap {
		current.counters.RenderedTruncated++
		rendered = truncateRendered(rendered, options.RenderedCap, digest, len(canonical))
	}

	skipped := make([]SkippedUnexported, 0, len(current.skipped))
	for key, count := range current.skipped {
		skipped = append(skipped, SkippedUnexported{Type: key.typeName, Field: key.field, Count: count})
	}
	sort.Slice(skipped, func(left, right int) bool {
		if skipped[left].Type != skipped[right].Type {
			return skipped[left].Type < skipped[right].Type
		}
		return skipped[left].Field < skipped[right].Field
	})

	return Result{
		SHA256:            digest,
		Canonical:         rendered,
		FullBytes:         len(canonical),
		Partial:           current.partial,
		Counters:          current.counters,
		SkippedUnexported: skipped,
	}
}

func normalizedOptions(options Options) Options {
	if options.RenderedCap <= 0 {
		options.RenderedCap = DefaultRenderedCap
	}
	if options.MaxDepth <= 0 {
		options.MaxDepth = DefaultMaxDepth
	}
	if options.MaxEntries <= 0 {
		options.MaxEntries = DefaultMaxEntries
	}
	return options
}

func newState(options Options) *state {
	return &state{
		options:       options,
		seen:          make(map[reference]int),
		nextReference: 1,
		skipped:       make(map[skippedKey]int),
	}
}

func (current *state) render(value reflect.Value, depth int) (rendered string) {
	current.counters.Values++
	deferred := true
	defer func() {
		if recovered := recover(); recovered != nil {
			current.counters.Errored++
			current.partial = true
			rendered = "<error:reflection-panic>"
		}
		deferred = false
	}()

	if !value.IsValid() {
		return "nil:any"
	}
	if depth >= current.options.MaxDepth {
		current.counters.DepthLimited++
		current.partial = true
		return "<skipped:max-depth:" + typeLabel(value.Type()) + ">"
	}

	typeName := typeLabel(value.Type())
	if value.Type() == timeType && value.CanInterface() {
		instant := value.Interface().(time.Time).Round(0).UTC()
		return "time.Time(" + strconv.Quote(instant.Format(time.RFC3339Nano)) + ")"
	}

	switch value.Kind() {
	case reflect.Bool:
		return typeName + "(" + strconv.FormatBool(value.Bool()) + ")"
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return typeName + "(" + strconv.FormatInt(value.Int(), 10) + ")"
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return typeName + "(" + strconv.FormatUint(value.Uint(), 10) + ")"
	case reflect.Uintptr:
		return current.blocked(typeName, "uintptr")
	case reflect.Float32:
		return current.renderFloat32(value, typeName)
	case reflect.Float64:
		return typeName + "(" + formatFloat(value.Float(), 64) + ")"
	case reflect.Complex64:
		complexValue := value.Complex()
		return typeName + "(" + formatFloat(real(complexValue), 32) + "," + formatFloat(imag(complexValue), 32) + ")"
	case reflect.Complex128:
		complexValue := value.Complex()
		return typeName + "(" + formatFloat(real(complexValue), 64) + "," + formatFloat(imag(complexValue), 64) + ")"
	case reflect.String:
		return typeName + "(" + strconv.QuoteToASCII(value.String()) + ")"
	case reflect.Interface:
		if value.IsNil() {
			return "interface:" + typeName + "(nil)"
		}
		return "interface:" + typeName + "(" + current.render(value.Elem(), depth+1) + ")"
	case reflect.Pointer:
		if value.IsNil() {
			return "pointer:" + typeName + "(nil)"
		}
		if value.Type().Elem().Size() == 0 {
			return current.blocked(typeName, "zero-size-pointer")
		}
		id, seen := current.referenceID(value)
		if seen {
			return "@ref:" + strconv.Itoa(id)
		}
		return "pointer:" + typeName + "#" + strconv.Itoa(id) + "(" + current.render(value.Elem(), depth+1) + ")"
	case reflect.Struct:
		return current.renderStruct(value, typeName, depth)
	case reflect.Array:
		return current.renderArray(value, typeName, depth)
	case reflect.Slice:
		if value.IsNil() {
			return "slice:" + typeName + "(nil)"
		}
		if value.Cap() == 0 || value.Type().Elem().Size() == 0 {
			return "slice:" + typeName + current.renderSequence(value, depth)
		}
		id, seen := current.referenceID(value)
		if seen {
			return "@ref:" + strconv.Itoa(id)
		}
		return "slice:" + typeName + "#" + strconv.Itoa(id) + current.renderSequence(value, depth)
	case reflect.Map:
		if value.IsNil() {
			return "map:" + typeName + "(nil)"
		}
		id, seen := current.referenceID(value)
		if seen {
			return "@ref:" + strconv.Itoa(id)
		}
		return current.renderMap(value, typeName, id, depth)
	case reflect.Func:
		return current.blocked(typeName, "func")
	case reflect.Chan:
		return current.blocked(typeName, "chan")
	case reflect.UnsafePointer:
		return current.blocked(typeName, "unsafe-pointer")
	case reflect.Invalid:
		return "nil:any"
	default:
		if deferred {
			return current.blocked(typeName, value.Kind().String())
		}
		return "<error:unreachable>"
	}
}

func (current *state) renderStruct(value reflect.Value, typeName string, depth int) string {
	var output strings.Builder
	output.WriteString("struct:")
	output.WriteString(typeName)
	output.WriteByte('{')
	limit := min(value.NumField(), current.options.MaxEntries)
	for index := 0; index < limit; index++ {
		if index > 0 {
			output.WriteByte(',')
		}
		field := value.Type().Field(index)
		output.WriteString(field.Name)
		output.WriteByte('=')
		if field.PkgPath != "" {
			markerType := typeLabel(value.Type())
			output.WriteString("<skipped:unexported:")
			output.WriteString(markerType)
			output.WriteByte('.')
			output.WriteString(field.Name)
			output.WriteByte('>')
			current.counters.Values++
			current.counters.Blocklisted++
			current.counters.Unexported++
			current.partial = true
			current.skipped[skippedKey{typeName: markerType, field: field.Name}]++
			continue
		}
		output.WriteString(current.render(value.Field(index), depth+1))
	}
	if value.NumField() > limit {
		current.entryLimited(&output, value.NumField()-limit)
	}
	output.WriteByte('}')
	return output.String()
}

func (current *state) renderArray(value reflect.Value, typeName string, depth int) string {
	return "array:" + typeName + current.renderSequence(value, depth)
}

func (current *state) renderSequence(value reflect.Value, depth int) string {
	var output strings.Builder
	output.WriteByte('[')
	limit := min(value.Len(), current.options.MaxEntries)
	for index := 0; index < limit; index++ {
		if index > 0 {
			output.WriteByte(',')
		}
		output.WriteString(current.render(value.Index(index), depth+1))
	}
	if value.Len() > limit {
		current.entryLimited(&output, value.Len()-limit)
	}
	output.WriteByte(']')
	return output.String()
}

func (current *state) renderMap(value reflect.Value, typeName string, id int, depth int) string {
	entries := make([]mapEntry, 0, value.Len())
	iterator := value.MapRange()
	for iterator.Next() {
		key := iterator.Key()
		mapped := iterator.Value()
		entries = append(entries, mapEntry{key: key, value: mapped, probe: current.mapProbe(key, mapped, depth+1)})
	}
	sort.Slice(entries, func(left, right int) bool {
		return entries[left].probe < entries[right].probe
	})

	var output strings.Builder
	output.WriteString("map:")
	output.WriteString(typeName)
	output.WriteByte('#')
	output.WriteString(strconv.Itoa(id))
	output.WriteByte('{')
	limit := min(len(entries), current.options.MaxEntries)
	written := 0
	index := 0
	for index < limit {
		groupEnd := index + 1
		for groupEnd < len(entries) && entries[groupEnd].probe == entries[index].probe {
			groupEnd++
		}
		if written > 0 {
			output.WriteByte(',')
		}
		if groupEnd-index > 1 {
			output.WriteString(current.mapTie(entries[index].probe, groupEnd-index))
		} else {
			output.WriteString(current.render(entries[index].key, depth+1))
			output.WriteString("=>")
			output.WriteString(current.render(entries[index].value, depth+1))
		}
		written++
		index = groupEnd
	}
	if index < len(entries) {
		current.entryLimited(&output, len(entries)-index)
	}
	output.WriteByte('}')
	return output.String()
}

func (current *state) mapTie(probe string, count int) string {
	sum := sha256.Sum256([]byte(probe))
	current.counters.Values++
	current.counters.Blocklisted++
	current.counters.MapTies += count
	current.partial = true
	return "<skipped:map-tie:count=" + strconv.Itoa(count) + ":probe-sha256=" + hex.EncodeToString(sum[:]) + ">"
}

func (current *state) mapProbe(key, value reflect.Value, depth int) string {
	probe := newState(current.options)
	probe.seen = make(map[reference]int, len(current.seen))
	for identity, id := range current.seen {
		probe.seen[identity] = id
	}
	probe.nextReference = current.nextReference
	return probe.render(key, depth) + "=>" + probe.render(value, depth)
}

func (current *state) referenceID(value reflect.Value) (int, bool) {
	identity := reference{
		kind:     value.Kind(),
		typeName: typeLabel(value.Type()),
		pointer:  value.Pointer(),
	}
	if value.Kind() == reflect.Slice {
		identity.length = value.Len()
		identity.capacity = value.Cap()
	}
	if id, exists := current.seen[identity]; exists {
		return id, true
	}
	id := current.nextReference
	current.nextReference++
	current.seen[identity] = id
	return id, false
}

func (current *state) blocked(typeName, reason string) string {
	current.counters.Blocklisted++
	current.partial = true
	return "<skipped:" + reason + ":" + typeName + ">"
}

func (current *state) errored(typeName, reason string) string {
	current.counters.Errored++
	current.partial = true
	return "<error:" + reason + ":" + typeName + ">"
}

func (current *state) entryLimited(output *strings.Builder, omitted int) {
	if output.Len() > 0 {
		output.WriteByte(',')
	}
	output.WriteString("<skipped:max-entries:")
	output.WriteString(strconv.Itoa(omitted))
	output.WriteByte('>')
	current.counters.Values++
	current.counters.EntryLimited++
	current.partial = true
}

func formatFloat(value float64, bits int) string {
	if math.IsNaN(value) {
		return "nan:0x" + strconv.FormatUint(math.Float64bits(value), 16)
	}
	if math.IsInf(value, 1) {
		return "+inf"
	}
	if math.IsInf(value, -1) {
		return "-inf"
	}
	if value == 0 && math.Signbit(value) {
		return "-0"
	}
	return strconv.FormatFloat(value, 'g', -1, bits)
}

func (current *state) renderFloat32(value reflect.Value, typeName string) string {
	if !value.CanConvert(builtinFloat32Type) {
		return current.errored(typeName, "float32-bits-unavailable")
	}
	converted := value.Convert(builtinFloat32Type)
	if !converted.CanInterface() {
		return current.errored(typeName, "float32-bits-unavailable")
	}
	scalar, ok := converted.Interface().(float32)
	if !ok {
		return current.errored(typeName, "float32-bits-unavailable")
	}
	return typeName + "(" + formatFloat32(scalar) + ")"
}

func formatFloat32(value float32) string {
	if math.IsNaN(float64(value)) {
		return "nan:0x" + strconv.FormatUint(uint64(math.Float32bits(value)), 16)
	}
	return formatFloat(float64(value), 32)
}

func typeLabel(value reflect.Type) string {
	if value.Name() != "" {
		if value.PkgPath() == "" {
			return value.Name()
		}
		return value.PkgPath() + "." + value.Name()
	}
	switch value.Kind() {
	case reflect.Pointer:
		return "*" + typeLabel(value.Elem())
	case reflect.Slice:
		return "[]" + typeLabel(value.Elem())
	case reflect.Array:
		return "[" + strconv.Itoa(value.Len()) + "]" + typeLabel(value.Elem())
	case reflect.Map:
		return "map[" + typeLabel(value.Key()) + "]" + typeLabel(value.Elem())
	case reflect.Chan:
		return value.ChanDir().String() + " " + typeLabel(value.Elem())
	default:
		return value.String()
	}
}

func truncateRendered(full string, cap int, digest string, fullBytes int) string {
	marker := "<truncated:sha256=" + digest + ":full-bytes=" + strconv.Itoa(fullBytes) + ">"
	if len(marker) >= cap {
		return marker[:validPrefix(marker, cap)]
	}
	prefixCap := cap - len(marker)
	return full[:validPrefix(full, prefixCap)] + marker
}

func validPrefix(value string, limit int) int {
	if limit >= len(value) {
		return len(value)
	}
	if limit <= 0 {
		return 0
	}
	for limit > 0 && !utf8.RuneStart(value[limit]) {
		limit--
	}
	return limit
}
