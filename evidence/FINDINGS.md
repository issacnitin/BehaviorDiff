# Measured evidence

This document records the measurements behind BehaviorDiff's architecture. It is written for readers who have not followed the implementation history. Commands under `tools/` reproduce maintained behavioral claims; wall times are reference measurements, not performance guarantees.

## Why there is a trace specification

BehaviorDiff began as a .NET runtime patcher. Building [`TRACE-FORMAT.md`](../TRACE-FORMAT.md) exposed assumptions that had been distributed across the tracer, engine, and PowerShell proofs rather than stated as a contract:

- call ordinals must be assigned at entry; event/file order is completion order and changes under nesting or asynchronous settlement;
- `isHarness` events remain as call-tree roots but cannot be frontier candidates;
- optional fields are omitted rather than serialized as `null`, and malformed non-empty lines invalidate a run;
- source-resolution states are assertions about attribution, not permission to guess a path;
- backend-specific skip strings cannot control engine behavior, so skip reasons need a neutral vocabulary plus language detail;
- run language is required because digest equality is defined only within one canonicalizer domain;
- module member counts and writer counts must reconcile in the engine, not only in a proof script;
- `Patched` is a guarantee that an executed member can emit, not a record that a patch API returned success;
- identical partial digests do not establish equality inside skipped, depth-limited, errored, or truncated regions;
- test fixture construction can happen before a framework callback opens a test extent, so correlation must be explicit and structurally checked;
- a rendered-value cap is not a digest cap; the original proof did not differ beyond the rendered suffix and had to be strengthened.

The resulting format is language-neutral. .NET, Java, and Node emit the same event and manifest schemas. The engine, findings schema, posters, MCP server, and deterministic comment renderer consume only that contract.

Java implementation exposed two remaining shared-contract omissions before Node inherited them: instrumentation discovery values were still .NET-only in the contracts/parser, and inaccessible-field markers were not counted in digest statistics. Both were fixed before the Node tracer was built. Node then required explicit rules for JavaScript callable identity, source-map failure, safe unreadable regions, callback-based test roots, and worker-thread boundaries.

## .NET backend experiment: Harmony versus Cecil

The first prototype used Harmony runtime patching. The equivalence experiment compared per-method event counts against build-time Mono.Cecil weaving. The retained CSV contains 111 method rows:

| Measurement | Harmony runtime patching | Cecil build-time weaving |
| --- | ---: | ---: |
| Aggregate events in retained rows | 1,480 | 7,511 |
| Rows with equal event counts | 14 | 14 |
| Methods reporting `Patched` but emitting zero while Cecil emitted | 29 | 0 |
| Events in those 29 silent methods | 0 | 6,157 |

The aggregate totals differ by 6,031 rather than 6,157 because the retained rows also contain cases where Harmony's count exceeds Cecil's; aggregate difference and the silent-method subset measure different slices. The decisive result is the silent set: all 29 methods reported `Patched`, yet downstream consumers had no way to distinguish their 6,157 missing events from behavior that never ran.

Mono.Cecil rewrites IL before the JIT sees it. Repeating the Cecil run with and without minimized JIT optimization produced identical method and event sets. Raw per-method measurements are retained in [`inlining-evidence.csv`](inlining-evidence.csv).

## Instrumentation timing

Three repetitions were run and the minimum wall time retained:

| Configuration | Test wall time | Runtime overhead |
| --- | ---: | ---: |
| No tracing | 1,069 ms | baseline |
| Harmony runtime patching | 1,594 ms | +49% |
| Cecil build-time weaving | 1,099 ms | +2.8% |

Harmony additionally paid 697 ms installation cost on every run. Cecil paid a 1,731 ms one-time weave/process cost in the measured script. The table separates recurring test overhead from setup cost; it does not claim total pipeline time is 1,099 ms.

## External scale: FluentValidation

A merged FluentValidation change was analyzed as an external scale case:

