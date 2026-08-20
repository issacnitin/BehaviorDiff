#requires -Version 7
<#
Wall clock and peak working set for each engine stage, sampled while the process is alive because
PeakWorkingSet64 is not readable once it has exited.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) 'fv-pipeline'
$exe = Join-Path $repo 'src/BehaviorDiff.Engine/bin/Release/net8.0/BehaviorDiff.Engine.exe'

function Measure-Stage {
    param([string]$Label, [string[]]$EngineArgs)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -ArgumentList $EngineArgs -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $env:TEMP "eng-$Label.out")
    $peak = 0L
    while (-not $p.HasExited) {
        try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { }
        Start-Sleep -Milliseconds 25
    }
    $sw.Stop()
    Write-Host ("  {0,-9} exit={1}  wall={2,6:N0} ms  peak working set={3,7:N1} MB" -f `
        $Label, $p.ExitCode, $sw.ElapsedMilliseconds, ($peak / 1MB))
}

$traceBytes = (Get-ChildItem $work -Recurse -Filter 'run.*.ndjson' |
    Where-Object { $_.Name -notmatch 'manifest' } | Measure-Object Length -Sum).Sum
Write-Host ("  input traces : {0:N0} bytes across three runs" -f $traceBytes)

Measure-Stage -Label 'diff' -EngineArgs @(
    'diff',
    '--base1', (Join-Path $work 'base_run1'),
    '--base2', (Join-Path $work 'base_run2'),
    '--pr', (Join-Path $work 'pr_run'),
    '--base-root', (Join-Path ([IO.Path]::GetTempPath()) 'fv-base'),
    '--pr-root', (Join-Path ([IO.Path]::GetTempPath()) 'fv-pr'),
    '--out', (Join-Path $work 'divergence-set.json'))

Measure-Stage -Label 'frontier' -EngineArgs @(
    'frontier',
    '--in', (Join-Path $work 'divergence-set.json'),
    '--changed-files', (Join-Path $work 'changed-files.txt'),
    '--out', (Join-Path $work 'frontier.json'))
