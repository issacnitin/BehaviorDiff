#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-java-sort-{0}" -f [Guid]::NewGuid().ToString('N'))
} else {
    [IO.Path]::GetFullPath($WorkDirectory)
}
$demoRepo = Join-Path $work 'repo'
$cliWork = Join-Path $work 'cli-work'
$findingsPath = Join-Path $cliWork 'findings.json'
$changedFile = 'src/main/java/io/behaviordiff/demo/sorting/RuleOrdering.java'
$headline = 'io.behaviordiff.demo.pricing.DiscountEngine.selectDiscount(D)Ljava/lang/String;'
$totalsMember = 'io.behaviordiff.demo.pricing.CheckoutTotals.compute(D)D'
$sortingPrefix = 'io.behaviordiff.demo.sorting.RuleOrdering.'
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

function Invoke-Maven([string[]]$arguments, [string]$failure) {
    & mvn @arguments | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$failure (exit $LASTEXITCODE)" }
}

function Read-TestTotals([string]$reportDirectory) {
    $reports = @(Get-ChildItem $reportDirectory -Filter 'TEST-*.xml')
    Assert-True ($reports.Count -gt 0) "No Surefire XML reports found under $reportDirectory"
    $tests = 0
    $failures = 0
    $errors = 0
    $skipped = 0
    foreach ($report in $reports) {
        [xml]$xml = Get-Content $report.FullName -Raw
        $tests += [int]$xml.testsuite.tests
        $failures += [int]$xml.testsuite.failures
        $errors += [int]$xml.testsuite.errors
        $skipped += [int]$xml.testsuite.skipped
    }
    return [pscustomobject]@{
        Tests = $tests
        Failures = $failures
        Errors = $errors
        Skipped = $skipped
    }
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
    Copy-Item (Join-Path $repo 'samples/JavaSortDemo/*') $demoRepo -Recurse -Force

    Write-Host '=== Standalone base fixture ===' -ForegroundColor Cyan
    Invoke-Maven @(
        '-f', (Join-Path $demoRepo 'pom.xml'), 'clean', 'test',
        '--batch-mode', '--no-transfer-progress'
    ) 'Standalone Java sort demo failed'
    $baseTests = Read-TestTotals (Join-Path $demoRepo 'target/surefire-reports')
    Assert-True ($baseTests.Tests -eq 3 -and $baseTests.Failures -eq 0 `
        -and $baseTests.Errors -eq 0 -and $baseTests.Skipped -eq 0) `
        "Expected standalone base tests 3/3, got $($baseTests | ConvertTo-Json -Compress)"

    Write-Host '=== Temporary git history ===' -ForegroundColor Cyan
    & git -C $demoRepo init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize temporary demo repository' }
    & git -C $demoRepo config user.name 'BehaviorDiff Proof'
    & git -C $demoRepo config user.email 'proof@behaviordiff.invalid'
    & git -C $demoRepo add .
    & git -C $demoRepo commit --quiet -m 'base: stable priority ordering'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit Java sort demo base' }
    $baseSha = (& git -C $demoRepo rev-parse HEAD).Trim()

    $orderingPath = Join-Path $demoRepo $changedFile
    $baseComparator = 'ordered.sort(Comparator.comparingInt(RuleOrdering::priority));'
    $prComparator = 'ordered.sort(Comparator.comparingInt(RuleOrdering::priority).thenComparing(RuleOrdering::code));'
    $ordering = Get-Content $orderingPath -Raw
    Assert-True ($ordering.Contains($baseComparator, [StringComparison]::Ordinal)) `
        'Base comparator line was not found exactly once'
    $ordering = $ordering.Replace($baseComparator, $prComparator, [StringComparison]::Ordinal)
    [IO.File]::WriteAllText($orderingPath, $ordering)
    $unstaged = @(& git -C $demoRepo diff --name-only)
    Assert-True ($unstaged.Count -eq 1 -and $unstaged[0] -ceq $changedFile) `
        "Mutation changed files other than $changedFile`: $($unstaged -join ', ')"
    & git -C $demoRepo add -- $changedFile
    & git -C $demoRepo commit --quiet -m 'pr: make priority ties deterministic by code'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit Java sort demo PR mutation' }
    $prSha = (& git -C $demoRepo rev-parse HEAD).Trim()
    $changed = @(& git -C $demoRepo diff --name-only $baseSha $prSha)
    Assert-True ($changed.Count -eq 1 -and $changed[0] -ceq $changedFile) `
        "Expected exactly one committed changed file, got $($changed -join ', ')"
    $patchPath = Join-Path $work 'sort.patch'
    @(& git -C $demoRepo diff --no-color $baseSha $prSha -- $changedFile) | Set-Content $patchPath

    Write-Host '=== Java agent and CLI builds ===' -ForegroundColor Cyan
    Invoke-Maven @(
        '-f', (Join-Path $repo 'src/BehaviorDiff.Java.Agent/pom.xml'), 'clean', 'package',
        '--batch-mode', '--no-transfer-progress'
    ) 'Java agent build failed'
    $agent = Join-Path $repo 'src/BehaviorDiff.Java.Agent/target/behaviordiff-java-agent-0.2.0-SNAPSHOT.jar'
    Assert-True (Test-Path $agent) "Built Java agent not found at $agent"
    $cliProject = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
    & dotnet build $cliProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed: $LASTEXITCODE" }
    $cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'
    Assert-True (Test-Path $cli) "Built CLI not found at $cli"

    Write-Host '=== Real CLI base/PR analysis ===' -ForegroundColor Cyan
    $oldAgent = [Environment]::GetEnvironmentVariable('BEHAVIORDIFF_JAVA_AGENT', 'Process')
    $oldExcludes = [Environment]::GetEnvironmentVariable('BEHAVIORDIFF_EXCLUDE_NAMESPACES', 'Process')
    try {
        $env:BEHAVIORDIFF_JAVA_AGENT = $agent
        $env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = 'io.behaviordiff.demo.sorting'
        $cliOutput = @(& dotnet $cli $demoRepo --base $baseSha --pr $prSha `
            --work $cliWork --findings $findingsPath --keep --keep-traces 1d 2>&1)
        $cliExit = $LASTEXITCODE
        $cliOutput | ForEach-Object { Write-Host $_ }
    } finally {
        [Environment]::SetEnvironmentVariable('BEHAVIORDIFF_JAVA_AGENT', $oldAgent, 'Process')
        [Environment]::SetEnvironmentVariable('BEHAVIORDIFF_EXCLUDE_NAMESPACES', $oldExcludes, 'Process')
    }
    Assert-True ($cliExit -eq 1) "Expected analyzed findings exit 1, got $cliExit"
    Assert-True (Test-Path $findingsPath) 'CLI did not write findings.json'

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
    $totalsManifest = @($manifest | Where-Object {
        $_.kind -eq 'member' -and [string]$_.method -ceq $totalsMember -and $_.status -eq 'Patched'
    })
    Assert-True ($editedEvents.Count -eq 0) 'Excluded RuleOrdering emitted trace events'
    Assert-True ($skippedMembers.Count -ge 4 `
        -and @($skippedMembers | Where-Object {
            $_.status -ne 'Skipped' -or $_.skipReason -ne 'ExcludedByScope' `
                -or $_.detail -ne 'Java: ExcludedPackage'
        }).Count -eq 0) 'RuleOrdering manifest members were not all ExcludedByScope'
    Assert-True ($totalsManifest.Count -eq 4) `
        "CheckoutTotals.compute was not patched in all four traced JVM processes"

    Assert-True ($divergences.counts.matchedKeys -gt 100) `
        "Matched key volume did not exceed 100: $($divergences.counts.matchedKeys)"
    Assert-True ($divergences.counts.noiseExcludedKeys -eq 0 `
        -and @($divergences.noiseExclusions).Count -eq 0) 'Base runs were not deterministic'
    Assert-True ($frontier.counts.toolingGaps -eq 0 `
        -and $frontier.counts.manifestNoiseCancelled -eq 0 `
        -and @($divergences.toolingGaps).Count -eq 0 `
        -and @($divergences.manifestNoise).Count -eq 0) 'Manifest noise or tooling gaps were reported'
    Assert-True ($frontier.counts.expected -eq 0 -and $frontier.counts.unexpected -eq 3) `
        "Expected 0 expected / 3 unexpected call sites: $($frontier.counts | ConvertTo-Json -Compress)"
    $collapse = [double]$frontier.counts.divergedKeys / [double]$frontier.counts.frontierNodes
    Assert-True ($collapse -gt 1.0) "Collapse ratio $collapse is not above 1x"

    $headlineNodes = @($frontier.frontier | Where-Object {
        $_.attribution -eq 'UNEXPECTED' -and $_.methodFullName -ceq $headline
    })
    Assert-True ($headlineNodes.Count -eq 3) `
        "Expected three unedited DiscountEngine.selectDiscount frontier call sites, got $($headlineNodes.Count)"
    Assert-True (@($headlineNodes | Where-Object untested -eq $true).Count -eq 2) `
        'Expected exactly two untested DiscountEngine call sites'
    $finding = @($findings.members | Where-Object memberName -CEQ $headline)
    Assert-True ($finding.Count -eq 1 -and $finding[0].callSiteCount -eq 3 `
        -and $finding[0].untestedCallSiteCount -eq 2 -and $finding[0].distinctTestCount -eq 3 `
        -and $finding[0].testsWithAssertionReaction -eq 1 `
        -and $finding[0].assertionReactionSummary -eq '3 tests executed this; 1 test had an assertion react.') `
        'Canonical findings lost the 2-of-3 assertion gap'

    $selectionEvidence = @($finding[0].evidence | Where-Object {
        $_.baseReturn -eq 'String:"Z_CLEARANCE"' -and $_.prReturn -eq 'String:"A_SEASONAL"'
    })
    Assert-True ($selectionEvidence.Count -eq 3) `
        "Expected the selection swap in all three tests, got $($selectionEvidence.Count)"
    Assert-True (@($selectionEvidence | Where-Object assertionReacted -eq $true).Count -eq 1 `
        -and @($selectionEvidence | Where-Object assertionReacted -eq $false).Count -eq 2) `
        'Expected one reacting assertion and two non-reacting assertions'
    $totalsDivergences = @($divergences.divergences | Where-Object {
        $_.methodFullName -ceq $totalsMember -and $_.baseReturnRendered -eq 'Double:60.0' `
            -and $_.prReturnRendered -eq 'Double:85.0'
    })
    Assert-True ($totalsDivergences.Count -eq 3) `
        "CheckoutTotals.compute did not retain three 60 -> 85 consequences"

    Write-Host '=== Fresh PR determinism ===' -ForegroundColor Cyan
    $repeatMessages = @()
    foreach ($repeat in 1..5) {
        $repeatOutput = @(& mvn -f (Join-Path $demoRepo 'pom.xml') `
            '--batch-mode' '--no-transfer-progress' `
            '-Dtest=SortStabilityTests#clearanceDiscountWinsCurrentTies' test 2>&1)
        $repeatExit = $LASTEXITCODE
        $repeatText = $repeatOutput -join "`n"
        Assert-True ($repeatExit -ne 0 -and $repeatText -match 'Z_CLEARANCE' `
            -and $repeatText -match 'A_SEASONAL') `
            "Fresh PR JVM $repeat did not fail with the stable Z_CLEARANCE/A_SEASONAL message"
        $message = @($repeatOutput | Where-Object { $_ -match 'expected:.*Z_CLEARANCE.*but was:.*A_SEASONAL' } |
            Select-Object -First 1)
        Assert-True ($message.Count -eq 1) "Fresh PR JVM $repeat did not print the expected assertion message"
        $repeatMessages += [string]$message[0]
        Write-Host "  repeat ${repeat}: $($message[0])"
    }
    Assert-True (@($repeatMessages | Select-Object -Unique).Count -eq 1) `
        'Fresh PR JVM assertion messages were not identical'

    Write-Host '=== Production deterministic GitHub comment ===' -ForegroundColor Cyan
    $commentPath = Join-Path $cliWork 'comment.md'
    $commentOutput = @(& dotnet run --project (Join-Path $repo 'tools/CommentPreview/BehaviorDiff.CommentPreview.csproj') `
        -c Release -- $findingsPath)
    if ($LASTEXITCODE -ne 0) { throw "CommentPreview failed: $LASTEXITCODE" }
    $commentText = $commentOutput -join "`n"
    $commentText | Set-Content $commentPath
    Assert-True ($commentText -match 'io\.behaviordiff\.demo\.pricing\.CheckoutTotals\.compute\(D\)D' `
        -and $commentText -match 'DiscountEngine\.selectDiscount' `
        -and $commentText -match 'Z_CLEARANCE' -and $commentText -match 'A_SEASONAL' `
        -and $commentText -match 'CheckoutTotals\.compute' `
        -and $commentText -match 'Double:60\.0' -and $commentText -match 'Double:85\.0' `
        -and $commentText -match '2 of the 3 tests that executed this did not assert on the change' `
        -and $commentText -notmatch 'SampleApp|Commerce\.Pricing|Infrastructure\.Collections') `
        'Rendered Java comment lost required evidence or contains .NET demo contamination'
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

    Write-Host '=== Java sort demo proof ===' -ForegroundColor Green
    Write-Host "  standalone base tests     : $($baseTests.Tests)/3 passed"
    Write-Host "  git changed files         : $($changed.Count) ($($changed -join ', '))"
    Write-Host "  edited traced/calls       : $($coverage[0].tracedMembers) / $($coverage[0].totalCallCount)"
    Write-Host "  edited skipped/events     : $($skippedMembers.Count) / $($editedEvents.Count)"
    Write-Host "  matched keys              : $($divergences.counts.matchedKeys)"
    Write-Host "  diverged/frontier/collapse: $($frontier.counts.divergedKeys) / $($frontier.counts.frontierNodes) / $($collapse.ToString('F1'))x"
    Write-Host "  expected/unexpected       : $($frontier.counts.expected) / $($frontier.counts.unexpected)"
    Write-Host "  headline call sites       : $($headlineNodes.Count) (untested=$(@($headlineNodes | Where-Object untested -eq $true).Count))"
    Write-Host "  test reactions            : 1 reacted / 2 unasserted"
    Write-Host "  deterministic PR repeats  : $($repeatMessages.Count)/5 identical failures"
    Write-Host "  manifest noise/tool gaps  : $($frontier.counts.manifestNoiseCancelled) / $($frontier.counts.toolingGaps)"
    Write-Host "  rendered comment          : $commentPath"
    Write-Host "  model explainer           : $modelExplainer"
    Write-Host 'verify-java-sort-demo: PASS' -ForegroundColor Green
} finally {
    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Java sort demo work retained at $work"
    }
}