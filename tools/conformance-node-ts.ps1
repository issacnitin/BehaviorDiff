#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-node-ts-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
Import-Module (Join-Path $PSScriptRoot 'BehaviorDiff.Conformance.psm1') -Force

$referenceRelativePath = 'samples/NodeReference.TypeScript'
$subjectSourcePattern = '^samples/NodeReference\.TypeScript/src/.+\.ts$'

function Copy-TracerSource([string]$destination) {
    Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem (Join-Path $repo 'src/BehaviorDiff.Node') -Force |
        Where-Object Name -ne 'node_modules' |
        ForEach-Object { Copy-Item $_.FullName -Destination $destination -Recurse -Force }
}

function Copy-ReferenceSource([string]$destination) {
    Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem (Join-Path $repo $referenceRelativePath) -Force |
        Where-Object Name -notin @('node_modules', 'dist') |
        ForEach-Object { Copy-Item $_.FullName -Destination $destination -Recurse -Force }
}

function Install-Tracer([string]$tracer) {
    & npm ci --prefix $tracer --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Node tracer install failed: $tracer" }
}

function Build-Reference([string]$reference) {
    & npm ci --prefix $reference --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "TypeScript reference install failed: $reference" }
    & npm run build --prefix $reference
    if ($LASTEXITCODE -ne 0) { throw "TypeScript reference build failed: $reference" }
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

