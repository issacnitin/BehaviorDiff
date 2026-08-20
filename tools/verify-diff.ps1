#requires -Version 7.0
<#
    Proof harness for Engine part 1.

    Builds a second worktree at a different absolute path so base and PR PDB paths genuinely differ -
    without that, path normalization is never exercised and STEP 0 proves nothing.

    Usage:
      tools/verify-diff.ps1              identical commit in both worktrees; expect 0 divergences
      tools/verify-diff.ps1 -Mutate      apply one deliberate one-line change to the PR worktree
#>
[CmdletBinding()]
param(
    [switch]$Mutate,
    [ValidateSet('discount', 'sort', 'retry', 'config', 'downgrade')]
    [string]$Change = 'discount',
    [switch]$SkipPrRebuild,
    [string]$WorkDirectory,
    [string]$PrTreeDirectory
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$providedPrTree = $PrTreeDirectory
$runId = [Guid]::NewGuid().ToString('N')
$ownsPrTree = -not $PrTreeDirectory
$ownsWork = -not $WorkDirectory
$prTree = if ($PrTreeDirectory) { $PrTreeDirectory } else { Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-pr-$runId" }
$work = if ($WorkDirectory) { $WorkDirectory } else { Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-run-$runId" }

if ($SkipPrRebuild -and (-not $providedPrTree -or -not (Test-Path $providedPrTree))) {
    throw '-SkipPrRebuild requires an existing -PrTreeDirectory.'
}

function Invoke-Suite([string]$stagedBin, [string]$outputDir) {
    Remove-Item $outputDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $outputDir | Out-Null

    $env:BEHAVIORDIFF_NAMESPACES = 'SampleApp,Commerce.Pricing,Infrastructure.Collections'
    $env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics,SampleApp.Persistence,Infrastructure.Collections'
    $env:BEHAVIORDIFF_BACKEND = 'cecil'
    $env:BEHAVIORDIFF_TRACE = Join-Path $outputDir 'run.ndjson'

    dotnet test (Join-Path $stagedBin 'SampleApp.Tests.dll') --nologo | Out-Null
    return $LASTEXITCODE
}

function Invoke-Proof {

Write-Host '=== preparing worktrees ===' -ForegroundColor Cyan
Push-Location $repo
try {
    dotnet build BehaviorDiff.sln -c Release --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'base build failed' }
}
finally { Pop-Location }

if (-not $SkipPrRebuild) {
    Remove-Item $prTree -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $prTree | Out-Null

    # Copy sources only. bin/obj carry PDBs pointing at the base path, which would mask a normalization bug.
    Get-ChildItem $repo -Force | Where-Object { $_.Name -notin @('.git', '.vs') } | ForEach-Object {
        Copy-Item $_.FullName -Destination $prTree -Recurse -Force -Exclude @()
    }
    Get-ChildItem $prTree -Include bin, obj -Recurse -Directory -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    if ($Mutate) {
        if ($Change -eq 'sort') {
            $target = Join-Path $prTree 'src/Infrastructure.Collections/SortingExtensions.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace '(?s)public static List<T> ByPriority<T>\(\s*this IEnumerable<T> src,\s*Func<T, int> key\)\s*\{\s*var list = src\.ToList\(\);\s*list\.Sort\(\(a, b\) => key\(a\)\.CompareTo\(key\(b\)\)\);\s*return list;\s*\}', @'
public static List<T> ByPriority<T>(
            this IEnumerable<T> src,
            Func<T, int> key) => src.OrderBy(key).ToList();
'@
            $label = 'SortingExtensions restores stable ordering for equal-priority items'
        }
        elseif ($Change -eq 'retry') {
            $target = Join-Path $prTree 'samples/SampleApp/ConfigParser.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace 'RetrySettings\.MaxAttempts = int\.Parse\(raw\["max_attempts"\], CultureInfo\.InvariantCulture\);', @'
RetrySettings.MaxAttempts = raw.TryGetValue("max_attempts", out string? value)
                ? int.Parse(value, CultureInfo.InvariantCulture)
                : 3;
'@
            $label = 'ConfigParser missing max_attempts fallback 10 -> 3'
        }
        elseif ($Change -eq 'discount') {
            $target = Join-Path $prTree 'samples/SampleApp/OrderService.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace 'quantity >= 10', 'quantity >= 5'
            $label = 'OrderService.ApplyDiscount threshold 10 -> 5'
        }
        elseif ($Change -eq 'config') {
            # Config-parser shape: only the parser file is touched, and only a constant it returns.
            $target = Join-Path $prTree 'samples/SampleApp/SettingsParser.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace 'DefaultFreeShippingThreshold = 50m', 'DefaultFreeShippingThreshold = 30m'
            $label = 'SettingsParser default free-shipping threshold 50 -> 30'
        }
        else {
            # Drives the frontier downgrade reasons. The edited file declares no methods.
            $target = Join-Path $prTree 'samples/SampleApp/DowngradeConfig.cs'
            $text = Get-Content $target -Raw
            $mutated = $text -replace 'Magnitude = 3', 'Magnitude = 4'
            $label = 'DowngradeConfig.Magnitude 3 -> 4'
        }

        if ($mutated -eq $text) { throw 'mutation did not apply - the anchor text was not found' }
        Set-Content $target $mutated -NoNewline
        Write-Host "  MUTATION: $label" -ForegroundColor Yellow
    }

    Push-Location $prTree
    try {
        dotnet build BehaviorDiff.sln -c Release --nologo -v quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'pr worktree build failed' }
    }
    finally { Pop-Location }
}

Write-Host "  base worktree : $repo"
Write-Host "  pr   worktree : $prTree"

Write-Host ''
Write-Host '=== running suites ===' -ForegroundColor Cyan
$stage = Join-Path $PSScriptRoot 'Stage-WovenSample.ps1'
$baseBin = Join-Path $work 'base-bin'
$prBin = Join-Path $work 'pr-bin'
& $stage -TreeRoot $repo -OutDir $baseBin
& $stage -TreeRoot $prTree -OutDir $prBin

$base1Exit = Invoke-Suite $baseBin (Join-Path $work 'base_run1')
if ($base1Exit -ne 0) { throw "base_run1 tests failed: $base1Exit" }
Write-Host '  base_run1 done'
$base2Exit = Invoke-Suite $baseBin (Join-Path $work 'base_run2')
if ($base2Exit -ne 0) { throw "base_run2 tests failed: $base2Exit" }
Write-Host '  base_run2 done'
$prExit = Invoke-Suite $prBin (Join-Path $work 'pr_run')
$expectedPrExit = if ($Mutate -and $Change -in @('sort', 'retry', 'config', 'downgrade')) { 1 } else { 0 }
if ($prExit -ne $expectedPrExit) {
    throw "pr_run test exit was $prExit, expected $expectedPrExit for change '$Change'"
}
Write-Host '  pr_run done'

Write-Host ''
$out = Join-Path $work 'divergence-set.json'
& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
    diff --base1 (Join-Path $work 'base_run1') --base2 (Join-Path $work 'base_run2') --pr (Join-Path $work 'pr_run') `
    --base-root $repo --pr-root $prTree --out $out

$engineExit = $LASTEXITCODE
Write-Host ''
Write-Host "engine exit = $engineExit"

if ($engineExit -ne 0) { return $engineExit }

# Stands in for `git diff --name-only base..pr`, computed by comparing the two worktrees. Repo-relative
# with forward slashes, matching the Step 0 normalization the traces went through.
Write-Host ''
Write-Host '=== changed files (base..pr) ===' -ForegroundColor Cyan
$changedList = Join-Path $work 'changed-files.txt'
$changed = @()
Get-ChildItem $repo -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $rel = $_.FullName.Substring($repo.Length).TrimStart('\', '/').Replace('\', '/')
        $counterpart = Join-Path $prTree $rel
        if (-not (Test-Path $counterpart)) { $changed += $rel }
        elseif ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash $counterpart).Hash) { $changed += $rel }
    }

$changed | Set-Content $changedList
$changed | ForEach-Object { Write-Host "  $_" }
if ($changed.Count -eq 0) { Write-Host '  (none)' }

Write-Host ''
$report = Join-Path $work 'frontier-report.json'
& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
    frontier --in $out --changed-files $changedList --out $report

$frontierExit = $LASTEXITCODE
if ($frontierExit -ne 0) { return $frontierExit }

Write-Host ''
Write-Host '=== canonical findings ===' -ForegroundColor Cyan
$findings = Join-Path $work 'findings.json'
$frontierDocument = Get-Content $report -Raw | ConvertFrom-Json
$findingsExit = if ($frontierDocument.counts.unexpected -gt 0) { 1 } else { 0 }
& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
    findings --divergences $out --frontier $report --out $findings --exit-code $findingsExit `
    --base-sha proof-base --pr-sha proof-pr --merge-base proof-merge-base

if ($LASTEXITCODE -ne 0) { return $LASTEXITCODE }
$document = Get-Content $findings -Raw | ConvertFrom-Json
if ($document.status -ne 'analyzed') { throw "findings status is $($document.status), expected analyzed" }
if ($document.members.Count -lt 1) { throw 'analyzed findings has no member evidence' }
if ($document.members[0].callSiteCount -lt 1) { throw 'member rollup has no call sites' }
if ($document.members[0].evidence.Count -lt 1) { throw 'member has no per-member evidence' }
Write-Host "  $($document.summary.unexpectedMembers) unexpected member(s), $($document.summary.unexpectedCallSites) call site(s)"
Write-Host "  first member evidence count: $($document.members[0].evidence.Count)"
Write-Host 'findings.json analyzed arm: PASS' -ForegroundColor Green

return 0
}

$mutex = [Threading.Mutex]::new($false, 'Local\BehaviorDiffVerifyDiff')
$acquired = $false
try {
    try {
        $acquired = $mutex.WaitOne()
    }
    catch [Threading.AbandonedMutexException] {
        $acquired = $true
    }

    $proofExit = Invoke-Proof
}
finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    if ($ownsWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    if ($ownsPrTree) { Remove-Item $prTree -Recurse -Force -ErrorAction SilentlyContinue }
}

exit $proofExit
