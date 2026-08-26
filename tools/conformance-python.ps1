#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-python-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$python = if ($env:REALDIFF_PYTHON) { $env:REALDIFF_PYTHON } else {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs/Python/Python312/python.exe'),
        (Get-Command python3.12 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        (Get-Command python3 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        (Get-Command python -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    ) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $candidates) { throw 'Python 3.12+ was not found. Set REALDIFF_PYTHON.' }
    $candidates
}

function Invoke-Checked([string]$label, [scriptblock]$command) {
    $output = @(& $command 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "$label failed with exit code $exitCode" }
    return $output
}

function Read-Events([string]$directory) {
    return @(Get-ChildItem $directory -Filter 'run.*.ndjson' -File | Where-Object Name -NotLike '*.manifest.*' |
        ForEach-Object { Get-Content $_.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json } })
}

function Assert-KeyIntegrity([object[]]$left, [object[]]$right) {
    $leftGroups = $left | Group-Object { "$($_.testId)`n$($_.methodFullName)" } -AsHashTable -AsString
    $rightGroups = $right | Group-Object { "$($_.testId)`n$($_.methodFullName)" } -AsHashTable -AsString
    if ($leftGroups.Count -ne $rightGroups.Count) { throw 'Python method/key set count differs between clean runs.' }
    foreach ($key in $leftGroups.Keys) {
        if (-not $rightGroups.ContainsKey($key)) { throw "Python key missing from second run: $key" }
        $leftOrdinals = @($leftGroups[$key] | ForEach-Object ordinal | Sort-Object)
        $rightOrdinals = @($rightGroups[$key] | ForEach-Object ordinal | Sort-Object)
        if (($leftOrdinals -join ',') -cne ($rightOrdinals -join ',')) { throw "Python ordinal mismatch: $key" }
        $expected = 0..($leftOrdinals.Count - 1)
        if (($leftOrdinals -join ',') -cne ($expected -join ',')) { throw "Python ordinal sequence has gaps: $key" }
    }
}

