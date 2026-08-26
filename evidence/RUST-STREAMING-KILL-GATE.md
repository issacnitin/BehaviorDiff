# Rust streaming prototype kill-gate

The streaming prototype is authorized only as a bounded probe against the retained FluentValidation #2136 corpus.

Before frontier implementation, one direct `stream-probe` process must satisfy every condition below:

- peak resident working set is strictly less than 300 MiB (`314,572,800` bytes);
- exactly `423,974` trace events are consumed across base1, base2, base3, and PR;
- exactly `53,245` subject behavioral keys match between base1 and PR;
- exactly `2,649` subject behavioral keys enter the three-run noise exclusion set;
- compact base1 and PR call graphs are constructed while consuming the same events.

The RSS condition is a hard stop. A result equal to or above 300 MiB ends streaming-engine work regardless of proximity. The threshold will not be raised and frontier work will not begin. Rust then remains frozen as the qualified alternative implementation and work moves to the Rust tracer prototypes.

If the pre-frontier gate passes, the completed streaming diff/frontier path must remain below 500 MiB and preserve byte-identical final `findings.json` modulo only `generatedUtc` before it can replace the existing Rust implementation behind `--engine=rust`. C# remains the default throughout this initial qualification. The old Rust implementation remains selected until the new implementation passes the full existing corpus gate.

## Prototype result

The isolated pre-frontier probe passed on the retained FluentValidation #2136 corpus:

| Measurement | Result | Required |
| --- | ---: | ---: |
| Peak RSS | 64,286,720 bytes (61.309 MiB) | strictly below 300 MiB |
| Wall clock | 2,580.463 ms | informational |
| Events consumed | 423,974 | 423,974 |
| Matched keys | 53,245 | 53,245 |
| Noise keys | 2,649 | 2,649 |
| Base1 compact call nodes | 105,989 | nonzero/full run |
| PR compact call nodes | 105,997 | nonzero/full run |

The probe validated each trace's writer accounting and failed on malformed or invalid event identity rather than skipping records. This result authorizes completed-path work; it does not qualify the streaming implementation for production dispatch.

## Completed-path FluentValidation result

The shadow `stream-diff` implementation then produced a semantically identical DivergenceSet and ran through the shared frontier/findings stages:

| Stage | Peak RSS | Wall clock |
| --- | ---: | ---: |
| Streaming Rust diff | 186,429,440 bytes (177.793 MiB) | 81,308.790 ms |
| Shared C# frontier | 331,595,776 bytes (316.234 MiB) | 7,962.990 ms |
| Shared C# findings | 392,015,872 bytes (373.855 MiB) | 15,356.680 ms |

Correctness remained exact:

- `423,974` events consumed;
- `53,245` matched keys;
- `2,649` noise keys;
- all DivergenceSet top-level sections semantically identical to C# after excluding only `generatedUtc`;
- frontier report byte-identical modulo `generatedUtc`;
- final `findings.json` byte-identical modulo `generatedUtc`.

The completed path therefore passes the 500 MiB requirement. Its diff is materially slower than both qualified implementations, so the result is a memory success rather than a throughput improvement.

## Complete retained-corpus qualification

The shadow implementation subsequently passed the complete retained corpus gate:

- generated normal fixture: semantic DivergenceSet equivalence;
- generated writer-accounting and ordinal faults: identical input errors, direct exit 2, and byte-identical refused findings modulo `generatedUtc`;
- generated all-harness volume fault: all seven refusal reasons identical, direct exit 4, and byte-identical refused findings modulo `generatedUtc`;
- .NET, Java, Node, and Go conformance corpora: equivalent results;
- retained .NET sort, retry, and config changes: equivalent DivergenceSets, frontiers, and findings;
- FluentValidation #2136: exact counts and completed-path results recorded above;
- JSON-java #1061, #1062, #1065, and #1068: equivalent DivergenceSets and byte-identical frontier/findings artifacts modulo `generatedUtc`;
- Commons Text #764: identical writer-accounting refusal and byte-identical refused findings modulo `generatedUtc`.

JSON-java #1068 produced the largest final artifact. Its streaming-path findings file and the retained reference were both exactly `223,596,124` bytes and byte-identical outside the `generatedUtc` values. The shared C# findings process reached an observed peak working set of `998,559,744` bytes (952.301 MiB). This exceptional output-cardinality cost is above 500 MiB, but it does not alter the explicitly FluentValidation-scoped completed-path gate. It is retained here so the production qualification does not imply a corpus-independent findings memory bound.

