#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-baseline-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Invoke-Cli([string[]]$arguments, [int]$expectedExit) {
    $output = @(& dotnet $cli @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne $expectedExit) {
        throw "CLI exit was $exitCode, expected $expectedExit"
    }
    return $output
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    & dotnet build (Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj') -c Release --nologo -v quiet `
        -p:RestoreSources=https://www.nuget.org/api/v2/ -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed: $LASTEXITCODE" }

    $members = @(
        [ordered]@{ memberName = 'Acme.Pricing.Accepted()'; attribution = 'unexpected'; filePath = 'src/pricing/accepted.cs'; line = 10; callSiteCount = 2; untestedCallSiteCount = 0; distinctTestCount = 1; assertionReactionSummary = '1 test executed this; 1 test had an assertion react.'; evidence = @([ordered]@{ baseDigest = 'sha256:accepted-base'; prDigest = 'sha256:accepted-pr' }) },
        [ordered]@{ memberName = 'Acme.Generated.Value()'; attribution = 'unexpected'; filePath = 'generated/value.cs'; line = 20; callSiteCount = 3; untestedCallSiteCount = 0; distinctTestCount = 1; assertionReactionSummary = '1 test executed this; 1 test had an assertion react.'; evidence = @([ordered]@{ baseDigest = 'sha256:generated-base'; prDigest = 'sha256:generated-pr' }) },
        [ordered]@{ memberName = 'Legacy.Cache.Read()'; attribution = 'unexpected'; filePath = 'src/cache/read.cs'; line = 30; callSiteCount = 4; untestedCallSiteCount = 0; distinctTestCount = 1; assertionReactionSummary = '1 test executed this; 1 test had an assertion react.'; evidence = @([ordered]@{ baseDigest = 'sha256:cache-base'; prDigest = 'sha256:cache-pr' }) },
        [ordered]@{ memberName = 'Acme.Active.Remains()'; attribution = 'unexpected'; filePath = 'src/active/remains.cs'; line = 40; callSiteCount = 5; untestedCallSiteCount = 0; distinctTestCount = 1; assertionReactionSummary = '1 test executed this; 1 test had an assertion react.'; evidence = @([ordered]@{ baseDigest = 'sha256:active-base'; prDigest = 'sha256:active-pr' }) }
    )
    $artifact = [ordered]@{
        schema = 'behaviordiff.findings/1'
        status = 'analyzed'
        verdict = 'findings'
        isCleanResult = $false
        exitCode = 1
        exitReason = 'unexpected_findings'
        refs = [ordered]@{ baseSha = 'proof-base'; prSha = 'proof-pr'; mergeBaseSha = 'proof-base' }
        summary = [ordered]@{
            unexpectedMembers = 4; unexpectedCallSites = 14
            expectedMembers = 0; expectedCallSites = 0; untestedMembers = 0
            editedFiles = 1; exercisedEditedFiles = 1; tracedMembers = 4
            observedCallSites = 14; totalCallCount = 14
        }
        coverage = [ordered]@{
            summary = [ordered]@{
                editedFiles = 1; exercisedEditedFiles = 1; tracedMembers = 4
                observedCallSites = 14; totalCallCount = 14
            }
            files = @()
        }
        members = $members
    }
    $original = $artifact | ConvertTo-Json -Depth 10
    $findings = Join-Path $work 'findings.json'
    [IO.File]::WriteAllText($findings, $original)

    $yesterday = [DateTime]::UtcNow.AddDays(-1).ToString('yyyy-MM-dd')
    $future = [DateTime]::UtcNow.AddDays(30).ToString('yyyy-MM-dd')
    $baseline = Join-Path $work '.behaviordiff/baseline.yml'
    New-Item -ItemType Directory -Path (Split-Path -Parent $baseline) -Force | Out-Null
    [IO.File]::WriteAllText($baseline, @"
schema: behaviordiff.baseline/2
acknowledgements:
  - id: accepted-pricing
    member: Acme.Pricing.Accepted()
    path: src/pricing/accepted.cs
    baseDigest: 'sha256:accepted-base'
    prDigest: 'sha256:accepted-pr'
    reason: Known accepted behavior
    expires: $future
  - id: expired-active
    member: Acme.Active.Remains()
    path: src/active/remains.cs
    baseDigest: 'sha256:active-base'
    prDigest: 'sha256:active-pr'
    reason: Expired acknowledgement must not suppress
    expires: $yesterday
  - id: stale-missing
    member: Acme.Missing.NoLongerReported()
    path: src/missing/no-longer-reported.cs
    baseDigest: 'sha256:missing-base'
    prDigest: 'sha256:missing-pr'
    reason: This entry should be stale
ignorePaths:
  - id: generated-output
    pattern: '**/generated/**'
    reason: Generated source is ignored
ignoreMembers:
  - id: legacy-cache
    pattern: Legacy.*
    reason: Legacy cache is ignored
"@)

    Write-Host '=== apply explicit policy ===' -ForegroundColor Cyan
    Invoke-Cli @('baseline', 'apply', '--findings', $findings, '--baseline', $baseline) 1 | Out-Null
    $applied = Get-Content $findings -Raw | ConvertFrom-Json
    Assert-True ($applied.members.Count -eq 4) 'Baseline removed raw findings members'
    Assert-True ($applied.summary.unexpectedMembers -eq 4) 'Baseline changed raw unexpected count'
    Assert-True ($applied.summary.actionableUnexpectedMembers -eq 1) 'Expected one actionable member'
    Assert-True ($applied.summary.suppressedMembers -eq 3) 'Expected three suppressed members'
    Assert-True ($applied.summary.suppressedCallSites -eq 9) 'Expected nine suppressed call sites'
    Assert-True ($applied.policyExitCode -eq 1 -and $applied.policyVerdict -eq 'findings') `
        'Policy verdict did not retain the actionable finding'
    Assert-True (@($applied.members | Where-Object {
        $_.PSObject.Properties.Name -contains 'suppression'
    }).Count -eq 3) `
        'Per-member suppression metadata count is wrong'
    Assert-True (@($applied.baseline.staleEntries).Count -eq 1 `
        -and $applied.baseline.staleEntries[0].ruleId -eq 'stale-missing') 'Stale rule was not reported'
    Assert-True (@($applied.baseline.digestMismatchEntries).Count -eq 0) `
        'Matching acknowledgement was incorrectly reported as changed behavior'
    Assert-True (@($applied.baseline.expiredEntries).Count -eq 1 `
        -and $applied.baseline.expiredEntries[0].ruleId -eq 'expired-active') 'Expired rule was not reported'

    $reapplyFindings = Join-Path $work 'reapply-findings.json'
    Copy-Item $findings $reapplyFindings
    $emptyBaseline = Join-Path $work 'empty-baseline.yml'
    [IO.File]::WriteAllText($emptyBaseline, @"
schema: behaviordiff.baseline/2
acknowledgements: []
ignorePaths: []
ignoreMembers: []
"@)
    Invoke-Cli @('baseline', 'apply', '--findings', $reapplyFindings, '--baseline', $emptyBaseline) 1 | Out-Null
    $reapplied = Get-Content $reapplyFindings -Raw | ConvertFrom-Json
    Assert-True ($reapplied.summary.suppressedMembers -eq 0 `
        -and @($reapplied.members | Where-Object {
            $_.PSObject.Properties.Name -contains 'suppression'
        }).Count -eq 0) 'Reapplying a different baseline retained old suppression metadata'

    Write-Host '=== render provider summaries ===' -ForegroundColor Cyan
    $preview = Join-Path $repo 'tools/CommentPreview/BehaviorDiff.CommentPreview.csproj'
    $github = @(& dotnet run --project $preview -c Release -- $findings 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "GitHub preview failed: $LASTEXITCODE" }
    $oldRepositoryUri = $env:BUILD_REPOSITORY_URI
    try {
        $env:BUILD_REPOSITORY_URI = 'https://dev.azure.com/acme/project/_git/repo'
        $azure = @(& dotnet run --project $preview -c Release -- --provider=azuredevops $findings 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Azure preview failed: $LASTEXITCODE" }
    } finally {
        $env:BUILD_REPOSITORY_URI = $oldRepositoryUri
    }
    foreach ($rendered in @($github -join "`n", $azure -join "`n")) {
        Assert-True ($rendered -match 'Active.Remains') 'Actionable member is absent from rendered summary'
        Assert-True ($rendered -notmatch 'Pricing.Accepted|Generated.Value|Cache.Read') `
            'Suppressed member leaked into rendered summary'
        Assert-True ($rendered -match '3 member\(s\), 9 call site\(s\) suppressed') `
            'Rendered summary omitted suppressed counts'
        Assert-True ($rendered -match '1 stale, 0 changed, 1 expired') `
            'Rendered summary omitted stale/changed/expired counts'
        Assert-True ($rendered -match 'stale-missing') 'Rendered summary omitted the stale rule id'
        Assert-True ($rendered -match '\.behaviordiff[/\\]baseline.yml') 'Rendered summary omitted baseline path'
    }
    Assert-True (($github -join "`n") -match 'github.com/acme/repo/blob/proof-pr/.behaviordiff/baseline.yml') `
        'GitHub summary omitted the committed baseline link'

    Write-Host '=== changed behavior resurfaces ===' -ForegroundColor Cyan
    $changedFindings = Join-Path $work 'changed-findings.json'
    $changed = $original | ConvertFrom-Json
    $changed.members[0].evidence[0].prDigest = 'sha256:accepted-pr-v2'
    $changed | ConvertTo-Json -Depth 10 | Set-Content $changedFindings
    Invoke-Cli @('baseline', 'apply', '--findings', $changedFindings, '--baseline', $baseline) 1 | Out-Null
    $changedApplied = Get-Content $changedFindings -Raw | ConvertFrom-Json
    Assert-True ($changedApplied.summary.actionableUnexpectedMembers -eq 2 `
        -and $changedApplied.summary.suppressedMembers -eq 2) `
        'Changed behavior did not resurface while broad ignores remained suppressed'
    Assert-True (@($changedApplied.baseline.digestMismatchEntries).Count -eq 1 `
        -and $changedApplied.baseline.digestMismatchEntries[0].ruleId -eq 'accepted-pricing') `
        'Changed behavior was not reported as a digest mismatch'

    Write-Host '=== write and merge acknowledgements ===' -ForegroundColor Cyan
    $writeFindings = Join-Path $work 'write-findings.json'
    [IO.File]::WriteAllText($writeFindings, $original)
    $written = Join-Path $work 'written-baseline.yml'
    $firstWrite = Invoke-Cli @('baseline', 'write', '--findings', $writeFindings, '--output', $written) 0
    Assert-True (($firstWrite -join "`n") -match 'acknowledgements added: 4') `
        'Baseline writer did not add all actionable members'
    $secondWrite = Invoke-Cli @('baseline', 'write', '--findings', $writeFindings, '--output', $written) 0
    Assert-True (($secondWrite -join "`n") -match 'acknowledgements added: 0') `
        'Baseline writer was not idempotent'
    Invoke-Cli @('baseline', 'apply', '--findings', $writeFindings, '--baseline', $written) 0 | Out-Null
    $writtenApplied = Get-Content $writeFindings -Raw | ConvertFrom-Json
    Assert-True ($writtenApplied.summary.actionableUnexpectedMembers -eq 0 `
        -and $writtenApplied.summary.suppressedMembers -eq 4 `
        -and $writtenApplied.policyExitCode -eq 0) 'Written baseline did not suppress all four members'

    Write-Host 'verify-baseline: PASS' -ForegroundColor Green
}
finally {
    if ($ownsWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}