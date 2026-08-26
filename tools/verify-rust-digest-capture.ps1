#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'realdiff-rust-digest-capture-gate'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'samples/RustReference'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$cache = Join-Path $work 'cache'
$trace = Join-Path $work 'capture.ndjson'
$manifest = Join-Path $repo 'src/RealDiff.Rust.Tracer/Cargo.toml'
$binary = Join-Path $repo 'src/RealDiff.Rust.Tracer/target/release/realdiff-rust-rewrite.exe'
if (-not $IsWindows) { $binary = $binary.Substring(0, $binary.Length - 4) }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $work -ItemType Directory -Force | Out-Null
& cargo build --release --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw "Rust tracer build failed: $LASTEXITCODE" }
$rewrite = (& $binary --source $source --cache-root $cache | ConvertFrom-Json)
if ($rewrite.sourceFiles -le 0 -or $rewrite.rustFiles -le 0) {
    throw "Rust digest capture inputs are empty: source=$($rewrite.sourceFiles) rust=$($rewrite.rustFiles)"
}
$env:REALDIFF_RUST_EXIT_TRACE = $trace
try {
    & cargo run --quiet --manifest-path (Join-Path $rewrite.output 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw "Rewritten Rust reference failed: $LASTEXITCODE" }
} finally {
    Remove-Item Env:REALDIFF_RUST_EXIT_TRACE -ErrorAction SilentlyContinue
}
$events = @(Get-Content $trace | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
if ($events.Count -le 0) { throw 'Rust digest capture emitted zero records' }
function Get-Optional([object]$InputObject, [string]$Name) {
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
$args = @($events | Where-Object { $null -ne $_.PSObject.Properties['argsDigest'] })
$returns = @($events | Where-Object { $null -ne $_.PSObject.Properties['returnDigest'] })
$partial = @($events | Where-Object {
    (Get-Optional $_ 'argsPartial') -eq $true -or (Get-Optional $_ 'returnPartial') -eq $true
})
$exact = @($events | Where-Object {
    ($null -ne $_.PSObject.Properties['argsDigest'] -or $null -ne $_.PSObject.Properties['returnDigest']) -and
    (Get-Optional $_ 'argsPartial') -ne $true -and (Get-Optional $_ 'returnPartial') -ne $true
})
$skippedMarkers = @($events | Where-Object {
    "$(Get-Optional $_ 'argsRendered')$(Get-Optional $_ 'returnRendered')" -match '<skipped:'
})
$panic = @($events | Where-Object outcome -ceq 'panic')
$panicReturns = @($panic | Where-Object { $null -ne $_.PSObject.Properties['returnDigest'] })
if ($args.Count -le 0 -or $returns.Count -le 0 -or $exact.Count -le 0 -or $partial.Count -le 0 -or $skippedMarkers.Count -le 0) {
    throw "Rust digest capture lacked required populations: events=$($events.Count) args=$($args.Count) returns=$($returns.Count) exact=$($exact.Count) partial=$($partial.Count) skipped=$($skippedMarkers.Count)"
}
if ($panic.Count -ne 1 -or $panicReturns.Count -ne 0) {
    throw "Rust panic capture mismatch: panic=$($panic.Count) returnFields=$($panicReturns.Count)"
}
$badDigests = @($events | Where-Object {
    $argsDigest = Get-Optional $_ 'argsDigest'
    $returnDigest = Get-Optional $_ 'returnDigest'
    ($argsDigest -and $argsDigest -notmatch '^sha256:[0-9a-f]{64}$') -or
    ($returnDigest -and $returnDigest -notmatch '^sha256:[0-9a-f]{64}$')
})
if ($badDigests.Count -ne 0) {
    throw "Rust digest capture produced $($badDigests.Count) malformed digest(s) from $($events.Count) events"
}

[pscustomobject]@{
    SourceFiles = $rewrite.sourceFiles
    RustFiles = $rewrite.rustFiles
    Events = $events.Count
    ArgumentCaptures = $args.Count
    ReturnCaptures = $returns.Count
    ExactEvents = $exact.Count
    PartialEvents = $partial.Count
    SkippedMarkerEvents = $skippedMarkers.Count
    PanicEvents = $panic.Count
    PanicReturnFields = $panicReturns.Count
    MalformedDigests = $badDigests.Count
} | Format-List
Write-Host 'RUST_DIGEST_CAPTURE: PASS'