The #1068 ceiling was subsequently removed. Writing the same unbounded object graph directly to a `FileStream` still peaked at `976.070 MiB`, disproving the final string serialization as the cause. The artifact instead retained every occurrence-level evidence object and repeated complete call paths; the old artifact contained `621,558` path nodes even though comment consumers render at most 20 observations per member.

Findings now retain complete member-level counts, observing tests, confidence, eligibility, and summary policy over every divergence while projecting at most 20 ordered evidence records per member. Each member records `evidenceTotalCount` and `evidenceTruncated`. On #1068, six of 16 members are marked truncated and `6,669` repeated observations are omitted. A structural comparison against the original artifact found no differences in any policy-bearing member field, summary, comment policy, or coverage; the retained observations are the byte-identical first 20 in their original order, and consequences and edited files on recorded paths are unchanged.

The final #1068 findings run produced an `812,206`-byte artifact in `14.551` seconds with a sampled peak working set of `341,770,240` bytes (325.938 MiB), down `656,789,504` bytes (626.363 MiB) from the original peak. The artifact is 99.637% smaller. A fresh `stream-diff` gate then passed all ten analyzed retained inputs byte-for-byte through frontier and findings modulo only `generatedUtc`: SampleApp sort/retry/config, all four language conformance corpora, and JSON-java #1061/#1062/#1065. FluentValidation #2136 and JSON-java #1068 passed the same fresh analyzed gate separately; Commons Text #764 again produced identical direct exit 2, CLI exit 3 mapping, and refused findings.

## Full-pipeline engine comparison

FluentValidation #2136 was then measured through the complete CLI with independent empty caches for the cold runs and each engine's own populated cache for the warm runs. Peak RSS is the maximum summed working set of the CLI and its direct engine child from `engine part 1` through frontier completion. Independently rerun PR traces retain small baseline-observed call-site variation, so correctness qualification remains the byte-equivalent retained-trace corpus gate above rather than this timing run.

| Engine | Cache | End-to-end wall | Diff + frontier | Peak engine-interval RSS |
| --- | --- | ---: | ---: | ---: |
| C# | cold miss | 112.611 s | 19.173 s | 2,135.594 MiB |
| C# | warm hit | 64.831 s | 17.879 s | 2,178.270 MiB |
| Rust `stream-diff` | cold miss | 174.319 s | 85.995 s | 311.602 MiB |
| Rust `stream-diff` | warm hit | 127.986 s | 84.391 s | 308.719 MiB |

In this pre-optimization benchmark, Rust reduces peak engine-interval RSS by 85.41% cold and 85.83% warm, but is 1.548x slower end-to-end cold, 1.974x slower end-to-end warm, and 4.485x to 4.720x slower across diff plus frontier. The requested default-switch condition therefore did not hold at that point, so C# remained the default and Rust remained explicit through `--engine=rust`.

### Streaming profile before optimization

The retained FluentValidation #2136 traces were profiled directly with opt-in phase timers before changing the algorithm. This run consumed 424,024 events and 596,672,852 trace bytes and produced a 129,720,903-byte DivergenceSet with 53,255 matched keys and 3,181 remaining divergences. The direct process took 80.125 seconds wall clock; instrumented internal time was 79.300 seconds.

Profiling is output-neutral. The same release executable rerun against the same retained inputs with `REALDIFF_RUST_PROFILE` disabled completed in 79.145 seconds and produced the same 129,720,903-byte artifact. Profiling-enabled and profiling-disabled outputs had the same SHA-256 after excluding only `generatedUtc`: `94B18AF13936FCB0E4E2148E4CB5FDB80100EEA5186AC062A412A10F1270D707`.

| Rust `stream-diff` phase | Time | Share of internal time |
| --- | ---: | ---: |
| Artifact serialization, total | 75.144 s | 94.76% |
| Base and PR call-tree JSON within serialization | 58.720 s | 74.05% |
| Matched-key JSON within serialization | 13.276 s | 16.74% |
| Load all four trace runs | 3.033 s | 3.82% |
| Compare traces | 1.025 s | 1.29% |
| Borrowed event deserialization | 0.845 s | 1.07% |
| Reading trace lines | 0.320 s | 0.40% |
| 424,028 `stream_position` calls | 0.189 s | 0.24% |
| 1,273,126 interner lookups | 0.121 s | 0.15% |
| Digest compaction | 0.075 s | 0.09% |
| Manifest reads | 0.091 s | 0.11% |

Event input does not materialize `serde_json::Value` trees. Each line deserializes directly into a borrowed `BorrowedEvent<'_>` and retains compact IDs, digests, and byte locators. The expensive path is the inverse: call-tree and matched-key output serialize each compact record through a newly constructed `serde_json::Value` from `json!`, then pretty-print 129.7 MB of repeated strings. Compact storage therefore succeeds at the RSS goal but is expanded back into an allocation-heavy interchange artifact before frontier.

