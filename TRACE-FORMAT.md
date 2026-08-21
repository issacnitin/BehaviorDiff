# BehaviorDiff Trace Format

Version: `behaviordiff.trace/1`

This document is the normative contract between a language tracer and the BehaviorDiff engine. A tracer is conforming only when it emits the trace and coverage files described here and passes the conformance procedure below. Language-specific implementation details are non-normative unless this document labels them as requirements.

## Files and encoding

A traced process writes two UTF-8 files without a byte-order mark:

- `<run>.<process>.ndjson` is the event stream.
- `<run>.<process>.manifest.ndjson` is the coverage manifest for that stream.

Both are newline-delimited JSON (NDJSON): one complete JSON object per physical line, terminated by LF (`U+000A`), including on Windows. Empty lines are ignored. Fields not defined by this version may be ignored by a reader. A malformed or torn non-empty line invalidates the run; dropping it would make missing behavior look unchanged or removed.

JSON numbers used for identifiers and counts are signed integers. Producers MUST keep them in the exact ranges stated below. Strings are JSON strings and therefore use JSON escaping. Paths use `/` after normalization.

## Run metadata

Every process manifest begins with exactly one run record:

```json
{"kind":"run","schema":"behaviordiff.trace/1","language":"dotnet"}
```

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `kind` | string | yes | Always `run`. |
| `schema` | string | yes | Exactly `behaviordiff.trace/1`. An unsupported value is refused. |
| `language` | string | yes | Digest/canonicalizer domain. Version 1 reserves `dotnet`, `java`, and `node`. |

All process manifests merged into one run MUST agree on `schema` and `language`. Base samples and the proposed run MUST have the same language. The engine refuses a cross-language comparison because digest equality is defined only within one language.

## Scope and callable identity

Include and exclude scopes are language-owned strings, but their matching semantics are shared. A prefix matches an exact namespace, package, or repository path segment and its descendants. It does not match a longer sibling segment: `Acme.Cart` matches `Acme.Cart.Checkout` but not `Acme.Carts`, and `src/cart` matches `src/cart/item.js` but not `src/cart-old/item.js`. Exclude scope wins over include scope. A member excluded after discovery still receives a `Skipped` manifest record with `skipReason:"ExcludedByScope"`; otherwise a configuration difference can masquerade as removed behavior.

Callable identity MUST include a stable module/source identity and the language's stable callable signature. Languages with declared parameter types include them. JavaScript, which has no runtime overload signature, uses the repository-relative module path plus lexical callable identity; anonymous callables include their original source line and column as a discriminator. Generated output positions are never used when a source map establishes an original position. Build-specific absolute roots, generated symbol numbers, and runtime object identities are forbidden in `methodFullName`.

## Trace events

Each event describes one completed call. The event is enqueued only when the call completes normally, throws an escaping exception, or its asynchronous result settles. A call that never completes because of process termination, a hang, or stack exhaustion has no event.

