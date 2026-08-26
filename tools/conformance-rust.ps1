#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'realdiff-rust-conformance'),
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$work = [IO.Path]::GetFullPath($WorkDirectory)
$source = Join-Path $repo 'samples/RustReference'
$tracerSource = Join-Path $repo 'src/RealDiff.Rust.Tracer'
Import-Module (Join-Path $PSScriptRoot 'RealDiff.Conformance.psm1') -Force

function Copy-CleanTree([string]$From, [string]$To) {
    New-Item $To -ItemType Directory -Force | Out-Null
    Get-ChildItem $From -Force | Where-Object Name -notin @('target', '.git') | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $To $_.Name) -Recurse -Force
    }
}

function Get-SourceHashes([string]$Root) {
    $files = @(Get-ChildItem $Root -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/]target[\\/]' -and $_.FullName -notmatch '[\\/]\.git[\\/]'
    } | Sort-Object FullName)
    if ($files.Count -le 0) { throw "Rust conformance source hash input is empty: $Root" }
    @($files | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
            Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
    })
}

function Get-MethodEvents([object]$Run, [string]$Method) {
    @($Run.Events | Where-Object { $_.methodFullName -like "*$Method*" -and $_.isHarness -ne $true })
}

function New-Proof([string]$Name, [bool]$Passed, [string]$Detail) {
    [pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

$digestEvaluator = {
    param($Run)
    $noUser = @(Get-MethodEvents $Run 'proof_no_user_code')
    $cycle = @(Get-MethodEvents $Run 'proof_cycle')
    $topology = @(Get-MethodEvents $Run 'proof_topology')
    $unordered = @(Get-MethodEvents $Run 'proof_unordered')
    $time = @(Get-MethodEvents $Run 'proof_time')
    $blocklist = @(Get-MethodEvents $Run 'proof_blocklist')
    $depth = @(Get-MethodEvents $Run 'proof_depth')
    $truncation = @(Get-MethodEvents $Run 'proof_truncation')
    $unreadable = @(Get-MethodEvents $Run 'proof_unreadable')
    $beyond = @(Get-MethodEvents $Run 'proof_beyond_cap')

    New-Proof 'NoUserCodeInvoked' ($noUser.Count -eq 1 -and $noUser[0].argsPartial -ne $true) "events=$($noUser.Count)"
    New-Proof 'CyclesTerminate' ($cycle.Count -eq 1 -and $cycle[0].argsRendered -match 'ref:') "events=$($cycle.Count)"
    New-Proof 'ReferenceTopology' ($topology.Count -eq 2 -and $topology[0].argsDigest -cne $topology[1].argsDigest) "events=$($topology.Count)"
    New-Proof 'UnorderedCollectionsStable' ($unordered.Count -eq 2 -and $unordered[0].argsDigest -ceq $unordered[1].argsDigest) "events=$($unordered.Count)"
    New-Proof 'TimeAndIdentityNormalized' ($time.Count -eq 2 -and $time[0].argsDigest -ceq $time[1].argsDigest) "events=$($time.Count)"
    New-Proof 'BlocklistBeforeRecursion' ($blocklist.Count -eq 1 -and $blocklist[0].argsRendered -match '<skipped:' -and [int]$blocklist[0].argsBlocklisted -gt 0) "events=$($blocklist.Count)"
    New-Proof 'DepthMarker' ($depth.Count -eq 1 -and $depth[0].argsRendered -match '<depth:' -and [int]$depth[0].argsDepthLimited -gt 0) "events=$($depth.Count)"
    New-Proof 'TruncationMarker' ($truncation.Count -eq 1 -and $truncation[0].argsRendered -match '<truncated>' -and [int]$truncation[0].argsRenderedTruncated -gt 0) "events=$($truncation.Count)"
    New-Proof 'UnreadableFieldMarker' ($unreadable.Count -eq 1 -and $unreadable[0].argsRendered -match '<skipped:.*union' -and [int]$unreadable[0].argsBlocklisted -gt 0) "events=$($unreadable.Count)"
    New-Proof 'BeyondRenderedCap' ($beyond.Count -eq 2 -and $beyond[0].argsRendered -ceq $beyond[1].argsRendered -and $beyond[0].argsDigest -cne $beyond[1].argsDigest) "events=$($beyond.Count)"
}

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $work -ItemType Directory -Force | Out-Null
$before = @(Get-SourceHashes $source)
$runnerCounts = @()
$rewriteReports = @()
$finalizeReports = @()

try {
    for ($index = 1; $index -le 2; $index++) {
        $tracer = Join-Path $work "tracer-$index"
        $cache = Join-Path $work "cache-$index"
        $run = Join-Path $work "run-$index"
        New-Item $run -ItemType Directory -Force | Out-Null
        Copy-CleanTree $tracerSource $tracer
        & cargo build --release --manifest-path (Join-Path $tracer 'Cargo.toml')
        if ($LASTEXITCODE -ne 0) { throw "Rust tracer build $index failed: $LASTEXITCODE" }
        $binary = Join-Path $tracer 'target/release/realdiff-rust-rewrite.exe'
        if (-not $IsWindows) { $binary = $binary.Substring(0, $binary.Length - 4) }
        $rewrite = (& $binary --source $source --cache-root $cache | ConvertFrom-Json)
        if ($rewrite.sourceFiles -le 0 -or $rewrite.rustFiles -le 0) {
            throw "Rust conformance rewrite $index is empty: source=$($rewrite.sourceFiles) rust=$($rewrite.rustFiles)"
        }
        $trace = Join-Path $run 'run.rust.ndjson'
        $manifest = Join-Path $run 'run.rust.manifest.ndjson'
        $env:REALDIFF_RUST_EXIT_TRACE = $trace
        try {
            $runner = @(& cargo test --quiet --manifest-path (Join-Path $rewrite.output 'Cargo.toml') --lib -- --test-threads=1 2>&1)
            $runnerExit = $LASTEXITCODE
        } finally {
            Remove-Item Env:REALDIFF_RUST_EXIT_TRACE -ErrorAction SilentlyContinue
        }
        $runner | ForEach-Object { Write-Host $_ }
        if ($runnerExit -ne 0) { throw "Rust conformance tests $index failed: $runnerExit" }
        $line = @($runner | Where-Object { $_ -match '^test result: ok\. ([0-9]+) passed;' })
        if ($line.Count -ne 1) { throw "Rust runner $index count is not unique from $($runner.Count) lines" }
        $null = $line[0] -match '^test result: ok\. ([0-9]+) passed;'
        $runnerCount = [int]$Matches[1]
        if ($runnerCount -le 0) { throw "Rust runner $index test input is empty" }
        $finalize = (& $binary finalize --origin (Join-Path $rewrite.output '.realdiff-rust-origin.json') --trace $trace --out $manifest | ConvertFrom-Json)
        if ($finalize.events -le 0 -or $finalize.discoveredMembers -le 0) {
            throw "Rust finalization $index is empty: events=$($finalize.events) members=$($finalize.discoveredMembers)"
        }
        $runnerCounts += $runnerCount
        $rewriteReports += $rewrite
        $finalizeReports += $finalize
    }

    $after = @(Get-SourceHashes $source)
    if (($before | ConvertTo-Json -Compress) -cne ($after | ConvertTo-Json -Compress)) {
        throw "Rust source hashes changed across $($before.Count) non-empty files"
    }

    $metrics = Assert-RealDiffConformanceRuns `
        -FirstRun (Join-Path $work 'run-1') `
        -SecondRun (Join-Path $work 'run-2') `
        -MinimumMatchedKeys 300 `
        -UsableSourceResolutions @('debugInfo') `
        -ReferenceSourcePathPatterns @('^src/.+\.rs$') `
        -DigestProofEvaluator $digestEvaluator

    $firstRun = Read-RealDiffConformanceRun (Join-Path $work 'run-1')
    $secondRun = Read-RealDiffConformanceRun (Join-Path $work 'run-2')
    $firstRoots = @($firstRun.Events | Where-Object { $_.isHarness -eq $true -and [int]$_.callDepth -eq 0 })
    $secondRoots = @($secondRun.Events | Where-Object { $_.isHarness -eq $true -and [int]$_.callDepth -eq 0 })
    if ($firstRoots.Count -ne $runnerCounts[0] -or $secondRoots.Count -ne $runnerCounts[1]) {
        throw "Rust runner/derived counts differ: runner=$($runnerCounts -join '/') derived=$($firstRoots.Count)/$($secondRoots.Count)"
    }
    $shapeEvents = @($firstRun.Events | Where-Object {
        $_.methodFullName -match 'private_shape|generic_shape|trait_object_shape|Score for PrivatePoint::score|async_shape|pending_shape|question_shape|panic_shape'
    })
    $shapeMethods = @($shapeEvents | ForEach-Object methodFullName | Sort-Object -Unique)
    $genericShapes = @($shapeEvents | Where-Object methodFullName -like '*generic_shape<*' | ForEach-Object methodFullName | Sort-Object -Unique)
    $panicShapes = @($shapeEvents | Where-Object { $_.methodFullName -like '*panic_shape' -and $_.outcome -ceq 'panic' })
    $cancelShapes = @($shapeEvents | Where-Object { $_.methodFullName -like '*pending_shape' -and $_.outcome -ceq 'cancelled' })
    $manifestObjects = @(Get-Content (Join-Path $work 'run-1/run.rust.manifest.ndjson') | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
    $macroBoundaries = @($manifestObjects | Where-Object { $_.kind -ceq 'member' -and $_.detail -ceq 'Rust: MacroExpansionUnavailable' })
    if ($shapeEvents.Count -le 0 -or $shapeMethods.Count -lt 8 -or $genericShapes.Count -ne 2 -or
        $panicShapes.Count -ne 1 -or $cancelShapes.Count -ne 1 -or $macroBoundaries.Count -le 0) {
        throw "Rust completion shape coverage differs: events=$($shapeEvents.Count) methods=$($shapeMethods.Count) generics=$($genericShapes.Count) panic=$($panicShapes.Count) cancel=$($cancelShapes.Count) macros=$($macroBoundaries.Count)"
    }

    $engineMetrics = Invoke-RealDiffEngineConformance `
        -FirstRun (Join-Path $work 'run-1') `
        -SecondRun (Join-Path $work 'run-2') `
        -BaseRoot $source `
        -PrRoot $source
    if ($engineMetrics.MatchedKeys -lt 300) {
        throw "Rust engine conformance matched input below floor: $($engineMetrics.MatchedKeys)"
    }

    $manifestRecords = @(Get-Content (Join-Path $work 'run-1/run.rust.manifest.ndjson') | Where-Object { $_.Trim().Length -gt 0 })
    [pscustomobject]@{
        SourceFiles = $rewriteReports[0].sourceFiles
        RustFiles = $rewriteReports[0].rustFiles
        RunnerTests = "$($runnerCounts[0])/$($runnerCounts[1])"
        DerivedTests = "$($firstRoots.Count)/$($secondRoots.Count)"
        TraceEvents = "$($firstRun.Events.Count)/$($secondRun.Events.Count)"
        ManifestRecords = $manifestRecords.Count
        MatchedKeys = $metrics.MatchedKeys
        SubjectMethods = $metrics.SubjectMethods
        SubjectEvents = "$($metrics.FirstSubjectEvents)/$($metrics.SecondSubjectEvents)"
        GuardFailures = 0
        UnusableSourceEvents = $metrics.UnusableSourceEvents
        SubjectRoots = $metrics.SubjectRoots
        WrongSourceEvents = $metrics.WrongSourceEvents
        DigestProofs = "$($metrics.DigestProofsPerRun)/$($metrics.DigestProofsPerRun)"
        EngineMatchedKeys = $engineMetrics.MatchedKeys
        EngineRawDifferences = $engineMetrics.RawDifferences
        EngineRemainingDivergences = $engineMetrics.RemainingDivergences
        SourceHashChanges = 0
        ValuesDigested = "$($finalizeReports[0].valuesDigested)/$($finalizeReports[1].valuesDigested)"
        Blocklisted = "$($finalizeReports[0].blocklisted)/$($finalizeReports[1].blocklisted)"
        CompletionShapeEvents = $shapeEvents.Count
        CompletionShapeMethods = $shapeMethods.Count
        GenericShapeIdentities = $genericShapes.Count
        PanicShapeEvents = $panicShapes.Count
        CancellationShapeEvents = $cancelShapes.Count
        MacroUnsupportedBoundaries = $macroBoundaries.Count
    } | Format-List
    Write-Host 'RUST_CONFORMANCE: PASS'
} finally {
    Remove-Item Env:REALDIFF_RUST_EXIT_TRACE -ErrorAction SilentlyContinue
    if (-not $KeepWork -and (Test-Path $work)) { Remove-Item $work -Recurse -Force }
}
