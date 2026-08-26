# Stable Rust tracer production report

## Landed boundaries

The Rust tracer uses stable Rust 1.98 source rewriting through `syn`/`quote`. It has no nightly, `rustc_private`, MIR-pass, derive, annotation, or compiler-version coupling. The implementation landed in independently validated commits:

| Boundary | Commit |
| --- | --- |
| TRACE-FORMAT Rust scope and parser identifiers | `a853a89` |
| External SHA-256 rewrite cache and source immutability gate | `b9590b4` |
| Normal, panic, cancellation, and `?` completion hooks | `b6a4202` |
| Generated private struct/enum canonicalizer core | `afeb388` |
| Argument/return digest capture and counted partial markers | `836306f` |
| Original `.rs` source mapping | `4116dd3` |
| Cargo test structural correlation | `7c24585` |
| Async context suspension across `Pending` | `e4d55df` |
| Reconciled trace coverage manifests | `e84a980` |
| Concrete generic monomorphization identities | `d58e650` |
| Two-build conformance gate | `abdaf88` |
| CLI attachment and NuGet/container packaging | `ddbbeb3`, `cfb405d`, `058b8df` |
| Qualified Rust behavior demo | `0a6f48a` |
| Completion-shape and macro-boundary closure | `db1cf9f` |

The source tree is never rewritten. The cache gate hashed 4 non-`target` project files containing 2 Rust files before and after rewrite/build/test; source hash changes were 0. A cache miss and hit addressed the same SHA-256 key, and the rewritten cache compiled independently.

## Classification achieved

| Shape | Observed production behavior |
| --- | --- |
| Primitive scalars, strings, tuples, arrays/slices, `Option`, `Result` | Exact when every child is supported |
| Local private structs and enums | Generated reader beside the defining type; private fields/variants participate without reflection |
| Standard `Vec`, deque, tree/hash maps, and tree/hash sets | Deterministic shape-tagged canonical text; unordered entries sorted by child canonical text |
| Reference graphs | Traversal-order references; cycles terminate; runtime addresses excluded |
| Generic callables | Runtime concrete type suffixes; two calls produced `generic_shape<alloc::string::String>` and `generic_shape<i32>` identities plus one skipped source template |
| Generic value regions not source-resolvable as concrete | Counted `<skipped:generic:...>` Partial capture |
| `dyn Trait` values | Counted `<skipped:trait-object:...>` Partial capture; concrete implementation calls remain separately traced |
| Unions | Counted `<skipped:union:...>` Partial capture; no active-field read |
| Dependency-owned/opaque values | Counted `<skipped:external:...>` Partial capture |
| Macro-generated callables unavailable to stable parsing | Original invocation recorded as `UnsupportedShape`, detail `Rust: MacroExpansionUnavailable` |
| Const/extern callable shapes | Manifest `UnsupportedShape`, not reported as patched |

No unread region is silently omitted. The final conformance run contained 13 blocklisted regions per run and 13 Partial matched keys: 9 skipped-marker keys, 2 depth-marker keys, and 2 truncation-marker keys.

## Completion and correlation measurements

The focused main-program gate consumed 4 source files and 2 Rust files and emitted 16 records: 14 normal, 1 panic, and 1 dropped-future cancellation. It observed 2 normal `?` exits, 1 async completion, 1 async cancellation, 0 synchronous normal returns for the cancelled future, and 0 panic return fields. Source hash changes were 0.

A runtime test polls a traced future to `Pending`, then verifies the logical stack is empty before cancellation. This prevents another future on the same executor thread from inheriting a suspended call as its parent.

## Conformance table

Two stable rewriters were independently built from separate clean source copies. Each rewrote and traced the same unchanged reference suite into a separate cache/run, then the shared conformance harness and real engine consumed both runs.

