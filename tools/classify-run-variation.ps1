#requires -Version 7
<#
Splits run-to-run key variation into two kinds: keys whose method total is constant across runs
(the same work redistributed across tests) and keys whose method total also moves (genuinely more or
fewer calls). Reads the samples left by sample-base-runs.ps1.
#>
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) 'fv-sample'
$runs = @(Get-ChildItem $root -Directory | Sort-Object Name)

$samples = @()
foreach ($dir in $runs) {
    $trace = Get-ChildItem $dir.FullName -Filter 'run.*' | Where-Object { $_.Name -notmatch 'manifest|log' } | Select-Object -First 1
    $byKey = @{}
    $byMethod = @{}
    foreach ($line in [IO.File]::ReadLines($trace.FullName)) {
        if ($line -match '"testId":"([^"]*)","methodFullName":"([^"]+)"') {
            $key = $Matches[1] + '|' + $Matches[2]
            $byKey[$key] = 1 + ($byKey[$key] ?? 0)
            $byMethod[$Matches[2]] = 1 + ($byMethod[$Matches[2]] ?? 0)
        }
    }
    $samples += [pscustomobject]@{ ByKey = $byKey; ByMethod = $byMethod }
}
Write-Host ("  samples: {0}" -f $samples.Count)

$varyingKeys = [System.Collections.Generic.HashSet[string]]::new()
for ($a = 0; $a -lt $samples.Count; $a++) {
    for ($b = $a + 1; $b -lt $samples.Count; $b++) {
        $ka = $samples[$a].ByKey
        $kb = $samples[$b].ByKey
        foreach ($k in (@($ka.Keys) + @($kb.Keys) | Sort-Object -Unique)) {
            if (($ka[$k] ?? 0) -ne ($kb[$k] ?? 0)) { [void]$varyingKeys.Add($k) }
        }
    }
}

$redistributed = 0
$extraWork = 0
$methods = @{}
foreach ($k in $varyingKeys) {
    $method = $k.Substring($k.IndexOf('|') + 1)
    $methods[$method] = 1 + ($methods[$method] ?? 0)
    $totals = $samples | ForEach-Object { $_.ByMethod[$method] ?? 0 }
    if (($totals | Sort-Object -Unique).Count -eq 1) { $redistributed++ } else { $extraWork++ }
}

Write-Host ''
Write-Host ("  varying keys                        : {0}" -f $varyingKeys.Count)
Write-Host ("    method total constant  (moved)    : {0}" -f $redistributed)
Write-Host ("    method total also varies (extra)  : {0}" -f $extraWork)
Write-Host ("  distinct methods involved           : {0}" -f $methods.Count)
Write-Host ''
Write-Host '  top methods by varying-key count:'
$methods.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10 | ForEach-Object {
    Write-Host ("    {0,5}  {1}" -f $_.Value, $_.Key.Substring(0, [Math]::Min(84, $_.Key.Length)))
}
