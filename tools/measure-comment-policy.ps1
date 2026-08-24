#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Manifest,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$BehaviorDiffCommand = 'behaviordiff',

    [int]$Limit = 0,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string]$Repository,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & git -C $Repository @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Read-Count {
    param(
        [object]$Object,
        [string]$Property
    )

    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Property]) {
        return 0
    }

    return [int]$Object.$Property
}

function Read-Reason {
    param([object]$Findings)

    if ($null -eq $Findings) {
        return ''
    }

    foreach ($property in 'refusal', 'failure') {
        $container = $Findings.PSObject.Properties[$property]
        if ($null -ne $container -and $null -ne $container.Value.PSObject.Properties['reason']) {
            return [string]$container.Value.reason
        }
    }

    return ''
}

$manifestPath = [IO.Path]::GetFullPath($Manifest)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$resultsPath = Join-Path $outputRoot 'results.csv'
$repositoryRoot = Join-Path $outputRoot 'repositories'
$artifactRoot = Join-Path $outputRoot 'artifacts'
New-Item -ItemType Directory -Path $repositoryRoot, $artifactRoot -Force | Out-Null

$cases = @(Import-Csv $manifestPath)
if ($Limit -gt 0) {
    $cases = @($cases | Select-Object -First $Limit)
}

$required = 'repository', 'language', 'pr', 'baseSha', 'headSha', 'mergedAt', 'url'
foreach ($case in $cases) {
    foreach ($property in $required) {
        if ([string]::IsNullOrWhiteSpace([string]$case.$property)) {
            throw "Manifest row for $($case.repository) PR $($case.pr) is missing $property"
        }
    }
}

