# Changelog

All notable changes to BehaviorDiff are documented here.

## 0.2.0 - 2026-08-26

### Added

- Single-pass streaming Rust engine implementing matching, noise filtering, frontier analysis, baseline suppression, and canonical findings generation.
- Production stable Rust tracer using cached `syn`/`quote` source rewriting, generated private-member readers, structural test correlation, async futures, and manifest finalization.
- Standalone native engine and tracer binaries packaged across RID distributions.
- Self-contained release archives for Linux x64/ARM64, macOS x64/ARM64, and Windows x64 with SHA-256 checksums.
- Repository configuration for custom build/test commands, work directories, test projects, scope, redaction, and baselines.
- Comprehensive cross-language verification across .NET, Java, Node, Go, and Rust reference suites.

### Changed

- Complete migration from the C# engine to the standalone Rust diff and frontier engine.
- Removed `--engine` selection; BehaviorDiff executes exclusively on the qualified Rust engine.
- Rebased the all-language container onto a runtime-free Ubuntu base while retaining the .NET SDK only for target builds.

### Fixed

- Bounded pre-frontier memory consumption and direct buffered JSON serialization matching .NET escaping.
- Pack `Mono.Cecil` beside the installed Weaver after clean builds.
- Preserve instrumentation under configured commands and refuse successful commands that produce zero events.

## 0.1.0 - 2026-08-20

Initial open-source preview.

### Added

- Build-time Mono.Cecil instrumentation for .NET assemblies.
- Three-base-run noise filtering and behavior-frontier analysis.
- Canonical `findings.json` output with execution coverage and assertion reaction.
- Explicit refusal states when evidence cannot support a verdict.
- GitHub Actions and Azure Pipelines pull-request integrations.
- Idempotent PR summaries and cause-hunk comments.
- Optional citation-grounded Anthropic explanations.
- Stable-sort, retry-policy, and configuration-parser executable demos.
- Optional MCP server over completed BehaviorDiff runs.
