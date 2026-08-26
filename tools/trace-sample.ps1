#requires -Version 7.0
<#
    Traces SampleApp through a real `dotnet test` run and proves the coverage manifest:
    assembly provenance, per-member status, and that the counts reconcile.

    Run from anywhere:  pwsh -NoProfile -File tools/trace-sample.ps1
#>
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$work = Join-Path ([System.IO.Path]::GetTempPath()) 'realdiff-sample'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work | Out-Null
$base = Join-Path $work 'run.ndjson'

# Must be set before the process starts: the runtime reads it during startup.
$env:REALDIFF_TRACE = $base
$env:REALDIFF_NAMESPACES = 'SampleApp'
$env:REALDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics'
$env:REALDIFF_VERBOSE = '1'
$env:REALDIFF_BACKEND = 'cecil'

Write-Host ''
Write-Host '=== build ===' -ForegroundColor Cyan
dotnet build RealDiff.sln -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
dotnet build tools/Weaver/Weaver.csproj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'weaver build failed' }

Write-Host ''
Write-Host '=== dotnet test (traced) ===' -ForegroundColor Cyan
$staged = Join-Path $work 'bin'
& (Join-Path $PSScriptRoot 'Stage-WovenSample.ps1') -TreeRoot (Split-Path -Parent $PSScriptRoot) -OutDir $staged
dotnet test (Join-Path $staged 'SampleApp.Tests.dll') --nologo
Write-Host "test exit code: $LASTEXITCODE"

# One trace file per process, process id folded into the name.
$traceFile = Get-ChildItem $work -Filter 'run.*.ndjson' |
    Where-Object { $_.Name -notlike '*.manifest.*' } | Select-Object -First 1
$manifestFile = Get-ChildItem $work -Filter 'run.*.manifest.ndjson' | Select-Object -First 1

if (-not $traceFile) { throw 'no trace file produced' }
if (-not $manifestFile) { throw 'no manifest produced' }

Write-Host ''
Write-Host "trace    : $($traceFile.Name)"
Write-Host "manifest : $($manifestFile.Name)"

$records = Get-Content $manifestFile.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$runMetadata = $records | Where-Object { $_.kind -eq 'run' }
$assemblies = $records | Where-Object { $_.kind -eq 'assembly' }
$members = $records | Where-Object { $_.kind -eq 'member' }

if ($runMetadata.Count -ne 1 `
    -or $runMetadata.schema -ne 'realdiff.trace/1' `
    -or $runMetadata.language -ne 'dotnet') {
    throw 'run metadata is missing, duplicated, or invalid'
}

Write-Host ''
Write-Host '=== assembly provenance ===' -ForegroundColor Cyan
$assemblies | Where-Object { $_.scanned } | ForEach-Object {
    [pscustomobject]@{
        Assembly     = $_.assembly
        Scanned      = [bool]$_.scanned
        Instrumented = [bool]$_.instrumented
        Patched      = $_.patchedMembers
        Discovery    = $_.discovery
        QueuedMs     = $_.queuedAtMs
        PatchedMs    = $_.patchedAtMs
        Late         = [bool]$_.latePatched
        TracedCalls  = $_.tracedCalls
        PrePatch     = if ($_.prePatchCoverage) { $_.prePatchCoverage } else { '-' }
    }
} | Format-Table -AutoSize

$scannedCount = ($assemblies | Where-Object { $_.scanned }).Count
$instrumentedCount = ($assemblies | Where-Object { $_.instrumented }).Count
Write-Host "considered: $($assemblies.Count)   scanned (types enumerated): $scannedCount   instrumented (>=1 member patched): $instrumentedCount"
Write-Host "harmony reachable: $(($assemblies | Where-Object { $_.scanned -and $_.assembly -match 'Harmony|MonoMod' }).Count)  <- must be 0"

Write-Host ''
Write-Host '=== SampleApp member manifest (test assembly omitted for length) ===' -ForegroundColor Cyan
$members | Where-Object { $_.assembly -eq 'SampleApp' } | ForEach-Object {
    [pscustomobject]@{
        Status = $_.status
        Reason = $_.skipReason
        Return = $_.returnKind
        Member = $_.method
    }
} | Sort-Object Status, Member | Format-Table -AutoSize -Wrap

