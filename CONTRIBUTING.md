# Contributing

Thanks for helping improve BehaviorDiff.

## Development setup

Prerequisites:

- .NET 8 SDK (the version in `global.json` is preferred)
- Git
- PowerShell 7 for the executable proof scripts
- Rust stable for changes to the Rust diff engine

```powershell
git clone https://github.com/issacnitin/BehaviorDiff.git
cd BehaviorDiff
dotnet restore
dotnet build BehaviorDiff.sln -c Release
pwsh -File tools/verify-demo-fixtures.ps1
```

## Before opening a pull request

Run the checks relevant to your change. For changes to tracing, matching, frontier analysis, or posting, run the complete proof set:

```powershell
dotnet build BehaviorDiff.sln -c Release
pwsh -File tools/verify-contracts.ps1
pwsh -File tools/verify-negative-tests.ps1
pwsh -File tools/verify-coverage.ps1
pwsh -File tools/verify-anthropic.ps1
pwsh -File tools/verify-demo-fixtures.ps1
pwsh -File tools/verify-pipeline.ps1
```

For changes to the Rust diff engine, run its compiler checks and regression proofs:

```powershell
cargo fmt --manifest-path src/BehaviorDiff.Engine.Rust/Cargo.toml -- --check
cargo clippy --manifest-path src/BehaviorDiff.Engine.Rust/Cargo.toml --all-targets -- -D warnings
cargo test --manifest-path src/BehaviorDiff.Engine.Rust/Cargo.toml
pwsh -File tools/verify-contracts.ps1
pwsh -File tools/verify-diff.ps1
pwsh -File tools/verify-cli-package.ps1
```

Keep pull requests focused. Include a regression proof for behavioral changes and explain any new refusal condition or observability limitation.

## Design principles

- Refuse when evidence is insufficient; never turn an invalid run into a clean result.
- Keep deterministic findings independent from optional model explanations.
- Report execution coverage alongside every verdict.
- Treat nondeterminism as measured noise, not as a behavior change.
- Preserve machine-readable output compatibility where practical.

## Reporting bugs

Open a GitHub issue with the command, exit code, relevant log tail, target framework, test framework, and a minimal reproduction when possible. Remove secrets and proprietary source before attaching traces.
