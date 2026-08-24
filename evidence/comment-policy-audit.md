# Comment policy audit

This audit records the fresh default-comment measurement after replacing edited-file reachability with causal call-tree connectivity. A default-eligible finding must be frontier-verified, use Exact digest evidence, be stable across the baseline runs, and have an ancestor or descendant divergence attributable to the change. An edited file contributing zero traced members is not itself a disqualifier.

## Known-true sensitivity

All maintained known-true fixtures are now visible under the production `high-confidence` policy. The edited helper is deliberately excluded in every row, so it contributes zero traced members, calls, divergences, and frontier nodes. Eligibility comes from the verified Exact finding's causal connection through the call tree.

| Fixture | Unexpected members / call sites | Edited traced members | Default result |
| --- | ---: | ---: | --- |
| .NET stable sort | 1 / 3 | 0 | visible |
| .NET retry fallback | 1 / 2 | 0 | visible |
| .NET configuration threshold | 1 / 2 | 0 | visible |
| Java stable sort | 1 / 3 | 0 | visible |
| Node stable sort | 1 / 3 | 0 | visible |

The Java proof retained 132 matched keys and collapsed 117 divergences to three frontier sites. The Node proof retained 129 matched keys and likewise collapsed 117 divergences to three frontier sites. Both rendered one default-visible high-confidence member and verified that the edited sorting helper emitted no events.

## Fresh clean-PR cohort

Twenty analyzable merged PRs were freshly run with three baseline traces, one PR trace, the production default policy, and pinned base/head SHAs. Two attempted JSON-java rows refused and were replaced to preserve an analyzed denominator of 20; refusals are reported separately and are not counted as clean.

| Outcome | Pull requests | Rate among analyzed |
| --- | ---: | ---: |
| Would post a default comment | 0 | 0 / 20 (0%) |
| Raw behavioral findings | 20 | 20 / 20 (100%) |
| Analyzed | 20 | 100% |
| Refused | 2 | excluded |

The 20 analyzed rows contain 463 raw unexpected members across 6,210 call sites and zero default-eligible members. All 20 had `no_causal_connectivity`; 18 also had baseline nondeterminism, 17 had non-Exact evidence, and 16 had an unverified frontier. Because no comment would post, there are no would-post comments to classify.

The analyzed denominator comprises 15 FluentValidation PRs, four JSON-java PRs, and GuardClauses #350: 16 .NET and four Java rows. [`comment-policy-results.csv`](comment-policy-results.csv) retains the 20 analyzed rows and both excluded refusals.

## Refused attempts

| Pull request | Cause | Assessment |
| --- | --- | --- |
| JSON-java #1062 | No member from the changed `CDL.java` reached the manifest. | Correct attribution refusal; the minimum attribution gate is unchanged. |
| JSON-java #1068 | 1,717 non-root events could not resolve a parent. | Correct call-tree integrity refusal; incomplete descendants cannot support a frontier verdict. |

## GuardClauses correlation

GuardClauses #350 now discovers the product scope as `Ardalis;GuardClauses`, weaves both project assemblies, and carries a test ID on 96.6% of 5,872 events. The run analyzes normally with one raw member and zero eligible members. Its former 47.8% correlation refusal was a project-scope discovery defect, not a policy threshold, and is fixed without weakening correlation requirements.

## Commons Text integrity

The Java tracer now bounds structural digestion to 16 elements per container and 1,024 total visited values, formats hashes and JSON control characters without per-byte/per-character `String.format`, recovers from unbalanced thread-local stacks, and contains sink failures. A session retains at most 100,000 complete events; every later event increments `dropped`, which preserves the existing engine refusal instead of treating truncation as sampling.

The previously runaway Commons Text method now completes in about 7.2 seconds. A full production-package rerun of Commons Text #764 completed all four instrumented runs. Each retained trace contains exactly 100,000 parseable NDJSON records, zero malformed lines, and a final newline. The three baseline traces are 132,031,595 bytes each and the PR trace is 132,029,568 bytes. Depending on the run, 45,041,416 to 45,042,129 events were enqueued and 44,941,416 to 44,942,129 were explicitly reported as dropped.

The engine refused on writer reconciliation before constructing a call tree: `run.7260` reported `enqueued=45041416 written=100000 records=100000 dropped=44941416`. The packaged CLI classified this `DiffInputException` as an explicit trust refusal (`status=refused`, exit 3, and `analysis_refused`) instead of a generic instrumentation failure. Commons Text is therefore refused for writer overflow, not malformed NDJSON or a corrupted call stack; dropped events are not treated as a valid sample.