```json
{"testId":"CartTest#1","methodFullName":"Acme.Cart.add(java.lang.String,int)","filePath":"src/main/java/Acme/Cart.java","filePathResolution":"debugInfo","line":42,"callDepth":1,"parentCallId":10,"callId":11,"ordinal":0,"argsDigest":"sha256:...","argsRendered":"item=Primitive:\"A\"","returnDigest":"sha256:...","returnRendered":"Primitive:true","threadId":7}
```

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `testId` | non-empty string | yes | Stable identity of the test extent containing the call. A test invocation, including a parameterized case, has one identity. Calls outside a test use `(no-test)` and cannot support normal comparison. |
| `methodFullName` | non-empty string | yes | Tracer-defined canonical member identity. It MUST be stable across processes and builds when the source-level callable is unchanged, follow the callable-identity rules above, and exactly match the manifest member key. Canonicalization is language-specific. |
| `filePath` | string | no | Source file declared by debug metadata, preferably repository-relative. Omitted only when unresolved. Never guess from a type or package name. |
| `filePathResolution` | string | yes | One of the resolution states below. |
| `line` | 32-bit integer | yes | One-based declaration/executable source line; `0` when the selected resolution does not establish a line. |
| `callDepth` | non-negative 32-bit integer | yes | Depth in the logical traced call stack at entry. A root is `0`. Logical context MUST cross supported asynchronous continuations. |
| `parentCallId` | 64-bit integer | no | `callId` of the logical traced parent in this process file. Omitted for a root. |
| `callId` | positive 64-bit integer | yes | Unique within one process trace. Assigned at entry, never reused. |
| `ordinal` | non-negative 32-bit integer | yes | Entry order within `(testId, methodFullName)` in this process: `0,1,2,...` without gaps or duplicates. It is assigned at entry because event/file order is completion order. |
| `argsDigest` | string | no | Digest of the full canonical argument rendering captured at entry. Omitted when capture was not possible. |
| `argsRendered` | string | no | Human-readable canonical argument rendering. It may be capped, but the digest covers the uncapped canonical text. Omitted with `argsDigest`. |
| `returnDigest` | string | no | Digest of a normally completed non-void result. Omitted for void, throw, cancellation represented as an exception, or failed capture. |
| `returnRendered` | string | no | Canonical result rendering corresponding to `returnDigest`. |
| `exceptionType` | string | no | Canonical fully qualified type/name of the escaping exception. Omitted on normal completion. |
| `threadId` | 32-bit integer | yes | Runtime thread/worker identity at call entry. It is diagnostic and not a matching key. |
| `isHarness` | boolean | no | `true` for test-framework/harness code; omission means `false`. |

The engine compares calls by `(testId, methodFullName, ordinal)`. It compares `argsDigest`, `returnDigest`, and `exceptionType`; rendered values explain a difference and determine whether equality is partial. `callId` and `parentCallId` reconstruct the tree and are not compared as behavior.

### Completion invariant

Instrumentation MUST enter before user instructions and MUST emit exactly once at method exit on every completion path:

- normal synchronous return: include a return digest for non-void values;
- escaping synchronous throw: include `exceptionType` and no return digest/rendering;
- asynchronous return: retain the frame and emit when the promise/task/future settles, not when the method synchronously returns its handle;
- asynchronous failure/cancellation: include the escaping/settlement exception category and no return digest.

Emission at synchronous return from an asynchronous method records an unfinished handle instead of behavior. Missing the exceptional path removes calls from one side and creates false call-count or missing-key divergences. Double emission shifts every later ordinal.

### Call-tree and harness invariant

Every non-root event MUST resolve `parentCallId` to an event in the same process file. The engine refuses orphaned trees because an omitted descendant can turn a changed caller into a false frontier.

`isHarness` identifies code owned by the test runner or test assembly, not merely methods with a test-like name. Harness events remain in the trace as roots and as assertion-reaction evidence. They are compared separately and are never frontier candidates. Removing them destroys test extents and the engine's evidence that an assertion reacted.

A framework-independent tracer SHOULD derive correlation structurally: one `isTestRoot` member invocation opens a test extent, and every event in its logical subtree receives that root's `testId`.

When a framework invokes user tests through callbacks rather than an attributable test method, a language adapter MAY open the root immediately around that callback. The adapter is responsible only for opening and closing the root; descendant correlation still derives from the logical call tree. Conformance MUST compare the number of opened root invocations with the runner's own executed-test count, including parameterized cases.

## Source resolution

`filePathResolution` records what the path asserts. Version 1 defines these language-neutral values:

| Value | `filePath` | `line` | Assertion |
| --- | --- | --- | --- |
| `debugInfo` | required | positive | The callable's own debug table/source map, or a parser reading the original ungenerated source directly, establishes this exact source position. |
| `generatedState` | required | positive | Debug metadata on a generated async/iterator state body establishes the source position for its source-level kickoff callable. |
| `declaringType` | required | `0` | A sibling member's debug metadata establishes the declaring source file, but no exact line. |
| `debugInfoMissing` | omitted | `0` | The module has no usable debug table/source map. |
| `unresolved` | omitted | `0` | Debug information exists but does not establish a source file for this member. |

The .NET v1 emitter uses wire aliases `sequencePoints`, `stateMachine`, `declaringType`, `noPdb`, and `unresolved`, respectively. Readers MUST treat those aliases as the corresponding states above during the v1 migration.

