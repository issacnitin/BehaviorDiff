#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-node-sort-{0}" -f [Guid]::NewGuid().ToString('N'))
} else {
    [IO.Path]::GetFullPath($WorkDirectory)
}
$demoRepo = Join-Path $work 'repo'
$cliWork = Join-Path $work 'cli-work'
$findingsPath = Join-Path $cliWork 'findings.json'
$changedFile = 'src/sorting/rule-ordering.js'
$discountFile = 'src/pricing/discount-engine.js'
$totalsFile = 'src/pricing/checkout-totals.js'
$headline = "$discountFile#DiscountEngine.selectDiscount"
$totalsMember = "$totalsFile#CheckoutTotals.compute"
$sortingPrefix = "$changedFile#"
$modelExplainer = 'unavailable (no API key)'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Read-Ndjson([string[]]$paths) {
    return @($paths | ForEach-Object {
        Get-Content $_ | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json }
    })
}

function Invoke-Npm([string]$directory, [string[]]$arguments, [string]$failure) {
    Push-Location $directory
    try {
        & npm @arguments 2>&1 | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($exitCode -ne 0) { throw "$failure (exit $exitCode)" }
}

function Get-ApiKey {
    if (-not [string]::IsNullOrWhiteSpace($env:ANTHROPIC_API_KEY)) {
        return $env:ANTHROPIC_API_KEY
    }

    $keyFile = Join-Path $HOME '.behaviordiff/anthropic.key'
    if (-not (Test-Path $keyFile)) { return $null }
    $protectedKey = (Get-Content $keyFile -Raw).Trim()
    try {
        $secureKey = ConvertTo-SecureString $protectedKey
        $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
        try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer) }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
            $secureKey.Dispose()
        }
    } catch {
        Write-Host "Model explainer unavailable: key file could not be decrypted ($($_.Exception.Message))" -ForegroundColor Yellow
        return $null
    }
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $demoRepo -Force | Out-Null
    Copy-Item (Join-Path $repo 'samples/NodeSortDemo/*') $demoRepo -Recurse -Force

    Write-Host '=== Standalone base fixture ===' -ForegroundColor Cyan
    Invoke-Npm $demoRepo @('ci', '--no-audit', '--no-fund') 'Standalone Node sort demo install failed'
    $baseReport = Join-Path $work 'base-runner-report.json'
    $oldReport = [Environment]::GetEnvironmentVariable('BEHAVIORDIFF_RUNNER_REPORT', 'Process')
    try {
        $env:BEHAVIORDIFF_RUNNER_REPORT = $baseReport
        Invoke-Npm $demoRepo @('test') 'Standalone Node sort demo failed'
    } finally {
        [Environment]::SetEnvironmentVariable('BEHAVIORDIFF_RUNNER_REPORT', $oldReport, 'Process')
    }
    Assert-True (Test-Path $baseReport -PathType Leaf) 'Standalone runner did not write its success report'
    $baseTests = [int](Get-Content $baseReport -Raw | ConvertFrom-Json).runnerTests
    Assert-True ($baseTests -eq 3) "Expected standalone base tests 3/3, got $baseTests"

    Write-Host '=== Temporary git history ===' -ForegroundColor Cyan
    & git -C $demoRepo init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize temporary demo repository' }
    & git -C $demoRepo config user.name 'BehaviorDiff Proof'
    & git -C $demoRepo config user.email 'proof@behaviordiff.invalid'
    & git -C $demoRepo add .
    & git -C $demoRepo commit --quiet -m 'base: stable priority ordering'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit Node sort demo base' }
    $baseSha = (& git -C $demoRepo rev-parse HEAD).Trim()

    $orderingPath = Join-Path $demoRepo $changedFile
    $baseComparator = 'a.priority - b.priority'
    $prComparator = '(a.priority - b.priority) || a.code.localeCompare(b.code)'
    $ordering = Get-Content $orderingPath -Raw
    Assert-True ($ordering.Contains($baseComparator, [StringComparison]::Ordinal)) `
        'Base comparator expression was not found'
    Assert-True (($ordering.Split($baseComparator, [StringSplitOptions]::None).Count - 1) -eq 1) `
        'Base comparator expression was not found exactly once'
    $ordering = $ordering.Replace($baseComparator, $prComparator, [StringComparison]::Ordinal)
    [IO.File]::WriteAllText($orderingPath, $ordering)
    $unstaged = @(& git -C $demoRepo diff --name-only)
    Assert-True ($unstaged.Count -eq 1 -and $unstaged[0] -ceq $changedFile) `
        "Mutation changed files other than $changedFile`: $($unstaged -join ', ')"
    & git -C $demoRepo add -- $changedFile
    & git -C $demoRepo commit --quiet -m 'pr: make priority ties deterministic by code'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit Node sort demo PR mutation' }
    $prSha = (& git -C $demoRepo rev-parse HEAD).Trim()
    $changed = @(& git -C $demoRepo diff --name-only $baseSha $prSha)
    Assert-True ($changed.Count -eq 1 -and $changed[0] -ceq $changedFile) `
        "Expected exactly one committed changed file, got $($changed -join ', ')"
    $patchPath = Join-Path $work 'sort.patch'
    @(& git -C $demoRepo diff --no-color $baseSha $prSha -- $changedFile) | Set-Content $patchPath

    Write-Host '=== Node tracer install and CLI build ===' -ForegroundColor Cyan
    $tracer = Join-Path $repo 'src/BehaviorDiff.Node'
    Invoke-Npm $tracer @('ci', '--no-audit', '--no-fund') 'Node tracer install failed'
    $cliProject = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
    & dotnet build $cliProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed: $LASTEXITCODE" }
    $cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'
    Assert-True (Test-Path $cli) "Built CLI not found at $cli"

    Write-Host '=== Real CLI base/PR analysis ===' -ForegroundColor Cyan
    $oldTracer = [Environment]::GetEnvironmentVariable('BEHAVIORDIFF_NODE_TRACER', 'Process')
    $oldExcludes = [Environment]::GetEnvironmentVariable('BEHAVIORDIFF_EXCLUDE_NAMESPACES', 'Process')
    try {
        $env:BEHAVIORDIFF_NODE_TRACER = $tracer
        $env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = $changedFile
        $cliOutput = @(& dotnet $cli $demoRepo --base $baseSha --pr $prSha `
            --work $cliWork --findings $findingsPath --keep --keep-traces 1d 2>&1)
        $cliExit = $LASTEXITCODE
        $cliOutput | ForEach-Object { Write-Host $_ }
    } finally {
        [Environment]::SetEnvironmentVariable('BEHAVIORDIFF_NODE_TRACER', $oldTracer, 'Process')
        [Environment]::SetEnvironmentVariable('BEHAVIORDIFF_EXCLUDE_NAMESPACES', $oldExcludes, 'Process')
    }
    Assert-True ($cliExit -eq 1) "Expected analyzed findings exit 1, got $cliExit"
    Assert-True (Test-Path $findingsPath) 'CLI did not write findings.json'
    Assert-True (($cliOutput -join "`n") -match '(?m)^  path scope : src$') `
        'CLI did not derive the Node trace scope from src'

    $divergencesPath = Join-Path $cliWork 'divergence-set.json'
    $frontierPath = Join-Path $cliWork 'frontier-report.json'
    $divergences = Get-Content $divergencesPath -Raw | ConvertFrom-Json
    $frontier = Get-Content $frontierPath -Raw | ConvertFrom-Json
    $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json

    $changedInputs = @($frontier.attributionInputs.changedFiles)
    Assert-True ($changedInputs.Count -eq 1 -and $changedInputs[0] -ceq $changedFile) `
        "CLI attribution did not retain exactly $changedFile"
    $coverage = @($findings.coverage.files | Where-Object filePath -CEQ $changedFile)
    Assert-True ($coverage.Count -eq 1 -and -not [bool]$coverage[0].exercised `
        -and $coverage[0].tracedMembers -eq 0 -and $coverage[0].observedCallSites -eq 0 `
        -and $coverage[0].totalCallCount -eq 0) `
        "Edited helper coverage was not zero: $($coverage | ConvertTo-Json -Compress)"
    $editedDivergences = @($divergences.divergences | Where-Object filePath -CEQ $changedFile)
    Assert-True ($editedDivergences.Count -eq 0) `
        "Edited helper emitted $($editedDivergences.Count) divergence(s)"

    $traceFiles = @(Get-ChildItem $cliWork -Recurse -Filter 'run.*.ndjson' |
        Where-Object FullName -NotMatch '\.manifest\.ndjson$' | Select-Object -ExpandProperty FullName)
    $manifestFiles = @(Get-ChildItem $cliWork -Recurse -Filter 'run.*.manifest.ndjson' |
        Select-Object -ExpandProperty FullName)
    $events = Read-Ndjson $traceFiles
    $manifest = Read-Ndjson $manifestFiles
    $editedEvents = @($events | Where-Object { [string]$_.methodFullName -like "$sortingPrefix*" })
    $skippedMembers = @($manifest | Where-Object {
        $_.kind -eq 'member' -and [string]$_.method -like "$sortingPrefix*"
    })
    $sortingModules = @($manifest | Where-Object {
        $_.kind -eq 'assembly' -and $_.assembly -ceq $changedFile
    })
    Assert-True ($editedEvents.Count -eq 0) 'Excluded rule-ordering.js emitted trace events'
    Assert-True ($skippedMembers.Count -eq 8 `
        -and @($skippedMembers | Where-Object {
            $_.status -ne 'Skipped' -or $_.skipReason -ne 'ExcludedByScope' `
                -or $_.detail -ne 'Node: ExcludedByScope'
        }).Count -eq 0) 'rule-ordering.js manifest members were not all ExcludedByScope'
    Assert-True ($sortingModules.Count -eq 4 `
        -and @($sortingModules | Where-Object { $_.tracedCalls -ne 0 -or $_.patchedMembers -ne 0 }).Count -eq 0) `
        'Excluded rule-ordering.js module accounting was not zero-call/skipped in all four runs'

    Assert-True ($divergences.counts.matchedKeys -eq 129) `
        "Expected exactly 129 matched keys, got $($divergences.counts.matchedKeys)"
    Assert-True ($divergences.counts.noiseExcludedKeys -eq 0 `
        -and @($divergences.noiseExclusions).Count -eq 0) 'Base runs were not deterministic'
    Assert-True ($frontier.counts.toolingGaps -eq 0 `
        -and $frontier.counts.manifestNoiseCancelled -eq 0 `
        -and @($divergences.toolingGaps).Count -eq 0 `
        -and @($divergences.manifestNoise).Count -eq 0) 'Manifest noise or tooling gaps were reported'
    Assert-True ($frontier.counts.expected -eq 0 -and $frontier.counts.unexpected -eq 3) `
        "Expected 0 expected / 3 unexpected call sites: $($frontier.counts | ConvertTo-Json -Compress)"
    $collapse = [double]$frontier.counts.divergedKeys / [double]$frontier.counts.frontierNodes
    Assert-True ($frontier.counts.divergedKeys -eq 117 -and $frontier.counts.frontierNodes -eq 3 `
        -and $collapse -eq 39.0) `
        "Expected exactly 117 / 3 / 39.0x diverged/frontier/collapse, got $($frontier.counts.divergedKeys) / $($frontier.counts.frontierNodes) / $collapse"

    $unexpectedNodes = @($frontier.frontier | Where-Object attribution -eq 'UNEXPECTED')
    $headlineNodes = @($unexpectedNodes | Where-Object methodFullName -CEQ $headline)
    Assert-True ($headlineNodes.Count -eq 3) `
        "Expected three unedited DiscountEngine.selectDiscount frontier call sites, got $($headlineNodes.Count)"
    Assert-True (@($headlineNodes | Where-Object untested -eq $true).Count -eq 2) `
        'Expected exactly two untested DiscountEngine call sites'
    Assert-True (@($headlineNodes | Where-Object filePath -CEQ $discountFile).Count -eq 3) `
        'Unexpected selection frontier was not attributed to unedited discount-engine.js'
    $totalsCollateral = @($frontier.collateral | Where-Object {
        $_.attribution -eq 'UNEXPECTED' -and $_.methodFullName -ceq $totalsMember `
            -and $_.filePath -ceq $totalsFile
    })
    Assert-True ($totalsCollateral.Count -eq 3) `
        'Unexpected consequences did not retain three unedited checkout-totals.js collateral nodes'

    $finding = @($findings.members | Where-Object memberName -CEQ $headline)
    Assert-True ($finding.Count -eq 1 -and $finding[0].callSiteCount -eq 3 `
        -and $finding[0].untestedCallSiteCount -eq 2 -and $finding[0].distinctTestCount -eq 3 `
        -and $finding[0].testsWithAssertionReaction -eq 1 `
        -and $finding[0].assertionReactionSummary -eq '3 tests executed this; 1 test had an assertion react.') `
        'Canonical findings lost the 2-of-3 assertion gap'

    $selectionEvidence = @($finding[0].evidence | Where-Object {
        $_.baseReturn -eq 'string:"Z_CLEARANCE"' -and $_.prReturn -eq 'string:"A_SEASONAL"'
    })
    Assert-True ($selectionEvidence.Count -eq 3) `
        "Expected the selection swap in all three tests, got $($selectionEvidence.Count)"
    Assert-True (@($selectionEvidence | Where-Object {
        @($_.baseCallPath | Where-Object memberName -CEQ $totalsMember).Count -eq 1 `
            -and @($_.prCallPath | Where-Object memberName -CEQ $totalsMember).Count -eq 1
    }).Count -eq 3) 'Selection evidence did not retain checkout-totals.js in all base/PR call paths'
    Assert-True (@($selectionEvidence | Where-Object assertionReacted -eq $true).Count -eq 1 `
        -and @($selectionEvidence | Where-Object assertionReacted -eq $false).Count -eq 2) `
        'Expected one reacting assertion and two non-reacting assertions'
    $totalsDivergences = @($divergences.divergences | Where-Object {
        $_.methodFullName -ceq $totalsMember -and $_.baseReturnRendered -eq 'number:60' `
            -and $_.prReturnRendered -eq 'number:85'
    })
    Assert-True ($totalsDivergences.Count -eq 3) `
        "CheckoutTotals.compute did not retain three 60 -> 85 consequences"

    Write-Host '=== Fresh PR determinism ===' -ForegroundColor Cyan
    $repeatMessages = @()
    foreach ($repeat in 1..5) {
        $repeatReport = Join-Path $work "repeat-$repeat-report.json"
        $oldReport = [Environment]::GetEnvironmentVariable('BEHAVIORDIFF_RUNNER_REPORT', 'Process')
        try {
            $env:BEHAVIORDIFF_RUNNER_REPORT = $repeatReport
            Push-Location $demoRepo
            try {
                $repeatOutput = @(& node test/run.cjs 2>&1)
                $repeatExit = $LASTEXITCODE
            } finally { Pop-Location }
        } finally {
            [Environment]::SetEnvironmentVariable('BEHAVIORDIFF_RUNNER_REPORT', $oldReport, 'Process')
        }
        $repeatText = $repeatOutput -join "`n"
        Assert-True ($repeatExit -ne 0 -and $repeatText -match 'Z_CLEARANCE' `
            -and $repeatText -match 'A_SEASONAL' -and -not (Test-Path $repeatReport)) `
            "Fresh PR Node process $repeat did not fail before the success report with Z_CLEARANCE/A_SEASONAL"
        $message = @($repeatOutput | Where-Object { $_ -match "^\+ 'A_SEASONAL'$|^- 'Z_CLEARANCE'$" })
        Assert-True ($message.Count -ge 2) "Fresh PR Node process $repeat did not print the expected assertion details"
        $repeatMessages += ($message -join ' | ')
        Write-Host "  repeat ${repeat}: $($message -join ' | ')"
    }
    Assert-True (@($repeatMessages | Select-Object -Unique).Count -eq 1) `
        'Fresh PR Node assertion messages were not identical'

    Write-Host '=== Production deterministic GitHub comment ===' -ForegroundColor Cyan
    $commentPath = Join-Path $cliWork 'comment.md'
    $commentOutput = @(& dotnet run --project (Join-Path $repo 'tools/CommentPreview/BehaviorDiff.CommentPreview.csproj') `
        -c Release -- $findingsPath)
    if ($LASTEXITCODE -ne 0) { throw "CommentPreview failed: $LASTEXITCODE" }
    $commentText = $commentOutput -join "`n"
    $commentText | Set-Content $commentPath
    Assert-True ($commentText -match 'src/pricing/checkout-totals\.js#CheckoutTotals\.compute' `
        -and $commentText -match 'DiscountEngine\.selectDiscount' `
        -and $commentText -match 'Z_CLEARANCE' -and $commentText -match 'A_SEASONAL' `
        -and $commentText -match 'CheckoutTotals\.compute' `
        -and $commentText -match 'number:60' -and $commentText -match 'number:85' `
        -and $commentText -match '2 of the 3 tests that executed this did not assert on the change' `
        -and $commentText -notmatch 'SampleApp|Commerce\.Pricing|Infrastructure\.Collections|io\.behaviordiff|\.java') `
        'Rendered Node comment lost required evidence or contains Java/.NET demo contamination'
    Write-Host '--- complete rendered comment ---'
    Write-Host $commentText
    Write-Host '--- end rendered comment ---'

    $apiKey = Get-ApiKey
    if (-not [string]::IsNullOrWhiteSpace($apiKey)) {
        Write-Host '=== Optional model explainer ===' -ForegroundColor Cyan
        $modelOutputPath = Join-Path $cliWork 'model-explainer.txt'
        $oldKey = [Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY', 'Process')
        try {
            $env:ANTHROPIC_API_KEY = $apiKey
            $modelOutput = @(& dotnet run --project (Join-Path $repo 'tools/AnthropicLive/BehaviorDiff.AnthropicLive.csproj') `
                -c Release -- $findingsPath $changedFile $patchPath 2>&1)
            $modelExit = $LASTEXITCODE
            $modelOutput | Set-Content $modelOutputPath
            if ($modelExit -eq 0) {
                $modelExplainer = "ran successfully ($modelOutputPath)"
            } else {
                $modelExplainer = "attempted but unavailable/invalid (exit $modelExit; $modelOutputPath)"
            }
        } catch {
            $modelExplainer = "attempted but unavailable ($($_.Exception.Message))"
        } finally {
            [Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY', $oldKey, 'Process')
            $apiKey = $null
        }
    }

    Write-Host '=== Node sort demo proof ===' -ForegroundColor Green
    Write-Host "  standalone base tests     : $baseTests/3 passed"
    Write-Host "  git changed files         : $($changed.Count) ($($changed -join ', '))"
    Write-Host "  derived Node scope        : src"
    Write-Host "  edited traced/calls/events: $($coverage[0].tracedMembers) / $($coverage[0].totalCallCount) / $($editedEvents.Count)"
    Write-Host "  edited skipped modules    : $($skippedMembers.Count) / $($sortingModules.Count)"
    Write-Host "  matched keys              : $($divergences.counts.matchedKeys)"
    Write-Host "  diverged/frontier/collapse: $($frontier.counts.divergedKeys) / $($frontier.counts.frontierNodes) / $($collapse.ToString('F1'))x"
    Write-Host "  expected/unexpected       : $($frontier.counts.expected) / $($frontier.counts.unexpected)"
    Write-Host "  headline call sites       : $($headlineNodes.Count) (untested=$(@($headlineNodes | Where-Object untested -eq $true).Count))"
    Write-Host "  selection/total changes   : $($selectionEvidence.Count) / $($totalsDivergences.Count)"
    Write-Host "  test reactions            : 1 reacted / 2 unasserted"
    Write-Host "  deterministic PR repeats  : $($repeatMessages.Count)/5 identical failures"
    Write-Host "  manifest noise/tool gaps  : $($frontier.counts.manifestNoiseCancelled) / $($frontier.counts.toolingGaps)"
    Write-Host "  rendered comment          : $commentPath"
    Write-Host "  model explainer           : $modelExplainer"
    Write-Host 'verify-node-sort-demo: PASS' -ForegroundColor Green
} finally {
    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Node sort demo work retained at $work"
    }
}