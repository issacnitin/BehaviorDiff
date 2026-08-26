#requires -Version 7
<#
Compares two namings of the same run: the xunit adapter's, and the one derived from the woven trace
itself (subtree under an IsTestRoot call). The question is not whether the names match -- they cannot,
one is a method name and the other carries an invocation ordinal -- but whether the two agree on how
events partition into tests, including for events that cross threads.
#>
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$env:REALDIFF_NAMESPACES = 'SampleApp'
$env:REALDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics'
$env:REALDIFF_BACKEND = 'cecil'

$staged = Join-Path ([System.IO.Path]::GetTempPath()) 'realdiff-corr-bin'
& (Join-Path $PSScriptRoot 'Stage-WovenSample.ps1') -TreeRoot (Split-Path -Parent $PSScriptRoot) -OutDir $staged

function Get-Run {
    param([string]$Name, [string]$Correlation)

    Remove-Item "$env:TEMP\corr-$Name.*" -Force -ErrorAction SilentlyContinue
    $env:REALDIFF_CORRELATION = $Correlation
    $env:REALDIFF_TRACE = "$env:TEMP\corr-$Name"
    dotnet test (Join-Path $staged 'SampleApp.Tests.dll') --nologo 2>&1 |
        Select-String 'Passed!|Failed!' | ForEach-Object { Write-Host "  $Name : $($_.Line.Trim())" }

    $file = Get-ChildItem $env:TEMP -Filter "corr-$Name.*" |
        Where-Object { $_.Name -notmatch 'manifest|log' } | Select-Object -First 1
    $events = [System.Collections.Generic.List[object]]::new()
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        $testId = if ($line -match '"testId":"([^"]*)"') { $Matches[1] } else { '?' }
        $method = if ($line -match '"methodFullName":"([^"]+)"') { $Matches[1] } else { '?' }
        $thread = if ($line -match '"threadId":(\d+)') { [int]$Matches[1] } else { -1 }
        $events.Add([pscustomobject]@{
            TestId   = $testId
            Method   = $method
            ThreadId = $thread
            Harness  = $line -match '"isHarness":true'
        })
    }
    return $events
}

$runs = @{
    framework = Get-Run -Name 'fw' -Correlation ''
    woven     = Get-Run -Name 'wv' -Correlation 'woven'
}
$env:REALDIFF_CORRELATION = ''
$env:REALDIFF_TRACE = ''

$shapes = @{}
foreach ($name in 'framework', 'woven') {
    $subject = @($runs[$name] | Where-Object { -not $_.Harness })
    $groups = @($subject | Group-Object TestId)
    $spanning = @($groups | Where-Object { ($_.Group.ThreadId | Sort-Object -Unique).Count -gt 1 })

    Write-Host ("  {0,-10} events={1,6}  subject={2,6}  tests={3,4}  thread-spanning={4}" -f `
        $name, $runs[$name].Count, $subject.Count, $groups.Count, $spanning.Count)
    foreach ($t in $spanning) {
        Write-Host ("               spans: {0}  threads={1}" -f `
            $t.Name, (($t.Group.ThreadId | Sort-Object -Unique) -join ','))
    }

    # A test's shape is the multiset of methods called under it. Names differ between the two runs by
    # construction, so the shapes are what can be compared.
    $shapes[$name] = @($groups | ForEach-Object {
        ($_.Group.Method | Sort-Object) -join '|'
    } | Sort-Object)

    if ($groups.Count -eq 0) { throw "$name produced no tests; nothing can be concluded" }
    if ($spanning.Count -eq 0) { throw "$name had no test spanning threads; the deciding case never ran" }
}

$onlyFramework = @($shapes.framework | Where-Object { $_ -notin $shapes.woven })
$onlyWoven = @($shapes.woven | Where-Object { $_ -notin $shapes.framework })

Write-Host ''
Write-Host ("  test shapes framework/woven : {0} / {1}" -f $shapes.framework.Count, $shapes.woven.Count)
Write-Host ("  shapes only in framework    : {0}" -f $onlyFramework.Count)
Write-Host ("  shapes only in woven        : {0}" -f $onlyWoven.Count)

foreach ($s in ($onlyFramework + $onlyWoven | Select-Object -First 3)) {
    Write-Host ("    differing shape: {0}" -f $s.Substring(0, [Math]::Min(150, $s.Length)))
}

if ($onlyFramework.Count -ne 0 -or $onlyWoven.Count -ne 0) {
    Write-Host '  RESULT: the two namings disagree on how events partition into tests' -ForegroundColor Red
    exit 1
}

Write-Host '  RESULT: woven correlation partitions events identically to the xunit adapter' -ForegroundColor Green