There is no spool file and no spool write/read pass. Evidence locators point into the original trace files. Artifact emission performed 400 seek/read/parse operations totaling 628,598 bytes and 3.763 ms: at most two reads for an evidence-bearing divergence, and 0.126 read-backs per remaining divergence averaged over this corpus. Evidence read-back is not a hotspot.

Each trace is read once. The three base runs and PR run took 0.847, 0.805, 0.576, and 0.805 seconds respectively. Additional work compares the retained compact key/signature maps; it does not reread event traces. Base1 and PR retain compact call graphs, while base2 and base3 do not.

The shared C# frontier was separately profiled against the same 129.7 MB Rust artifact:

| Frontier phase | Time | Share of 6.238 s |
| --- | ---: | ---: |
| Input JSON deserialization | 0.990 s | 15.86% |
| Call-tree indexing | 0.107 s | 1.72% |
| Per-diverged-key frontier loop | 4.848 s | 77.71% |
| Repeated linear source-line lookup within that loop | 4.445 s | 71.25% |
| Recursive descendant traversal within that loop | 0.372 s | 5.96% |
| Attribution | 0.148 s | 2.38% |
| Report write | 0.047 s | 0.75% |

The frontier graph traversal itself is not the slowdown. For every diverged key, source-line selection independently scans the 106,007-node base call tree with `FirstOrDefault`; that repeated linear lookup accounts for nearly all frontier computation.

The profile identifies two concrete optimization candidates, neither applied in this measurement commit: serialize compact records directly without per-record `json!` value trees, ideally avoiding the expanded call-tree/matched-key interchange entirely; and index source lines by `(testId, methodFullName)` once before frontier iteration. Default selection remains unchanged until an optimized implementation is rerun through the byte-equivalence corpus gate and the same cold/warm pipeline benchmark. The proposed reconsideration threshold is Rust within approximately 1.5x of C# while retaining the approximately 311 MiB peak.

### Optimized write path qualification

The interchange writer now emits compact JSON through a buffered `serde_json::Serializer`, and every production artifact section serializes borrowed projections directly from compact records without per-record `json!` or `Value` construction. The shared frontier builds its first-seen `(testId, methodFullName)` source-line index during the existing call-tree indexing pass.

On the same retained FluentValidation traces, direct profiled `stream-diff` wall time fell from 80.125 seconds to 4.390 seconds. Artifact serialization fell from 75.144 seconds to 0.236 seconds; base and PR call-tree emission took 0.089 seconds each, and matched-key emission took 0.042 seconds. Compact output reduced the DivergenceSet from 129,720,903 bytes to 107,343,031 bytes. The indexed frontier's internal time fell from 6.238 seconds to 2.045 seconds, with source-line lookup falling from 4.445 seconds to 0.004 seconds.

The post-change gate passed all ten retained analyzed inputs through semantic DivergenceSet comparison and byte-identical frontier and final findings comparison modulo only `generatedUtc`: SampleApp sort/retry/config, .NET/Java/Node/Go conformance, and JSON-java #1061/#1062/#1065. FluentValidation #2136 separately produced the same DivergenceSet semantic hash and a byte-identical 1,054,165-byte findings artifact modulo only `generatedUtc`.

The complete CLI was then remeasured with independent cold caches and per-engine warm caches:

| Engine | Cache | End-to-end wall | Diff + frontier | Peak engine-interval RSS |
| --- | --- | ---: | ---: | ---: |
| C# | cold miss | 114.469 s | 13.685 s | 2,130.137 MiB |
| C# | warm hit | 62.383 s | 14.037 s | 2,155.504 MiB |
| Rust `stream-diff` | cold miss | 94.419 s | 6.626 s | 321.410 MiB |
| Rust `stream-diff` | warm hit | 55.430 s | 7.331 s | 314.102 MiB |

Rust is 0.484x the C# diff/frontier interval cold and 0.522x warm while reducing peak engine-interval RSS by 84.91% cold and 85.43% warm. It also completes the full pipeline 17.52% faster cold and 11.15% faster warm. This clears the approximately 1.5x reconsideration threshold while retaining the bounded-memory design, so Rust is now the default engine. C# remains available explicitly through `--engine=csharp`.

The default `--engine=rust` dispatch uses `stream-diff`. C# remains available as an explicit managed fallback, and the old Rust `diff` command remains available as a qualified implementation fallback.

Run the hard gate with:

```powershell
pwsh -File tools/verify-rust-streaming-kill-gate.ps1 `
  -WorkDirectory $env:TEMP\realdiff-fluentvalidation-cli\rust-corpus
```