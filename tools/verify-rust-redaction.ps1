#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-rust-redaction-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$fixture = Join-Path $work 'repository'
$analysis = Join-Path $work 'analysis'
$findingsPath = Join-Path $analysis 'findings.json'
$commentPath = Join-Path $analysis 'comment.md'
$baseToken = 'AKIA1234567890ABCDEF'
$prToken = 'AKIAFEDCBA0987654321'
$password = 'correct-horse-battery-staple'
$typeSecret = 'type-only-value-4381'
$pathSecret = 'path-only-value-7294'
$tracerName = if ($IsWindows) { 'behaviordiff-rust-rewrite.exe' } else { 'behaviordiff-rust-rewrite' }
$tracer = Join-Path $repo "src/BehaviorDiff.Rust.Tracer/target/release/$tracerName"

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$label failed with exit code $LASTEXITCODE" }
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path (Join-Path $fixture 'src'), (Join-Path $fixture '.behaviordiff') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'Cargo.toml'), @'
[package]
name = "rust-redaction-fixture"
version = "0.1.0"
edition = "2021"
publish = false
'@)
    [IO.File]::WriteAllText((Join-Path $fixture '.behaviordiff/config.yml'), @'
language: rust
redaction:
  types:
    - rust_redaction_fixture::SecretEnvelope
  paths:
    - src/path_secret.rs
'@)
    [IO.File]::WriteAllText((Join-Path $fixture 'src/config.rs'), @"
pub const TOKEN: &str = `"$baseToken`";

pub fn marker() -> usize { 0 }
"@)
    [IO.File]::WriteAllText((Join-Path $fixture 'src/auth.rs'), @'
use crate::config;

pub fn authenticate(password: &str) -> String {
    assert!(!password.is_empty());
    config::marker();
    config::TOKEN.to_owned()
}
'@)
    [IO.File]::WriteAllText((Join-Path $fixture 'src/path_secret.rs'), @"
pub fn path_value() -> &'static str { `"$pathSecret`" }
"@)
    $testCases = @((1..120) | ForEach-Object {
@"
    #[test]
    fn credentials_execute_$($_)() {
        let token = auth::authenticate(`"$password`");
        assert!(token.starts_with(`"AKIA`"));
        assert_eq!(echo_envelope(SecretEnvelope { value: `"$typeSecret`".to_owned() }).value.len(), $($typeSecret.Length));
        assert_eq!(path_secret::path_value().len(), $($pathSecret.Length));
    }
"@
    }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $fixture 'src/lib.rs'), @"
mod auth;
mod config;
mod path_secret;

#[derive(Clone)]
struct SecretEnvelope {
    value: String,
}

fn echo_envelope(value: SecretEnvelope) -> SecretEnvelope { value }

#[cfg(test)]
mod tests {
    use super::*;

$testCases
}
"@)

    Invoke-Checked 'git init' { & git -C $fixture init --initial-branch=main --quiet }
    Invoke-Checked 'git identity' { & git -C $fixture config user.email 'rust-redaction-proof@behaviordiff.invalid' }
    Invoke-Checked 'git identity' { & git -C $fixture config user.name 'BehaviorDiff Rust Redaction Proof' }
    Invoke-Checked 'git add' { & git -C $fixture add . }
    Invoke-Checked 'base commit' { & git -C $fixture commit --quiet -m 'base secret' }
    $base = (& git -C $fixture rev-parse HEAD).Trim()
    $configPath = Join-Path $fixture 'src/config.rs'
    [IO.File]::WriteAllText($configPath, (Get-Content $configPath -Raw).Replace($baseToken, $prToken))
    Invoke-Checked 'PR add' { & git -C $fixture add src/config.rs }
    Invoke-Checked 'PR commit' { & git -C $fixture commit --quiet -m 'rotate token' }
    $pr = (& git -C $fixture rev-parse HEAD).Trim()

    Invoke-Checked 'Rust tracer build' {
        & cargo build --release --locked --manifest-path (Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/Cargo.toml')
    }
    Invoke-Checked 'Rust engine build' {
        & cargo build --release --locked --manifest-path (Join-Path $repo 'src/BehaviorDiff.Engine.Rust/Cargo.toml')
    }
    Invoke-Checked 'CLI build' {
        & dotnet build (Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj') -c Release --nologo -v quiet
    }

    $previousTracer = $env:BEHAVIORDIFF_RUST_TRACER
    $env:BEHAVIORDIFF_RUST_TRACER = $tracer
    try {
        $output = @(& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Cli') -c Release --no-build -- `
            $fixture --base $base --pr $pr --work $analysis --findings $findingsPath `
            --no-baseline --strict --keep --keep-traces 1d 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $env:BEHAVIORDIFF_RUST_TRACER = $previousTracer
    }
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 1) { throw "Rust redaction analysis exited $exitCode instead of findings exit 1" }

    $traceFiles = @(Get-ChildItem $analysis -Recurse -File -Filter 'run.*.ndjson' |
        Where-Object Name -NotLike '*.manifest.ndjson')
    $traceText = ($traceFiles | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $divergenceText = Get-Content (Join-Path $analysis 'divergence-set.json') -Raw
    $findingsText = Get-Content $findingsPath -Raw
    $comment = @(& dotnet run --project (Join-Path $repo 'tools/CommentPreview/BehaviorDiff.CommentPreview.csproj') `
        -c Release -- $findingsPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Rust redaction comment rendering failed: $LASTEXITCODE" }
    $commentText = $comment -join "`n"
    [IO.File]::WriteAllText($commentPath, $commentText)

    $published = $traceText + "`n" + $divergenceText + "`n" + $findingsText + "`n" + $commentText
    foreach ($secret in @($baseToken, $prToken, $password, $typeSecret, $pathSecret)) {
        if ($published.Contains($secret, [StringComparison]::Ordinal)) {
            throw "Rust redaction leaked fixture secret '$secret'"
        }
    }
    if ($traceText -notmatch '<redacted>' -or $commentText -notmatch '<redacted>') {
        throw 'Rust redaction marker was missing from trace or rendered comment'
    }
    $events = @($traceText -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $pathEvents = @($events | Where-Object filePath -CEQ 'src/path_secret.rs')
    $typeEvents = @($events | Where-Object methodFullName -Match 'echo_envelope')
    if ($pathEvents.Count -eq 0 -or $typeEvents.Count -eq 0 -or
        @($pathEvents | Where-Object returnRendered -CNE '<redacted>').Count -ne 0 -or
        @($typeEvents | Where-Object returnRendered -CNE '<redacted>').Count -ne 0 -or
        @($typeEvents | Where-Object argsRendered -NotMatch '<redacted>').Count -ne 0) {
        throw 'Configured Rust type/path opt-out did not redact retained trace rendering'
    }

    $divergence = $divergenceText | ConvertFrom-Json -Depth 100
    $auth = @($divergence.divergences | Where-Object filePath -CEQ 'src/auth.rs')
    if ($auth.Count -eq 0) { throw 'Secret rotation produced no authenticate divergence' }
    $digestPairs = @($auth | ForEach-Object {
        [pscustomobject]@{ Base = $_.baseReturnDigest; Pr = $_.prReturnDigest }
    } | Where-Object { $_.Base -match '^sha256:' -and $_.Pr -match '^sha256:' -and $_.Base -cne $_.Pr })
    if ($digestPairs.Count -eq 0) { throw 'Redacted token rotation did not preserve unequal real-value digests' }

    $findings = $findingsText | ConvertFrom-Json -Depth 100
    if ($findings.status -cne 'analyzed' -or $findings.summary.unexpectedMembers -lt 1) {
        throw 'Rust secret rotation did not produce analyzed findings'
    }

    Write-Host '--- RUST REDACTED COMMENT ---'
    Write-Host $commentText
    Write-Host '--- END RUST REDACTED COMMENT ---'
    Write-Host ("RUST_REDACTION_PROOF traces={0} events={1} authDivergences={2} unequalDigestPairs={3} secretsLeaked=0 typePathRedacted=true commentBytes={4}" -f `
        $traceFiles.Count, $events.Count,
        $auth.Count, $digestPairs.Count, [Text.Encoding]::UTF8.GetByteCount($commentText))
    Write-Host 'verify-rust-redaction: PASS' -ForegroundColor Green
}
finally {
    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Rust redaction proof work kept at $work"
    }
}