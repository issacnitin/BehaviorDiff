#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-rust-source-resolution-gate'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'samples/RustReference'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$cache = Join-Path $work 'cache'
$trace = Join-Path $work 'source.ndjson'
$manifest = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/Cargo.toml'
$binary = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/target/release/behaviordiff-rust-rewrite.exe'
if (-not $IsWindows) { $binary = $binary.Substring(0, $binary.Length - 4) }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $work -ItemType Directory -Force | Out-Null
& cargo build --release --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw "Rust tracer build failed: $LASTEXITCODE" }
$rewrite = (& $binary --source $source --cache-root $cache | ConvertFrom-Json)
if ($rewrite.sourceFiles -le 0 -or $rewrite.rustFiles -le 0) {
    throw "Rust source-resolution inputs are empty: source=$($rewrite.sourceFiles) rust=$($rewrite.rustFiles)"
}
$env:BEHAVIORDIFF_RUST_EXIT_TRACE = $trace
try {
    & cargo run --quiet --manifest-path (Join-Path $rewrite.output 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw "Rewritten Rust reference failed: $LASTEXITCODE" }
} finally {
    Remove-Item Env:BEHAVIORDIFF_RUST_EXIT_TRACE -ErrorAction SilentlyContinue
}

$events = @(Get-Content $trace | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
if ($events.Count -le 0) { throw 'Rust source-resolution trace has zero events' }
$usable = @($events | Where-Object {
    $_.filePathResolution -ceq 'debugInfo' -and
    -not [string]::IsNullOrWhiteSpace($_.filePath) -and
    [int]$_.line -gt 0
})
$original = @($events | Where-Object {
    $path = [string]$_.filePath
    $path -match '\.rs$' -and (Test-Path (Join-Path $source $path))
})
$cachePaths = @($events | Where-Object {
    ([string]$_.filePath).Contains($rewrite.output, [StringComparison]::OrdinalIgnoreCase) -or
    ([string]$_.filePath) -match '(^|/|\\)\.behaviordiff($|/|\\)' -or
    [IO.Path]::IsPathRooted([string]$_.filePath)
})
$wrongPaths = @($events | Where-Object { [string]$_.filePath -cne 'src/main.rs' })
if ($usable.Count -ne $events.Count -or $original.Count -ne $events.Count) {
    throw "Rust source resolution incomplete: events=$($events.Count) usable=$($usable.Count) original=$($original.Count)"
}
if ($cachePaths.Count -ne 0 -or $wrongPaths.Count -ne 0) {
    throw "Rust source paths escaped original source: events=$($events.Count) cache=$($cachePaths.Count) wrong=$($wrongPaths.Count)"
}

[pscustomobject]@{
    SourceFiles = $rewrite.sourceFiles
    RustFiles = $rewrite.rustFiles
    Events = $events.Count
    UsableOriginalSourceEvents = $usable.Count
    ExistingOriginalFiles = $original.Count
    CachePathEvents = $cachePaths.Count
    WrongSourceEvents = $wrongPaths.Count
    PositiveLineEvents = @($events | Where-Object { [int]$_.line -gt 0 }).Count
} | Format-List
Write-Host 'RUST_SOURCE_RESOLUTION: PASS'