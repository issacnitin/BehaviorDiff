#requires -Version 7.0
<#
    Negative-test suite. Each case states a condition the pipeline must refuse to run under, and the
    diagnostic it must say so with. Add cases here as new refusal conditions appear.
#>
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

Write-Host '=== build ===' -ForegroundColor Cyan
dotnet build BehaviorDiff.sln -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$assert = Join-Path $PSScriptRoot 'Assert-RunRefused.ps1'
$results = @()

$results += & $assert `
    -Name 'DebugType=none assembly must invalidate the run' `
    -FilePath 'pwsh' `
    -ArgumentList @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'negative\run-nopdb-scenario.ps1')) `
    -ExpectedMessages @(
        'RUN INVALID - SourceUnavailable',
        "assembly 'SampleApp.NoPdb'",
        'would be silently classified EXPECTED',
        '<DebugType>portable</DebugType>'
    ) `
    -WorkingDirectory $repo

Write-Host ''
Write-Host '=== negative test summary ===' -ForegroundColor Cyan
foreach ($r in $results) {
    "  {0,-6} {1}" -f $(if ($r.Passed) { 'PASS' } else { 'FAIL' }), $r.Name
}

$failed = ($results | Where-Object { -not $_.Passed }).Count
Write-Host "  $($results.Count) case(s), $failed failed"
if ($failed -gt 0) { throw "$failed negative test(s) failed" }
