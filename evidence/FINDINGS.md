# Measured evidence

This document records the measurements behind BehaviorDiff's major design choices. Commands under `tools/` reproduce the maintained sample claims.

## Build-time weaving versus runtime patching

The original prototype patched methods at runtime. JIT inlining caused methods to report as patched while producing no events. On the sample suite, 29 methods produced events under Cecil weaving but zero events under runtime patching, accounting for 6,157 missing events.

Build-time Mono.Cecil instrumentation rewrites IL before the JIT sees it. With and without minimized JIT optimization, Cecil produced the same method and event sets in the equivalence run.

Raw per-method measurements are retained in [inlining-evidence.csv](inlining-evidence.csv).

## Instrumentation cost

Three repetitions were run and the minimum wall time retained:

| Configuration | Wall time | Overhead |
| --- | ---: | ---: |
| No tracing | 1,069 ms | baseline |
| Runtime patching | 1,594 ms | +49% |
| Cecil weaving | 1,099 ms | +2.8% |

The runtime patcher also paid a 697 ms installation cost on every run. Cecil paid a 1,731 ms one-time weave cost in the measured script, including process startup.

## External scale case

A merged FluentValidation change was analyzed as an external scale case:

```text
instrumented    : 638 library members, 1,054 test members
events per run  : 105,743
traces          : 555 MB across four runs
matched keys    : 45,519
end to end      : 51.9 s on the measurement machine
```

The run demonstrated member lifecycle changes, source-generated members that cannot be attributed through normal Git paths, and nondeterministic residual behavior in unedited caches.

Peak diff working set was 1.33 GB against 416 MB of parsed traces. Memory currently scales with event count because parsed events are retained for comparison.

## Noise baseline

Nondeterministic keys found as base samples increased:

| Base runs | Noise keys |
| ---: | ---: |
| 2 | 2,122 |
| 3 | 2,326 |
| 4 | 2,388 |

Three base runs are the current operating point. They captured roughly 97% of the keys found by four runs in this subject while saving one full test invocation.

## Public stable-sort demonstration

The maintained public demo changes one file in an excluded infrastructure project:

```text
src/Infrastructure.Collections/SortingExtensions.cs
```

The proposed implementation restores stable ordering:

```csharp
src.OrderBy(key).ToList()
```

The hosted result reports:

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

The unstable baseline selection was identical across 20 fresh probe processes. The executable fixture proof also launches five fresh proposed-change test processes and verifies that every one selects `SEASONAL_15`.

- [Demo pull request](https://github.com/issacnitin/behaviordiff-live-verification/pull/4)
- [Hosted workflow run](https://github.com/issacnitin/behaviordiff-live-verification/actions/runs/32369192452)
- [BehaviorDiff summary](https://github.com/issacnitin/behaviordiff-live-verification/pull/4#issuecomment-5353980185)

## Reproduce maintained claims

```powershell
dotnet build BehaviorDiff.sln -c Release
pwsh -File tools/verify-contracts.ps1
pwsh -File tools/verify-coverage.ps1
pwsh -File tools/verify-demo-fixtures.ps1
pwsh -File tools/verify-pipeline.ps1
```

Measurements vary by hardware and SDK patch version. Behavioral assertions in the proof scripts are exact; wall-clock and memory figures are reference values rather than performance guarantees.
