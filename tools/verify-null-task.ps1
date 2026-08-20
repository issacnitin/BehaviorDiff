#requires -Version 7
<#
A non-async method declared to return Task that returns null reaches AttachContinuation with no task.
That path emits immediately; EndCall then runs too. If the frame's emit claim were not honoured the
call would appear twice, which would read as the method having been called twice.
#>
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$env:BEHAVIORDIFF_NAMESPACES = 'SampleApp'
$env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics'
$env:BEHAVIORDIFF_BACKEND = 'cecil'

$work = Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-nulltask'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work | Out-Null
$env:BEHAVIORDIFF_TRACE = Join-Path $work 'run.ndjson'

$staged = Join-Path $work 'bin'
& (Join-Path $PSScriptRoot 'Stage-WovenSample.ps1') -TreeRoot (Split-Path -Parent $PSScriptRoot) -OutDir $staged
dotnet test (Join-Path $staged 'SampleApp.Tests.dll') --nologo | Out-Null

$trace = Get-ChildItem $work -Filter 'run.*.ndjson' |
    Where-Object { $_.Name -notlike '*manifest*' } | Select-Object -First 1
if (-not $trace) { throw 'no trace produced' }

$counts = @{}
foreach ($line in [IO.File]::ReadLines($trace.FullName)) {
    if ($line -match '"testId":"([^"]*)","methodFullName":"(SampleApp\.NullTaskProbe\.[^"]+)"') {
        # Per test, not per method: the constructor is legitimately called once by each test.
        $key = $Matches[2] + '  [' + $Matches[1] + ']'
        $counts[$key] = 1 + ($counts[$key] ?? 0)
    }
}

Write-Host '=== null-Task emission ==='
if ($counts.Count -eq 0) { throw 'NullTaskProbe produced no events at all; the fixture did not run' }

$failed = 0
foreach ($entry in $counts.GetEnumerator() | Sort-Object Name) {
    $ok = $entry.Value -eq 1
    if (-not $ok) { $failed++ }
    Write-Host ("  {0,-6} {1}  events={2} (expected 1)" -f ($ok ? 'PASS' : 'FAIL'), $entry.Name, $entry.Value)
}

if ($failed -gt 0) { throw "$failed null-Task method(s) emitted more than once" }
Write-Host '  RESULT: the null-Task path emits exactly once per call' -ForegroundColor Green