Write-Host ''
Write-Host '=== test roots (excluded from frontier candidacy) ===' -ForegroundColor Cyan
$members | Where-Object { $_.isTestRoot } | ForEach-Object { "  $($_.method)" }

Write-Host ''
Write-Host '=== reconciliation ===' -ForegroundColor Cyan
$discovered = $members.Count
$byStatus = $members | Group-Object status
$patched = ($byStatus | Where-Object Name -eq 'Patched').Count
$skipped = $members | Where-Object { $_.status -eq 'Skipped' }
$failed = ($byStatus | Where-Object Name -eq 'PatchFailed').Count
$enumFailed = ($byStatus | Where-Object Name -eq 'EnumerationFailed').Count

Write-Host ("  discovered           : {0}" -f $discovered)
Write-Host ("  Patched              : {0}" -f $patched)
foreach ($group in ($skipped | Group-Object skipReason | Sort-Object Name)) {
    Write-Host ("  Skipped/{0,-20}: {1}" -f $group.Name, $group.Count)
}
Write-Host ("  PatchFailed          : {0}" -f $failed)
Write-Host ("  EnumerationFailed    : {0}" -f $enumFailed)

$sum = $patched + $skipped.Count + $failed + $enumFailed
Write-Host ("  sum of statuses      : {0}" -f $sum)
Write-Host ("  reconciles           : {0}" -f ($sum -eq $discovered))
if ($sum -ne $discovered) { throw "manifest counts do not reconcile: $sum vs $discovered" }

foreach ($assembly in $assemblies) {
    $moduleRecords = @($members | Where-Object { $_.assembly -eq $assembly.assembly }).Count
    $moduleSum = $assembly.patchedMembers + $assembly.skippedMembers
    if ($assembly.discoveredMembers -ne $moduleSum `
        -or $assembly.discoveredMembers -ne $moduleRecords `
        -or $assembly.patchFailedMembers -ne 0) {
        throw "module $($assembly.assembly) does not reconcile: discovered=$($assembly.discoveredMembers) instrumented=$($assembly.patchedMembers) skipped=$($assembly.skippedMembers) records=$moduleRecords failed=$($assembly.patchFailedMembers)"
    }
}

Write-Host ''
Write-Host '=== patched vs traced ===' -ForegroundColor Cyan
$events = Get-Content $traceFile.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$tracedMethods = $events | ForEach-Object { $_.methodFullName } | Sort-Object -Unique
$patchedNames = $members | Where-Object { $_.status -eq 'Patched' } | ForEach-Object { $_.method }

$orphans = $tracedMethods | Where-Object { $_ -notin $patchedNames }
Write-Host "  patched                 : $($patchedNames.Count)"
Write-Host "  distinct traced         : $($tracedMethods.Count)"
Write-Host "  traced but not patched  : $($orphans.Count)"
if ($orphans.Count -gt 0) { $orphans | ForEach-Object { Write-Host "    ORPHAN $_" -ForegroundColor Red } }

$silent = $patchedNames | Where-Object { $_ -notin $tracedMethods }
Write-Host "  patched but never traced: $($silent.Count)"
foreach ($name in $silent) {
    $owner = ($members | Where-Object { $_.method -eq $name -and $_.status -eq 'Patched' } | Select-Object -First 1).assembly
    $late = ($assemblies | Where-Object { $_.assembly -eq $owner }).latePatched
    $why = if ($late) { 'LatePatched assembly - coverage before patch unknown' } else { 'ran before or outside instrumentation' }
    Write-Host "    $name`n        assembly=$owner  $why"
}