For JavaScript loaded directly from its repository source, the parser's original location is `debugInfo`. For generated JavaScript with a valid source map, only the mapped original path and position are `debugInfo`; the generated `.js` position is not an attribution fallback. If generated output has no usable map, emit `debugInfoMissing` with no path. If a map exists but is malformed, names no matching source, or cannot map the callable position, emit `unresolved` with no path. TypeScript positions therefore resolve to `.ts`/`.tsx` sources or remain explicitly unusable; a tracer never guesses by replacing a `.js` extension.

Node worker threads are separate JavaScript isolates with separate globals and asynchronous context. Version 1 does not propagate tracing into `worker_threads`. A transformed module that imports `node:worker_threads`/`worker_threads` or constructs `Worker` MUST add a skipped boundary member with `skipReason:"UnsupportedShape"` and language detail such as `Node: WorkerThreadsOutOfScope`. This makes potentially unobserved descendants degrade confidence instead of disappearing silently. A future implementation may bootstrap each worker as an independent trace/manifest producer, but it must not merge worker call stacks into the main isolate by assumption.

Usable attribution requires `debugInfo`, `generatedState`, or `declaringType`. Exact source statistics count only `debugInfo` and `generatedState`. An unresolved state is evidence, not permission to infer a path. Guessing makes every changed path miss and can invert an unexpected change into a clean result.

The conformance source tripwire requires all exercised subject events in the reference project to have a usable state and requires zero subject roots. Harness events may be unresolved because they are not attributed.

## Coverage manifest

The manifest is a snapshot written after registration and completed after the event writer drains. Its records may appear in any order except that the run record is first and the writer record is last.

### Module record

The historical wire discriminator and identifier are `kind:"assembly"` and `assembly`; in version 1 their normative meaning is language module. Java uses a class-loader/module unit and Node uses an instrumented source module or package unit.

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `kind` | string | yes | `assembly`. |
| `assembly` | non-empty string | yes | Stable module identity within the run. |
| `discovery` | string | yes | Instrumentation mechanism: `BuildTimeWeave`, `JavaAgentTransform`, or `NodeAstTransform`. |
| `scanned` | boolean | yes | Member discovery completed for this module. |
| `instrumented` | boolean | yes | At least one member was instrumented. |
| `patchedMembers` | non-negative integer | yes | Number of instrumented members. The historical name means `instrumentedMembers`. |
| `discoveredMembers` | non-negative integer | yes | Number of member records discovered in this module. |
| `skippedMembers` | non-negative integer | yes | Number of deliberately skipped member records. |
| `patchFailedMembers` | non-negative integer | yes | Must be `0` in a conforming completed run. A transform/verification failure is a build error. |
| `queuedAtMs` | non-negative integer | yes | Milliseconds from tracer startup to transform queueing; `0` for build-time transformation. |
| `patchedAtMs` | non-negative integer | no | Milliseconds to successful transform completion; `0` for build-time transformation. |
| `tracedCalls` | non-negative integer | yes | Events observed from this module, not calls that may have escaped instrumentation. |
| `membersWithExactSource` | non-negative integer | yes | Instrumented members resolved by exact debug information. |
| `exactSourcePercent` | integer 0..100 | yes | `membersWithExactSource / patchedMembers`, or `100` when no members were instrumented. |
| `sourceRule` | string | yes | `ratio`, `anyNone`, or `notApplicable`; states how source availability was judged. |
| `sourceUnavailable` | boolean | no | `true` means attribution is too incomplete for a verdict. Omission means false. |
| `sourcePartial` | boolean | no | `true` means some individual members are unresolved; those descendants degrade frontier confidence. |
| `isTestAssembly` | boolean | no | Historical name for a module wholly owned by the test harness. Omission means false. |
| `testFrameworkReference` | string | no | Language-specific evidence used to classify a harness module. |
| `detail` | string | no | Diagnostic detail; never a matching key. |

For every module, this reconciliation MUST hold:

```text
discoveredMembers = patchedMembers + skippedMembers
patchFailedMembers = 0
```

The member-record count for the module MUST equal `discoveredMembers`. The engine refuses a mismatch because an unaccounted member is an unknown call edge; treating it as absent would make frontier analysis unsound.

