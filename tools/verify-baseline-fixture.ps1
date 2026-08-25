#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$root = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-baseline-fixture-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'
$preview = Join-Path $repo 'tools/CommentPreview/BehaviorDiff.CommentPreview.csproj'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Invoke-Cli([string[]]$arguments, [int]$expectedExit) {
    $output = @(& dotnet $cli @arguments 2>&1)
    if ($LASTEXITCODE -ne $expectedExit) {
        throw "CLI exit was $LASTEXITCODE, expected $expectedExit`: $($output -join "`n")"
    }
    return $output
}

function Behavior-Pairs($member) {
    return @($member.evidence | ForEach-Object { "$($_.baseDigest)|$($_.prDigest)" } | Sort-Object -Unique)
}

function Optional-Value($value, [string]$property) {
    $found = $value.PSObject.Properties[$property]
    if ($null -eq $found -or $null -eq $found.Value) {
        return '<absent>'
    }
    return [string]$found.Value
}

try {
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    $firstWork = Join-Path $root 'first-work'
    $firstPr = Join-Path $root 'first-pr'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change config `
        -WorkDirectory $firstWork -PrTreeDirectory $firstPr
    if ($LASTEXITCODE -ne 0) { throw "First fixture run failed: $LASTEXITCODE" }

    $firstFindings = Join-Path $firstWork 'findings.json'
    $first = Get-Content $firstFindings -Raw | ConvertFrom-Json
    Assert-True ($first.status -eq 'analyzed' -and $first.summary.unexpectedMembers -gt 0) `
        'First fixture findings were empty or not analyzed'
    Assert-True ($first.members.Count -eq 1 -and $first.members[0].evidence.Count -gt 0) `
        'First fixture did not produce one member with non-empty evidence'
    $memberName = [string]$first.members[0].memberName
    $memberParts = ($memberName.Split('(')[0]).Split('.')
    $shortMember = $memberParts[-2] + '.' + $memberParts[-1]
    $memberPattern = [regex]::Escape($shortMember)
    $firstCallSites = [int]$first.members[0].callSiteCount
    Assert-True ($firstCallSites -gt 0) 'First fixture produced zero call sites'
    $firstPairs = @(Behavior-Pairs $first.members[0])
    Assert-True ($firstPairs.Count -gt 0) 'First fixture produced no behavior digest pairs'

    $baseline = Join-Path $root '.behaviordiff/baseline.yml'
    Invoke-Cli @('baseline', 'write', '--findings', $firstFindings, '--output', $baseline, '--no-expiry') 0 | Out-Null
    $acknowledgedFindings = Join-Path $root 'acknowledged-findings.json'
    Copy-Item $firstFindings $acknowledgedFindings
    Invoke-Cli @('baseline', 'apply', '--findings', $acknowledgedFindings, '--baseline', $baseline) 0 | Out-Null
    $acknowledged = Get-Content $acknowledgedFindings -Raw | ConvertFrom-Json
    Assert-True ($acknowledged.summary.actionableUnexpectedMembers -eq 0 `
        -and $acknowledged.summary.suppressedMembers -eq 1 `
        -and $acknowledged.summary.suppressedCallSites -eq $firstCallSites) `
        'Acknowledged fixture did not suppress its measured call sites'
    $suppressedComment = @(& dotnet run --project $preview -c Release -- $acknowledgedFindings 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Suppressed fixture comment failed: $LASTEXITCODE" }
    Assert-True ($suppressedComment -match "1 member\(s\), $firstCallSites call site\(s\) suppressed" `
        -and $suppressedComment -notmatch $memberPattern) `
        'Acknowledged fixture comment did not collapse to the suppressed count'

    $secondWork = Join-Path $root 'second-work'
    $secondPr = Join-Path $root 'second-pr'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change config-high `
        -WorkDirectory $secondWork -PrTreeDirectory $secondPr
    if ($LASTEXITCODE -ne 0) { throw "Second fixture run failed: $LASTEXITCODE" }

    $secondFindings = Join-Path $secondWork 'findings.json'
    $second = Get-Content $secondFindings -Raw | ConvertFrom-Json
    Assert-True ($second.status -eq 'analyzed' -and $second.summary.unexpectedMembers -gt 0) `
        'Second fixture findings were empty or not analyzed'
    $sameMember = @($second.members | Where-Object memberName -eq $memberName)
    Assert-True ($sameMember.Count -eq 1 -and $sameMember[0].evidence.Count -gt 0) `
        'Second fixture did not produce the same member with non-empty evidence'
    $secondPairs = @(Behavior-Pairs $sameMember[0])
    Assert-True ($secondPairs.Count -gt 0 `
        -and (Compare-Object $firstPairs $secondPairs).Count -gt 0) `
        'Second fixture did not produce a different behavior digest pair'

    $resurfacedFindings = Join-Path $root 'resurfaced-findings.json'
    Copy-Item $secondFindings $resurfacedFindings
    Invoke-Cli @('baseline', 'apply', '--findings', $resurfacedFindings, '--baseline', $baseline) 1 | Out-Null
    $resurfaced = Get-Content $resurfacedFindings -Raw | ConvertFrom-Json
    Assert-True ($resurfaced.summary.actionableUnexpectedMembers -gt 0 `
        -and $resurfaced.summary.suppressedMembers -eq 0 `
        -and @($resurfaced.baseline.digestMismatchEntries).Count -gt 0) `
        'Changed fixture behavior did not resurface with a digest mismatch'
    $resurfacedComment = @(& dotnet run --project $preview -c Release -- $resurfacedFindings 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Resurfaced fixture comment failed: $LASTEXITCODE" }
    Assert-True ($resurfacedComment -match $memberPattern `
        -and $resurfacedComment -match 'Changed behavior since acknowledgement') `
        'Changed fixture behavior or mismatch report was absent from the comment'

    $firstValues = @($first.members[0].evidence | ForEach-Object {
        "$(Optional-Value $_ 'baseReturn') -> $(Optional-Value $_ 'prReturn')"
    } | Sort-Object -Unique)
    $secondValues = @($sameMember[0].evidence | ForEach-Object {
        "$(Optional-Value $_ 'baseReturn') -> $(Optional-Value $_ 'prReturn')"
    } | Sort-Object -Unique)
    Write-Host '=== baseline fixture proof ===' -ForegroundColor Cyan
    Write-Host "  member                    : $memberName"
    Write-Host "  first digest pairs        : $($firstPairs.Count)"
    Write-Host "  second digest pairs       : $($secondPairs.Count)"
    Write-Host "  first values              : $($firstValues -join '; ')"
    Write-Host "  second values             : $($secondValues -join '; ')"
    Write-Host "  acknowledged comment      : 0 actionable / 1 member, $firstCallSites sites suppressed"
    Write-Host "  resurfaced comment        : $($resurfaced.summary.actionableUnexpectedMembers) actionable / $(@($resurfaced.baseline.digestMismatchEntries).Count) changed acknowledgement(s)"
    Write-Host 'verify-baseline-fixture: PASS' -ForegroundColor Green
}
finally {
    if ($ownsWork) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}