Write-Host ''
Write-Host '=== depth-0 composition ===' -ForegroundColor Cyan
$roots = $events | Where-Object { $_.callDepth -eq 0 }
Write-Host "  root events: $($roots.Count)"
$roots | Group-Object methodFullName | Sort-Object Name | ForEach-Object {
    $isRoot = ($members | Where-Object { $_.method -eq $_.Name } | Select-Object -First 1).isTestRoot
    "    {0,2}x  {1}" -f $_.Count, $_.Name
}
$rootTestRoots = ($roots | Where-Object { $_.methodFullName -in ($members | Where-Object { $_.isTestRoot } | ForEach-Object { $_.method }) }).Count
Write-Host "  of which tagged IsTestRoot: $rootTestRoots"
Write-Host "  remainder (not test roots): $($roots.Count - $rootTestRoots)"

Write-Host ''
Write-Host '=== late-loaded assembly ===' -ForegroundColor Cyan
$late = $assemblies | Where-Object { $_.assembly -eq 'SampleApp.Plugin' }
if ($late) {
    Write-Host "  discovery        : $($late.discovery)"
    Write-Host "  queuedAtMs       : $($late.queuedAtMs)"
    Write-Host "  patchedAtMs      : $($late.patchedAtMs)"
    Write-Host "  latePatched      : $([bool]$late.latePatched)"
    Write-Host "  patchedMembers   : $($late.patchedMembers)"
    Write-Host "  tracedCalls      : $($late.tracedCalls)"
    Write-Host "  prePatchCoverage : $($late.prePatchCoverage)"
}
else {
    Write-Host '  SampleApp.Plugin was never seen' -ForegroundColor Red
}

Write-Host ''
Write-Host '=== test-assembly classification (drives IsHarness) ===' -ForegroundColor Cyan
$assemblies | Where-Object { $_.scanned } | ForEach-Object {
    [pscustomobject]@{
        Assembly       = $_.assembly
        IsTestAssembly = [bool]$_.isTestAssembly
        TriggeredBy    = if ($_.testFrameworkReference) { $_.testFrameworkReference } else { '-' }
    }
} | Format-Table -AutoSize

$harness = $events | Where-Object { $_.isHarness }
$subject = $events | Where-Object { -not $_.isHarness }
Write-Host "  harness events : $($harness.Count)  (excluded from frontier candidacy at any depth)"
Write-Host "  subject events : $($subject.Count)"
$harnessAssemblies = $members | Where-Object { $_.method -in ($harness | ForEach-Object { $_.methodFullName }) } | ForEach-Object { $_.assembly } | Sort-Object -Unique
Write-Host "  harness events come from: $($harnessAssemblies -join ', ')"
$subjectDepth0 = $subject | Where-Object { $_.callDepth -eq 0 }
Write-Host "  subject events at depth 0: $($subjectDepth0.Count)  (would be frontier candidates)"

Write-Host ''
Write-Host '=== PROOF 3: graphs differing only in a private field ===' -ForegroundColor Cyan
$priceCalls = $events | Where-Object { $_.methodFullName -like 'SampleApp.PricingRules.Price(*' }
Write-Host "  calls to PricingRules.Price: $($priceCalls.Count)"
$i = 0
foreach ($call in $priceCalls) {
    $i++
    Write-Host "  [$i] returnDigest = $($call.returnDigest)"
    Write-Host "      argsDigest   = $($call.argsDigest)"
}
if ($priceCalls.Count -ge 2) {
    $a = $priceCalls[0].argsDigest
    $b = $priceCalls[1].argsDigest
    Write-Host "  return values identical : $($priceCalls[0].returnDigest -eq $priceCalls[1].returnDigest)"
    Write-Host "  arg digests differ      : $($a -ne $b)"
    if ($a -eq $b) { Write-Host '  PROOF 3 FAILED - digests are identical' -ForegroundColor Red }
}

Write-Host ''
Write-Host '=== V1: source path resolution ===' -ForegroundColor Cyan
$events | Group-Object filePathResolution | Sort-Object Name | ForEach-Object {
    "  {0,4}x  {1}" -f $_.Count, $_.Name
}
$nullPath = ($events | Where-Object { [string]::IsNullOrEmpty($_.filePath) }).Count
Write-Host "  events with null/empty filePath : $nullPath"