### Member record

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `kind` | string | yes | `member`. |
| `assembly` | non-empty string | yes | Owning module key. |
| `method` | non-empty string | yes | Exact `methodFullName` used by events. |
| `status` | string | yes | `Patched` or `Skipped`. `PatchFailed` and `EnumerationFailed` are legacy values and invalidate a conforming completed run. |
| `skipReason` | string | iff skipped | One neutral reason from the table below. |
| `detail` | string | iff skipped/failure | Language-specific reason, prefixed by language, for example `.NET: ByRefOrPointer`. |
| `returnKind` | string | yes | Language-specific completion shape used to select synchronous/asynchronous instrumentation. |
| `isTestRoot` | boolean | no | `true` when invocation opens a test extent. Omission means false. |
| `sourceResolution` | string | yes | Same resolution vocabulary as events. |

`Patched` is a behavioral guarantee, not a transform-attempt log entry. If the member executes, its entry hook and every completion hook MUST be reachable and produce one event. A verifier failure MUST stop the build. The retired .NET runtime patcher violated this invariant: 29 methods reported `Patched` but silently lost 6,157 events because JIT inlining bypassed hooks. No downstream stage could distinguish those missing events from removed behavior.

### Neutral skip reasons

`DescendantSkipped` and intentional-scope logic key only on `skipReason`; `detail` is explanatory and MUST NOT affect engine semantics.

| Neutral value | Meaning |
| --- | --- |
| `Unobservable` | A generated/runtime companion is represented by another instrumented source-level member, or observing it would violate runtime safety. It does not create a hidden descendant candidate. |
| `CompilerGenerated` | Compiler-generated callable not represented as an independently attributable source member. |
| `ExcludedByScope` | Explicit include/exclude or callable-kind policy excluded it. |
| `UnsupportedShape` | The tracer cannot preserve semantics for this callable shape. |
| `DeclaredExternally` | No executable body belongs to the instrumented module. |

.NET worked mapping:

| .NET detail | Neutral value |
| --- | --- |
| `CompilerGeneratedType`, `CompilerGenerated` | `CompilerGenerated` |
| `StateMachineType`, `TypeInitializer` | `Unobservable` |
| `ExcludedNamespace`, `PropertyOrOperator` | `ExcludedByScope` |
| `ByRefOrPointer`, `GenericTypeDefinition`, `GenericDefinition`, `Unresolvable`, `WeaverAsyncNotSupported` | `UnsupportedShape` |
| `NoBody`, `DeclaredOnSystemType` | `DeclaredExternally` |

### Digest statistics record

One `kind:"digest"` record summarizes the process canonicalizer:

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `valuesDigested` | non-negative integer | yes | Values entered into canonicalization, including nested values. |
| `depthLimited` | non-negative integer | yes | Values replaced by a depth marker. |
| `blocklisted` | non-negative integer | yes | Values deliberately not traversed. |
| `errored` | non-negative integer | yes | Field/internal reads represented by an error marker. |
| `renderedTruncated` | non-negative integer | yes | Renderings capped after hashing their full canonical text. |

Optional `kind:"unruled"` records contain `typeName` (string) and `count` (non-negative integer). They identify collection/enumerable shapes that fell back to field digestion and may expose incidental storage state.

### Writer record

The final `kind:"writer"` record has required integer fields `enqueued`, `written`, `dropped`, and `capacity`. A conforming run satisfies:

```text
enqueued = written = non-empty physical event lines
dropped = 0
capacity > 0
```

The engine refuses a mismatch. Missing events are observationally identical to removed calls.

## Digest contract

A digest establishes self-consistency only within one language and canonicalizer version. Java, Node, and .NET digests are never compared with one another. A conforming canonicalizer is deterministic and stable across processes for the same supported value graph and options.

Canonicalization MUST NOT execute user code. In particular it MUST NOT invoke getters, `toString`/`ToString`, user equality/hash methods, iterators, enumeration protocols, proxy traps, or callbacks. It may use runtime primitives that read fields or collection backing storage without dispatching into the observed program.

Canonicalization MUST:

