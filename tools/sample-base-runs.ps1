#requires -Version 7
<#
Characterises run-to-run variation of the woven base build. Nothing here fixes anything: it reports
which methods vary, whether the same ones vary each time, and whether the set of keys called
nondeterministic keeps growing as more base runs are sampled.

Reuses the already-woven base-bin so every run is the same bytes, not just the same commit.
#>
param([int]$Runs = 4)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) 'fv-pipeline'
$staged = Join-Path $work 'base-bin'
$sampleRoot = Join-Path ([IO.Path]::GetTempPath()) 'fv-sample'

if (-not (Test-Path (Join-Path $staged 'FluentValidation.Tests.dll'))) {
    throw "no woven base-bin at $staged; run fluentvalidation-pipeline.ps1 first"
}

Remove-Item $sampleRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $sampleRoot -Force | Out-Null

$samples = @()
for ($i = 1; $i -le $Runs; $i++) {
    $dir = Join-Path $sampleRoot "run$i"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null

    $env:REALDIFF_BACKEND = 'cecil'
    $env:REALDIFF_NAMESPACES = 'FluentValidation'
    $env:REALDIFF_TRACE = Join-Path $dir 'run.ndjson'
    dotnet test (Join-Path $staged 'FluentValidation.Tests.dll') --nologo 2>&1 | Out-Null

    $trace = Get-ChildItem $dir -Filter 'run.*' | Where-Object { $_.Name -notmatch 'manifest|log' } | Select-Object -First 1
    if (-not $trace) { throw "run$i produced no trace" }

    # Per-method counts and per-key counts. "Key" is what the engine matches on: test plus method.
    $byMethod = @{}
    $byKey = @{}
    $events = 0
    foreach ($line in [IO.File]::ReadLines($trace.FullName)) {
        $events++
        if ($line -match '"testId":"([^"]*)","methodFullName":"([^"]+)"') {
            $test = $Matches[1]
            $method = $Matches[2]
            $byMethod[$method] = 1 + ($byMethod[$method] ?? 0)
            $k = "$test|$method"
            $byKey[$k] = 1 + ($byKey[$k] ?? 0)
        }
    }

    Write-Host ("  run{0}  events={1,8:N0}  methods={2,6:N0}  keys={3,7:N0}" -f $i, $events, $byMethod.Count, $byKey.Count)
    $samples += [pscustomobject]@{ Index = $i; Events = $events; ByMethod = $byMethod; ByKey = $byKey }
}
$env:REALDIFF_BACKEND = ''
$env:REALDIFF_TRACE = ''

Write-Host ''
Write-Host '=== event count spread ===' -ForegroundColor Cyan
$counts = $samples.Events
Write-Host ("  min {0:N0}   max {1:N0}   spread {2}" -f ($counts | Measure-Object -Minimum).Minimum,
    ($counts | Measure-Object -Maximum).Maximum,
    (($counts | Measure-Object -Maximum).Maximum - ($counts | Measure-Object -Minimum).Minimum))

Write-Host ''
Write-Host '=== methods whose event count is not identical across all runs ===' -ForegroundColor Cyan
$allMethods = $samples.ByMethod.Keys | Sort-Object -Unique
$varying = @()
foreach ($m in $allMethods) {
    $series = $samples | ForEach-Object { $_.ByMethod[$m] ?? 0 }
    if (($series | Sort-Object -Unique).Count -gt 1) {
        $varying += [pscustomobject]@{ Method = $m; Series = $series; Spread = (($series | Measure-Object -Maximum).Maximum - ($series | Measure-Object -Minimum).Minimum) }
    }
}
Write-Host ("  varying methods : {0} of {1}" -f $varying.Count, $allMethods.Count)
$varying | Sort-Object Spread -Descending | Select-Object -First 20 | ForEach-Object {
    Write-Host ("    {0,-6} {1}  [{2}]" -f ("+/-" + $_.Spread), $_.Method.Substring(0, [Math]::Min(88, $_.Method.Length)), ($_.Series -join ','))
}

Write-Host ''
Write-Host '=== is it the same methods each time? ===' -ForegroundColor Cyan
# Pairwise disagreement sets, so we can see whether run1-vs-2 finds the same methods as run3-vs-4.
function Get-DisagreeingMethods([hashtable]$a, [hashtable]$b) {
    $keys = @($a.Keys) + @($b.Keys) | Sort-Object -Unique
    $out = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($k in $keys) { if (($a[$k] ?? 0) -ne ($b[$k] ?? 0)) { [void]$out.Add($k) } }
    return $out
}
$pair12 = Get-DisagreeingMethods $samples[0].ByMethod $samples[1].ByMethod
if ($Runs -ge 4) {
    $pair34 = Get-DisagreeingMethods $samples[2].ByMethod $samples[3].ByMethod
    $both = @($pair12 | Where-Object { $pair34.Contains($_) })
    Write-Host ("  run1-vs-run2 disagreeing methods : {0}" -f $pair12.Count)
    Write-Host ("  run3-vs-run4 disagreeing methods : {0}" -f $pair34.Count)
    Write-Host ("  in both pairs                    : {0}" -f $both.Count)
    Write-Host ("  found only by the 1-2 pair       : {0}" -f (@($pair12 | Where-Object { -not $pair34.Contains($_) })).Count)
    Write-Host ("  found only by the 3-4 pair       : {0}" -f (@($pair34 | Where-Object { -not $pair12.Contains($_) })).Count)
}

Write-Host ''
Write-Host '=== does the nondeterministic KEY set grow with more runs? ===' -ForegroundColor Cyan
Write-Host '  (union of keys that disagree in any pair drawn from the first N runs)'
for ($n = 2; $n -le $Runs; $n++) {
    $union = [System.Collections.Generic.HashSet[string]]::new()
    for ($a = 0; $a -lt $n; $a++) {
        for ($b = $a + 1; $b -lt $n; $b++) {
            $ka = $samples[$a].ByKey
            $kb = $samples[$b].ByKey
            $keys = @($ka.Keys) + @($kb.Keys) | Sort-Object -Unique
            foreach ($k in $keys) { if (($ka[$k] ?? 0) -ne ($kb[$k] ?? 0)) { [void]$union.Add($k) } }
        }
    }
    Write-Host ("    first {0} run(s) -> {1,6:N0} nondeterministic key(s)" -f $n, $union.Count)
}
