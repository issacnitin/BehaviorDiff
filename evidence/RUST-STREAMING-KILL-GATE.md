# Rust streaming prototype kill-gate

The streaming prototype is authorized only as a bounded probe against the retained FluentValidation #2136 corpus.

Before frontier implementation, one direct `stream-probe` process must satisfy every condition below:

- peak resident working set is strictly less than 300 MiB (`314,572,800` bytes);
- exactly `423,974` trace events are consumed across base1, base2, base3, and PR;
- exactly `53,245` subject behavioral keys match between base1 and PR;
- exactly `2,649` subject behavioral keys enter the three-run noise exclusion set;
- compact base1 and PR call graphs are constructed while consuming the same events.

The RSS condition is a hard stop. A result equal to or above 300 MiB ends streaming-engine work regardless of proximity. The threshold will not be raised and frontier work will not begin. Rust then remains frozen as the qualified alternative implementation and work moves to the Rust tracer prototypes.

If the pre-frontier gate passes, the completed streaming diff/frontier path must remain below 500 MiB and preserve byte-identical final `findings.json` modulo only `generatedUtc` before it can replace the existing Rust implementation behind `--engine=rust`. C# remains the default throughout. The old Rust implementation remains selected until the new implementation passes the full existing corpus gate.

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

Rust reduces peak engine-interval RSS by 85.41% cold and 85.83% warm, but is 1.548x slower end-to-end cold, 1.974x slower end-to-end warm, and 4.485x to 4.720x slower across diff plus frontier. The requested default-switch condition therefore does not hold. C# remains the default; Rust remains explicit through `--engine=rust`.

After this qualification, explicit `--engine=rust` dispatch moved from `diff` to `stream-diff`. C# remains the default engine, and the old Rust `diff` command remains available as a qualified fallback.

Run the hard gate with:

```powershell
pwsh -File tools/verify-rust-streaming-kill-gate.ps1 `
  -WorkDirectory $env:TEMP\behaviordiff-fluentvalidation-cli\rust-corpus
```