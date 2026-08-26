# RealDiff 0.3.0

RealDiff 0.3.0 completes the distribution and confidentiality work around the Rust architecture.

## Highlights

- The retired C# diff engine is no longer shipped or selectable. Matching, noise filtering, frontier analysis, baseline policy, and findings generation run through the single-pass streaming Rust engine.
- The public `realdiff` executable is now a thin Rust launcher. It owns argument routing, `.realdiff/config.yml` loading, language detection, and managed-process spawning; risky repository orchestration remains in the self-contained managed component.
- Self-contained archives are published for `linux-x64`, `linux-arm64`, `darwin-x64`, `darwin-arm64`, and `win-x64`, each with a sidecar checksum and aggregate `SHA256SUMS`. A .NET runtime is not required to run the CLI.
- The Rust tracer now applies the same rendering confidentiality contract as the other tracers: sensitive-name and credential-shape redaction plus configured type/path opt-outs. SHA-256 digests still cover the complete real value, so a secret rotation remains detectable while comments and traces show `<redacted>`.

The release workflow executes all five native archives and then downloads the Linux artifact into a clean Ubuntu image with no .NET runtime. Node and Go analyses must both reach rendered GitHub comments before the release is published.