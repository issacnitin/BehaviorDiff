#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$module = Join-Path $repo 'src/RealDiff.Go.Prototype'

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    $localGoBin = Join-Path $env:LOCALAPPDATA 'Programs/RealDiffGo/go/bin'
    if (-not (Test-Path (Join-Path $localGoBin 'go.exe') -PathType Leaf)) {
        throw "Go was not found on PATH or at $localGoBin"
    }
    $env:PATH = "$localGoBin;$env:PATH"
}

function Invoke-GoTest {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& go @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "go $($Arguments -join ' ') failed with exit code $exitCode"
    }
    return $output
}

Push-Location $module
try {
    Write-Host "Go toolchain: $(& go version)"

    $summaryOutput = Invoke-GoTest @('test', '-v', './...', '-run', 'TestExplicitTokenParallelCorrelation', '-count=1')
    $summaryText = $summaryOutput -join "`n"
    $expectedSummary = 'GO_CORRELATION_SUMMARY events=224 tests=8 wrong_test_ids=0 parent_orphans=0 ordinal_gaps=0 depth_errors=0 duplicates=0 loss=0'
    if (-not $summaryText.Contains($expectedSummary, [StringComparison]::Ordinal)) {
        throw "Required correlation summary was not found: $expectedSummary"
    }

    $boundaryOutput = Invoke-GoTest @('test', '-v', './...', '-run', 'TestDirectGoroutineWithoutCapturedToken', '-count=1')
    $boundaryText = $boundaryOutput -join "`n"
    $expectedBoundary = 'GO_CORRELATION_BOUNDARY uncorrelated=1 test_id=(no-test) method=subject.leaf'
    if (-not $boundaryText.Contains($expectedBoundary, [StringComparison]::Ordinal)) {
        throw "Required uncorrelated-boundary result was not found: $expectedBoundary"
    }

    foreach ($processors in 1, 2, 8) {
        Write-Host "Stress: GOMAXPROCS=$processors, shuffle=on, count=20"
        $env:GOMAXPROCS = [string]$processors
        $null = Invoke-GoTest @('test', './...', '-shuffle=on', '-count=20')
    }

    Write-Host $expectedSummary -ForegroundColor Green
    Write-Host $expectedBoundary -ForegroundColor Green
    Write-Host 'GO_CORRELATION_STRESS passes=60 gomaxprocs=1,2,8 count_per_setting=20' -ForegroundColor Green
} finally {
    Remove-Item Env:GOMAXPROCS -ErrorAction SilentlyContinue
    Pop-Location
}