# RealDiff 0.4.0

RealDiff 0.4.0 is the breaking product rename and adds Python 3.12+ as the sixth traced language.

## Highlights

- The repository, projects, namespaces, package IDs, command, environment variables, schemas, action, container, and artifacts are now `RealDiff`/`realdiff`/`REALDIFF`. This release intentionally carries no BehaviorDiff compatibility aliases.
- Python 3.12+ attaches through PEP 669 `sys.monitoring` at process start. There is no target build, bytecode weaving, AST rewriting, compiler coupling, or `sys.settrace` fallback.
- pytest and unittest test extents derive from structural roots. Generators, coroutines, and async generators retain one logical call across suspension and emit only at final completion or escaping unwind.
- The fields-only Python canonicalizer avoids properties, descriptors, user attribute hooks, iterators, formatting, equality, and hashing. Unsafe regions are counted partial markers; redaction occurs after real-value hashing.
- Python source members are inventoried by a side-effect-free AST pass because a no-build tracer still must reconcile unexecuted coverage. Runtime events remain exclusively `sys.monitoring`-owned.
- The maintained Python gate reports 378 matched keys across 73 subject methods and 384 events per run, with six pytest roots, six equivalent unittest roots, zero clean divergences, and a verified base-trace cache hit.
- `RealDiff.Tool.0.4.0` packages all six tracers. The installed-package proof records four Python runs and 1,536 Python events in addition to the existing Java, Node, Go, and Rust proofs.

The all-language container includes Python 3.12 and pytest. The local six-language image proof verifies every staged Python tracer file and the `realdiff` executable.