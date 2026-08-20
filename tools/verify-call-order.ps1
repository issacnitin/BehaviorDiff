#requires -Version 7.0
<#
    The engine matches events on (TestId, MethodFullName, call-order-index). Call-order-index is the
    position of an event among the events sharing that key, in file order.

    Under parallelism, file order is arrival order at a shared writer, so events from different tests
    interleave. That is harmless for the key as long as ordering is stable WITHIN a key. This script
    checks exactly that, by running the suite twice and comparing the per-key digest sequences.

    A key whose sequence differs between runs would produce a phantom divergence on unchanged code.
#>
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$env:BEHAVIORDIFF_NAMESPACES = 'SampleApp'
$env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics'
$env:BEHAVIORDIFF_BACKEND = 'cecil'

$staged = Join-Path ([System.IO.Path]::GetTempPath()) 'behaviordiff-order-bin'
& (Join-Path $PSScriptRoot 'Stage-WovenSample.ps1') -TreeRoot (Split-Path -Parent $PSScriptRoot) -OutDir $staged

$runs = @()
foreach ($i in 1, 2) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) "behaviordiff-order$i"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $dir | Out-Null
    $env:BEHAVIORDIFF_TRACE = Join-Path $dir 'run.ndjson'

    dotnet test (Join-Path $staged 'SampleApp.Tests.dll') --nologo | Out-Null

    $file = Get-ChildItem $dir -Filter 'run.*.ndjson' | Where-Object { $_.Name -notlike '*manifest*' } | Select-Object -First 1

    # Field extraction by regex. ConvertFrom-Json per line costs minutes at this event count, and the
    # fields needed here are flat strings.
    $events = foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
        if ($line.Length -eq 0) { continue }
        [pscustomobject]@{
            TestId     = if ($line -match '"testId":"([^"]*)"') { $Matches[1] } else { '' }
            Method     = if ($line -match '"methodFullName":"([^"]*)"') { $Matches[1] } else { '' }
            ArgsDigest = if ($line -match '"argsDigest":"([^"]*)"') { $Matches[1] } else { '' }
            Return     = if ($line -match '"returnDigest":"([^"]*)"') { $Matches[1] } else { '' }
            Exception  = if ($line -match '"exceptionType":"([^"]*)"') { $Matches[1] } else { '' }
            ThreadId   = if ($line -match '"threadId":(\d+)') { [int]$Matches[1] } else { -1 }
            IsHarness  = $line -match '"isHarness":true'
        }
    }

    $runs += , @{ Dir = $dir; Events = @($events) }
}

function Get-KeySequences($events) {
    $map = @{}
    foreach ($e in $events) {
        if ($e.IsHarness) { continue }
        $key = "$($e.TestId)|$($e.Method)"
        if (-not $map.ContainsKey($key)) { $map[$key] = [System.Collections.Generic.List[string]]::new() }
        $map[$key].Add("$($e.ArgsDigest)/$($e.Return)/$($e.Exception)")
    }
    return $map
}

$a = Get-KeySequences $runs[0].Events
$b = Get-KeySequences $runs[1].Events

Write-Host ''
Write-Host '=== call-order-index stability across two runs ===' -ForegroundColor Cyan
Write-Host "  run A events (subject) : $(@($runs[0].Events | Where-Object { -not $_.IsHarness }).Count)"
Write-Host "  run B events (subject) : $(@($runs[1].Events | Where-Object { -not $_.IsHarness }).Count)"
Write-Host "  run A keys             : $($a.Count)"
Write-Host "  run B keys             : $($b.Count)"

$onlyA = @($a.Keys | Where-Object { -not $b.ContainsKey($_) })
$onlyB = @($b.Keys | Where-Object { -not $a.ContainsKey($_) })
Write-Host "  keys only in A         : $($onlyA.Count)"
Write-Host "  keys only in B         : $($onlyB.Count)"
foreach ($k in ($onlyA + $onlyB)) { Write-Host "    $k" -ForegroundColor Red }

$countMismatch = @()
$orderMismatch = @()
foreach ($key in $a.Keys) {
    if (-not $b.ContainsKey($key)) { continue }
    if ($a[$key].Count -ne $b[$key].Count) { $countMismatch += $key; continue }
    for ($i = 0; $i -lt $a[$key].Count; $i++) {
        if ($a[$key][$i] -ne $b[$key][$i]) { $orderMismatch += "$key [index $i]"; break }
    }
}

$multi = @($a.Keys | Where-Object { $a[$_].Count -gt 1 })
Write-Host "  keys with >1 call      : $($multi.Count)  (these are the ones the index actually orders)"
Write-Host "  max calls under one key: $((($a.Keys | ForEach-Object { $a[$_].Count }) | Measure-Object -Maximum).Maximum)"
Write-Host "  keys with differing call counts : $($countMismatch.Count)"
foreach ($k in $countMismatch) { Write-Host "    $k  A=$($a[$k].Count) B=$($b[$k].Count)" -ForegroundColor Red }
Write-Host "  keys with differing sequence    : $($orderMismatch.Count)"
foreach ($k in $orderMismatch) { Write-Host "    $k" -ForegroundColor Red }

$threadSpan = $runs[0].Events | Where-Object { -not $_.IsHarness } | Group-Object TestId |
    Where-Object { ($_.Group.ThreadId | Sort-Object -Unique).Count -gt 1 }
Write-Host "  tests whose subject events span >1 thread : $($threadSpan.Count)"
foreach ($t in $threadSpan) {
    Write-Host "    $($t.Name)  threads=$((($t.Group.ThreadId | Sort-Object -Unique) -join ','))"
}

$failed = $onlyA.Count + $onlyB.Count + $countMismatch.Count + $orderMismatch.Count

# A comparison over nothing compares equal. Without this the script reports "stable" when the test run
# produced no events at all, which is how it first passed after the test hosts were killed underneath it.
$subjectA = @($runs[0].Events | Where-Object { -not $_.IsHarness }).Count
$subjectB = @($runs[1].Events | Where-Object { -not $_.IsHarness }).Count
$minimumEvents = 1000
$minimumMultiKeys = 10
Write-Host ''
Write-Host '  evidence floor:'
Write-Host "    subject events A/B  : $subjectA / $subjectB  (need >= $minimumEvents each)"
Write-Host "    keys with >1 call   : $($multi.Count)  (need >= $minimumMultiKeys, else nothing was ordered)"
if ($subjectA -lt $minimumEvents -or $subjectB -lt $minimumEvents) {
    throw "not enough events to conclude anything: A=$subjectA B=$subjectB"
}
if ($multi.Count -lt $minimumMultiKeys) {
    throw "only $($multi.Count) key(s) had more than one call, so call-order-index was never exercised"
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host '  RESULT: call-order-index is stable across runs under parallelism' -ForegroundColor Green
}
else {
    Write-Host "  RESULT: $failed key(s) unstable - the engine would report phantom divergences" -ForegroundColor Red
    throw 'call-order-index is not stable'
}
