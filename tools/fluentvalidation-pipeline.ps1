#requires -Version 7
<#
Three-run pipeline against a real merged upstream PR.

Subject: FluentValidation 6eac0afe (#2136), "Improve performance by removing sync-over-async by
generating sync methods using Zomp.SyncMethodGenerator". Chosen because it touches AbstractValidator
and the Internal rule-execution path (PropertyRule, CollectionPropertyRule, RuleComponent, IncludeRule)
that every validation traverses, and changes no test file, so every test key matches on both sides.

base1/base2 are the same binaries run twice: the diff needs a noise floor before it can call anything
a divergence.
#>
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$baseTree = Join-Path ([IO.Path]::GetTempPath()) 'fv-base'
$prTree = Join-Path ([IO.Path]::GetTempPath()) 'fv-pr'
$work = Join-Path ([IO.Path]::GetTempPath()) 'fv-pipeline'
$tfm = 'net8.0'

function Initialize-Tree {
    param([string]$Tree, [string]$Label)

    $built = Join-Path $Tree "src/FluentValidation.Tests/bin/Release/$tfm"
    $staged = Join-Path $work "$Label-bin"
    Remove-Item $staged -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $staged -Force | Out-Null
    Copy-Item "$built\*" $staged -Recurse -Force

    foreach ($lib in 'RealDiff.Tracer', 'RealDiff.Contracts') {
        Copy-Item (Join-Path $repo "src/$lib/bin/Release/netstandard2.0/$lib.dll") $staged -Force
    }

    foreach ($pair in @(@('FluentValidation', $false), @('FluentValidation.Tests', $true))) {
        $name = $pair[0]
        $weaveArgs = @('--assembly', (Join-Path $staged "$name.dll"), '--include', 'FluentValidation')
        if ($pair[1]) { $weaveArgs += '--test-assembly' }

        $out = dotnet run --project (Join-Path $repo 'tools/Weaver/Weaver.csproj') -c Release -v quiet --no-build -- @weaveArgs 2>&1
        if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 5 | ForEach-Object { Write-Host "    $_" }; throw "weave failed: $Label/$name" }
        $woven = ($out | Select-String 'woven\s+:\s+(\d+)').Matches.Groups[1].Value
        Write-Host ("    {0,-24} woven {1}" -f $name, $woven)
        Move-Item (Join-Path $staged "$name.dll.woven") (Join-Path $staged "$name.dll") -Force
    }
    return $staged
}

function Invoke-Suite {
    param([string]$Staged, [string]$RunName)

    $dir = Join-Path $work $RunName
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $dir -Force | Out-Null

    $env:REALDIFF_BACKEND = 'cecil'
    $env:REALDIFF_NAMESPACES = 'FluentValidation'
    $env:REALDIFF_TRACE = Join-Path $dir 'run.ndjson'

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = dotnet test (Join-Path $Staged 'FluentValidation.Tests.dll') --nologo 2>&1
    $sw.Stop()

    $summary = ($out | Select-String 'Passed!|Failed!' | Select-Object -First 1).Line
    $trace = Get-ChildItem $dir -Filter 'run.*' | Where-Object { $_.Name -notmatch 'manifest|log' } | Select-Object -First 1
    if (-not $trace) { throw "$RunName produced no trace" }

    Write-Host ("    {0,-10} {1,7:N0} ms  {2,14:N0} bytes  {3}" -f `
        $RunName, $sw.ElapsedMilliseconds, $trace.Length, $summary.Trim())
    return [pscustomobject]@{ Dir = $dir; Ms = $sw.ElapsedMilliseconds; Bytes = $trace.Length; Trace = $trace.FullName }
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work -Force | Out-Null

Write-Host '=== weaving ===' -ForegroundColor Cyan
$baseBin = Initialize-Tree -Tree $baseTree -Label 'base'
$prBin = Initialize-Tree -Tree $prTree -Label 'pr'

Write-Host ''
Write-Host '=== running suites ===' -ForegroundColor Cyan
$runs = @{
    base1 = Invoke-Suite -Staged $baseBin -RunName 'base_run1'
    base2 = Invoke-Suite -Staged $baseBin -RunName 'base_run2'
    base3 = Invoke-Suite -Staged $baseBin -RunName 'base_run3'
    pr    = Invoke-Suite -Staged $prBin -RunName 'pr_run'
}
$env:REALDIFF_BACKEND = ''
$env:REALDIFF_TRACE = ''

Write-Host ''
Write-Host '=== engine ===' -ForegroundColor Cyan
$divergence = Join-Path $work 'divergence-set.json'
Import-Module (Join-Path $PSScriptRoot 'RealDiff.Engine.psm1') -Force
$engine = Get-RealDiffEngine

# diff needs this too now: an added member only counts as behavior if its file is one the PR edited.
$changedList = Join-Path $work 'changed-files.txt'
Push-Location $baseTree
git diff --name-only 6eac0afe^ 6eac0afe | Set-Content $changedList
Pop-Location
Write-Host ("  changed files from git : {0}" -f (Get-Content $changedList).Count)

$sw = [Diagnostics.Stopwatch]::StartNew()
& $engine stream-diff --base1 $runs.base1.Dir --base2 $runs.base2.Dir --base3 $runs.base3.Dir --pr $runs.pr.Dir --changed-files $changedList `
    --base-root $baseTree --pr-root $prTree --out $divergence
$diffExit = $LASTEXITCODE
$sw.Stop()
Write-Host ("  diff     : exit={0}  {1:N0} ms" -f $diffExit, $sw.ElapsedMilliseconds)
if ($diffExit -ne 0) { exit $diffExit }

$report = Join-Path $work 'frontier.json'
$sw = [Diagnostics.Stopwatch]::StartNew()
& $engine frontier --in $divergence --changed-files $changedList --out $report
$frontierExit = $LASTEXITCODE
$sw.Stop()
Write-Host ("  frontier : exit={0}  {1:N0} ms" -f $frontierExit, $sw.ElapsedMilliseconds)

Write-Host ''
Write-Host '=== totals ===' -ForegroundColor Cyan
$testMs = $runs.base1.Ms + $runs.base2.Ms + $runs.base3.Ms + $runs.pr.Ms
$totalBytes = $runs.base1.Bytes + $runs.base2.Bytes + $runs.base3.Bytes + $runs.pr.Bytes
Write-Host ("  four test runs  : {0:N0} ms" -f $testMs)
Write-Host ("  trace bytes     : {0:N0} total across four runs" -f $totalBytes)
Write-Host ("  divergence set  : {0}" -f $divergence)
Write-Host ("  frontier report : {0}" -f $report)
