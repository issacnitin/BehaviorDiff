# Go Canonicalizer

`internal/runtime/canonical` is the reusable value canonicalizer intended for a future generated Go trace runtime. It has no trace emission or NDJSON integration.

## API and limits

`Digest(value any)` returns a `Result` containing:

- `SHA256`: lowercase SHA-256 of the complete canonical UTF-8 text.
- `Canonical`: a diagnostic rendering capped at 2,000 bytes. A truncated rendering ends with the full-text digest and byte count.
- `FullBytes`: byte length of the complete canonical text.
- `Partial`: whether any value content was skipped, blocked, depth-limited, entry-limited, or lost to a contained reflection panic.
- `Counters`: coverage and loss counters described below.
- `SkippedUnexported`: sorted type, field, and occurrence details suitable for aggregation into a manifest.

`DigestWithOptions` permits focused callers and tests to lower the rendered, depth, or per-composite entry limits. Defaults are 2,000 rendered bytes, depth 64, and 10,000 entries per struct, map, slice, or array.

Rendered truncation does not set `Partial`: the SHA-256 still covers the complete canonical text. Depth and entry truncation do set `Partial` because value content did not reach that text.

## Canonical rules

- Scalar output includes the concrete type. Integer and unsigned widths, float widths, complex widths, booleans, and strings remain distinct. Floats preserve negative zero, infinities, and NaN payload bits.
- Nil interfaces, pointers, maps, and slices are explicit.
- Pointer, map, and observable slice storage addresses are never rendered. Encounter-order reference IDs preserve cycles and distinguish shared references from equal copies.
- Non-nil slices with zero capacity render structurally without a reference ID. Their storage cannot be reused by `append`, so topology through that storage is not semantically observable. Slices of zero-size elements also render structurally because their runtime base may be shared while their storage carries no element state.
- Non-nil pointers to zero-size values emit `<skipped:zero-size-pointer:TYPE>` and make the result partial. Go permits distinct zero-size variables to have equal or unequal implementation addresses, so those addresses cannot provide canonical identity.
- Interfaces include the static interface type and recurse into the dynamic value.
- Arrays and slices preserve order. Structs preserve declaration order and recurse through exported fields, including exported anonymous fields.
- Maps are ordered by isolated canonical key/value probes, then traversed once into the final reference topology. Probe counters are discarded. A group of entries with identical probes emits one bounded `<skipped:map-tie:count=N:probe-sha256=...>` marker. Tied keys and values are not traversed into shared reference state, no address is used as a tie-breaker, and the result is explicitly partial.
- `time.Time` is the only semantic special case. Monotonic metadata and location presentation are removed, and the instant is rendered in UTC with RFC 3339 nanosecond precision. Arbitrary strings and integers are never guessed to be timestamps or IDs.
- `uintptr`, functions, channels, and unsafe-pointer-shaped values emit explicit skipped markers. Reference identity is normalized only for pointers, maps, and slices.
- Reflection panics are contained at the current value and emit `<error:reflection-panic>`.

The implementation invokes no getters, callbacks, `String`, `MarshalJSON`, or other user methods. It uses built-in reflection over fields, arrays, slices, and maps. Go has already performed its built-in map hashing before `MapRange`; the canonicalizer does not invoke application methods during iteration.

## No `unsafe`

The package deliberately does not import or use `unsafe`. An unexported field is never converted with `Interface` and is never read through an address. It emits:

```text
<skipped:unexported:package/path.Type.field>
```

Values that differ only inside such fields intentionally have the same digest. Both results are `Partial`, so matching partial digests do not prove equality.

## Counter semantics

All counters are per `Digest` call:

| Counter | Meaning |
| --- | --- |
| `Values` | Reflection values and synthetic omission markers incorporated into the final canonical traversal. Discarded map sort probes do not count. |
| `DepthLimited` | Values replaced by a maximum-depth marker. |
| `EntryLimited` | Composites with one or more entries replaced by a maximum-entry marker. |
| `Blocklisted` | Values or synthetic groups intentionally not inspected: unexported fields, `uintptr`, functions, channels, unsafe-pointer shapes, zero-size pointers, and canonically tied map groups. One tied group increments this counter once. |
| `MapTies` | Individual map entries omitted because two or more entries had the same isolated canonical probe. A tied group of size $N$ increments this counter by $N$. |
| `Errored` | Values replaced after a contained reflection panic. |
| `RenderedTruncated` | Diagnostic renderings shortened to the rendered cap. The full digest remains complete. |
| `Unexported` | Individual unexported field occurrences skipped. This is also included in `Blocklisted`. |

`SkippedUnexported` aggregates `Unexported` occurrences by declaring type and field so a future runtime can record coverage gaps without exposing field contents.

## Confidence implications

An equal non-partial digest is strong evidence of equality under these canonical rules, not proof of Go semantic equality. Type identity includes package paths and is intentionally runtime/build specific.

An equal partial digest is only evidence that the observed portions matched. Unexported state, blocked runtime identities, and content beyond depth or entry limits may differ. Reflection panic markers also collapse distinct unreadable states. The caller should lower confidence using `Partial`, the counters, and skipped-field details.

Map ordering is deterministic for canonical key/value content and does not depend on insertion order or raw addresses. Pathological maps whose distinct entries have identical isolated canonical probes are represented only by their tie count and probe digest. Their entry details and identity relationships are omitted, and `Partial`, `Blocklisted`, and `MapTies` prevent the result from silently claiming equality.