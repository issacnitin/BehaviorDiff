# Rust stable tracer production scope

This scope is the pre-implementation contract for the production Rust tracer. The implementation uses stable Rust source rewriting through `syn` and `quote`, writes only to a content-addressed build cache, and does not depend on `rustc_private`, nightly Rust, MIR pass registration, target-owned derives, annotations, or handwritten serializers.

## Prototype evidence boundary

The stable prototype emitted 15 runtime records from the unchanged `samples/RustReference` program: 13 normal completions, one panic unwind, and one dropped-future cancellation. It emitted records for private structs and enums, a generic function, a trait implementation and trait-object caller, async completion and cancellation, `?` success and early `Err`, panic unwinding, and standard collections. Source-project hashes outside `target` were unchanged before and after rewriting.

Those records contained method, source, line, and outcome only. They did not contain arguments, returns, canonical text, digests, call identities, ordinals, test correlation, manifests, or production callable identities. The prototype therefore proved exit-hook feasibility for the listed shapes and proved no shape Exact or Partial under `TRACE-FORMAT.md`.

Unions, dependency-owned value types, and macro-generated callables were not present in the prototype reference project. They were neither traced nor canonicalized and are not claimed as achieved prototype coverage.

## Digest classification

A call is Exact only when every captured argument and return region is Exact. Any visible partial marker makes the key Partial under the existing engine rule.

| Value shape | Production classification | Required behavior |
| --- | --- | --- |
| Booleans, integers, floats, chars, strings, and unit | Exact | Canonical type-tagged scalar text; exceptional float values remain distinct. |
| Tuples, arrays, slices, `Option`, and `Result` with fully supported children | Exact | Preserve shape and position; recurse without invoking target code. |
| Locally defined structs, including private fields | Exact | Generate a reader beside the type in its defining module so normal Rust privacy grants field access. Every declared field is represented. |
| Locally defined enums, including private variants and fields | Exact | Generate a variant match beside the type and represent the discriminant plus every active field. |
| References and local pointer graphs | Exact when the pointee shape is Exact | Track address identity only within one canonicalization, emit stable traversal-order back-references, and terminate cycles. Runtime addresses never enter canonical text. |
| `Vec`, `VecDeque`, `BTreeMap`, `BTreeSet`, `HashMap`, and `HashSet` with supported children and standard hashers | Exact | Use only sealed standard-library traversal. Sort unordered entries by child canonical text/digest; never call target formatting, equality, hashing, comparison, or iterator implementations. |
| Concrete local trait implementation receiver | Exact when its concrete local type is Exact | Instrument the implementation method and use the generated concrete receiver reader. |
| Generic parameter or associated type erased at the rewritten definition | Partial | Emit `<skipped:generic:TYPE>` and increment `blocklisted`; include stable `type_name` in the marker and callable identity where a concrete monomorphization executes. Do not add trait bounds to user APIs. |
| `dyn Trait` region | Partial | Emit `<skipped:trait-object:TRAIT>` and increment `blocklisted`. Do not invoke trait methods to inspect it. The concrete implementation method may still produce its own Exact event. |
| Union value or union field | Partial | Emit `<skipped:union:TYPE>` and increment `blocklisted`; never guess the active field or read union storage. |
| Dependency-owned or otherwise opaque type without a generated local reader | Partial | Emit `<skipped:external:TYPE>` and increment `blocklisted`. Do not use `Debug`, `Display`, `Serialize`, iteration, equality, hashing, or reflection-like target traits. |
| Function pointers, closures, futures as values, channels, locks, file/socket handles, and runtime handles | Partial | Emit a shape-specific `<skipped:...>` marker and increment `blocklisted`; never execute or poll the value during canonicalization. |
| Depth- or breadth-limited supported values | Partial | Emit the normative depth/skipped marker and increment the corresponding digest counter. |
| Renderings beyond the display cap | Partial display, full digest | Hash complete canonical text first, render `<truncated>`, and increment `renderedTruncated`. |

Opaque regions are never omitted. Two equal partial digests do not establish equality inside a skipped region; the engine must retain Partial confidence exactly as `TRACE-FORMAT.md` specifies.

## Callable classification

| Callable shape | Production status | Required behavior |
| --- | --- | --- |
| Free functions, inherent methods, and trait implementation methods with bodies | Patched | Entry before user instructions and one completion event on every normal or unwinding path. |
| Trait default methods with source bodies | Patched | Rewrite the default body and preserve the source-level trait identity. |
| `async fn` and functions returning an instrumented async block | Patched | Enter when the future first executes and emit only when it resolves, panics, or is dropped after polling. Never emit at synchronous future construction. |
| `?` early return | Patched | Capture the returned `Result`/`Option` at the wrapper boundary and emit one normal completion event. |
| Panic unwinding | Patched | Drop guard emits `exceptionType` and no return digest/rendering. `panic=abort` remains outside observable completion. |
| `#[test]` functions | Patched test root | Emit one `isHarness:true` root per runner invocation with `isTestRoot:true` in the manifest; descendants inherit its structural test extent. |
| `const fn`, extern ABI functions, naked functions, declarations without local bodies, and forms whose rewrite changes ABI/const validity | Skipped: `UnsupportedShape` | Emit a manifest member with `detail` prefixed `Rust:`. No transform attempt may be reported as Patched. |
| Callables generated only by macro expansion unavailable to stable source parsing | Skipped boundary: `UnsupportedShape` | Record the original-source macro boundary with stable path/line identity and `Rust: MacroExpansionUnavailable`. Do not claim generated callable coverage. |
| Dependency-owned callables outside the selected source package | Out of instrumentation scope | Their values remain visible through Partial external markers at traced local boundaries. No dependency source is mutated. |

## Source, cache, and identity rules

- Hash every non-`target` input file, relevant Cargo metadata, tracer version, canonicalizer options, and selected scope into the cache key.
- Copy and rewrite into a staging directory, then atomically publish the content-addressed cache entry. Never write into the user's checkout.
- Prove checkout immutability by comparing non-`target` source hashes immediately before and after rewrite/build/test.
- Every event and member path names the original repository-relative `.rs` source and original one-based line. Cache/staging paths are forbidden.
- Stable callable identity includes crate/module path, callable name/signature, implementation type or trait, and concrete generic type names when available at runtime. It excludes cache roots, compiler symbol IDs, and runtime addresses.

## Completion and correlation rules

- Arguments are captured at entry. Returns are captured after normal completion.
- Panic unwinding emits `exceptionType` and no return digest/rendering.
- A dropped, previously-polled future emits the Rust cancellation category and no return digest/rendering.
- `?` returns are normal completions and retain their returned `Result`/`Option` digest.
- The root `#[test]` invocation owns one test extent. Every event in its logical synchronous/async subtree receives that root's `testId`.
- Conformance must report Cargo's executed-test count and the independently derived count of `isTestRoot` event invocations. Both inputs must be greater than zero before equality or zero guard failures are reported.

## Production gate

Two independently built stable rewriters must transform and trace the unchanged Rust reference suite into separate non-empty runs. The gate targets at least 300 matched subject keys and 50 subject methods, with a substantial reference suite covering the classifications above. It must report:

- Cargo runner tests and derived test-root invocations per run;
- matched subject keys, subject methods, and subject events per run;
- non-empty trace and manifest file counts;
- event-count and ordinal guard failures;
- unusable source events, subject roots, and cache-path source events;
- all ten shared digest proofs per run;
- engine raw and remaining divergences.

A reported zero is accepted only after the gate records the non-zero input population from which that zero was derived.
