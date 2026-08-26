# Linux container proof

Final functional image: `realdiff:rust-tracer-final`, image ID `91e420b1e171`, reported Docker size `898,678,469` bytes. The runtime image contains stable `rustc 1.98.0`, `cargo 1.98.0`, a native linker toolchain, the default Rust diff engine, and the packaged stable Rust tracer.

## Functional proof

The full proof ran each phase in a fresh container while preserving only explicit mounted state. All checks used non-empty trace/finding inputs.

### Rendered comment

The cold Node Action path emitted 138 events per run and posted a captured GitHub comment of 4,216 bytes. The proof required a RealDiff heading, deterministic comment marker, and the actual unexpected member token `selectDiscount`; a successful process exit alone was insufficient.

### Cache and baseline mounts

| Phase | Host wall | Measured pipeline | Container overhead | Cache | Saved base time |
| --- | ---: | ---: | ---: | --- | ---: |
| Node cold | 16,962 ms | 7,085 ms | 9,877 ms | miss | 0 ms |
| Node warm | 8,139 ms | 3,060 ms | 5,079 ms | hit | 3,811 ms |

The cold container persisted 1 trace-cache metadata entry. A fresh warm container reused that mounted entry and a mounted `realdiff.baseline/2` file. The baseline changed actionable unexpected members to 0 and suppressed 1 member. The proof fails if cache status is not `hit`, saved time is not positive, the baseline schema is wrong, or suppression is absent.

### Java and Rust image contents

The Java phase emitted 135 events per run and completed analyzed findings using `/opt/realdiff/tracers/java/realdiff-java-agent.jar`.

The Rust phase omitted `--engine`, printed `engine: rust`, and selected `/opt/realdiff/tracers/rust/linux-x64/realdiff-rust-rewrite`. Each of its four runs emitted 111 events with 100% test IDs. It matched 104 keys, retained 9 occurrence divergences across 3 method keys, collapsed them to 1 frontier, and produced the unedited `src/service.rs::biased_priority` headline. `VERIFY_CONTAINER_FIXTURES: PASS` and `VERIFY_CONTAINER: PASS` both completed.

The first Rust image proof selected the packaged tracer but failed before events because linker `cc` was absent. Adding `build-essential` closed that image dependency; no result from the failed empty Rust run was reported as a passing zero.

## FluentValidation cold/warm measurement

The benchmark used retained FluentValidation #2136:

- base `ef50516decf652fd9f97090a4a4a9e114d458ce8`;
- PR `6eac0afe0f7c406a5ac3e9830fa4d9d7b03c25dc`.

The FluentValidation timing image was `realdiff:rust-tracer-proof`, image ID `43c631fdcc76`. The subsequent current-HEAD image changes only Rust tracer conformance coverage and documentation; the default Rust diff engine and .NET FluentValidation tracing path measured here are unchanged.

Large traces and the persistent trace cache lived on a Docker-managed Linux volume. Docker Desktop bind-mounted NTFS traces repeatedly tore their final event and manifest records at about 142 MB; those refused runs were discarded. Dependencies used the host's non-empty `C:\Nuget` global package cache read-only plus a local feed for FluentValidation's floating `Microsoft.SourceLink.GitHub` `1.*` reference.

The benchmark refuses an empty findings artifact, non-positive diff/frontier timings, cold non-miss, warm non-hit, or an engine interval without memory samples. The final sampler read summed descendant `VmRSS`, `memory.current`, and `memory.stat` directly inside the container about every 300 ms. Both completed runs retained 19 peak-interval samples and analyzed findings.

| Environment | Cache | End-to-end wall | Diff + frontier | Peak process-tree RSS | Peak cgroup current | Cache saved |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Host Rust | cold miss | 94.419 s | 6.626 s | 321.410 MiB | not measured | 0 s |
| Container Rust | cold miss | 186.251 s | 5.441 s | 1,777.156 MiB | 2,661.461 MiB | 0 s |
| Host Rust | warm hit | 55.430 s | 7.331 s | 314.102 MiB | not measured | host report did not retain this field |
| Container Rust | warm hit | 149.090 s | 5.656 s | 2,064.219 MiB | 2,524.680 MiB | 65.246 s |

At each temperature, maximum process-tree RSS and maximum `memory.current` occurred in the same retained sample:

| Cgroup-v2 component at peak | Cold | Warm |
| --- | ---: | ---: |
| `memory.current` | 2,661.461 MiB | 2,524.680 MiB |
| `anon` | 1,259.879 MiB | 1,555.715 MiB |
| `file` | 1,345.234 MiB | 926.832 MiB |
| `kernel` | 54.559 MiB | 41.363 MiB |
| `shmem` (subset of `file`) | 195.313 MiB | 191.230 MiB |
| `file_mapped` (subset of `file`) | 195.324 MiB | 191.277 MiB |

`anon + file + kernel` accounts for 99.93% cold and 99.97% warm of `memory.current`; the small remainder is normal cgroup accounting outside those three reported fields.

Container-to-host ratios:

| Measurement | Cold | Warm |
| --- | ---: | ---: |
| End-to-end wall | 1.973x | 2.690x |
| Diff + frontier | 0.821x | 0.772x |
| Process-tree RSS | 5.529x | 6.572x |
| Container cgroup current / host process working set | 8.280x | 8.038x |

The memory columns use different ownership domains. Host peak is the summed working set of the CLI and its direct engine child between engine-part-1 and frontier completion. Container process-tree RSS sums `VmRSS` for PID 1 and all descendants; shared and file-backed resident pages can appear in more than one process. Cgroup `memory.current` charges pages once to the container and includes anonymous memory, filesystem cache, shared memory, and kernel memory.

The previously reported approximately 1.96 GiB `docker stats` peak was a sparse-sampling artifact, not the measured deployment ceiling and not a like-for-like process-RSS value. The direct samples put the workload's cgroup peak at 2.52-2.66 GiB. A container limit must therefore allow at least that observed peak plus operating margin. Conversely, describing the warm 2.02 GiB summed `VmRSS` as private application memory would also overstate ownership: cgroup anonymous memory was 1.56 GiB, while 0.93 GiB was cgroup file memory and summed process RSS can double-count shared mappings.

Cold and warm findings were both non-empty and analyzed, but independent reruns retained known trace variation: cold had 11 unexpected members/161 call sites; warm had 8/144. Correctness qualification remains the retained-trace byte-equivalence gate, not equality between independently rerun benchmark findings.

## Not closed

- Container FluentValidation end-to-end wall remains 91.751 s slower cold and 61.648 s slower warm than the host measurement.
- Whole-cgroup peak remains 2.52-2.66 GiB for this exact FluentValidation corpus and image. File/cache memory explains 0.93-1.35 GiB, but anonymous memory alone remains 1.26-1.56 GiB.
