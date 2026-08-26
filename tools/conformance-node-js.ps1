#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-node-js-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
Import-Module (Join-Path $PSScriptRoot 'RealDiff.Conformance.psm1') -Force

$expandedSubjectMethods = @(
    'BaseFormatter.constructor', 'BaseFormatter.describe', 'BaseFormatter.decorate',
    'DerivedFormatter.describe', 'dispatchFormatter',
    'ValueBox.constructor', 'ValueBox.current', 'ValueBox.scale', 'ValueBox.create', 'dispatchValueBox',
    'arrowIncrement', 'arrowMultiply', 'arrowLabel', 'dispatchArrows',
    'objectPipeline.normalize', 'objectPipeline.decorate', 'objectPipeline.summarize', 'dispatchObjectPipeline',
    'createClosurePipeline', 'createClosurePipeline.addOffset', 'createClosurePipeline.multiply',
    'createClosurePipeline.apply', 'dispatchClosurePipeline',
    'requireNonNegative', 'isEven', 'doubleReading', 'processReadings',
    'AsyncSettlement.constructor', 'AsyncSettlement.settle', 'incrementPromise', 'labelPromise',
    'promiseWorkflow', 'promiseWorkflow.settleValue'
)

function Copy-TracerSource([string]$destination) {
    Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem (Join-Path $repo 'src/RealDiff.Node') -Force |
        Where-Object Name -ne 'node_modules' |
        ForEach-Object { Copy-Item $_.FullName -Destination $destination -Recurse -Force }
}

function Install-Tracer([string]$tracer) {
    & npm ci --prefix $tracer --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Node tracer install failed: $tracer" }
}

function Get-ManifestStats([object]$run, [string]$label) {
    $digests = @($run.ManifestRecords | Where-Object kind -eq 'digest')
    $writers = @($run.ManifestRecords | Where-Object kind -eq 'writer')
    if ($digests.Count -ne 1) { throw "Node manifest ($label): expected one digest record, got $($digests.Count)" }
    if ($writers.Count -ne 1) { throw "Node manifest ($label): expected one writer record, got $($writers.Count)" }

    $digest = $digests[0]
    foreach ($counter in @('depthLimited', 'blocklisted', 'renderedTruncated')) {
        if ([long]$digest.$counter -le 0) { throw "Node digest counter was not exercised ($label): $counter" }
    }
    if ([long]$digest.errored -lt 0) { throw "Node digest errored counter was negative ($label)" }

    $writer = $writers[0]
    if ([long]$writer.enqueued -ne $run.Events.Count -or
        [long]$writer.written -ne $run.Events.Count -or
        [long]$writer.dropped -ne 0 -or
        [long]$writer.capacity -le 0) {
        throw "Node writer reconciliation failed ($label): events=$($run.Events.Count) enqueued=$($writer.enqueued) written=$($writer.written) dropped=$($writer.dropped) capacity=$($writer.capacity)"
    }

    return [pscustomobject]@{ Digest = $digest; Writer = $writer }
}

function Get-ExpandedMethodStats([object]$run, [string]$label) {
    $source = 'samples/NodeReference/src/subject.js'
    $expected = @($expandedSubjectMethods | ForEach-Object { "$source#$_" })
    $observed = @($run.Events | ForEach-Object methodFullName |
        Where-Object { $_ -in $expected } | Sort-Object -Unique)
    $missing = @($expected | Where-Object { $_ -notin $observed })
    if ($missing.Count -ne 0) {
        throw "Node expanded method coverage failed ($label): observed=$($observed.Count)/$($expected.Count) missing=$($missing -join ', ')"
    }
    return [pscustomobject]@{ Observed = $observed.Count; Expected = $expected.Count }
}

function Run-Reference([string]$tracer, [string]$runDirectory, [string]$label) {
    Remove-Item $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $trace = Join-Path $runDirectory 'run.ndjson'
    $report = Join-Path $runDirectory 'runner-report.json'
    $register = Join-Path $tracer 'register.cjs'
    $runner = Join-Path $repo 'samples/NodeReference/test/run.cjs'
    $oldTrace = $env:REALDIFF_TRACE
    $oldNamespaces = $env:REALDIFF_NAMESPACES
    $oldReport = $env:REALDIFF_RUNNER_REPORT
    $oldNodePath = $env:NODE_PATH
    try {
        $env:REALDIFF_TRACE = $trace
        $env:REALDIFF_NAMESPACES = 'samples/NodeReference/src'
        $env:REALDIFF_RUNNER_REPORT = $report
        $env:NODE_PATH = Join-Path $tracer 'node_modules'
        Push-Location $repo
        try { & node --require $register $runner | ForEach-Object { Write-Host $_ } }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "Node reference tests failed with $tracer" }
    } finally {
        $env:REALDIFF_TRACE = $oldTrace
        $env:REALDIFF_NAMESPACES = $oldNamespaces
        $env:REALDIFF_RUNNER_REPORT = $oldReport
        $env:NODE_PATH = $oldNodePath
    }

    if (-not (Test-Path $report -PathType Leaf)) { throw "Node runner report was not written ($label): $report" }
    $runnerTests = [int](Get-Content $report -Raw | ConvertFrom-Json).runnerTests
    if ($runnerTests -ne 120) { throw "Node runner count mismatch ($label): expected=120 actual=$runnerTests" }

    $run = Read-RealDiffConformanceRun $runDirectory
    $rootMethods = @($run.ManifestRecords | Where-Object {
        $_.kind -eq 'member' -and $null -ne $_.PSObject.Properties['isTestRoot'] -and [bool]$_.isTestRoot
    } | ForEach-Object method | Sort-Object -Unique)
    if ($rootMethods.Count -eq 0) { throw "Node manifest has no test-root methods ($label)" }
    $derivedTests = @($run.Events | Where-Object methodFullName -In $rootMethods).Count
    if ($runnerTests -ne $derivedTests) {
        throw "Node test count mismatch ($label): runner=$runnerTests derived=$derivedTests"
    }

    $manifest = Get-ManifestStats $run $label
    $expanded = Get-ExpandedMethodStats $run $label
    return [pscustomobject]@{
        Run = $run
        RunnerTests = $runnerTests
        DerivedTests = $derivedTests
        ExpandedMethods = $expanded
        Digest = $manifest.Digest
        Writer = $manifest.Writer
    }
}