function Get-SourceStats([object]$run, [string]$label) {
    $subject = @($run.Events | Where-Object {
        $null -eq $_.PSObject.Properties['isHarness'] -or -not [bool]$_.isHarness
    })
    $wrongResolution = @($subject | Where-Object { $_.filePathResolution -cne 'debugInfo' })
    $wrongPath = @($subject | Where-Object { $_.filePath -notmatch $subjectSourcePattern })
    $generatedPaths = @($subject | Where-Object { $_.filePath -match '(^|/)dist/' -or $_.filePath -match '\.js$' })
    $wrongEventIdentity = @($subject | Where-Object {
        $_.methodFullName -notmatch "^samples/NodeReference\.TypeScript/src/.+\.ts#"
    })
    if ($wrongResolution.Count -ne 0 -or $wrongPath.Count -ne 0 -or
        $generatedPaths.Count -ne 0 -or $wrongEventIdentity.Count -ne 0) {
        throw "TypeScript event source tripwire failed ($label): resolution=$($wrongResolution.Count) path=$($wrongPath.Count) js/dist=$($generatedPaths.Count) identity=$($wrongEventIdentity.Count)"
    }

    $members = @($run.ManifestRecords | Where-Object {
        $_.kind -eq 'member' -and $_.assembly -cne '(node-harness)'
    })
    if ($members.Count -eq 0) { throw "TypeScript manifest has no subject members ($label)" }
    $wrongMemberResolution = @($members | Where-Object { $_.sourceResolution -cne 'debugInfo' })
    $wrongMemberIdentity = @($members | Where-Object {
        $_.assembly -notmatch $subjectSourcePattern -or $_.method -notmatch "^samples/NodeReference\.TypeScript/src/.+\.ts#"
    })
    $generatedMemberIdentity = @($members | Where-Object {
        $_.assembly -match '(^|/)dist/' -or $_.assembly -match '\.js$' -or
        $_.method -match '(^|/)dist/' -or $_.method -match '\.js#'
    })
    if ($wrongMemberResolution.Count -ne 0 -or $wrongMemberIdentity.Count -ne 0 -or
        $generatedMemberIdentity.Count -ne 0) {
        throw "TypeScript member source tripwire failed ($label): resolution=$($wrongMemberResolution.Count) identity=$($wrongMemberIdentity.Count) js/dist=$($generatedMemberIdentity.Count)"
    }

    $stateRollup = @($subject | Group-Object filePathResolution | Sort-Object Name |
        ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', '
    $paths = @($subject | ForEach-Object filePath | Sort-Object -Unique)
    $tsPaths = @($paths | Where-Object { $_ -match '\.ts$' })
    $jsPaths = @($paths | Where-Object { $_ -match '\.js$' -or $_ -match '(^|/)dist/' })

    return [pscustomobject]@{
        StateRollup = $stateRollup
        SubjectEvents = $subject.Count
        SubjectMembers = $members.Count
        TypeScriptPaths = $tsPaths.Count
        JavaScriptPaths = $jsPaths.Count
        WrongResolution = $wrongResolution.Count + $wrongMemberResolution.Count
        WrongPaths = $wrongPath.Count + $wrongMemberIdentity.Count
        GeneratedPaths = $generatedPaths.Count + $generatedMemberIdentity.Count
    }
}

function Run-Reference(
    [string]$tracer,
    [string]$repositoryRoot,
    [string]$reference,
    [string]$runDirectory,
    [string]$label
) {
    Remove-Item $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $trace = Join-Path $runDirectory 'run.ndjson'
    $report = Join-Path $runDirectory 'runner-report.json'
    $register = Join-Path $tracer 'register.cjs'
    $runner = Join-Path $reference 'dist/test/run.js'
    $oldTrace = $env:BEHAVIORDIFF_TRACE
    $oldNamespaces = $env:BEHAVIORDIFF_NAMESPACES
    $oldReport = $env:BEHAVIORDIFF_RUNNER_REPORT
    $oldNodePath = $env:NODE_PATH
    try {
        $env:BEHAVIORDIFF_TRACE = $trace
        $env:BEHAVIORDIFF_NAMESPACES = "$referenceRelativePath/dist/src"
        $env:BEHAVIORDIFF_RUNNER_REPORT = $report
        $env:NODE_PATH = Join-Path $tracer 'node_modules'
        Push-Location $repositoryRoot
        try { & node --require $register $runner | ForEach-Object { Write-Host $_ } }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "TypeScript reference tests failed with $tracer" }
    } finally {
        $env:BEHAVIORDIFF_TRACE = $oldTrace
        $env:BEHAVIORDIFF_NAMESPACES = $oldNamespaces
        $env:BEHAVIORDIFF_RUNNER_REPORT = $oldReport
        $env:NODE_PATH = $oldNodePath
    }

    if (-not (Test-Path $report -PathType Leaf)) { throw "Node runner report was not written ($label): $report" }
    $runnerTests = [int](Get-Content $report -Raw | ConvertFrom-Json).runnerTests
    if ($runnerTests -ne 113) { throw "Node runner count mismatch ($label): expected=113 actual=$runnerTests" }

    $run = Read-BehaviorDiffConformanceRun $runDirectory
    $rootMethods = @($run.ManifestRecords | Where-Object {
        $_.kind -eq 'member' -and $null -ne $_.PSObject.Properties['isTestRoot'] -and [bool]$_.isTestRoot
    } | ForEach-Object method | Sort-Object -Unique)
    if ($rootMethods.Count -eq 0) { throw "Node manifest has no test-root methods ($label)" }
    $derivedTests = @($run.Events | Where-Object methodFullName -In $rootMethods).Count
    if ($runnerTests -ne $derivedTests) {
        throw "Node test count mismatch ($label): runner=$runnerTests derived=$derivedTests"
    }

    $manifest = Get-ManifestStats $run $label
    $source = Get-SourceStats $run $label
    return [pscustomobject]@{
        Run = $run
        RunnerTests = $runnerTests
        DerivedTests = $derivedTests
        Digest = $manifest.Digest
        Writer = $manifest.Writer
        Source = $source
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
    $methodFullName = "samples/NodeReference.TypeScript/src/subject.ts#$method"
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
    $repositoryRoot = Join-Path $work 'repository'
    $reference = Join-Path $repositoryRoot $referenceRelativePath
    $tracer1 = Join-Path $work 'tracer-1'; $tracer2 = Join-Path $work 'tracer-2'
    $run1 = Join-Path $work 'run-1'; $run2 = Join-Path $work 'run-2'
    Copy-TracerSource $tracer1; Copy-TracerSource $tracer2
    Copy-ReferenceSource $reference
    Write-Host '=== independent Node tracer install 1 ===' -ForegroundColor Cyan; Install-Tracer $tracer1
    Write-Host '=== independent Node tracer install 2 ===' -ForegroundColor Cyan; Install-Tracer $tracer2
    Write-Host '=== clean TypeScript reference install/build ===' -ForegroundColor Cyan; Build-Reference $reference
    Write-Host '=== TypeScript reference run 1 ===' -ForegroundColor Cyan; $result1 = Run-Reference $tracer1 $repositoryRoot $reference $run1 'run 1'
    Write-Host '=== TypeScript reference run 2 ===' -ForegroundColor Cyan; $result2 = Run-Reference $tracer2 $repositoryRoot $reference $run2 'run 2'

    $guard = Assert-BehaviorDiffConformanceRuns -FirstRun $run1 -SecondRun $run2 -MinimumMatchedKeys 100 `
        -UsableSourceResolutions @('debugInfo') `
        -ReferenceSourcePathPatterns @($subjectSourcePattern) -DigestProofEvaluator $digestEvaluator

    $engineProject = Join-Path $repo 'src/BehaviorDiff.Engine/BehaviorDiff.Engine.csproj'
    & dotnet build $engineProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed' }
    $engine = Invoke-BehaviorDiffEngineConformance -FirstRun $run1 -SecondRun $run2 -EngineProject $engineProject

    $proofs1 = @(& $digestEvaluator $result1.Run)
    $proofs2 = @(& $digestEvaluator $result2.Run)
    Write-Host '=== Node TypeScript conformance report ===' -ForegroundColor Green
    Write-Host '  counts'
    Write-Host "    runner / derived run 1    : $($result1.RunnerTests) / $($result1.DerivedTests)"
    Write-Host "    runner / derived run 2    : $($result2.RunnerTests) / $($result2.DerivedTests)"
    Write-Host "    matched keys              : $($guard.MatchedKeys)"
    Write-Host "    identical subject methods : $($guard.SubjectMethods)"
    Write-Host "    subject events per run     : $($guard.FirstSubjectEvents) / $($guard.SecondSubjectEvents)"
    Write-Host '  tripwires'
    Write-Host "    unusable/root/wrong        : $($guard.UnusableSourceEvents) / $($guard.SubjectRoots) / $($guard.WrongSourceEvents)"
    Write-Host "    member/event source errors : $($result1.Source.WrongResolution + $result2.Source.WrongResolution) / $($result1.Source.WrongPaths + $result2.Source.WrongPaths)"
    Write-Host "    generated .js/dist errors  : $($result1.Source.GeneratedPaths + $result2.Source.GeneratedPaths)"
    foreach ($item in @(@{ Label = 'run 1'; Result = $result1 }, @{ Label = 'run 2'; Result = $result2 })) {
        Write-Host "  source $($item.Label)"
        Write-Host "    state rollup              : $($item.Result.Source.StateRollup)"
        Write-Host "    subject events / members  : $($item.Result.Source.SubjectEvents) / $($item.Result.Source.SubjectMembers)"
        Write-Host "    distinct .ts / .js paths  : $($item.Result.Source.TypeScriptPaths) / $($item.Result.Source.JavaScriptPaths)"
    }
    Write-Host '  digest'
    Write-Host "    proofs per run            : $($guard.DigestProofsPerRun)"
    foreach ($item in @(@{ Label = 'run 1'; Result = $result1 }, @{ Label = 'run 2'; Result = $result2 })) {
        $digest = $item.Result.Digest
        Write-Host "    counters $($item.Label)         : values=$($digest.valuesDigested) depthLimited=$($digest.depthLimited) blocklisted=$($digest.blocklisted) errored=$($digest.errored) renderedTruncated=$($digest.renderedTruncated)"
    }
    for ($index = 0; $index -lt $proofs1.Count; $index++) {
        Write-Host "    $($proofs1[$index].Name): PASS (run 1: $($proofs1[$index].Detail); run 2: $($proofs2[$index].Detail))"
    }
    Write-Host '  writer'
    Write-Host "    run 1 e/w/d/c             : $($result1.Writer.enqueued) / $($result1.Writer.written) / $($result1.Writer.dropped) / $($result1.Writer.capacity)"
    Write-Host "    run 2 e/w/d/c             : $($result2.Writer.enqueued) / $($result2.Writer.written) / $($result2.Writer.dropped) / $($result2.Writer.capacity)"
    Write-Host '  engine'
    Write-Host "    raw / divergences         : $($engine.RawDifferences) / $($engine.RemainingDivergences)"
} finally {
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "conformance work retained at $work" }
}