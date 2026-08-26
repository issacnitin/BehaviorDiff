#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-rust-trace-manifest-gate'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'samples/RustReference'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$cache = Join-Path $work 'cache'
$run = Join-Path $work 'run'
$trace = Join-Path $run 'run.rust.ndjson'
$traceManifest = Join-Path $run 'run.rust.manifest.ndjson'
$output = Join-Path $work 'divergence-set.json'
$tracerManifest = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/Cargo.toml'
$binary = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/target/release/behaviordiff-rust-rewrite.exe'
$engineManifest = Join-Path $repo 'src/BehaviorDiff.Engine.Rust/Cargo.toml'
$engine = Join-Path $repo 'src/BehaviorDiff.Engine.Rust/target/release/behaviordiff-engine.exe'
if (-not $IsWindows) { $binary = $binary.Substring(0, $binary.Length - 4) }
if (-not $IsWindows) { $engine = $engine.Substring(0, $engine.Length - 4) }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $run -ItemType Directory -Force | Out-Null
& cargo build --release --manifest-path $tracerManifest
if ($LASTEXITCODE -ne 0) { throw "Rust tracer build failed: $LASTEXITCODE" }
$rewrite = (& $binary --source $source --cache-root $cache | ConvertFrom-Json)
if ($rewrite.sourceFiles -le 0 -or $rewrite.rustFiles -le 0) {
    throw "Rust manifest inputs are empty: source=$($rewrite.sourceFiles) rust=$($rewrite.rustFiles)"
}

$env:BEHAVIORDIFF_RUST_EXIT_TRACE = $trace
try {
    & cargo test --quiet --manifest-path (Join-Path $rewrite.output 'Cargo.toml') --lib -- --test-threads=1
    if ($LASTEXITCODE -ne 0) { throw "Rewritten Rust tests failed: $LASTEXITCODE" }
} finally {
    Remove-Item Env:BEHAVIORDIFF_RUST_EXIT_TRACE -ErrorAction SilentlyContinue
}
$origin = Join-Path $rewrite.output '.behaviordiff-rust-origin.json'
$finalize = (& $binary finalize --origin $origin --trace $trace --out $traceManifest | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw "Rust manifest finalization failed: $LASTEXITCODE" }
if ($finalize.events -le 0 -or $finalize.discoveredMembers -le 0 -or $finalize.patchedMembers -le 0) {
    throw "Rust finalized populations are empty: events=$($finalize.events) discovered=$($finalize.discoveredMembers) patched=$($finalize.patchedMembers)"
}

$events = @(Get-Content $trace | Where-Object { $_.Trim().Length -gt 0 })
$records = @(Get-Content $traceManifest | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
if ($events.Count -ne $finalize.events -or $records.Count -le 0) {
    throw "Rust trace/manifest populations differ: trace=$($events.Count) finalizer=$($finalize.events) manifest=$($records.Count)"
}
$runRecords = @($records | Where-Object kind -ceq 'run')
$assemblies = @($records | Where-Object kind -ceq 'assembly')
$members = @($records | Where-Object kind -ceq 'member')
$digests = @($records | Where-Object kind -ceq 'digest')
$writers = @($records | Where-Object kind -ceq 'writer')
if ($runRecords.Count -ne 1 -or $assemblies.Count -ne 1 -or $digests.Count -ne 1 -or $writers.Count -ne 1) {
    throw "Rust manifest singleton counts differ: run=$($runRecords.Count) assembly=$($assemblies.Count) digest=$($digests.Count) writer=$($writers.Count)"
}
$assembly = $assemblies[0]
$writer = $writers[0]
$digest = $digests[0]
if ($members.Count -ne [int]$assembly.discoveredMembers -or
    [int]$assembly.discoveredMembers -ne ([int]$assembly.patchedMembers + [int]$assembly.skippedMembers)) {
    throw "Rust member accounting differs: records=$($members.Count) discovered=$($assembly.discoveredMembers) patched=$($assembly.patchedMembers) skipped=$($assembly.skippedMembers)"
}
if ([int]$writer.enqueued -ne $events.Count -or [int]$writer.written -ne $events.Count -or [int]$writer.dropped -ne 0) {
    throw "Rust writer accounting differs from $($events.Count) events: enqueued=$($writer.enqueued) written=$($writer.written) dropped=$($writer.dropped)"
}
if ([long]$digest.valuesDigested -le 0) {
    throw "Rust digest accounting is empty for $($events.Count) events"
}

& cargo build --release --locked --manifest-path $engineManifest
if ($LASTEXITCODE -ne 0) { throw "BehaviorDiff engine build failed: $LASTEXITCODE" }
& $engine stream-diff --base1 $run --base2 $run --base3 $run --pr $run --base-root $source --pr-root $source --out $output
if ($LASTEXITCODE -ne 0) { throw "BehaviorDiff engine rejected Rust trace/manifest: $LASTEXITCODE" }
$document = Get-Content $output -Raw | ConvertFrom-Json
if ([int]$document.counts.matchedKeys -lt 300) {
    throw "Rust engine matched-key input is below floor: $($document.counts.matchedKeys)"
}
if ([int]$document.counts.rawDifferences -ne 0 -or [int]$document.counts.remainingDivergences -ne 0) {
    throw "Rust engine found differences from matched=$($document.counts.matchedKeys): raw=$($document.counts.rawDifferences) remaining=$($document.counts.remainingDivergences)"
}

[pscustomobject]@{
    SourceFiles = $rewrite.sourceFiles
    RustFiles = $rewrite.rustFiles
    Events = $events.Count
    ManifestRecords = $records.Count
    DiscoveredMembers = [int]$assembly.discoveredMembers
    PatchedMembers = [int]$assembly.patchedMembers
    SkippedMembers = [int]$assembly.skippedMembers
    ValuesDigested = [long]$digest.valuesDigested
    Blocklisted = [long]$digest.blocklisted
    WriterEnqueued = [int]$writer.enqueued
    WriterWritten = [int]$writer.written
    WriterDropped = [int]$writer.dropped
    MatchedKeys = [int]$document.counts.matchedKeys
    RawDifferences = [int]$document.counts.rawDifferences
    RemainingDivergences = [int]$document.counts.remainingDivergences
} | Format-List
Write-Host 'RUST_TRACE_MANIFEST: PASS'