Write-Host ''
Write-Host '  per-assembly source rollup:'
$assemblies | Where-Object { $_.instrumented } | ForEach-Object {
    "    {0,-20} patched={1,-3} exact={2,-3} pct={3,-4} rule={4,-13} partial={5,-6} unavailable={6,-6} test={7,-6} tracedCalls={8}" -f `
        $_.assembly, $_.patchedMembers, $_.membersWithExactSource, $_.exactSourcePercent, $_.sourceRule, `
        [bool]$_.sourcePartial, [bool]$_.sourceUnavailable, [bool]$_.isTestAssembly, $_.tracedCalls
}

# A SourceUnavailable assembly does not degrade the output, it inverts it: every divergence in it is
# classified EXPECTED, so the run reports "no unexpected behavior changes" for code it never analysed.
# Harness assemblies are exempt because their divergences are never reported in the first place.
$sourceDead = $assemblies | Where-Object { $_.sourceUnavailable -and $_.tracedCalls -gt 0 -and -not $_.isTestAssembly }
$harnessDead = $assemblies | Where-Object { $_.sourceUnavailable -and $_.tracedCalls -gt 0 -and $_.isTestAssembly }
foreach ($a in $harnessDead) {
    Write-Host "    NOTE harness assembly $($a.assembly) at $($a.exactSourcePercent)% (rule $($a.sourceRule)) - exempt, not frontier-eligible" -ForegroundColor DarkYellow
}
Write-Host "  ASSERT SourceUnavailable subject assemblies with traced calls = $($sourceDead.Count) (must be 0)"
if ($sourceDead.Count -gt 0) {
    foreach ($a in $sourceDead) {
        Write-Host "    $($a.assembly): $($a.tracedCalls) traced call(s), $($a.exactSourcePercent)% resolvable source" -ForegroundColor Red
    }
    throw "RUN INVALID: $($sourceDead.Count) assembly/assemblies produced traced calls but too little resolvable source. Their divergences would be silently classified EXPECTED. Build with <DebugType>portable</DebugType>."
}

# Permanent assertions. The engine matches FilePath against `git diff --name-only` and classifies an
# unmatched path as EXPECTED, so an unresolved path on subject code silently empties the headline output.
$unusable = $events | Where-Object {
    -not $_.isHarness -and $_.filePathResolution -notin @('sequencePoints', 'stateMachine', 'declaringType')
}
Write-Host "  ASSERT subject events with unusable filePath = $($unusable.Count) (must be 0)"
if ($unusable.Count -gt 0) {
    $unusable | Group-Object methodFullName | ForEach-Object { Write-Host "    $($_.Name) -> $($_.Group[0].filePathResolution)" -ForegroundColor Red }
    throw "V1 assertion failed: $($unusable.Count) subject event(s) have no usable FilePath"
}

# Harness assemblies were classified by reference, not by name. A miss shows up here as a subject root.
$subjectRoots = ($events | Where-Object { -not $_.isHarness -and $_.callDepth -eq 0 }).Count
Write-Host "  ASSERT subject events at depth 0 = $subjectRoots (must be 0, else a harness assembly was missed)"
if ($subjectRoots -gt 0) { throw "harness classification missed an assembly: $subjectRoots subject root event(s)" }

# The engine matches on (TestId, MethodFullName, index); a subject event with no test correlation cannot be paired.
$orphanTest = ($events | Where-Object { -not $_.isHarness -and $_.testId -eq '(no-test)' }).Count
Write-Host "  ASSERT subject events with testId '(no-test)' = $orphanTest (must be 0)"
if ($orphanTest -gt 0) { throw "$orphanTest subject event(s) carry no test correlation" }

