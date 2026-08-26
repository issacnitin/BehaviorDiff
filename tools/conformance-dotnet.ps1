#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$WorkDirectory,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-dotnet-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
}
else {
    [IO.Path]::GetFullPath($WorkDirectory)
}

Import-Module (Join-Path $PSScriptRoot 'RealDiff.Conformance.psm1') -Force

function Get-ProofEvents {
    param([object]$Run, [string]$Method)

    return @($Run.Events | Where-Object {
        $_.testId -like '*DigestProofTests*' -and $_.methodFullName -like "*$Method*"
    })
}

function New-ProofResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    return [pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

$digestProofEvaluator = {
    param($Run)

    $observed = Get-ProofEvents $Run 'Probes.ObservedCalls'
    $inspect = Get-ProofEvents $Run 'Probes.Inspect'
    New-ProofResult 'NoUserCodeInvoked' (
        $observed.Count -eq 1 -and $inspect.Count -eq 1 -and $observed[0].returnRendered -ceq 'Primitive:""'
    ) "observedCalls=$($observed[0].returnRendered)"

    $cycles = Get-ProofEvents $Run 'Probes.Traverse'
    New-ProofResult 'CyclesTerminate' (
        $cycles.Count -eq 2 -and $cycles[0].argsDigest -ceq $cycles[1].argsDigest
    ) "events=$($cycles.Count), equal=$($cycles.Count -eq 2 -and $cycles[0].argsDigest -ceq $cycles[1].argsDigest)"

    $relations = Get-ProofEvents $Run 'Probes.Relate'
    New-ProofResult 'ReferenceTopology' (
        $relations.Count -eq 2 -and $relations[0].argsDigest -cne $relations[1].argsDigest
    ) "events=$($relations.Count), different=$($relations.Count -eq 2 -and $relations[0].argsDigest -cne $relations[1].argsDigest)"

    $dictionary = Get-ProofEvents $Run 'Probes.BuildDictionaryWithRemovals'
    $set = Get-ProofEvents $Run 'Probes.BuildSetWithRemovals'
    New-ProofResult 'UnorderedCollectionsStable' (
        $dictionary.Count -eq 1 -and $set.Count -eq 1 -and
        $dictionary[0].returnRendered -like 'ShapeRule:Dictionary*' -and
        $set[0].returnRendered -like 'ShapeRule:HashSet*'
    ) "dictionary=$($dictionary[0].returnRendered); set=$($set[0].returnRendered)"

    $stamps = Get-ProofEvents $Run 'Probes.Stamp'
    $normalized = $stamps.Count -eq 2 -and $stamps[0].argsDigest -ceq $stamps[1].argsDigest -and
        $stamps[0].argsRendered -like '*<guid>*' -and $stamps[0].argsRendered -like '*<datetime>*'
    New-ProofResult 'TimeAndIdentityNormalized' $normalized "events=$($stamps.Count), equal=$($stamps.Count -eq 2 -and $stamps[0].argsDigest -ceq $stamps[1].argsDigest)"

    $blocked = Get-ProofEvents $Run 'Probes.UseServices'
    New-ProofResult 'BlocklistBeforeRecursion' (
        $blocked.Count -eq 1 -and $blocked[0].argsRendered -like '*<skipped:System.IO.Stream>*' -and
        $blocked[0].argsRendered -like '*<skipped:System.Threading.Tasks.Task>*'
    ) "events=$($blocked.Count), skipped=$($blocked[0].argsRendered -like '*<skipped:*>*')"

    $deep = Get-ProofEvents $Run 'Probes.Descend'
    New-ProofResult 'DepthMarker' (
        $deep.Count -eq 1 -and $deep[0].argsRendered -like '*<depth:*'
    ) "events=$($deep.Count), marker=$($deep[0].argsRendered -like '*<depth:*')"

    $long = Get-ProofEvents $Run 'Probes.LongText(System.Int32)'
    $truncatedCount = @($long | Where-Object { $_.returnRendered -like '*<truncated>' }).Count
    New-ProofResult 'TruncationMarker' (
        $long.Count -ge 2 -and $truncatedCount -eq $long.Count
    ) "events=$($long.Count), truncated=$truncatedCount"

    $readable = Get-ProofEvents $Run 'ErrorProbes.Readable'
    $unreadable = Get-ProofEvents $Run 'ErrorProbes.Unreadable('
    $unreadableField = $readable.Count -eq 1 -and $unreadable.Count -eq 1 -and
        $unreadable[0].argsRendered -like '*<error:_payload:TypeInitializationException>*' -and
        $unreadable[0].argsDigest -cne $readable[0].argsDigest
    New-ProofResult 'UnreadableFieldMarker' $unreadableField "events=$($unreadable.Count), marker=$($unreadable.Count -eq 1 -and $unreadable[0].argsRendered -like '*<error:*>*')"

    $hidden = Get-ProofEvents $Run 'Probes.LongTextWithHiddenSuffix'
    $beyondCap = $hidden.Count -eq 2 -and $hidden[0].returnRendered -ceq $hidden[1].returnRendered -and
        $hidden[0].returnRendered -like '*<truncated>' -and $hidden[0].returnDigest -cne $hidden[1].returnDigest
    New-ProofResult 'BeyondRenderedCap' $beyondCap "events=$($hidden.Count), sameRendered=$($hidden.Count -eq 2 -and $hidden[0].returnRendered -ceq $hidden[1].returnRendered), differentDigest=$($hidden.Count -eq 2 -and $hidden[0].returnDigest -cne $hidden[1].returnDigest)"
}

function Expand-CleanTree {
    param([string]$Archive, [string]$Destination)

    Remove-Item $Destination -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $Archive -DestinationPath $Destination
}

function Build-Tree {
    param([string]$Tree)

    & dotnet build (Join-Path $Tree 'RealDiff.sln') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "solution build failed: $Tree" }
    & dotnet build (Join-Path $Tree 'tools/Weaver/Weaver.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "weaver build failed: $Tree" }
}

function Invoke-ReferenceRun {
    param([string]$StagedBin, [string]$RunDirectory)

    Remove-Item $RunDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $RunDirectory | Out-Null

    $env:REALDIFF_TRACE = Join-Path $RunDirectory 'run.ndjson'
    $env:REALDIFF_NAMESPACES = 'SampleApp,Commerce.Pricing,Infrastructure.Collections'
    $env:REALDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics,SampleApp.Persistence,Infrastructure.Collections'
    $env:REALDIFF_BACKEND = 'cecil'

    & dotnet test (Join-Path $StagedBin 'SampleApp.Tests.dll') --nologo
    if ($LASTEXITCODE -ne 0) { throw "reference tests failed: $StagedBin" }
}

$originalEnvironment = @{
    REALDIFF_TRACE = $env:REALDIFF_TRACE
    REALDIFF_NAMESPACES = $env:REALDIFF_NAMESPACES
    REALDIFF_EXCLUDE_NAMESPACES = $env:REALDIFF_EXCLUDE_NAMESPACES
    REALDIFF_BACKEND = $env:REALDIFF_BACKEND
}

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $archive = Join-Path $work 'source.zip'
    $firstTree = Join-Path $work 'tree-1'
    $secondTree = Join-Path $work 'tree-2'
    $firstBin = Join-Path $work 'bin-1'
    $secondBin = Join-Path $work 'bin-2'
    $firstRun = Join-Path $work 'run-1'
    $secondRun = Join-Path $work 'run-2'

    & git -C $repo archive --format=zip --output=$archive HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
    Expand-CleanTree $archive $firstTree
    Expand-CleanTree $archive $secondTree

    Write-Host '=== independent build 1 ===' -ForegroundColor Cyan
    Build-Tree $firstTree
    Write-Host '=== independent build 2 ===' -ForegroundColor Cyan
    Build-Tree $secondTree

    & (Join-Path $PSScriptRoot 'Stage-WovenSample.ps1') `
        -TreeRoot $firstTree -InstrumentationTreeRoot $firstTree -OutDir $firstBin
    & (Join-Path $PSScriptRoot 'Stage-WovenSample.ps1') `
        -TreeRoot $firstTree -InstrumentationTreeRoot $secondTree -OutDir $secondBin

    Write-Host '=== reference run 1 ===' -ForegroundColor Cyan
    Invoke-ReferenceRun $firstBin $firstRun
    Write-Host '=== reference run 2 ===' -ForegroundColor Cyan
    Invoke-ReferenceRun $secondBin $secondRun

    $guardReport = Assert-RealDiffConformanceRuns `
        -FirstRun $firstRun `
        -SecondRun $secondRun `
        -MinimumMatchedKeys 100 `
        -UsableSourceResolutions @('sequencePoints', 'stateMachine', 'declaringType') `
        -ReferenceSourcePathPatterns @(
            '[/\\]samples[/\\]SampleApp',
            '[/\\]src[/\\]Commerce\.Pricing',
            '[/\\]src[/\\]Infrastructure\.Collections') `
        -DigestProofEvaluator $digestProofEvaluator

    Write-Host '=== engine base1/base2 conformance ===' -ForegroundColor Cyan
    $engineReport = Invoke-RealDiffEngineConformance `
        -FirstRun $firstRun `
        -SecondRun $secondRun `
        -BaseRoot $firstTree `
        -PrRoot $firstTree

    Write-Host '=== .NET conformance report ===' -ForegroundColor Green
    Write-Host "  matched keys             : $($guardReport.MatchedKeys)"
    Write-Host "  identical subject methods: $($guardReport.SubjectMethods)"
    Write-Host "  run 1 subject events      : $($guardReport.FirstSubjectEvents)"
    Write-Host "  run 2 subject events      : $($guardReport.SecondSubjectEvents)"
    Write-Host "  unusable source events    : $($guardReport.UnusableSourceEvents)"
    Write-Host "  subject depth-0 events    : $($guardReport.SubjectRoots)"
    Write-Host "  wrong source mappings     : $($guardReport.WrongSourceEvents)"
    Write-Host "  digest proofs per run     : $($guardReport.DigestProofsPerRun)"
    Write-Host "  engine raw differences    : $($engineReport.RawDifferences)"
    Write-Host "  engine divergences        : $($engineReport.RemainingDivergences)"

    foreach ($proof in @(& $digestProofEvaluator (Read-RealDiffConformanceRun $firstRun))) {
        Write-Host "  digest/$($proof.Name): PASS ($($proof.Detail))"
    }
}
finally {
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }

    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host "conformance work retained at $work"
    }
}