try {
    $version = @(& $python -c "import sys; print('.'.join(map(str, sys.version_info[:3]))); raise SystemExit(0 if sys.version_info >= (3, 12) and hasattr(sys, 'monitoring') else 12)" 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Python 3.12+ with sys.monitoring is required: $version" }
    $env:REALDIFF_PYTHON = $python
    $env:REALDIFF_PYTHON_TRACER = Join-Path $repo 'src/RealDiff.Python'
    $env:PYTHONPATH = Join-Path $repo 'samples/PythonReference/src'
    $pytestOutput = Invoke-Checked 'Python reference pytest' { & $python -m pytest -q (Join-Path $repo 'samples/PythonReference/tests_pytest') }
    if (($pytestOutput -join "`n") -notmatch '6 passed') { throw 'Python pytest runner count was not 6.' }
    $unittestOutput = Invoke-Checked 'Python reference unittest' { & $python -m unittest discover -s (Join-Path $repo 'samples/PythonReference/tests_unittest') -v }
    if (($unittestOutput -join "`n") -notmatch 'Ran 6 tests') { throw 'Python unittest runner count was not 6.' }
    $env:PYTHONPATH = Join-Path $repo 'src/RealDiff.Python'
    $unitOutput = Invoke-Checked 'Python tracer tests' { & $python -m unittest discover -s (Join-Path $repo 'src/RealDiff.Python/tests') -v }
    if (($unitOutput -join "`n") -notmatch 'Ran 17 tests') { throw 'Python tracer test count was not 17.' }

    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    $target = Join-Path $work 'repository'
    Copy-Item (Join-Path $repo 'samples/PythonReference') $target -Recurse
    & git -C $target init -b main | Out-Null
    & git -C $target config user.name RealDiff
    & git -C $target config user.email realdiff@example.invalid
    & git -C $target add .
    & git -C $target commit -m baseline | Out-Null
    & git -C $target branch proposed

    $env:DOTNET_ROOT = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet' }
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    $env:NuGetAudit = 'false'
    $null = Invoke-Checked 'RealDiff CLI build' { & dotnet build (Join-Path $repo 'src/RealDiff.Cli/RealDiff.Cli.csproj') -c Release --nologo }
    $null = Invoke-Checked 'RealDiff Rust engine build' { & cargo build --release --manifest-path (Join-Path $repo 'src/RealDiff.Engine.Rust/Cargo.toml') }
    $cli = Join-Path $repo 'src/RealDiff.Cli/bin/Release/net8.0/realdiff.dll'
    $cache = Join-Path $work 'cache'
    $run = Join-Path $work 'run'
    $output = Invoke-Checked 'Python CLI conformance run' {
        & dotnet $cli $target --base main --pr proposed --work $run --findings (Join-Path $run 'findings.json') `
            --cache-dir $cache --cache-retention 1d --keep --keep-traces 1d
    }
    $events1 = Read-Events (Join-Path $run 'base_run1')
    $events2 = Read-Events (Join-Path $run 'base_run2')
    Assert-KeyIntegrity $events1 $events2
    $subjects = @($events1 | Where-Object {
        $null -eq $_.PSObject.Properties['isHarness'] -or -not [bool]$_.isHarness
    })
    $methods = @($subjects.methodFullName | Sort-Object -Unique)
    $roots = @($events1 | Where-Object {
        $null -ne $_.PSObject.Properties['isHarness'] -and [bool]$_.isHarness
    })
    if ($roots.Count -ne 6) { throw "Python derived pytest roots: expected=6 actual=$($roots.Count)" }
    if (@($subjects | Where-Object testId -eq '(no-test)').Count -ne 0) { throw 'Python subject no-test tripwire failed.' }
    if (@($subjects | Where-Object { $_.callDepth -le 0 }).Count -ne 0) { throw 'Python subject root tripwire failed.' }
    if (@($subjects | Where-Object { $_.filePathResolution -ne 'debugInfo' -or $_.line -le 0 }).Count -ne 0) {
        throw 'Python source-resolution tripwire failed.'
    }
    if ($methods.Count -lt 70) { throw "Python method volume below 70: $($methods.Count)" }
    $divergence = Get-Content (Join-Path $run 'divergence-set.json') -Raw | ConvertFrom-Json
    if ($divergence.counts.matchedKeys -lt 325) { throw "Python matched keys below 325: $($divergence.counts.matchedKeys)" }
    if ($divergence.counts.remainingDivergences -ne 0 -or $divergence.counts.toolingGaps -ne 0) {
        throw 'Python clean engine gate produced differences or tooling gaps.'
    }
    $manifest = @(Get-Content (Join-Path $run 'base_run1/run.python.manifest.ndjson') | ForEach-Object { $_ | ConvertFrom-Json })
    $writer = $manifest[-1]
    if ($writer.enqueued -ne $writer.written -or $writer.dropped -ne 0 -or $writer.written -ne $events1.Count) {
        throw 'Python writer reconciliation failed.'
    }
    foreach ($module in @($manifest | Where-Object kind -eq 'assembly')) {
        if ($module.discoveredMembers -ne ($module.patchedMembers + $module.skippedMembers) -or $module.patchFailedMembers -ne 0) {
            throw "Python module reconciliation failed: $($module.assembly)"
        }
    }

    $cacheRun = Join-Path $work 'cache-run'
    $null = Invoke-Checked 'Python cache-hit run' {
        & dotnet $cli $target --base main --pr proposed --work $cacheRun --findings (Join-Path $cacheRun 'findings.json') `
            --cache-dir $cache --cache-retention 1d --keep --keep-traces 1d
    }
    $cacheFindings = Get-Content (Join-Path $cacheRun 'findings.json') -Raw | ConvertFrom-Json
    if ($cacheFindings.baseTraceCache.status -ne 'hit') { throw 'Python base-trace cache did not hit on identical tracer/runtime/scope.' }

    Write-Host "PYTHON_CONFORMANCE: PASS" -ForegroundColor Green
    Write-Host "  python       : $version"
    Write-Host "  runner tests : pytest=6 unittest=6"
    Write-Host "  matched keys : $($divergence.counts.matchedKeys)"
    Write-Host "  methods      : $($methods.Count)"
    Write-Host "  events       : $($events1.Count)"
    Write-Host "  cache        : hit"
} finally {
    Get-ChildItem (Join-Path $repo 'src/RealDiff.Python'), (Join-Path $repo 'samples/PythonReference') -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -In @('__pycache__', '.pytest_cache') | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}