#requires -Version 7.0
<#
  Proves changed-file coverage from real SampleApp traces. The input includes:
  - SettingsParser.cs: executed by five tests on each compared side;
  - README.md: deliberately not executable, and therefore an explicit zero-coverage row.
#>
[CmdletBinding()]
param(
    [string]$WorkDirectory,
    [string]$PrTreeDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'RealDiff.Engine.psm1') -Force
$engine = Get-RealDiffEngine
$runId = [Guid]::NewGuid().ToString('N')
$ownsWork = -not $WorkDirectory
$ownsPrTree = -not $PrTreeDirectory
$work = if ($WorkDirectory) { $WorkDirectory } else { Join-Path ([IO.Path]::GetTempPath()) "realdiff-coverage-$runId" }
$prTree = if ($PrTreeDirectory) { $PrTreeDirectory } else { Join-Path ([IO.Path]::GetTempPath()) "realdiff-coverage-pr-$runId" }
$divergences = Join-Path $work 'divergence-set.json'

try {
& (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change config `
    -WorkDirectory $work -PrTreeDirectory $prTree
if ($LASTEXITCODE -ne 0) { throw "verify-diff failed: $LASTEXITCODE" }
if (-not (Test-Path $divergences)) { throw "missing real divergence set: $divergences" }

$changed = Join-Path $work 'coverage-changed-files.txt'
@(
    'samples/SampleApp/SettingsParser.cs'
    'README.md'
) | Set-Content $changed

$frontier = Join-Path $work 'coverage-frontier-report.json'
$output = & $engine frontier --in $divergences --changed-files $changed --out $frontier 2>&1
$frontierExit = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($frontierExit -ne 0) { throw "coverage frontier failed: $frontierExit" }

$findings = Join-Path $work 'coverage-findings.json'
& $engine findings --divergences $divergences --frontier $frontier --out $findings --exit-code 1 `
    --base-sha proof-base --pr-sha proof-pr --merge-base proof-merge-base | Out-Null
if ($LASTEXITCODE -ne 0) { throw "coverage findings projection failed: $LASTEXITCODE" }

$document = Get-Content $findings -Raw | ConvertFrom-Json
$summary = $document.coverage.summary
$parser = $document.coverage.files | Where-Object filePath -eq 'samples/SampleApp/SettingsParser.cs'
$readme = $document.coverage.files | Where-Object filePath -eq 'README.md'
$unexpected = $document.members | Where-Object attribution -eq 'unexpected' | Select-Object -First 1

if ($summary.editedFiles -ne 2 -or $summary.exercisedEditedFiles -ne 1) {
    throw "expected 1 of 2 edited files exercised, got $($summary.exercisedEditedFiles) of $($summary.editedFiles)"
}
if (-not $parser.exercised -or $parser.tracedMembers -ne 1 -or $parser.totalCallCount -ne 10) {
    throw "unexpected parser coverage: $($parser | ConvertTo-Json -Compress)"
}
if ($readme.exercised -or $readme.tracedMembers -ne 0 -or $readme.totalCallCount -ne 0) {
    throw "unexecuted README was not represented honestly: $($readme | ConvertTo-Json -Compress)"
}
if ($readme.interpretation -notmatch 'not observed' -or $readme.interpretation -notmatch 'not evidence') {
    throw 'zero-coverage interpretation is missing its non-claim'
}
if (($output -join "`n") -notmatch 'NOT EXERCISED\s+README.md.*no behavioral claim') {
    throw 'console output omitted the explicit zero-coverage row'
}
if ($unexpected.assertionReactionSummary -ne '5 tests executed this; 1 test had an assertion react.') {
    throw "unexpected assertion reaction summary: $($unexpected.assertionReactionSummary)"
}
if ($unexpected.evidence.Count -ne 2) {
    throw "expected two concrete observations, got $($unexpected.evidence.Count)"
}
$first = $unexpected.evidence[0]
if ($first.baseArgs -ne 'orderTotal=Primitive:40' -or $first.prArgs -ne 'orderTotal=Primitive:40' `
    -or $first.baseReturn -ne 'Primitive:false' -or $first.prReturn -ne 'Primitive:true') {
    throw "unexpected rendered value evidence: $($first | ConvertTo-Json -Compress -Depth 8)"
}
if ($first.baseCallPath.Count -ne 4 -or $first.prCallPath.Count -ne 4 `
    -or $first.baseCallPath[0].memberName -ne "$($first.testId)()" `
    -or $first.baseCallPath[-1].memberName -ne $unexpected.memberName `
    -or $first.prCallPath[0].memberName -ne "$($first.testId)()" `
    -or $first.prCallPath[-1].memberName -ne $unexpected.memberName) {
    throw "observation did not join to its exact base/PR call path: $($first | ConvertTo-Json -Compress -Depth 8)"
}
if ($unexpected.changedFilesReachingMember.Count -ne 0) {
    throw "invented edited-file reachability: $($unexpected.changedFilesReachingMember -join ', ')"
}

Write-Host ''
Write-Host 'PASS: findings.json reports 1 of 2 edited files exercised' -ForegroundColor Green
Write-Host 'PASS: SettingsParser.cs reports 1 member, 5 call sites, 10 calls' -ForegroundColor Green
Write-Host 'PASS: README.md reports zero members/calls and makes no behavioral claim' -ForegroundColor Green
Write-Host 'PASS: concrete values join to exact base/PR call paths and assertion reaction' -ForegroundColor Green
Write-Host 'verify-coverage: PASS' -ForegroundColor Green
}
finally {
    if ($ownsWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    if ($ownsPrTree) { Remove-Item $prTree -Recurse -Force -ErrorAction SilentlyContinue }
}