$results = @()
if (Test-Path $resultsPath) {
    $results += @(Import-Csv $resultsPath)
}
foreach ($case in $cases) {
    $existing = @($results | Where-Object {
        $_.repository -eq $case.repository -and $_.pr -eq $case.pr
    })
    if ($existing.Count -gt 0 -and -not $Force) {
        Write-Host "SKIP $($case.repository)#$($case.pr): result already recorded"
        continue
    }

    if ($existing.Count -gt 0) {
        $results = @($results | Where-Object {
            $_.repository -ne $case.repository -or $_.pr -ne $case.pr
        })
    }

    $repositoryName = $case.repository.Replace('/', '__')
    $repository = Join-Path $repositoryRoot $repositoryName
    if (-not (Test-Path (Join-Path $repository '.git'))) {
        & git clone --filter=blob:none --no-checkout "https://github.com/$($case.repository).git" $repository
        if ($LASTEXITCODE -ne 0) {
            throw "Could not clone $($case.repository): $LASTEXITCODE"
        }
    }
    else {
        Invoke-Git $repository fetch origin --prune
    }

    Invoke-Git $repository fetch origin "refs/pull/$($case.pr)/head:refs/behaviordiff/pr-$($case.pr)" --force
    $actualBase = (& git -C $repository rev-parse "$($case.baseSha)^{commit}").Trim()
    $actualHead = (& git -C $repository rev-parse "refs/behaviordiff/pr-$($case.pr)^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or $actualBase -ne $case.baseSha -or $actualHead -ne $case.headSha) {
        throw "$($case.repository)#$($case.pr) did not resolve to the pinned base/head SHAs"
    }

    $caseName = "$repositoryName-pr-$($case.pr)"
    $caseArtifacts = Join-Path $artifactRoot $caseName
    $work = Join-Path $caseArtifacts 'work'
    $findingsPath = Join-Path $caseArtifacts 'findings.json'
    $logPath = Join-Path $caseArtifacts 'run.log'
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $caseArtifacts -Force | Out-Null

    $findings = $null
    $processExitCode = 0
    $elapsedSeconds = 0
    if (-not $Force -and (Test-Path $findingsPath) -and (Test-Path $logPath)) {
        $candidate = Get-Content $findingsPath -Raw | ConvertFrom-Json -Depth 100
        if ($candidate.refs.baseSha -eq $case.baseSha -and $candidate.refs.prSha -eq $case.headSha) {
            $findings = $candidate
            $processExitCode = [int]$candidate.exitCode
            $elapsedSeconds = [Math]::Round(
                ((Get-Item $findingsPath).LastWriteTimeUtc - (Get-Item $logPath).CreationTimeUtc).TotalSeconds,
                3)
            Write-Host "RECOVER $($case.repository)#$($case.pr): completed artifact" -ForegroundColor DarkCyan
        }
    }

    if ($null -eq $findings) {
        Write-Host "RUN  $($case.repository)#$($case.pr) ($($case.language))" -ForegroundColor Cyan
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        & $BehaviorDiffCommand $repository --base $case.baseSha --pr $case.headSha `
            --work $work --findings $findingsPath --no-baseline *> $logPath
        $processExitCode = $LASTEXITCODE
        $stopwatch.Stop()
        $elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        $findings = if (Test-Path $findingsPath) {
            Get-Content $findingsPath -Raw | ConvertFrom-Json -Depth 100
        }
        else {
            $null
        }
    }

    $members = if ($null -ne $findings -and $null -ne $findings.PSObject.Properties['members']) {
        @($findings.members | Where-Object attribution -eq 'unexpected')
    }
    else {
        @()
    }
    $suppressionReasons = @($members | ForEach-Object commentSuppressionReasons | Sort-Object -Unique)
    $summary = if ($null -ne $findings -and $null -ne $findings.PSObject.Properties['summary']) {
        $findings.summary
    } else { $null }
    $commentPolicy = if ($null -ne $findings -and $null -ne $findings.PSObject.Properties['commentPolicy']) {
        $findings.commentPolicy
    } else { $null }

    $result = [pscustomobject][ordered]@{
        repository = $case.repository
        language = $case.language
        pr = $case.pr
        baseSha = $case.baseSha
        headSha = $case.headSha
        mergedAt = $case.mergedAt
        url = $case.url
        status = if ($null -ne $findings) { [string]$findings.status } else { 'missing-artifact' }
        verdict = if ($null -ne $findings) { [string]$findings.verdict } else { '' }
        exitCode = $processExitCode
        unexpectedMembers = Read-Count $summary 'unexpectedMembers'
        unexpectedCallSites = Read-Count $summary 'unexpectedCallSites'
        eligibleMembers = Read-Count $commentPolicy 'eligibleUnexpectedMembers'
        eligibleCallSites = Read-Count $commentPolicy 'eligibleUnexpectedCallSites'
        suppressedMembers = Read-Count $commentPolicy 'suppressedUnexpectedMembers'
        suppressedCallSites = Read-Count $commentPolicy 'suppressedUnexpectedCallSites'
        nondeterministicMembers = Read-Count $summary 'nondeterministicUnexpectedMembers'
        wouldComment = (Read-Count $commentPolicy 'eligibleUnexpectedMembers') -gt 0
        suppressionReasons = $suppressionReasons -join ';'
        elapsedSeconds = $elapsedSeconds
        reason = Read-Reason $findings
        findings = [IO.Path]::GetRelativePath($outputRoot, $findingsPath).Replace('\', '/')
        log = [IO.Path]::GetRelativePath($outputRoot, $logPath).Replace('\', '/')
    }

    $results += $result
    $results | Sort-Object repository, { [int]$_.pr } | Export-Csv $resultsPath -NoTypeInformation
    Write-Host ("  status={0} verdict={1} raw={2} eligible={3} elapsed={4}s" -f `
        $result.status, $result.verdict, $result.unexpectedMembers, $result.eligibleMembers, $result.elapsedSeconds)
}

$analyzed = @($results | Where-Object status -eq 'analyzed')
$defaultComments = @($analyzed | Where-Object { $_.wouldComment -eq 'True' -or $_.wouldComment -eq $true })
$rawFindings = @($analyzed | Where-Object { [int]$_.unexpectedMembers -gt 0 })
$summaryPath = Join-Path $outputRoot 'summary.json'
$measurementSummary = [pscustomobject][ordered]@{
    schema = 'behaviordiff.comment-policy-measurement/1'
    sampledPrs = $results.Count
    analyzedPrs = $analyzed.Count
    refusedPrs = @($results | Where-Object status -eq 'refused').Count
    failedPrs = @($results | Where-Object status -eq 'failed').Count
    missingArtifactPrs = @($results | Where-Object status -eq 'missing-artifact').Count
    defaultCommentPrs = $defaultComments.Count
    defaultCommentRateAmongAnalyzed = if ($analyzed.Count -eq 0) { $null } else {
        [Math]::Round($defaultComments.Count / $analyzed.Count, 6)
    }
    rawFindingPrs = $rawFindings.Count
    rawFindingRateAmongAnalyzed = if ($analyzed.Count -eq 0) { $null } else {
        [Math]::Round($rawFindings.Count / $analyzed.Count, 6)
    }
}
$measurementSummary | ConvertTo-Json | Set-Content $summaryPath

Write-Host "Results: $resultsPath" -ForegroundColor Green
Write-Host "Summary: $summaryPath" -ForegroundColor Green