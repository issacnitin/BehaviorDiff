#requires -Version 7
<#
The engine calls a key nondeterministic if anything about it differs between the two base runs,
including the rendered argument and return digests, not just how many times it was called. Counting
only calls therefore understates the baseline badly. This measures the digest-inclusive set, and how
it grows as more base runs are sampled, which is what decides whether two runs is enough.
#>
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) 'fv-sample'
$runs = @(Get-ChildItem $root -Directory | Sort-Object Name)

$samples = @()
foreach ($dir in $runs) {
    $trace = Get-ChildItem $dir.FullName -Filter 'run.*' | Where-Object { $_.Name -notmatch 'manifest|log' } | Select-Object -First 1
    $byKey = @{}
    foreach ($line in [IO.File]::ReadLines($trace.FullName)) {
        if ($line -notmatch '"testId":"([^"]*)","methodFullName":"([^"]+)"') { continue }
        $key = $Matches[1] + '|' + $Matches[2]

        $args = if ($line -match '"argsDigest":"([^"]*)"') { $Matches[1] } else { '' }
        $ret = if ($line -match '"returnDigest":"([^"]*)"') { $Matches[1] } else { '' }
        $exc = if ($line -match '"exceptionType":"([^"]*)"') { $Matches[1] } else { '' }

        if (-not $byKey.ContainsKey($key)) { $byKey[$key] = [System.Collections.Generic.List[string]]::new() }
        $byKey[$key].Add("$args~$ret~$exc")
    }

    # Order within a key is arrival order under parallelism, so compare as a multiset.
    $sig = @{}
    foreach ($k in $byKey.Keys) { $sig[$k] = (($byKey[$k] | Sort-Object) -join "`n") }
    $samples += $sig
    Write-Host ("  {0}: {1,7:N0} keys" -f $dir.Name, $sig.Count)
}

Write-Host ''
Write-Host '=== digest-inclusive nondeterministic key set, by number of base runs ===' -ForegroundColor Cyan
$prev = 0
for ($n = 2; $n -le $samples.Count; $n++) {
    $union = [System.Collections.Generic.HashSet[string]]::new()
    for ($a = 0; $a -lt $n; $a++) {
        for ($b = $a + 1; $b -lt $n; $b++) {
            foreach ($k in (@($samples[$a].Keys) + @($samples[$b].Keys) | Sort-Object -Unique)) {
                if (($samples[$a][$k] ?? '<absent>') -ne ($samples[$b][$k] ?? '<absent>')) { [void]$union.Add($k) }
            }
        }
    }
    $delta = if ($prev -eq 0) { '' } else { ("  (+{0})" -f ($union.Count - $prev)) }
    Write-Host ("    first {0} run(s) -> {1,6:N0} key(s){2}" -f $n, $union.Count, $delta)
    $prev = $union.Count
}

Write-Host ''
Write-Host '=== what a 2-run baseline would miss ===' -ForegroundColor Cyan
$twoRun = [System.Collections.Generic.HashSet[string]]::new()
foreach ($k in (@($samples[0].Keys) + @($samples[1].Keys) | Sort-Object -Unique)) {
    if (($samples[0][$k] ?? '<absent>') -ne ($samples[1][$k] ?? '<absent>')) { [void]$twoRun.Add($k) }
}
$allRun = [System.Collections.Generic.HashSet[string]]::new()
for ($a = 0; $a -lt $samples.Count; $a++) {
    for ($b = $a + 1; $b -lt $samples.Count; $b++) {
        foreach ($k in (@($samples[$a].Keys) + @($samples[$b].Keys) | Sort-Object -Unique)) {
            if (($samples[$a][$k] ?? '<absent>') -ne ($samples[$b][$k] ?? '<absent>')) { [void]$allRun.Add($k) }
        }
    }
}
$missed = @($allRun | Where-Object { -not $twoRun.Contains($_) })
Write-Host ("  nondeterministic per 2 runs      : {0:N0}" -f $twoRun.Count)
Write-Host ("  nondeterministic per {0} runs      : {1:N0}" -f $samples.Count, $allRun.Count)
Write-Host ("  missed by the 2-run baseline     : {0:N0}  ({1:N1}% of the {2}-run set)" -f `
    $missed.Count, (100.0 * $missed.Count / [Math]::Max(1, $allRun.Count)), $samples.Count)
Write-Host '  these would leak into the PR comparison as divergences:'
$missed | Select-Object -First 6 | ForEach-Object {
    $m = $_.Substring($_.IndexOf('|') + 1)
    Write-Host ("    {0}" -f $m.Substring(0, [Math]::Min(96, $m.Length)))
}
