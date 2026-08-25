#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [ValidateSet('none', 'writer', 'ordinal')]
    [string]$Fault = 'none'
)

$ErrorActionPreference = 'Stop'
$baseRoot = Join-Path $OutputDirectory 'base-root'
$prRoot = Join-Path $OutputDirectory 'pr-root'
$base1 = Join-Path $OutputDirectory 'base_run1'
$base2 = Join-Path $OutputDirectory 'base_run2'
$pr = Join-Path $OutputDirectory 'pr_run'

function New-Event(
    [int]$Index,
    [string]$Root,
    [int]$Ordinal = 0,
    [string]$ReturnDigest = 'same',
    [string]$ReturnRendered = 'Primitive:1'
) {
    [ordered]@{
        testId = 'Fixture.Tests.Case{0:D3}' -f $Index
        methodFullName = 'Fixture.Subject.Method{0:D3}()' -f $Index
        filePath = Join-Path $Root ('src/Method{0:D3}.cs' -f $Index)
        filePathResolution = 'sequencePoints'
        line = $Index + 1
        callDepth = 1
        parentCallId = 1
        callId = 1000 + ($Index * 2) + $Ordinal
        ordinal = $Ordinal
        argsDigest = 'args'
        argsRendered = if ($Index -eq 2) { '<depth:4>' } else { 'Primitive:1' }
        returnDigest = $ReturnDigest
        returnRendered = $ReturnRendered
        exceptionType = $null
        threadId = 1
        isHarness = $false
    }
}

function New-HarnessEvent([string]$Root, [string]$ExceptionType) {
    [ordered]@{
        testId = 'Fixture.Tests.Root'
        methodFullName = 'Fixture.Tests.Root.Run()'
        filePath = Join-Path $Root 'tests/Root.cs'
        filePathResolution = 'sequencePoints'
        line = 10
        callDepth = 0
        parentCallId = $null
        callId = 1
        ordinal = 0
        argsDigest = 'root-args'
        argsRendered = 'Primitive:1'
        returnDigest = if ($ExceptionType) { $null } else { 'root-return' }
        returnRendered = if ($ExceptionType) { $null } else { 'Primitive:1' }
        exceptionType = if ($ExceptionType) { $ExceptionType } else { $null }
        threadId = 1
        isHarness = $true
    }
}

function New-Member([int]$Index, [string]$Status = 'Patched') {
    $member = [ordered]@{
        kind = 'member'
        assembly = 'Fixture.Subject'
        method = 'Fixture.Subject.Method{0:D3}()' -f $Index
        status = $Status
        returnKind = 'Sync'
        sourceResolution = 'sequencePoints'
    }
    if ($Status -eq 'Skipped') {
        $member.skipReason = 'UnsupportedShape'
    }

    $member
}

function Write-Run(
    [string]$Directory,
    [string]$Root,
    [ValidateSet('base1', 'base2', 'pr')]
    [string]$Variant
) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $events = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 120; $index++) {
        if ($Variant -eq 'pr' -and $index -eq 4) {
            continue
        }

        $returnDigest = if ($Variant -eq 'base2' -and $index -eq 3) {
            'base-noise'
        }
        elseif ($Variant -eq 'pr' -and $index -eq 0) {
            'changed'
        }
        else {
            'same'
        }
        $returnRendered = if ($returnDigest -eq 'changed') { 'Primitive:2' } else { 'Primitive:1' }
        $ordinal = if ($Variant -eq 'pr' -and $Fault -eq 'ordinal' -and $index -eq 5) { 1 } else { 0 }
        $events.Add((New-Event -Index $index -Root $Root -Ordinal $ordinal -ReturnDigest $returnDigest -ReturnRendered $returnRendered))
        if ($Variant -eq 'pr' -and $index -eq 1) {
            $events.Add((New-Event -Index $index -Root $Root -Ordinal 1))
        }
    }
    $events.Add((New-HarnessEvent -Root $Root -ExceptionType $(if ($Variant -eq 'pr') { 'FixtureFailure' } else { '' })))

    $tracePath = Join-Path $Directory 'run.ndjson'
    $events | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content $tracePath -Encoding utf8NoBOM

    $members = for ($index = 0; $index -lt 120; $index++) {
        New-Member -Index $index -Status $(if ($Variant -eq 'pr' -and $index -eq 4) { 'Skipped' } else { 'Patched' })
    }
    $members += [ordered]@{
        kind = 'member'
        assembly = 'Fixture.Subject'
        method = 'Fixture.Tests.Root.Run()'
        status = 'Patched'
        returnKind = 'Sync'
        isTestRoot = $true
        sourceResolution = 'sequencePoints'
    }
    $skipped = if ($Variant -eq 'pr') { 1 } else { 0 }
    $manifest = @(
        [ordered]@{ kind = 'run'; schema = 'behaviordiff.trace/1'; language = 'dotnet' }
        [ordered]@{
            kind = 'assembly'
            assembly = 'Fixture.Subject'
            discovery = 'BuildTimeWeave'
            scanned = $true
            instrumented = $true
            patchedMembers = 121 - $skipped
            discoveredMembers = 121
            skippedMembers = $skipped
            patchFailedMembers = 0
            queuedAtMs = 0
            patchedAtMs = 0
            tracedCalls = $events.Count
            membersWithExactSource = 121
            exactSourcePercent = 100
            sourceRule = 'ratio'
        }
    ) + $members + @(
        [ordered]@{
            kind = 'writer'
            enqueued = $events.Count
            written = if ($Variant -eq 'pr' -and $Fault -eq 'writer') { $events.Count - 1 } else { $events.Count }
            dropped = 0
            capacity = 1024
        }
    )
    $manifest | ForEach-Object { $_ | ConvertTo-Json -Compress } |
        Set-Content (Join-Path $Directory 'run.manifest.ndjson') -Encoding utf8NoBOM
}

Remove-Item $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
Write-Run -Directory $base1 -Root $baseRoot -Variant base1
Write-Run -Directory $base2 -Root $baseRoot -Variant base2
Write-Run -Directory $pr -Root $prRoot -Variant pr

[pscustomobject]@{
    Base1Directory = $base1
    Base2Directory = $base2
    PrDirectory = $pr
    BaseRoot = $baseRoot
    PrRoot = $prRoot
}