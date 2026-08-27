# RealDiff comment ranking: before and after

The verbatim six-comment snapshot from before this change is in [realdiff-six-demo-comments.md](realdiff-six-demo-comments.md). The pull requests linked below contain the live rerendered comments.

## Before

All six comments began with the same generic heading:

```text
## RealDiff: 1 behavior gap outside this diff
```

The unasserted origin and the consequence caught by a test were presented together in the lead or evidence. The reader could not tell which change would have merged silently and which one CI had already exposed. Added members could also enter lifecycle divergences even though they had no base behavior to compare.

## After

All six comments now begin:

```text
## RealDiff: 1 member changed behavior with no assertion reacting

These would have merged silently.
```

The unasserted origin remains the headline. A caught consequence is retained directly beneath it:

```text
Caught by existing assertions
- checkout total changed from the base value to the PR value; an assertion reacted.
```

Only unasserted members receive full finding details and inline cause comments. Added members and tests are reported under `Added-code coverage` and are excluded from behavior-change counts.

## Six live rerenders

| Language | Unasserted origin | Caught supporting consequence | Live comment |
| --- | --- | --- | --- |
| .NET | `SelectDiscount`: `A_SEASONAL` -> `Z_CLEARANCE` | `Compute`: `85.00` -> `60.00` | [PR #1](https://github.com/issacnitin/realdiff-sort-dotnet/pull/1) |
| Java | `selectDiscount`: `A_SEASONAL` -> `Z_CLEARANCE` | `compute`: `85.0` -> `60.0` | [PR #1](https://github.com/issacnitin/realdiff-sort-java/pull/1) |
| Node | `selectDiscount`: `Z_CLEARANCE` -> `A_SEASONAL` | `compute`: `60` -> `85` | [PR #1](https://github.com/issacnitin/realdiff-sort-node/pull/1) |
| Go | `selectDiscount`: `Z_CLEARANCE` -> `A_SEASONAL` | `checkoutTotal`: `(60, Z_CLEARANCE)` -> `(85, A_SEASONAL)` | [PR #1](https://github.com/issacnitin/realdiff-sort-go/pull/1) |
| Rust | `by_priority`: stable order -> unstable order | `checkout_total`: `(85, A_SEASONAL)` -> `(60, Z_CLEARANCE)` | [PR #1](https://github.com/issacnitin/realdiff-sort-rust/pull/1) |
| Python | `select_discount`: `Z_CLEARANCE` -> `A_SEASONAL` | `checkout_total`: `(60, Z_CLEARANCE)` -> `(85, A_SEASONAL)` | [PR #1](https://github.com/issacnitin/realdiff-sort-python/pull/1) |

Every support line names the reacting test. None of the caught totals is rendered as a peer `Evidence for ...` entry. Each pull request still changes exactly one source file and has exactly one current-head inline cause comment.

## Added-code example

The Python pull request adds two helper members. Its comment reports:

```text
Added-code coverage
This PR added 2 members and 0 tests.
Added code has no base behavior to compare, so it is not included in the behavior-change count.
```