Write-Host ''
Write-Host '=== CANONICALIZER COUNTERS (actual values, from the manifest) ===' -ForegroundColor Cyan
$digest = $records | Where-Object { $_.kind -eq 'digest' } | Select-Object -First 1
if ($digest) {
    "  valuesDigested    : {0}" -f $digest.valuesDigested
    "  depthLimited      : {0}" -f $digest.depthLimited
    "  blocklisted       : {0}" -f $digest.blocklisted
    "  errored           : {0}" -f $digest.errored
    "  renderedTruncated : {0}" -f $digest.renderedTruncated
    foreach ($pair in @(
            @{ n = 'depthLimited'; v = $digest.depthLimited },
            @{ n = 'blocklisted'; v = $digest.blocklisted },
            @{ n = 'renderedTruncated'; v = $digest.renderedTruncated })) {
        if ($pair.v -le 0) { Write-Host "  ASSERT $($pair.n) > 0 FAILED - the path has never executed" -ForegroundColor Red }
    }
}
$unruled = $records | Where-Object { $_.kind -eq 'unruled' }
Write-Host "  unruled enumerables: $($unruled.Count)"
$unruled | ForEach-Object { "    {0,4}x  {1}" -f $_.count, $_.typeName }

function Get-Rendered([string]$method, [string]$field) {
    $ev = $events | Where-Object { $_.methodFullName -like "*$method*" -and $_.testId -like '*DigestProofTests*' } | Select-Object -First 1
    if ($ev) { return $ev.$field }
    return $null
}

# Scoped to the proof test. Under parallelism the load generator calls some of the same entry points,
# and an unscoped match silently reports its events instead - the proof would still print, and be wrong.
$proofEvents = $events | Where-Object { $_.testId -like '*DigestProofTests*' }

Write-Host ''
Write-Host '=== SEVEN PROOFS ===' -ForegroundColor Cyan

$p1 = $proofEvents | Where-Object { $_.methodFullName -like '*Probes.ObservedCalls*' } | Select-Object -First 1
Write-Host "  P1 no overridable member invoked"
Write-Host "     ObservedCalls returnRendered = $($p1.returnRendered)"
Write-Host "     probe argsRendered           = $(Get-Rendered 'Probes.Inspect' 'argsRendered')"

$p2 = $proofEvents | Where-Object { $_.methodFullName -like '*Probes.Traverse*' }
Write-Host "  P2 cyclic graph terminates: $($p2.Count) call(s), digests equal = $($p2[0].argsDigest -eq $p2[1].argsDigest)"
Write-Host "     $($p2[0].argsRendered)"

$p4 = $proofEvents | Where-Object { $_.methodFullName -like '*Probes.Relate*' }
Write-Host "  P4 shared node vs two equal copies: differ = $($p4[0].argsDigest -ne $p4[1].argsDigest)"
Write-Host "     shared = $($p4[0].argsRendered)"
Write-Host "     copies = $($p4[1].argsRendered)"

Write-Host "  P5 hash-collection determinism"
Write-Host "     dictionary = $(Get-Rendered 'BuildDictionaryWithRemovals' 'returnRendered')"
Write-Host "     hashset    = $(Get-Rendered 'BuildSetWithRemovals' 'returnRendered')"
$dictRendered = Get-Rendered 'BuildDictionaryWithRemovals' 'returnRendered'
$setRendered = Get-Rendered 'BuildSetWithRemovals' 'returnRendered'
foreach ($pair in @(@{ n = 'Dictionary'; v = $dictRendered }, @{ n = 'HashSet'; v = $setRendered })) {
    $ok = $pair.v -like "ShapeRule:$($pair.n)*"
    Write-Host "     ASSERT $($pair.n) used ShapeRule not StructuralFields = $ok"
    if (-not $ok) { throw "$($pair.n) silently degraded to StructuralFields - the .NET Core layout assumption is wrong" }
}

$p6 = $proofEvents | Where-Object { $_.methodFullName -like '*Probes.Stamp*' }
Write-Host "  P6 Guid/DateTime normalized: digests equal = $($p6[0].argsDigest -eq $p6[1].argsDigest)"
Write-Host "     $($p6[0].argsRendered)"

Write-Host "  P7 blocklist checked before recursion"
Write-Host "     $(Get-Rendered 'Probes.UseServices' 'argsRendered')"

$deep = Get-Rendered 'Probes.Descend' 'argsRendered'
Write-Host "  depth limiter: $(([regex]::Matches($deep, '<depth:')).Count) marker(s) in the rendered text"

$long = $proofEvents | Where-Object { $_.methodFullName -like '*Probes.LongText*' } | Select-Object -First 1
Write-Host "  truncation: returnRendered length = $($long.returnRendered.Length), ends with <truncated> = $($long.returnRendered.EndsWith('<truncated>'))"

