#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$WorkDirectory,

    [string]$RustEngine,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$work = [IO.Path]::GetFullPath($WorkDirectory)
$engine = if ([string]::IsNullOrWhiteSpace($RustEngine)) {
    Join-Path $repo 'src/RealDiff.Engine.Rust/target/release/realdiff-engine.exe'
} else {
    [IO.Path]::GetFullPath($RustEngine)
}
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $work 'streaming-kill-gate'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}

$maximumPeakRssBytes = 300L * 1MB
$expectedEvents = 423974L
$expectedMatchedKeys = 53245L
$expectedNoiseKeys = 2649L

foreach ($path in @(
    $engine,
    (Join-Path $work 'base_run1'),
    (Join-Path $work 'base_run2'),
    (Join-Path $work 'base_run3'),
    (Join-Path $work 'pr_run'),
    (Join-Path $work 'base'),
    (Join-Path $work 'pr')
)) {
    if (-not (Test-Path $path)) {
        throw "Streaming kill-gate input is missing: $path"
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$reportPath = Join-Path $output 'stream-probe.json'
$stdoutPath = Join-Path $output 'stream-probe.stdout.log'
$stderrPath = Join-Path $output 'stream-probe.stderr.log'
Remove-Item $reportPath, $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

$arguments = @(
    'stream-probe',
    '--base1', (Join-Path $work 'base_run1'),
    '--base2', (Join-Path $work 'base_run2'),
    '--base3', (Join-Path $work 'base_run3'),
    '--pr', (Join-Path $work 'pr_run'),
    '--base-root', (Join-Path $work 'base'),
    '--pr-root', (Join-Path $work 'pr'),
    '--out', $reportPath
)

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $engine -ArgumentList $arguments -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
$peakRssBytes = 0L
do {
    try {
        $process.Refresh()
        $peakRssBytes = [Math]::Max($peakRssBytes, $process.WorkingSet64)
    }
    catch { }
} while (-not $process.WaitForExit(5))
$process.WaitForExit()
$stopwatch.Stop()

if ($process.ExitCode -ne 0) {
    throw "Streaming prototype exited $($process.ExitCode): $(Get-Content $stderrPath -Raw)"
}
if (-not (Test-Path $reportPath -PathType Leaf)) {
    throw 'Streaming prototype emitted no report.'
}

$report = Get-Content $reportPath -Raw | ConvertFrom-Json -Depth 20
$failures = @()
if ($peakRssBytes -ge $maximumPeakRssBytes) {
    $failures += "peak RSS $peakRssBytes is not strictly below $maximumPeakRssBytes bytes (300 MiB HARD STOP)"
}
if ([long]$report.eventsConsumed -ne $expectedEvents) {
    $failures += "events consumed=$($report.eventsConsumed), expected=$expectedEvents"
}
if ([long]$report.matchedKeys -ne $expectedMatchedKeys) {
    $failures += "matched keys=$($report.matchedKeys), expected=$expectedMatchedKeys"
}
if ([long]$report.noiseKeys -ne $expectedNoiseKeys) {
    $failures += "noise keys=$($report.noiseKeys), expected=$expectedNoiseKeys"
}
if ([long]$report.baseCallNodes -le 0 -or [long]$report.prCallNodes -le 0) {
    $failures += "compact call graphs are empty: base=$($report.baseCallNodes) pr=$($report.prCallNodes)"
}

$result = [pscustomobject][ordered]@{
    passed = $failures.Count -eq 0
    peakRssBytes = $peakRssBytes
    peakRssMiB = [Math]::Round($peakRssBytes / 1MB, 3)
    hardLimitBytes = $maximumPeakRssBytes
    wallMilliseconds = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
    eventsConsumed = [long]$report.eventsConsumed
    matchedKeys = [long]$report.matchedKeys
    noiseKeys = [long]$report.noiseKeys
    baseCallNodes = [long]$report.baseCallNodes
    prCallNodes = [long]$report.prCallNodes
    failures = $failures
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'kill-gate-result.json')
$result | Format-List

if ($failures.Count -ne 0) {
    throw "RUST STREAMING HARD GATE FAILED: $($failures -join '; ')"
}

Write-Host 'RUST STREAMING PRE-FRONTIER KILL-GATE: PASS' -ForegroundColor Green