- distinguish scalar types and container shapes so unlike values do not share text accidentally;
- sort unordered collection entries by child canonical text/digest;
- track reference identity without calling user equality, terminate cycles, and emit stable back-references;
- normalize wall-clock timestamps, random/runtime identity values, absolute paths, temporary file names, and equivalent process-specific values;
- cap recursion and collection breadth deterministically;
- hash the complete canonical text before applying a display cap;
- record every unread region with a marker rather than silently omitting it.

Version 1 digest strings use `sha256:<lowercase hex>` over UTF-8 canonical text. Canonical text grammar and shape rules are language-specific; agreement across languages is neither required nor meaningful.

The engine recognizes these partial markers anywhere in `argsRendered` or `returnRendered`:

| Marker | Meaning |
| --- | --- |
| `<skipped:DETAIL>` | A blocklisted value/region was deliberately not traversed. |
| `<depth:TYPE>` | The depth limit replaced the remaining subgraph. |
| `<error:DETAIL>` | A field/internal read failed and the unread value was represented only by the failure. |
| `<truncated>` | Human rendering was capped. The digest still covers full canonical text, but a reader cannot inspect the hidden suffix. |

A key containing any marker is `Partial`. Different partial digests still prove an observed difference. Identical partial digests do **not** establish identical behavior: two values may differ solely inside skipped, depth-limited, errored, or undisplayed regions. A partial node or identical partial descendant therefore degrades a frontier to `frontier_unverified`.

## Conformance suite

Conformance uses a language-owned reference project containing nested calls, throws, repeated calls, asynchronous completion, source/debug metadata, harness roots, cycles, private-field differences, normalized time/identity values, blocked values, depth limits, truncation, and unreadable fields.

Build the tracer twice independently from clean outputs. Trace the unchanged reference project once with each build. Feed those run directories to the normal engine as `base1` and `base2` (and one again as `pr` when the command requires it), with an empty changed-file set. The engine MUST produce zero divergences. This tests the real consumer rather than a tracer-specific comparator.

Before accepting zero divergences, the reusable harness enforces four guards:

1. **Matched-key threshold:** at least the reference project's declared minimum number of subject `(testId, methodFullName)` keys matched. An empty comparison never passes.
2. **Identical method sets:** the distinct subject `methodFullName` sets are exactly equal. This catches scope/canonical-name drift.
3. **Identical per-key event counts:** for every `(testId, methodFullName)`, both runs contain the same number of events. This catches missing exits and async early emission.
4. **Identical per-key ordinal sequences:** each run has exactly `0..count-1`, and the sequences match. This catches completion-order ordinals, duplicates, and dropped entry assignments.

The **source-resolution tripwire** requires zero exercised subject events with an unusable resolution and zero subject events at depth 0. It also checks that the language's source path maps to the reference source file, not generated output.

The **digest proofs** require: no user-overridable operation was invoked; cycles terminate deterministically; shared references differ from equal copies when topology differs; unordered collections are process-stable; time and identity normalize; blocklisting occurs before recursion; depth and truncation markers execute; unreadable regions produce an explicit partial marker and do not collide with readable values; and differences beyond the rendered cap still change the full digest. A failed field/internal read uses `<error:...>`. A region that the runtime can identify as unsafe before reading, such as a JavaScript proxy or accessor, uses `<skipped:...>` instead; conformance never requires triggering user code merely to manufacture an error.

Each language runs this mechanism against its own reference project and reports actual guard counts, tripwire counts/states, and each digest proof result. A format gap found by a language gate updates this document before the next language starts.

## Ownership boundary

Language-agnostic components consume only this contract:

- matching, noise filtering, manifest reconciliation, confidence classification, frontier analysis, attribution, and findings generation in the engine;
- `findings.json` and its schemas;
- GitHub and Azure DevOps posters;
- MCP server and deterministic/model explainers.

Language-specific components are:

- attachment/build integration and bytecode/AST/IL rewriting;
- callable identity canonicalization and scope selection;
- value canonicalization and collection shape rules;
- source resolution (PDB/sequence points, JVM debug attributes, or JavaScript source maps);
- test-root recognition and correlation adapters where structural correlation cannot open the root;
- async frame propagation and runtime concurrency integration.

Path normalization after a tracer has supplied a real source path is engine-owned and language-agnostic. A tracer never fabricates a path to satisfy attribution.