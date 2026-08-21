# Go Goroutine Correlation Prototype

This standalone module tests one claim only: explicit immutable frame-token propagation can preserve test identity, parentage, depth, and per-method entry ordinals through parallel tests, direct calls, and nested goroutine spawns. It is hand-transformed code representing potential future rewrite output; it is not an AST rewriter, canonicalizer, manifest implementation, or full tracer.

## Conclusion

Explicit frame/context token propagation is reliable for rewritten direct calls and rewritten `go` statements. The token must be captured at the spawn site and passed into the new goroutine; standard Go offers no ambient goroutine-local context.

Dynamic interface and function-value calls, channel worker pools, callbacks from uninstrumented packages, and goroutines spawned outside rewritten scope cannot be correlated without API or context changes. They must become explicit skipped or unobserved boundaries. The negative test demonstrates this by recording one direct goroutine call as `(no-test)`.

Mutating public API signatures is not acceptable. The subject keeps its original `Run()` wrapper and uses an internal frame-taking companion; `RunRewritten` is the prototype bridge standing in for an in-scope rewritten call site. A future rewriter therefore needs companion or internal functions, public wrappers, and rewrites of in-scope call sites and `go` statements. This prototype deliberately does not start that rewriter.

## Run

From the repository root:

```powershell
pwsh -NoProfile -File tools/verify-go-correlation.ps1
```

The positive workload uses eight parallel subtests sharing one recorder. Each subtest emits 28 completed-call events, for an exact total of 224 events.