```text
instrumented    : 638 library members, 1,054 test members
events per run  : approximately 99,600
traces          : approximately 563 MB across four runs
matched keys    : 45,519-45,537 across the measured runs
```

The same target and PR were run cold against an empty local baseline cache and then warm against the populated cache:

| Measurement | Cold cache miss | Warm cache hit |
| --- | ---: | ---: |
| Total CLI wall time | 339.705 s | 53.962 s |
| Build | 23.675 s | 13.264 s |
| Weave | 5.598 s | 2.729 s |
| Instrumented runs | 46.978 s | 11.844 s |
| Engine diff | 9.649 s | 10.386 s |
| Engine frontier | 6.998 s | 6.503 s |
| Five-stage measured total | 92.898 s | 44.726 s |

The warm run reduced end-to-end wall time by 84.1%, from 5 minutes 39.705 seconds to 53.962 seconds, a 6.3x speedup. It still pays for both clean builds, PR weaving, one instrumented test run, and the full engine. The cold run had 246.807 seconds outside the original five counters; that interval includes cache storage, worktree setup and cleanup, trace deletion, and orchestration. Subsequent telemetry records cache restore and store separately in the console and `findings.json.timings`; cleanup occurs after the analyzed artifact is written and remains outside its measured total.

The run exercised member lifecycle changes, generated members that could not be attributed through normal Git paths, and nondeterministic residual behavior in unedited caches. Peak diff working set was 1.33 GB against 416 MB of parsed traces. Memory currently scales with event count because parsed events are retained for comparison.

### C# versus Rust diff prototype

The qualified Rust diff prototype was measured on the later retained FluentValidation #2136 corpus with 53,245 matched keys, 602,256,055 event-trace bytes, and 5,853,647 manifest bytes. Each executable ran directly for three repetitions; wall time is the median, peak RSS is the maximum sampled working set, and amplification uses event-trace bytes as its denominator:

| Stage | Median wall | Peak RSS | Amplification over input trace bytes |
| --- | ---: | ---: | ---: |
| C# diff | 11.932 s | 2,058,170,368 bytes (1,962.8 MiB) | 3.4174x |
| Rust diff | 17.517 s | 2,150,195,200 bytes (2,050.6 MiB) | 3.5702x |
| C# frontier over C# output | 9.351 s | 738,562,048 bytes (704.3 MiB) | 1.2263x |
| C# frontier over Rust output | 8.961 s | 723,832,832 bytes (690.3 MiB) | 1.2019x |

Rust did not improve memory: it peaked 4.5% higher and its diff was 46.8% slower. The port retained the same whole-comparison materialization strategy. Rust currently implements diff only, so both frontier rows use the same C# frontier implementation over equivalent engine outputs. [`measure-engine-cost.ps1`](../tools/measure-engine-cost.ps1) writes the per-iteration JSON record.

## Container packaging

The single Linux/amd64 image was built with the production Dockerfile and inspected at 588,122,113 bytes (560.9 MiB). Its executable inventory was .NET SDK 8.0.424, OpenJDK 17.0.17, Maven 3.9.11, Node 24.19.0, npm 11.17.0, Go 1.27.0, and the Rust diff engine. The image also contains the .NET tracer and Cecil weaver, shaded Java agent, production Node tracer, Go source rewriter, CLI, and both diff implementations.

The Docker-only proof creates fresh Git histories from the Node and Java sort fixtures, runs complete base/PR analyses, and requires analyzed findings from both. The Node path enters through the published Action dispatch and verifies its outputs; the Java path enters through the normal image CLI. The host proof command invokes only Bash and Docker.

## Conformance gates

Each language tracer is built twice independently, traces an unchanged language-owned reference project, and feeds those runs to the real engine. The reusable harness requires a matched-key floor, identical subject method sets, identical per-key event counts, contiguous identical entry ordinals, usable source attribution, and ten digest proofs.

### Current language gates

