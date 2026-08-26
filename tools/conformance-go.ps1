#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-go-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$expectedRunnerTests = 10
Import-Module (Join-Path $PSScriptRoot 'BehaviorDiff.Conformance.psm1') -Force

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    $goBin = @(
        (Join-Path $env:LOCALAPPDATA 'Programs/BehaviorDiffGo/go/bin'),
        (Join-Path $HOME '.behaviordiff-tools/go/bin')
    ) | Where-Object { Test-Path (Join-Path $_ 'go.exe') -PathType Leaf } | Select-Object -First 1
    if (-not $goBin) { throw 'Go was not found on PATH or in a BehaviorDiff local tool directory.' }
    $env:PATH = "$goBin;$env:PATH"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $dotnetBin = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet'),
        'C:\.tools\dotnet'
    ) | Where-Object { Test-Path (Join-Path $_ 'dotnet.exe') -PathType Leaf } | Select-Object -First 1
    if (-not $dotnetBin) { throw 'dotnet was not found on PATH or in a known local tool directory.' }
    $env:PATH = "$dotnetBin;$env:PATH"
}

function Copy-CleanDirectory([string]$source, [string]$destination) {
    Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem $source -Force | Where-Object Name -NotIn @('.git', 'bin', 'obj') | ForEach-Object {
        Copy-Item $_.FullName -Destination $destination -Recurse -Force
    }
}

function New-CleanTree([string]$tree) {
    New-Item -ItemType Directory -Path (Join-Path $tree 'src') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $tree 'samples') -Force | Out-Null
    Copy-CleanDirectory (Join-Path $repo 'src/BehaviorDiff.Go') (Join-Path $tree 'src/BehaviorDiff.Go')
    Copy-CleanDirectory (Join-Path $repo 'samples/GoReference') (Join-Path $tree 'samples/GoReference')
}