| Measurement | Run 1 | Run 2 |
| --- | ---: | ---: |
| Cargo runner tests | 8 | 8 |
| Derived test-root invocations | 8 | 8 |
| Trace events | 416 | 416 |
| Subject events | 408 | 408 |
| Matched subject keys | 390 | 390 |
| Subject methods | 85 | 85 |
| Manifest records | 115 | 115 |
| Values digested | 967 | 967 |
| Blocklisted regions | 13 | 13 |
| Digest proofs passed | 10 | 10 |
| Completion-shape events | 12 | 12 |
| Completion-shape methods | 11 | 11 |
| Generic identities | 2 | 2 |
| Panic shape events | 1 | 1 |
| Cancellation shape events | 1 | 1 |
| Macro unsupported boundaries | 1 | 1 |

From those non-empty populations: guard failures were 0, unusable source events 0, subject depth-0 events 0, wrong/cache source events 0, source hash changes 0, engine raw differences 0, and engine remaining divergences 0.

The ten shared proofs were `NoUserCodeInvoked`, `CyclesTerminate`, `ReferenceTopology`, `UnorderedCollectionsStable`, `TimeAndIdentityNormalized`, `BlocklistBeforeRecursion`, `DepthMarker`, `TruncationMarker`, `UnreadableFieldMarker`, and `BeyondRenderedCap`.

## Installed-package proof

The installed tool package was 7.52 MiB with 1,038 entries. The installed Rust tracer resolved from `tools/net8.0/any/tracers/rust/win-x64/behaviordiff-rust-rewrite.exe`, not the source tree. Four Rust runs emitted 1,604 events total (401 per run), matched 378 keys, produced 7 harness roots and 0 orphans, and completed analyzed/clean. Existing package proofs remained non-empty: Java 1,084 events and Node 1,228 events.

## Behavior demo

The real CLI demo changed exactly one file, `src/config.rs`, which contains zero callables. The manifest represented it as an unobservable scanned source boundary; edited traced members remained 0.

| Measurement | Result |
| --- | ---: |
| Base events | 111 |
| Matched keys | 104 |
| Remaining occurrence divergences | 9 |
| Diverged method keys | 3 |
| Frontier nodes | 1 |
| Collapse | 3.0x |
| Edited files | 1 |
| Exercised edited files | 0 |
| Edited traced members | 0 |
| Untested members | 1 |
| Headline | `src/service.rs::biased_priority` |
| Headline source | `src/service.rs` (unedited) |
| Retained evidence records | 3 |
| Deterministic rendered comment | 1,059 bytes |

The rendered comment states that `i32:10` became `i32:15`, no test asserted on the changed value, and the repeated path was `src/lib.rs::priority_observation -> select_priority -> apply_priority_bias -> biased_priority` (`6x`).

The real Anthropic explainer was invoked against the same non-empty findings and exact one-file patch. Its response failed required exact-citation validation, so the model explanation was rejected and the deterministic evidence comment was rendered. No rejected model prose was accepted into the comment.

## Not landed or not conforming

- Rendering-only sensitive-field/content redaction is not implemented in the Rust canonicalizer. Digests include real values, but rendered values are not yet transformed according to the TRACE-FORMAT password/token/secret/content rules. This is a confidentiality blocker for unrestricted production use.
- `panic=abort`, process termination, stack exhaustion, and never-polled futures produce no completion event, consistent with the completion boundary but not observable behavior.
- Extern ABI, const, naked, and callable macro expansions unavailable to stable parsing remain `UnsupportedShape` boundaries.
- Dependency callables are not rewritten. Their values remain visible only as counted Partial regions at traced local boundaries.
- Standard `cargo test` `#[test]` correlation is qualified. Third-party test frameworks and parameterized adapters have not been separately qualified.
- The stable parser records a macro invocation boundary; it does not inspect compiler-expanded generated functions.

`TRACE-FORMAT.md` already covered generated build-time canonicalizers and explicit unread-region markers. Rust exposed the need to reserve `rust`/`RustAstRewrite`, define original-source mapping from a rewrite cache, and define a stable-parser macro-expansion boundary. The remaining redaction implementation gap is not a contract gap; the contract is explicit and the tracer does not yet satisfy it.