| Metric | Java | Node JavaScript | Node TypeScript |
| --- | ---: | ---: | ---: |
| Runner tests / structurally derived roots | 120 / 120 | 120 / 120 | 120 / 120 |
| Matched keys | 139 | 158 | 158 |
| Distinct subject methods | 30 | 49 | 49 |
| Subject events per run | 151 | 187 | 187 |
| Unusable source / subject roots / wrong paths | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| Digest proofs | 10 / 10 | 10 / 10 | 10 / 10 |
| Engine raw differences / remaining divergences | 0 / 0 | 0 / 0 | 0 / 0 |

The broadened Java reference covers interfaces and virtual dispatch, abstract inheritance, generic methods and types, overloads, constructor chaining, named functional interfaces, arrays/lists, exceptions and recovery, recursion, and `CompletableFuture` chains. All 391 Java events per run, including 151 subject and 240 harness events, resolve to real repository-relative `src/main/java` or `src/test/java` paths.

The broadened Node references cover class inheritance, constructors and static methods, arrows, object methods, nested closures, arrays, recovered exceptions, promises, CommonJS, ESM, and explicit unsupported generators. Per run, writer accounting is `307 enqueued = 307 written`, zero dropped. TypeScript resolves 187 subject events and 52 subject members through source maps to one `.ts` source path and zero generated `.js` paths.

Jest and Vitest are exercised with their real runners:

| Framework | Runner executed/passed | Derived/distinct roots | Subject events | Correlation/source tripwires |
| --- | ---: | ---: | ---: | ---: |
| Jest | 3 / 3 | 3 / 3 | 7 | 0 |
| Vitest | 3 / 3 | 3 / 3 | 7 | 0 |

The .NET reference gate remains above the same 100-key refusal floor (242 matched keys in the current reference run). The language-specific table emphasizes Java and Node because they are the independent implementations used to test whether the specification was self-contained.

## Real source attribution and consumers

Conformance equality alone does not prove changed-file attribution. A Java mutation proof supplied exactly one real changed-file path and produced:

```text
events base1/base2/pr  : 391 / 391 / 391
exact source events    : 1,173
matched keys           : 139
changed files matched  : 1
edited files exercised : 1 / 1
expected / unexpected  : 110 / 0
```

A second Java proof and a Node proof edited intentionally excluded helpers, forcing downstream behavior in unedited files. Java rendered `io.behaviordiff.reference.Subject.observe(I)I`; Node rendered `samples/NodeReference/src/subject.js#AsyncSettlement.settle`. The real findings pipeline, GitHub renderer, Azure renderer payload, and MCP queries preserved native member names and paths:

| Consumer output | Java | Node |
| --- | ---: | ---: |
| GitHub deterministic comment | 3,741 bytes | 2,369 bytes |
| Azure renderer payload | 1,070 bytes | 1,251 bytes |
| MCP unexpected members | 1 | 3 |
| MCP selected call-path nodes | 2 | 4 |

No live network post or live model request is part of this proof. It exercises production rendering and MCP query code locally on freshly generated artifacts.

The packaged CLI proof installs the generated .NET tool without source-tree tracer overrides. The Rust-enabled package was 4.89 MiB with 1,036 entries; the installed tool completed four Java runs (1,084 events) and four Node runs (1,228 events), both with clean analyzed findings.

## Noise baseline

Nondeterministic keys found as base samples increased:

| Base runs | Noise keys |
| ---: | ---: |
| 2 | 2,122 |
| 3 | 2,326 |
| 4 | 2,388 |

Three base runs are the current operating point. They captured roughly 97% of the keys found by four runs in this subject while saving one full test invocation. This is sampled evidence, not a proof that a fourth or later run cannot reveal more noise.

## Comment confidence benchmark

The maintained demo suite measures the default comment policy against three known behavioral changes. A finding is default-eligible when it is frontier-verified, Exact, stable across the baseline runs, and connected to the change by an ancestor or descendant divergence in the call tree. The edited helper is deliberately excluded in each demo and contributes zero traced members; that absence is not a disqualifier.