Write-Host ''
Write-Host '=== ERRORED FIELD (the only source of a PARTIAL digest) ===' -ForegroundColor Cyan
$readable = $proofEvents | Where-Object { $_.methodFullName -like '*ErrorProbes.Readable*' } | Select-Object -First 1
$errored = $proofEvents | Where-Object { $_.methodFullName -like '*ErrorProbes.Unreadable(*' } | Select-Object -First 1
$erroredOther = $proofEvents | Where-Object { $_.methodFullName -like '*ErrorProbes.UnreadableOther*' } | Select-Object -First 1
"  readable : $($readable.argsRendered)"
"  errored  : $($errored.argsRendered)"
"  other    : $($erroredOther.argsRendered)"
$hasMarker = $errored.argsRendered -like '*<error:_payload:TypeInitializationException>*'
"  ASSERT marker present with field name and exception = $hasMarker"
if (-not $hasMarker) { throw 'errored field was omitted rather than marked - two different graphs would now collide' }
"  ASSERT errored digest differs from readable digest   = $($errored.argsDigest -ne $readable.argsDigest)"
if ($errored.argsDigest -eq $readable.argsDigest) { throw 'errored and readable graphs digest identically' }
"  KNOWN GAP: two graphs differing only inside unreadable fields still collide = $($errored.argsDigest -eq $erroredOther.argsDigest)"

Write-Host ''
Write-Host '=== V3: writer reconciliation under parallelism ===' -ForegroundColor Cyan
$writer = $records | Where-Object { $_.kind -eq 'writer' } | Select-Object -First 1
$fileLines = (Get-Content $traceFile.FullName | Where-Object { $_.Trim().Length -gt 0 }).Count
"  enqueued   : $($writer.enqueued)"
"  written    : $($writer.written)"
"  dropped    : $($writer.dropped)"
"  capacity   : $($writer.capacity)"
"  file lines : $fileLines"
"  emitting threads : $(($events.threadId | Sort-Object -Unique).Count)"
"  ASSERT enqueued == written == file lines = $(($writer.enqueued -eq $writer.written) -and ($writer.written -eq $fileLines))"
if ($writer.enqueued -ne $writer.written) { throw "buffer lost events: enqueued=$($writer.enqueued) written=$($writer.written)" }
if ($writer.written -ne $fileLines) { throw "file is short: written=$($writer.written) lines=$fileLines" }
"  ASSERT dropped == 0 = $($writer.dropped -eq 0)"
if ($writer.dropped -ne 0) { throw "$($writer.dropped) event(s) dropped after buffer close" }

$dupes = $events | Group-Object callId | Where-Object Count -gt 1
"  ASSERT no duplicate callIds = $($dupes.Count -eq 0)  (duplicates=$($dupes.Count))"
if ($dupes.Count -ne 0) { throw 'duplicate callIds under parallelism' }

Write-Host ''
Write-Host '  call-order-index stability check (the engine matches on this):'
$byKey = $events | Where-Object { -not $_.isHarness } | Group-Object testId, methodFullName
"    distinct (testId, methodFullName) keys : $($byKey.Count)"
"    keys with more than one call          : $(($byKey | Where-Object Count -gt 1).Count)"
$interleaved = $events | Group-Object testId | Where-Object { ($_.Group.threadId | Sort-Object -Unique).Count -gt 1 }
"    tests whose events span >1 thread      : $($interleaved.Count)"

Write-Host ''
Write-Host '=== engine read ===' -ForegroundColor Cyan
$engineManifest = Join-Path $repo 'src/RealDiff.Engine.Rust/Cargo.toml'
& cargo build --release --locked --manifest-path $engineManifest
if ($LASTEXITCODE -ne 0) { throw 'Rust engine build failed' }
$engine = Join-Path $repo 'src/RealDiff.Engine.Rust/target/release/realdiff-engine.exe'
if (-not $IsWindows) { $engine = $engine.Substring(0, $engine.Length - 4) }
& $engine read $traceFile.FullName
