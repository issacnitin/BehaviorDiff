#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'RealDiff.Conformance.psm1') -Force

$work = Join-Path ([IO.Path]::GetTempPath()) ("realdiff-conformance-harness-{0}" -f [Guid]::NewGuid().ToString('N'))

function New-Event {
    param(
        [string]$TestId,
        [string]$Method,
        [int]$Ordinal,
        [int]$CallDepth = 1,
        [string]$FilePath = 'src/reference/Subject.cs',
        [string]$Resolution = 'sequencePoints',
        [bool]$IsHarness = $false
    )

    return [ordered]@{
        testId = $TestId
        methodFullName = $Method
        filePath = $FilePath
        filePathResolution = $Resolution
        line = 1
        callDepth = $CallDepth
        callId = [long](Get-Random -Minimum 1 -Maximum ([int]::MaxValue))
        ordinal = $Ordinal
        threadId = 1
        isHarness = $IsHarness
    }
}

function Write-Run {
    param([string]$Path, [object[]]$Events)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $Events | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content (Join-Path $Path 'run.1.ndjson')
}

function Copy-Events {
    param([object[]]$Events)
    return @(($Events | ConvertTo-Json -Depth 10) | ConvertFrom-Json)
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$ExpectedMessage)

    $failure = $null
    try {
        & $Action
    }
    catch {
        $failure = $_
    }

    if ($null -eq $failure) { throw "Expected failure containing '$ExpectedMessage'" }
    if ($failure.Exception.Message -notlike "*$ExpectedMessage*") { throw $failure }
}

$digestEvaluator = {
    param($Run)
    @(
        'NoUserCodeInvoked',
        'CyclesTerminate',
        'ReferenceTopology',
        'UnorderedCollectionsStable',
        'TimeAndIdentityNormalized',
        'BlocklistBeforeRecursion',
        'DepthMarker',
        'TruncationMarker',
        'UnreadableFieldMarker',
        'BeyondRenderedCap'
    ) | ForEach-Object { [pscustomobject]@{ Name = $_; Passed = $true; Detail = 'synthetic proof' } }
}

$firstEvents = @(
    (New-Event 'ReferenceTest.Works' 'ReferenceTests.Works()' 0 -CallDepth 0 -FilePath 'src/reference/ReferenceTests.cs' -IsHarness $true),
    (New-Event 'ReferenceTest.Works' 'Reference.Subject.One()' 0),
    (New-Event 'ReferenceTest.Works' 'Reference.Subject.Two()' 0),
    (New-Event 'ReferenceTest.Works' 'Reference.Subject.Two()' 1),
    (New-Event 'ReferenceTest.Works' 'Reference.Subject.Three()' 0)
)

try {
    $firstRun = Join-Path $work 'first'
    $secondRun = Join-Path $work 'second'
    Write-Run $firstRun $firstEvents
    Write-Run $secondRun (Copy-Events $firstEvents)

    $parameters = @{
        FirstRun = $firstRun
        SecondRun = $secondRun
        MinimumMatchedKeys = 3
        UsableSourceResolutions = @('sequencePoints')
        ReferenceSourcePathPatterns = @('^src/reference/')
        DigestProofEvaluator = $digestEvaluator
    }

    $result = Assert-RealDiffConformanceRuns @parameters
    if ($result.MatchedKeys -ne 3 -or $result.SubjectMethods -ne 3 -or $result.DigestProofsPerRun -ne 10) {
        throw 'Passing fixture returned unexpected conformance counts'
    }

    Assert-Throws { Assert-RealDiffConformanceRuns @parameters -MinimumMatchedKeys 4 } 'Matched-keys guard failed'

    $changedMethod = Copy-Events $firstEvents
    $changedMethod[4].methodFullName = 'Reference.Subject.Four()'
    Write-Run $secondRun $changedMethod
    Assert-Throws { Assert-RealDiffConformanceRuns @parameters -MinimumMatchedKeys 2 } 'Method-set guard failed'

    Write-Run $secondRun @((Copy-Events $firstEvents)[0..2] + (Copy-Events $firstEvents)[4])
    Assert-Throws { Assert-RealDiffConformanceRuns @parameters } 'Event-count guard failed'

    $badOrdinal = Copy-Events $firstEvents
    $badOrdinal[3].ordinal = 0
    Write-Run $secondRun $badOrdinal
    Assert-Throws { Assert-RealDiffConformanceRuns @parameters } 'Ordinal-sequence guard failed'

    $badResolution = Copy-Events $firstEvents
    $badResolution[1].filePathResolution = 'unresolved'
    Write-Run $secondRun $badResolution
    Assert-Throws { Assert-RealDiffConformanceRuns @parameters } 'unusable subject event'

    $subjectRoot = Copy-Events $firstEvents
    $subjectRoot[1].callDepth = 0
    Write-Run $secondRun $subjectRoot
    Assert-Throws { Assert-RealDiffConformanceRuns @parameters } 'subject depth-0 event'

    $generatedSource = Copy-Events $firstEvents
    $generatedSource[1].filePath = 'build/generated/Subject.cs'
    Write-Run $secondRun $generatedSource
    Assert-Throws { Assert-RealDiffConformanceRuns @parameters } 'mapped outside reference sources'

    Write-Run $secondRun (Copy-Events $firstEvents)
    $failingDigestEvaluator = {
        param($Run)
        & $digestEvaluator $Run | ForEach-Object {
            if ($_.Name -eq 'CyclesTerminate') { $_.Passed = $false; $_.Detail = 'deliberate failure' }
            $_
        }
    }
    Assert-Throws { Assert-RealDiffConformanceRuns @parameters -DigestProofEvaluator $failingDigestEvaluator } 'Digest proof failed'

    Write-Host 'Conformance harness guards: PASS' -ForegroundColor Green
    Write-Host '  matched-key threshold, method sets, event counts, ordinal sequences'
    Write-Host '  source resolution, subject roots, reference source mapping, digest proof contract'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}