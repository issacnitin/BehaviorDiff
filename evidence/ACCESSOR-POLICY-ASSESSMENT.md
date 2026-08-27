# Property accessor and operator policy assessment

Date: 2026-08-27

## Decision

Do not lift the property-accessor or operator trace policy on the measured evidence. A disposable .NET tracer probe removed most accessor-related `DescendantSkipped` reasons, but more than doubled event volume, increased trace bytes and engine peak RSS materially, and reduced rather than increased the number of verified frontier nodes.

This assessment changes only tracing policy in a disposable worktree. It does not change canonicalization: the canonicalizer must never invoke a getter while digesting an object.

## Corpus

- Repository: FluentValidation/FluentValidation
- Base: `ef50516decf652fd9f97090a4a4a9e114d458ce8`
- PR: `6eac0afe0f7c406a5ac3e9830fa4d9d7b03c25dc`
- Four runs per variant: three base and one PR
- Engine: current release build from `6e0c859`
- Measurement artifacts: `%TEMP%/realdiff-accessor-assessment-results`

The baseline retained traces predated the RealDiff schema rename. The measurement used hard links for event traces and changed only the temporary manifest schema literal from `behaviordiff.trace/1` to `realdiff.trace/1`; source traces were not modified.

## Results

| Measure | Current policy | Accessors enabled | Delta |
| --- | ---: | ---: | ---: |
| Event records | 424,024 | 889,590 | +465,566 (+109.8%) |
| Event trace bytes | 596,672,852 | 1,035,965,572 | +439,292,720 (+73.6%) |
| Manifest bytes | 5,853,631 | 8,328,093 | +2,474,462 (+42.3%) |
| `stream-diff` peak RSS | 182,255,616 B (173.8 MiB) | 308,908,032 B (294.6 MiB) | +69.6% |
| `frontier` peak RSS | 330,358,784 B (315.1 MiB) | 578,822,144 B (552.0 MiB) | +75.2% |
| Matched keys | 53,255 | 94,873 | +78.2% |
| Remaining divergences | 3,181 | 3,134 | -1.5% |
| Frontier nodes | 3,094 | 3,072 | -0.7% |
| Verified frontier nodes | 21 | 5 | -76.2% |
| `frontier_unverified` nodes | 3,073 | 3,067 | -0.2% |
| Unverified nodes citing a `get_`/`set_` descendant | 2,215 | 3 | -99.9% |

The earlier retained frontier report, produced before Bucket 3 generalized structural skip degradation, had 1,404 accessor-citing unverified nodes. Reprocessing the same traces through the current engine produces the 2,215 current-policy count above.

## Manifest inventory

One representative base manifest contains 4,602 members:

| Member policy | Current | Accessor probe |
| --- | ---: | ---: |
| Skipped property accessors | 501 | 111 |
| Patched property accessors | 0 | 390 |
| Skipped operators | 4 | 4 |
| Patched operators | 0 | 0 |
| Skipped event accessors | 4 | 4 |

The 111 accessors that remain skipped have independent structural reasons such as no executable body; the probe did not relabel them as patched.

## Interpretation

Tracing a getter body is semantically legitimate and distinct from invoking a getter during canonicalization. The probe preserved that distinction. However, on this corpus, tracing accessors creates many additional keys and events whose own partial or skipped descendants produce a weaker verified frontier set. The memory increase is no longer an automatic hard stop, but the combination of +109.8% event volume, +75.2% peak frontier RSS, and 21 to 5 verified frontiers does not justify lifting the policy.

Operators were inventoried but not enabled by this accessor-specific probe. Their measured population is four members, too small to explain the current degradation; they remain a policy exclusion pending a separate corpus with meaningful operator execution.