$subjectMethods = @{
    NoUserCodeInvoked = @('observedCalls')
    CyclesTerminate = @('cycle')
    ReferenceTopology = @('topology')
    UnorderedCollectionsStable = @('map', 'set')
    TimeAndIdentityNormalized = @('stamp')
    BlocklistBeforeRecursion = @('block')
    DepthMarker = @('deep')
    TruncationMarker = @('longText')
    UnreadableFieldMarker = @('unreadable', 'cycle')
    BeyondRenderedCap = @('longText')
}

function Events([object]$run, [string]$method) {
    $methodFullName = "samples/NodeReference/src/subject.js#$method"
    return @($run.Events | Where-Object {
        $_.testId -eq 'node-reference/digest-proof' -and $_.methodFullName -eq $methodFullName
    })
}

function Proof([string]$name, [bool]$passed, [string]$detail) {
    [pscustomobject]@{ Name = $name; Passed = $passed; Detail = $detail }
}

$digestEvaluator = {
    param($run)
    $observed = Events $run $subjectMethods.NoUserCodeInvoked[0]
    $observedEmpty = @($observed | Where-Object returnRendered -eq 'string:""').Count
    Proof 'NoUserCodeInvoked' ($observed.Count -eq 2 -and $observedEmpty -eq 2) "events=$($observed.Count) empty=$observedEmpty"

    $cycles = Events $run $subjectMethods.CyclesTerminate[0]
    Proof 'CyclesTerminate' ($cycles.Count -eq 2 -and $cycles[0].argsDigest -eq $cycles[1].argsDigest) "events=$($cycles.Count)"

    $topology = Events $run $subjectMethods.ReferenceTopology[0]
    Proof 'ReferenceTopology' ($topology.Count -eq 2 -and $topology[0].argsDigest -ne $topology[1].argsDigest) "events=$($topology.Count)"

    $maps = Events $run $subjectMethods.UnorderedCollectionsStable[0]
    $sets = Events $run $subjectMethods.UnorderedCollectionsStable[1]
    $mapMarkers = @($maps | Where-Object returnRendered -Like '*<skipped:Map>*').Count
    $setMarkers = @($sets | Where-Object returnRendered -Like '*<skipped:Set>*').Count
    $collectionsStable = $maps.Count -eq 2 -and $sets.Count -eq 2 -and
        $maps[0].returnDigest -eq $maps[1].returnDigest -and
        $sets[0].returnDigest -eq $sets[1].returnDigest -and
        $mapMarkers -eq 2 -and $setMarkers -eq 2
    Proof 'UnorderedCollectionsStable' $collectionsStable "maps=$($maps.Count)/markers=$mapMarkers sets=$($sets.Count)/markers=$setMarkers"

    $stamps = Events $run $subjectMethods.TimeAndIdentityNormalized[0]
    Proof 'TimeAndIdentityNormalized' ($stamps.Count -eq 2 -and $stamps[0].argsDigest -eq $stamps[1].argsDigest) "events=$($stamps.Count)"

    $blocked = Events $run $subjectMethods.BlocklistBeforeRecursion[0]
    $blockedSafe = $blocked.Count -eq 1 -and $blocked[0].argsRendered -like '*<skipped:Proxy>*' -and
        $blocked[0].argsRendered -like '*<skipped:accessor>*'
    Proof 'BlocklistBeforeRecursion' $blockedSafe "events=$($blocked.Count)"

    $deep = Events $run $subjectMethods.DepthMarker[0]
    Proof 'DepthMarker' ($deep.Count -eq 1 -and $deep[0].argsRendered -like '*<depth:*') "events=$($deep.Count)"

    $long = Events $run $subjectMethods.TruncationMarker[0]
    $truncated = @($long | Where-Object argsRendered -Like '*<truncated>').Count
    Proof 'TruncationMarker' ($long.Count -eq 2 -and $truncated -eq 2) "events=$($long.Count) truncated=$truncated"

    $unreadable = Events $run $subjectMethods.UnreadableFieldMarker[0]
    $readable = Events $run $subjectMethods.UnreadableFieldMarker[1]
    $unreadableSafe = $unreadable.Count -eq 1 -and $unreadable[0].argsRendered -like '*<skipped:Proxy>*' -and
        $unreadable[0].argsRendered -like '*<skipped:accessor>*' -and $readable.Count -eq 2 -and
        $readable[0].argsRendered -notlike '*<skipped:Proxy>*' -and
        $readable[0].argsRendered -notlike '*<skipped:accessor>*' -and
        $unreadable[0].argsDigest -ne $readable[0].argsDigest
    Proof 'UnreadableFieldMarker' $unreadableSafe "unreadable=$($unreadable.Count) readable=$($readable.Count)"

    $beyondCap = $long.Count -eq 2 -and $long[0].argsRendered -eq $long[1].argsRendered -and
        $long[0].argsDigest -ne $long[1].argsDigest
    Proof 'BeyondRenderedCap' $beyondCap "events=$($long.Count)"
}

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $tracer1 = Join-Path $work 'tracer-1'; $tracer2 = Join-Path $work 'tracer-2'
    $run1 = Join-Path $work 'run-1'; $run2 = Join-Path $work 'run-2'
    Copy-TracerSource $tracer1; Copy-TracerSource $tracer2
    Write-Host '=== independent Node tracer install 1 ===' -ForegroundColor Cyan; Install-Tracer $tracer1
    Write-Host '=== independent Node tracer install 2 ===' -ForegroundColor Cyan; Install-Tracer $tracer2
    Write-Host '=== Node reference run 1 ===' -ForegroundColor Cyan; $result1 = Run-Reference $tracer1 $run1 'run 1'
    Write-Host '=== Node reference run 2 ===' -ForegroundColor Cyan; $result2 = Run-Reference $tracer2 $run2 'run 2'

    $guard = Assert-RealDiffConformanceRuns -FirstRun $run1 -SecondRun $run2 -MinimumMatchedKeys 100 `
        -UsableSourceResolutions @('debugInfo', 'generatedState', 'declaringType') `
        -ReferenceSourcePathPatterns @('^samples/NodeReference/src/.+\.js$') -DigestProofEvaluator $digestEvaluator

    $engine = Invoke-RealDiffEngineConformance -FirstRun $run1 -SecondRun $run2

    $proofs1 = @(& $digestEvaluator $result1.Run)
    $proofs2 = @(& $digestEvaluator $result2.Run)
    Write-Host '=== Node.js conformance report ===' -ForegroundColor Green
    Write-Host "  runner / derived run 1    : $($result1.RunnerTests) / $($result1.DerivedTests)"
    Write-Host "  runner / derived run 2    : $($result2.RunnerTests) / $($result2.DerivedTests)"
    Write-Host "  matched keys              : $($guard.MatchedKeys)"
    Write-Host "  identical subject methods : $($guard.SubjectMethods)"
    Write-Host "  subject events per run     : $($guard.FirstSubjectEvents) / $($guard.SecondSubjectEvents)"
    Write-Host "  expanded methods per run   : $($result1.ExpandedMethods.Observed) / $($result2.ExpandedMethods.Observed) (expected $($result1.ExpandedMethods.Expected))"
    Write-Host "  tripwires unusable/root/wrong: $($guard.UnusableSourceEvents) / $($guard.SubjectRoots) / $($guard.WrongSourceEvents)"
    Write-Host "  digest proofs per run      : $($guard.DigestProofsPerRun)"
    Write-Host "  engine raw / divergences   : $($engine.RawDifferences) / $($engine.RemainingDivergences)"
    Write-Host "  writer run 1 e/w/d/c       : $($result1.Writer.enqueued) / $($result1.Writer.written) / $($result1.Writer.dropped) / $($result1.Writer.capacity)"
    Write-Host "  writer run 2 e/w/d/c       : $($result2.Writer.enqueued) / $($result2.Writer.written) / $($result2.Writer.dropped) / $($result2.Writer.capacity)"
    foreach ($item in @(@{ Label = 'run 1'; Result = $result1 }, @{ Label = 'run 2'; Result = $result2 })) {
        $digest = $item.Result.Digest
        Write-Host "  digest counters $($item.Label)    : values=$($digest.valuesDigested) depthLimited=$($digest.depthLimited) blocklisted=$($digest.blocklisted) errored=$($digest.errored) renderedTruncated=$($digest.renderedTruncated)"
    }
    for ($index = 0; $index -lt $proofs1.Count; $index++) {
        Write-Host "  digest/$($proofs1[$index].Name): PASS (run 1: $($proofs1[$index].Detail); run 2: $($proofs2[$index].Detail))"
    }
} finally {
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "conformance work retained at $work" }
}