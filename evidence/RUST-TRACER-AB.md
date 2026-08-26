# Rust tracer A/B prototype

## Decision

The stable cached source rewriter is the only prototype that emits runtime records on the shared feature corpus. The pinned-nightly MIR wrapper can discover every requested shape, including the two compiler-generated bodies for each async function, but it does not inject code and emits no runtime events. Neither prototype is a RealDiff-conforming Rust tracer yet.

Do not continue the `rustc_private` path as the production design. It already incurred one API break, requires an exact nightly plus `rustc-dev`, and this pinned API exposes MIR queries without a supported external MIR-pass registration point. Retain it as a compiler-coverage probe. Continue the stable rewriter only behind an explicit experimental boundary and only after resolving the contract gaps listed below.

## Test corpus

`samples/RustReference` exercises:

- private structs and enums;
- a generic function;
- a trait implementation and a call through `dyn Trait`;
- normal async completion and cancellation by dropping a pending future;
- both success and early `Err` return through `?`;
- caught panic unwinding;
- `Vec`, `HashMap`, `BTreeMap`, and `HashSet`.

The unchanged reference program prints `RUST_REFERENCE_OK` and exits 0 on stable Rust. The intentional panic is caught.

## Coverage result

| Shape | Pinned-nightly MIR bodies discovered | Stable runtime events | Stable outcome |
| --- | ---: | ---: | --- |
| Private struct and enum | 1 | 1 | normal |
| Generic function | 1 | 1 | normal |
| Trait implementation method | 1 | 1 | normal |
| Trait-object caller | 1 | 1 | normal |
| Async completion | 2 | 1 | normal |
| Async cancellation | 2 | 1 | cancelled |
| `?` success and early `Err` | 1 | 2 | normal |
| Panic unwinding | 1 | 1 | panic |
| Standard collections | 1 | 1 | normal |

The MIR report contains 16 distinct local body names and zero runtime records. Discovery proves compiler visibility only; it does not prove entry/exit, values, correlation, or digest conformance.

The rewritten run emits 15 valid JSONL records: 13 normal, one panic, and one cancelled. The additional records are the sample entry point and helper functions. SHA-256 hashes of every non-`target` source-project file are identical before and after rewriting. The transformed project exists only under the content-addressed cache.

## Performance

Measurements were taken on the same Windows host after tool installation and warmup. They characterize this small prototype corpus, not a production workload.

| Measurement | Baseline | Prototype | Ratio |
| --- | ---: | ---: | ---: |
| Clean pinned-nightly compile, median of 6 | 994.2 ms | MIR wrapper 1,056.6 ms | 1.06x |
| Release process runtime, median of 50 | 21.387 ms | Stable traced 26.292 ms | 1.23x |
| Release process runtime, p95 of 50 | 22.863 ms | Stable traced 27.731 ms | 1.21x |

The direct stable rewrite took 265.2 ms on a cold cache and 44.5 ms on a cache hit. Runtime measurement used 10 warmup pairs followed by 50 alternating baseline/traced process runs. Each traced process emitted 15 records; all 900 records were present across warmup and measured runs.

## Private API inventory

Option A uses 10 distinct `rustc_private` types, variants, callbacks, functions, or query entry points across three compiler crates:

1. `rustc_driver::run_compiler`
2. `rustc_driver::Callbacks`
3. `rustc_driver::Callbacks::after_analysis`
4. `rustc_driver::Compilation::Continue`
5. `rustc_interface::interface::Compiler`
6. `rustc_middle::ty::TyCtxt`
7. `TyCtxt::hir_body_owners`
8. `LocalDefId::to_def_id`
9. `TyCtxt::optimized_mir`
10. `TyCtxt::def_path_str`

The first implementation attempt used `rustc_driver::RunCompiler`; pinned nightly `nightly-2026-08-20` had removed it in favor of `run_compiler`. That source break occurred before the probe performed any useful work and is concrete maintenance evidence, not a hypothetical risk.

## Stable rewriter boundary

The stable prototype parses Rust files with `syn`, instruments free and implementation functions, writes a formatted copy plus a small runtime into a SHA-256-keyed cache, and builds that copy with ordinary stable Cargo. A drop guard distinguishes normal completion, panic unwinding, and cancellation.

It does not yet satisfy `TRACE-FORMAT.md`. In particular, it does not capture arguments or returns, compute canonical digests, assign call/test correlation, instrument macro-generated or dependency code, preserve a production callable identity, or map all generated locations back to the original checkout. `panic=abort`, process termination, FFI, const functions, extern functions, and rewrite-induced borrow or async-capture changes remain unsupported. The current JSONL is feasibility evidence, not a production trace format.

The next go/no-go gate for stable rewriting is a semantic-preservation and identity prototype, followed by no-user-code digest proofs. Until those pass, Rust tracing remains unqualified.