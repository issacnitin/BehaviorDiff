# Changelog

All notable changes to BehaviorDiff are documented here.

## Unreleased

### Added

- Qualified Rust diff engine selectable with `--engine=csharp|rust`; C# remains the default.
- RID-aware Rust engine payloads in the .NET tool and Linux container, plus a matching GitHub Action input.
- Reproducible C#/Rust peak-RSS and stage-timing measurements on FluentValidation #2136.

### Fixed

- Pack `Mono.Cecil` beside the installed Weaver after clean builds.

### Known limitations

- Rust currently replaces diff only; frontier, findings, and comments remain shared C# stages.
- Rust retains the whole comparison and measured slightly higher peak RSS and slower diff time than C# on FluentValidation #2136.

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
