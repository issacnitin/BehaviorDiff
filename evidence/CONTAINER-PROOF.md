# Linux container proof

Final functional image: `behaviordiff:rust-tracer-final`, image ID `91e420b1e171`, reported Docker size `898,678,469` bytes. The runtime image contains stable `rustc 1.98.0`, `cargo 1.98.0`, a native linker toolchain, the default Rust diff engine, and the packaged stable Rust tracer.

## Functional proof

The full proof ran each phase in a fresh container while preserving only explicit mounted state. All checks used non-empty trace/finding inputs.

### Rendered comment

The cold Node Action path emitted 138 events per run and posted a captured GitHub comment of 4,216 bytes. The proof required a BehaviorDiff heading, deterministic comment marker, and the actual unexpected member token `selectDiscount`; a successful process exit alone was insufficient.

### Cache and baseline mounts

| Phase | Host wall | Measured pipeline | Container overhead | Cache | Saved base time |
| --- | ---: | ---: | ---: | --- | ---: |
| Node cold | 16,962 ms | 7,085 ms | 9,877 ms | miss | 0 ms |
| Node warm | 8,139 ms | 3,060 ms | 5,079 ms | hit | 3,811 ms |

The cold container persisted 1 trace-cache metadata entry. A fresh warm container reused that mounted entry and a mounted `behaviordiff.baseline/2` file. The baseline changed actionable unexpected members to 0 and suppressed 1 member. The proof fails if cache status is not `hit`, saved time is not positive, the baseline schema is wrong, or suppression is absent.

### Java and Rust image contents

The Java phase emitted 135 events per run and completed analyzed findings using `/opt/behaviordiff/tracers/java/behaviordiff-java-agent.jar`.

The Rust phase omitted `--engine`, printed `engine: rust`, and selected `/opt/behaviordiff/tracers/rust/linux-x64/behaviordiff-rust-rewrite`. Each of its four runs emitted 111 events with 100% test IDs. It matched 104 keys, retained 9 occurrence divergences across 3 method keys, collapsed them to 1 frontier, and produced the unedited `src/service.rs::biased_priority` headline. `VERIFY_CONTAINER_FIXTURES: PASS` and `VERIFY_CONTAINER: PASS` both completed.

The first Rust image proof selected the packaged tracer but failed before events because linker `cc` was absent. Adding `build-essential` closed that image dependency; no result from the failed empty Rust run was reported as a passing zero.

## FluentValidation cold/warm measurement

The benchmark used retained FluentValidation #2136:

- base `ef50516decf652fd9f97090a4a4a9e114d458ce8`;
- PR `6eac0afe0f7c406a5ac3e9830fa4d9d7b03c25dc`.

The FluentValidation timing image was `behaviordiff:rust-tracer-proof`, image ID `43c631fdcc76`. The subsequent current-HEAD image changes only Rust tracer conformance coverage and documentation; the default Rust diff engine and .NET FluentValidation tracing path measured here are unchanged.

Large traces and the persistent trace cache lived on a Docker-managed Linux volume. Docker Desktop bind-mounted NTFS traces repeatedly tore their final event and manifest records at about 142 MB; those refused runs were discarded. Dependencies used the host's non-empty `C:\Nuget` global package cache read-only plus a local feed for FluentValidation's floating `Microsoft.SourceLink.GitHub` `1.*` reference.

The benchmark refuses an empty findings artifact, non-positive diff/frontier timings, cold non-miss, warm non-hit, or zero RSS samples. Both completed runs had 4 engine-interval RSS samples and analyzed findings.

| Environment | Cache | End-to-end wall | Diff + frontier | Peak memory | Cache saved |
| --- | --- | ---: | ---: | ---: | ---: |
| Host Rust | cold miss | 94.419 s | 6.626 s | 321.410 MiB | 0 s |
| Container Rust | cold miss | 186.170 s | 5.362 s | 1,967.104 MiB | 0 s |
| Host Rust | warm hit | 55.430 s | 7.331 s | 314.102 MiB | host report did not retain this field |
| Container Rust | warm hit | 117.078 s | 5.261 s | 1,956.864 MiB | 65.265 s |

Container-to-host ratios:

| Measurement | Cold | Warm |
| --- | ---: | ---: |
| End-to-end wall | 1.972x | 2.112x |
| Diff + frontier | 0.809x | 0.718x |
| Reported peak memory | 6.120x | 6.230x |

The memory columns use different ownership domains. Host peak is the summed working set of the CLI and its direct engine child between engine-part-1 and frontier completion. Container peak is whole-container cgroup memory from `docker stats` over the same output-marker interval; it includes the .NET CLI, Rust child, and cgroup-accounted file/cache memory. The container's approximately 1.96 GiB is therefore the correct Docker Desktop cgroup ceiling observed by this proof, but it is not a process-RSS comparison to the host's approximately 314-321 MiB.

Cold and warm findings were both non-empty and analyzed, but independent reruns retained known trace variation: cold had 11 unexpected members/161 call sites; warm had 8/144. Correctness qualification remains the retained-trace byte-equivalence gate, not equality between independently rerun benchmark findings.

## Not closed

- Container FluentValidation end-to-end wall remains 91.751 s slower cold and 61.648 s slower warm than the host measurement.
- Whole-cgroup peak remains approximately 1.96 GiB. The benchmark identifies the deployment ceiling but does not yet attribute cgroup memory among anonymous heap, mapped files, and page cache.
- The image carries the Rust tracer, but the tracer's rendering-only sensitive-value redaction gap remains as documented in `RUST-TRACER-PRODUCTION.md`.