function Get-SourceHashes([string]$source) {
    $hashes = [ordered]@{}
    Get-ChildItem $source -File -Recurse | Where-Object Extension -In @('.go', '.mod', '.sum') |
        Sort-Object FullName | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($source, $_.FullName).Replace('\', '/')
            $hashes[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
    return $hashes
}

function Assert-SourceHashes([Collections.IDictionary]$expected, [Collections.IDictionary]$actual, [string]$label) {
    if ($expected.Count -ne $actual.Count) {
        throw "Go source hash guard failed ($label): file count $($expected.Count) vs $($actual.Count)"
    }
    foreach ($path in $expected.Keys) {
        if (-not $actual.Contains($path) -or $expected[$path] -cne $actual[$path]) {
            throw "Go source hash guard failed ($label): $path changed"
        }
    }
}

function Invoke-Go([string]$directory, [string[]]$arguments) {
    Push-Location $directory
    try {
        $output = @(& go @arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) { throw "go $($arguments -join ' ') failed in $directory with exit code $exitCode" }
        return $output
    } finally { Pop-Location }
}

function Build-Rewriter([string]$tree, [string]$binary) {
    $null = Invoke-Go (Join-Path $tree 'src/BehaviorDiff.Go') @(
    'build', '-o', $binary, './cmd/behaviordiff-go-rewrite')
}

function Rewrite-Reference([string]$tree, [string]$binary, [string]$cache, [string]$label) {
    $source = Join-Path $tree 'samples/GoReference'
    $before = Get-SourceHashes $source
    $previousRoot = $env:BEHAVIORDIFF_REPOSITORY_ROOT
    try {
        $env:BEHAVIORDIFF_REPOSITORY_ROOT = $tree
        $output = @(& $binary --source $source --out $cache 2>&1)
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) { throw "Go rewrite failed ($label) with exit code $exitCode" }
    } finally { $env:BEHAVIORDIFF_REPOSITORY_ROOT = $previousRoot }
    Assert-SourceHashes $before (Get-SourceHashes $source) $label
    $reportPath = Join-Path $cache 'behaviordiff-rewrite-report.json'
    if (-not (Test-Path $reportPath -PathType Leaf)) { throw "Go rewrite report missing ($label)" }
    return Get-Content $reportPath -Raw | ConvertFrom-Json
}

function Run-Reference([string]$cache, [string]$runDirectory, [string]$label) {
    Remove-Item $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $runnerPath = Join-Path $runDirectory 'go-test.jsonl'
    $previousTrace = $env:BEHAVIORDIFF_TRACE
    try {
        $env:BEHAVIORDIFF_TRACE = Join-Path $runDirectory 'run.ndjson'
        $output = @(Invoke-Go $cache @('test', '-count=1', '-json', './...'))
        $output | Set-Content $runnerPath -Encoding utf8NoBOM
    } finally { $env:BEHAVIORDIFF_TRACE = $previousTrace }

    $events = @($output | ForEach-Object {
        try { $_ | ConvertFrom-Json -ErrorAction Stop } catch { $null }
    } | Where-Object { $null -ne $_ })
    $runnerTests = @($events | Where-Object {
        $_.Action -eq 'pass' -and
        $null -ne $_.PSObject.Properties['Test'] -and
        -not [string]::IsNullOrWhiteSpace([string]$_.Test) -and
        [string]$_.Test -notmatch '/'
    }).Count
    if ($runnerTests -ne $expectedRunnerTests) {
        throw "Go runner count mismatch ($label): expected=$expectedRunnerTests actual=$runnerTests"
    }
    return $runnerTests
}

function Assert-RunIntegrity([string]$runDirectory, [string]$tree, [object]$report, [string]$label) {
    $run = Read-BehaviorDiffConformanceRun $runDirectory
    $rootMethods = @($run.ManifestRecords | Where-Object {
        $_.kind -eq 'member' -and $null -ne $_.PSObject.Properties['isTestRoot'] -and [bool]$_.isTestRoot
    } | ForEach-Object method | Sort-Object -Unique)
    $derivedTests = @($run.Events | Where-Object methodFullName -In $rootMethods).Count
    if ($derivedTests -ne $expectedRunnerTests) {
        throw "Go derived root mismatch ($label): expected=$expectedRunnerTests actual=$derivedTests"
    }

    $noTest = @($run.Events | Where-Object testId -eq '(no-test)')
    if ($noTest.Count -ne 0) { throw "Go no-test guard failed ($label): $($noTest.Count) event(s)" }

    $wrongPaths = @($run.Events | Where-Object {
        [string]$_.filePath -notmatch '^samples/GoReference/.+\.go$' -or
        [string]$_.filePath -match '(?i)cache|internal/behaviordiffrt|\\'
    })
    if ($wrongPaths.Count -ne 0) { throw "Go source path guard failed ($label): $($wrongPaths.Count) event(s)" }
    foreach ($path in @($run.Events.filePath | Sort-Object -Unique)) {
        if (-not (Test-Path (Join-Path $tree ([string]$path).Replace('/', [IO.Path]::DirectorySeparatorChar)) -PathType Leaf)) {
            throw "Go source path does not resolve to original source ($label): $path"
        }
    }

    $manifestFiles = @(Get-ChildItem $runDirectory -Filter '*.manifest.ndjson' -File)
    $orphanCount = 0
    $writerEvents = 0
    $writerEnqueued = 0L
    $writerWritten = 0L
    $writerDropped = 0L
    $writerCapacity = 0L
    $moduleTraced = 0
    $memberCount = 0
    $skippedMembers = 0
    $digestTotals = [ordered]@{
        valuesDigested = 0L; depthLimited = 0L; blocklisted = 0L; errored = 0L
        renderedTruncated = 0L; unreadableFields = 0L; ambiguousMapEntries = 0L
    }
    foreach ($manifestFile in $manifestFiles) {
        $records = @(Get-Content $manifestFile.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        $tracePath = $manifestFile.FullName.Replace('.manifest.ndjson', '.ndjson')
        if (-not (Test-Path $tracePath -PathType Leaf)) { throw "Go trace pair missing ($label): $tracePath" }
        $traceEvents = @(Get-Content $tracePath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        $callIDs = @{}
        foreach ($event in $traceEvents) { $callIDs[[string]$event.callId] = $true }
        foreach ($event in $traceEvents) {
            if ($null -ne $event.PSObject.Properties['parentCallId'] -and -not $callIDs.ContainsKey([string]$event.parentCallId)) {
                $orphanCount++
            }
        }

        $runRecords = @($records | Where-Object kind -eq 'run')
        $writers = @($records | Where-Object kind -eq 'writer')
        $digests = @($records | Where-Object kind -eq 'digest')
        if ($runRecords.Count -ne 1 -or $runRecords[0].schema -ne 'behaviordiff.trace/1' -or $runRecords[0].language -ne 'go') {
            throw "Go run metadata mismatch ($label/$($manifestFile.Name))"
        }
        if ($writers.Count -ne 1 -or $digests.Count -ne 1) {
            throw "Go process summary count mismatch ($label/$($manifestFile.Name))"
        }
        $writer = $writers[0]
        if ([long]$writer.enqueued -ne $traceEvents.Count -or [long]$writer.written -ne $traceEvents.Count -or
            [long]$writer.dropped -ne 0 -or [long]$writer.capacity -le 0) {
            throw "Go writer reconciliation failed ($label/$($manifestFile.Name))"
        }
        $writerEvents += $traceEvents.Count
        $writerEnqueued += [long]$writer.enqueued
        $writerWritten += [long]$writer.written
        $writerDropped += [long]$writer.dropped
        $writerCapacity += [long]$writer.capacity
        foreach ($name in @($digestTotals.Keys)) { $digestTotals[$name] += [long]$digests[0].$name }

        $members = @($records | Where-Object kind -eq 'member')
        $modules = @($records | Where-Object kind -eq 'assembly')
        foreach ($module in $modules) {
            $owned = @($members | Where-Object assembly -eq $module.assembly)
            if ([int]$module.discoveredMembers -ne [int]$module.patchedMembers + [int]$module.skippedMembers -or
                [int]$module.patchFailedMembers -ne 0 -or $owned.Count -ne [int]$module.discoveredMembers) {
                throw "Go module reconciliation failed ($label/$($module.assembly))"
            }
            $moduleTraced += [int]$module.tracedCalls
        }
        $memberCount += $members.Count
        $skippedMembers += @($members | Where-Object status -eq 'Skipped').Count
        $memberMethods = @{}
        foreach ($member in $members) { $memberMethods[[string]$member.method] = [string]$member.status }
        foreach ($event in $traceEvents) {
            if (-not $memberMethods.ContainsKey([string]$event.methodFullName) -or $memberMethods[[string]$event.methodFullName] -ne 'Patched') {
                throw "Go event/member join failed ($label): $($event.methodFullName)"
            }
        }
    }
    if ($orphanCount -ne 0) { throw "Go orphan guard failed ($label): $orphanCount event(s)" }
    if ($writerEvents -ne $run.Events.Count -or $moduleTraced -ne $run.Events.Count) {
        throw "Go run reconciliation failed ($label): events=$($run.Events.Count) writer=$writerEvents modules=$moduleTraced"
    }
    foreach ($counter in @('valuesDigested', 'depthLimited', 'blocklisted', 'renderedTruncated', 'unreadableFields', 'ambiguousMapEntries')) {
        if ($digestTotals[$counter] -le 0) { throw "Go digest counter not exercised ($label): $counter" }
    }
    if ($digestTotals.errored -lt 0) { throw "Go digest error counter invalid ($label)" }

    $boundaryKinds = @($report.boundaries.kind | Sort-Object)
    $templates = @($report.genericTemplates)
    if ([int]$report.metrics.genericTemplates -ne $templates.Count -or
        [int]$report.metrics.skipped -ne [int]$report.metrics.boundaries + $templates.Count -or
        @($templates | Where-Object { $_.skipReason -ne 'Unobservable' -or $_.detail -ne 'Go: GenericTemplate' }).Count -ne 0 -or
        $report.metrics.boundaries -lt 3 -or
        'interface-call' -notin $boundaryKinds -or 'function-value-call' -notin $boundaryKinds) {
        throw "Go rewrite boundary report failed ($label): $($boundaryKinds -join ',')"
    }
    if ($skippedMembers -ne [int]$report.metrics.skipped) {
        throw "Go manifest boundary count failed ($label): report=$($report.metrics.skipped) manifest=$skippedMembers"
    }
        $manifestBoundaryKinds = @($run.ManifestRecords | Where-Object {
		$_.kind -eq 'member' -and $_.status -eq 'Skipped' -and $_.detail -ne 'Go: GenericTemplate'
    } | ForEach-Object { ([string]$_.detail).Replace('Go: ', '') } | Sort-Object)
    if (($manifestBoundaryKinds -join ',') -cne ($boundaryKinds -join ',')) {
        throw "Go manifest boundary kinds failed ($label): $($manifestBoundaryKinds -join ',')"
    }
    $manifestTemplates = @($run.ManifestRecords | Where-Object {
        $_.kind -eq 'member' -and $_.status -eq 'Skipped' -and
        $_.skipReason -eq 'Unobservable' -and $_.detail -eq 'Go: GenericTemplate'
    } | ForEach-Object method | Sort-Object -Unique)
    if ($manifestTemplates.Count -ne $templates.Count) {
        throw "Go manifest generic template count failed ($label): report=$($templates.Count) manifest=$($manifestTemplates.Count)"
    }

    $requiredGenericPrefixes = @(
        '.Identity[int](', '.Identity[string](', '.Convert[int,string](',
        '.SumNumbers[int](', '.SumNumbers[int64](', '.PairValues[int,string](',
        '.Box[int].Get(', '.Box[string].Get('
    )
    $genericMembers = @($run.ManifestRecords | Where-Object {
        if ($_.kind -ne 'member' -or $_.status -ne 'Patched') { return $false }
        $method = [string]$_.method
        return @($requiredGenericPrefixes | Where-Object {
            $method.Contains($_, [StringComparison]::Ordinal)
        }).Count -gt 0
    } | ForEach-Object method | Sort-Object -Unique)
    foreach ($prefix in $requiredGenericPrefixes) {
        if (@($genericMembers | Where-Object { $_.Contains($prefix, [StringComparison]::Ordinal) }).Count -ne 1) {
            throw "Go concrete generic member missing or duplicated ($label): $prefix"
        }
    }

    return [pscustomobject]@{
        Run = $run; DerivedTests = $derivedTests; Events = $run.Events.Count; ManifestFiles = $manifestFiles.Count
        Members = $memberCount; SkippedMembers = $skippedMembers; Boundaries = $boundaryKinds
        GenericTemplates = $manifestTemplates; GenericMembers = $genericMembers
        SourceFiles = @($run.Events.filePath | Sort-Object -Unique).Count
        Digest = [pscustomobject]$digestTotals; WriterEvents = $writerEvents; Orphans = $orphanCount
        WriterEnqueued = $writerEnqueued; WriterWritten = $writerWritten
        WriterDropped = $writerDropped; WriterCapacity = $writerCapacity
    }
}

function ProofEvents([object]$run, [string]$method) {
    return @($run.Events | Where-Object {
        $_.testId -eq 'TestDigestProofs' -and $_.methodFullName -like "*.$method(*"
    })
}

function Proof([string]$name, [bool]$passed, [string]$detail) {
    [pscustomobject]@{ Name = $name; Passed = $passed; Detail = $detail }
}

$digestEvaluator = {
    param($run)
    $trap = ProofEvents $run 'EchoTrap'; $observed = ProofEvents $run 'ObservedTrapCalls'
    Proof 'NoUserCodeInvoked' ($trap.Count -eq 1 -and $observed.Count -eq 1 -and $observed[0].returnRendered -like '*int32(0)*') "trap=$($trap.Count) observed=$($observed.Count)"
    $cycles = ProofEvents $run 'EchoCycle'
    Proof 'CyclesTerminate' ($cycles.Count -eq 2 -and $cycles[0].argsDigest -ceq $cycles[1].argsDigest) "events=$($cycles.Count)"
    $topology = ProofEvents $run 'EchoTopology'
    Proof 'ReferenceTopology' ($topology.Count -eq 2 -and $topology[0].argsDigest -cne $topology[1].argsDigest) "events=$($topology.Count)"
    $maps = ProofEvents $run 'EchoMap'
    Proof 'UnorderedCollectionsStable' ($maps.Count -eq 2 -and $maps[0].returnDigest -ceq $maps[1].returnDigest) "events=$($maps.Count)"
    $stamps = ProofEvents $run 'EchoTime'
    Proof 'TimeAndIdentityNormalized' ($stamps.Count -eq 2 -and $stamps[0].argsDigest -ceq $stamps[1].argsDigest) "events=$($stamps.Count)"
    $blocked = ProofEvents $run 'EchoBlocked'
    Proof 'BlocklistBeforeRecursion' ($blocked.Count -eq 1 -and $blocked[0].argsRendered -like '*<skipped:func:*' -and $blocked[0].argsRendered -like '*<skipped:chan:*') "events=$($blocked.Count)"
    $deep = ProofEvents $run 'EchoDepth'
    Proof 'DepthMarker' ($deep.Count -eq 1 -and $deep[0].argsRendered -like '*<depth:*') "events=$($deep.Count)"
    $long = ProofEvents $run 'EchoLong'; $truncated = @($long | Where-Object argsRendered -like '*<truncated>').Count
    Proof 'TruncationMarker' ($long.Count -eq 2 -and $truncated -eq 2) "events=$($long.Count) truncated=$truncated"
    $private = ProofEvents $run 'EchoPrivate'
    Proof 'UnreadableFieldMarker' ($private.Count -eq 1 -and $private[0].argsRendered -like '*<skipped:unexported:*') "events=$($private.Count)"
    Proof 'BeyondRenderedCap' ($long.Count -eq 2 -and $long[0].argsRendered -ceq $long[1].argsRendered -and $long[0].argsDigest -cne $long[1].argsDigest) "events=$($long.Count)"
}

$originalEnvironment = @{
    BEHAVIORDIFF_TRACE = $env:BEHAVIORDIFF_TRACE
    BEHAVIORDIFF_REPOSITORY_ROOT = $env:BEHAVIORDIFF_REPOSITORY_ROOT
}

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $tree1 = Join-Path $work 'tree-1'; $tree2 = Join-Path $work 'tree-2'
    $cache1 = Join-Path $work 'cache-1'; $cache2 = Join-Path $work 'cache-2'
    $run1 = Join-Path $work 'run-1'; $run2 = Join-Path $work 'run-2'
    $rewriter1 = Join-Path $work 'behaviordiff-go-rewrite-1.exe'
    $rewriter2 = Join-Path $work 'behaviordiff-go-rewrite-2.exe'

    Write-Host '=== original Go reference ===' -ForegroundColor Cyan
    $null = Invoke-Go (Join-Path $repo 'samples/GoReference') @('test', '-count=1', './...')
    New-CleanTree $tree1; New-CleanTree $tree2
    Write-Host '=== independent Go rewriter build 1 ===' -ForegroundColor Cyan; Build-Rewriter $tree1 $rewriter1
    Write-Host '=== independent Go rewriter build 2 ===' -ForegroundColor Cyan; Build-Rewriter $tree2 $rewriter2
    Write-Host '=== independent Go rewrite 1 ===' -ForegroundColor Cyan; $report1 = Rewrite-Reference $tree1 $rewriter1 $cache1 'run 1'
    Write-Host '=== independent Go rewrite 2 ===' -ForegroundColor Cyan; $report2 = Rewrite-Reference $tree2 $rewriter2 $cache2 'run 2'
    Write-Host '=== traced Go reference run 1 ===' -ForegroundColor Cyan; $runner1 = Run-Reference $cache1 $run1 'run 1'
    Write-Host '=== traced Go reference run 2 ===' -ForegroundColor Cyan; $runner2 = Run-Reference $cache2 $run2 'run 2'
    $integrity1 = Assert-RunIntegrity $run1 $tree1 $report1 'run 1'
    $integrity2 = Assert-RunIntegrity $run2 $tree2 $report2 'run 2'

    $guard = Assert-BehaviorDiffConformanceRuns -FirstRun $run1 -SecondRun $run2 -MinimumMatchedKeys 100 `
        -UsableSourceResolutions @('debugInfo') -ReferenceSourcePathPatterns @('^samples/GoReference/.+\.go$') `
        -DigestProofEvaluator $digestEvaluator
    if ($guard.SubjectMethods -lt 30) { throw "Go observed method guard failed: $($guard.SubjectMethods), minimum is 30" }

    Write-Host '=== solution and engine ===' -ForegroundColor Cyan
    & dotnet build (Join-Path $repo 'BehaviorDiff.sln') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed' }
    $engine = Invoke-BehaviorDiffEngineConformance -FirstRun $run1 -SecondRun $run2 `
        -BaseRoot $tree1 -PrRoot $tree2

    $proofs1 = @(& $digestEvaluator $integrity1.Run)
    $proofs2 = @(& $digestEvaluator $integrity2.Run)
    Write-Host '=== Go conformance report ===' -ForegroundColor Green
    Write-Host "  runner / derived run 1     : $runner1 / $($integrity1.DerivedTests)"
    Write-Host "  runner / derived run 2     : $runner2 / $($integrity2.DerivedTests)"
    Write-Host "  matched keys               : $($guard.MatchedKeys)"
    Write-Host "  identical subject methods  : $($guard.SubjectMethods)"
    Write-Host "  subject events per run      : $($guard.FirstSubjectEvents) / $($guard.SecondSubjectEvents)"
    Write-Host "  manifests per run           : $($integrity1.ManifestFiles) / $($integrity2.ManifestFiles)"
    Write-Host "  members / skipped per run   : $($integrity1.Members) / $($integrity1.SkippedMembers)"
    Write-Host "  boundaries                  : $($integrity1.Boundaries -join ',')"
    Write-Host "  generic templates / concrete: $($integrity1.GenericTemplates.Count) / $($integrity1.GenericMembers.Count)"
    Write-Host "  source events / files run 1 : $($integrity1.Events) / $($integrity1.SourceFiles)"
    Write-Host "  source events / files run 2 : $($integrity2.Events) / $($integrity2.SourceFiles)"
    Write-Host "  source hashes unchanged     : true / true"
    Write-Host "  tripwires unusable/root/wrong: $($guard.UnusableSourceEvents) / $($guard.SubjectRoots) / $($guard.WrongSourceEvents)"
    Write-Host "  no-test / orphans           : 0 / $($integrity1.Orphans + $integrity2.Orphans)"
    Write-Host "  writer e/w/d/c run 1        : $($integrity1.WriterEnqueued) / $($integrity1.WriterWritten) / $($integrity1.WriterDropped) / $($integrity1.WriterCapacity)"
    Write-Host "  writer e/w/d/c run 2        : $($integrity2.WriterEnqueued) / $($integrity2.WriterWritten) / $($integrity2.WriterDropped) / $($integrity2.WriterCapacity)"
    Write-Host "  digest proofs per run       : $($guard.DigestProofsPerRun)"
    Write-Host "  engine raw / divergences    : $($engine.RawDifferences) / $($engine.RemainingDivergences)"
    foreach ($item in @(@{ Label = 'run 1'; Value = $integrity1 }, @{ Label = 'run 2'; Value = $integrity2 })) {
        $digest = $item.Value.Digest
        Write-Host "  digest counters $($item.Label): values=$($digest.valuesDigested) depth=$($digest.depthLimited) blocklisted=$($digest.blocklisted) errored=$($digest.errored) truncated=$($digest.renderedTruncated) unreadable=$($digest.unreadableFields) ambiguous=$($digest.ambiguousMapEntries)"
    }
    for ($index = 0; $index -lt $proofs1.Count; $index++) {
        Write-Host "  digest/$($proofs1[$index].Name): PASS (run 1: $($proofs1[$index].Detail); run 2: $($proofs2[$index].Detail))"
    }
} finally {
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "conformance work retained at $work" }
}