| Demo | Confidence | Unexpected members / call sites | Default comment | Edited traced members |
| --- | --- | ---: | --- | ---: |
| Stable sort | high | 1 / 3 | visible | 0 |
| Retry fallback | high | 1 / 2 | visible | 0 |
| Configuration threshold | high | 1 / 2 | visible | 0 |

None of the three findings was classified as baseline nondeterministic. Independent Java and Node stable-sort proofs also render one high-confidence default finding each while the edited helper emits no events.

## Real-repository comment policy measurement

The fresh empirical cohort pinned merged pull requests by base and head SHA across three public repositories in .NET and Java. Each analyzed case used three baseline runs, one pull-request run, the production CLI, and the default confidence policy. Two refused attempts were replaced to retain 20 analyzed rows; refusals remain visible and are not counted as clean.

| Outcome | Pull requests | Rate among analyzed |
| --- | ---: | ---: |
| Default comment | 0 | 0 / 20 (0%) |
| Raw behavioral findings | 20 | 20 / 20 (100%) |
| Analyzed | 20 | 100% |
| Refused | 2 | excluded |

The analyzed rows comprise 15 FluentValidation PRs, four JSON-java PRs, and GuardClauses #350: 16 .NET and four Java cases. They contain 463 raw unexpected members across 6,210 call sites and zero default-eligible members.

All 20 analyzed rows lacked causal connectivity. Eighteen also had baseline nondeterminism, 17 had non-Exact evidence, and 16 had an unverified frontier. No would-post comments existed to classify. Unlike the former reachability rule, the same policy makes every maintained known-true fixture default-visible, so the zero comment rate is measured with positive-control sensitivity restored. It remains a comment-rate result, not a precision estimate.

[`comment-policy-results.csv`](comment-policy-results.csv) retains the 20 analyzed outcomes and the two excluded refusals. [`comment-policy-summary.json`](comment-policy-summary.json) records the aggregate denominators and rates. The broader pinned candidate manifest remains in [`comment-policy-merged-prs.csv`](comment-policy-merged-prs.csv).

[`comment-policy-audit.md`](comment-policy-audit.md) records known-true sensitivity, the two refusals, GuardClauses correlation repair, and Commons Text trace-integrity repair.

## Public stable-sort demonstration

The maintained .NET demo changes one file in an excluded infrastructure project:

```text
src/Infrastructure.Collections/SortingExtensions.cs
```

The proposed implementation restores stable ordering with `src.OrderBy(key).ToList()`. The result is:

```text
edited files             : 1
traced edited members    : 0
diverged keys            : 5
verified frontier nodes  : 3
unexpected members       : 1
headline                 : Commerce.Pricing.DiscountEngine.SelectDiscount
selection                : CLEARANCE_40 -> SEASONAL_15
checkout total           : 60 -> 85
executing tests          : 3
assertion reactions      : 1
```

The unstable baseline selection was identical across 20 fresh probe processes. The executable fixture proof also launches five fresh proposed-change processes and verifies that every one selects `SEASONAL_15`.

- [Demo pull request](https://github.com/issacnitin/behaviordiff-live-verification/pull/4)
- [Hosted workflow run](https://github.com/issacnitin/behaviordiff-live-verification/actions/runs/32369192452)
- [BehaviorDiff summary](https://github.com/issacnitin/behaviordiff-live-verification/pull/4#issuecomment-5353980185)

## Reproduce maintained claims

```powershell
dotnet build BehaviorDiff.sln -c Release
pwsh -File tools/verify-contracts.ps1
pwsh -File tools/conformance-dotnet.ps1
pwsh -File tools/conformance-java.ps1
pwsh -File tools/conformance-node-js.ps1
pwsh -File tools/conformance-node-ts.ps1
pwsh -File tools/verify-java-attribution.ps1
pwsh -File tools/verify-node-attribution.ps1
pwsh -File tools/verify-cross-language-consumers.ps1
pwsh -File tools/verify-cli-package.ps1
```

Measurements vary by hardware and SDK patch version. Behavioral assertions in the proof scripts are exact; wall-clock and memory figures are reference values.
