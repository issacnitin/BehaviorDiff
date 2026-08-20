#requires -Version 7
<#
Checks whether the PR's changed types produce different events at all. If the counts are identical,
the change is invisible at method granularity and EXPECTED 0 is a property of where the change lives,
not of the tool failing to look.
#>
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) 'fv-pipeline'
$pattern = 'AbstractValidator|PropertyRule|RuleComponent|IncludeRule|CollectionPropertyRule'

function Get-Counts {
    param([string]$Dir)

    $trace = Get-ChildItem $Dir -Filter 'run.*.ndjson' |
        Where-Object { $_.Name -notmatch 'manifest' } | Select-Object -First 1
    $counts = @{}
    foreach ($line in [IO.File]::ReadLines($trace.FullName)) {
        if ($line -match '"methodFullName":"([^"]+)"') {
            $method = $Matches[1]
            if ($method -match $pattern) { $counts[$method] = 1 + ($counts[$method] ?? 0) }
        }
    }
    return $counts
}

$base = Get-Counts (Join-Path $work 'base_run1')
$pr = Get-Counts (Join-Path $work 'pr_run')

$all = @($base.Keys) + @($pr.Keys) | Sort-Object -Unique
$differing = @($all | Where-Object { ($base[$_] ?? 0) -ne ($pr[$_] ?? 0) })

Write-Host ("  methods in the PR's changed types : {0}" -f $all.Count)
Write-Host ("  with differing event counts       : {0}" -f $differing.Count)
foreach ($m in ($differing | Select-Object -First 10)) {
    Write-Host ("    base={0,6}  pr={1,6}   {2}" -f ($base[$m] ?? 0), ($pr[$m] ?? 0), $m.Substring(0, [Math]::Min(74, $m.Length)))
}
Write-Host ("  total events in those types       : base={0:N0}  pr={1:N0}" -f `
    ($base.Values | Measure-Object -Sum).Sum, ($pr.Values | Measure-Object -Sum).Sum)
