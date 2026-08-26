#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-rust-test-correlation-gate'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'samples/RustReference'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$cache = Join-Path $work 'cache'
$trace = Join-Path $work 'correlation.ndjson'
$manifest = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/Cargo.toml'
$binary = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/target/release/behaviordiff-rust-rewrite.exe'
if (-not $IsWindows) { $binary = $binary.Substring(0, $binary.Length - 4) }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $work -ItemType Directory -Force | Out-Null
& cargo build --release --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw "Rust tracer build failed: $LASTEXITCODE" }
$rewrite = (& $binary --source $source --cache-root $cache | ConvertFrom-Json)
if ($rewrite.sourceFiles -le 0 -or $rewrite.rustFiles -le 0) {
    throw "Rust correlation inputs are empty: source=$($rewrite.sourceFiles) rust=$($rewrite.rustFiles)"
}

$env:BEHAVIORDIFF_RUST_EXIT_TRACE = $trace
try {
    $runnerOutput = @(& cargo test --quiet --manifest-path (Join-Path $rewrite.output 'Cargo.toml') --lib -- --test-threads=1 2>&1)
    $runnerExit = $LASTEXITCODE
} finally {
    Remove-Item Env:BEHAVIORDIFF_RUST_EXIT_TRACE -ErrorAction SilentlyContinue
}
$runnerOutput | ForEach-Object { Write-Host $_ }
if ($runnerExit -ne 0) { throw "Rewritten Rust tests failed: $runnerExit" }
$runnerLine = @($runnerOutput | Where-Object { $_ -match '^test result: ok\. ([0-9]+) passed;' })
if ($runnerLine.Count -ne 1) {
    throw "Rust runner count was not uniquely established from $($runnerOutput.Count) output line(s)"
}
$null = $runnerLine[0] -match '^test result: ok\. ([0-9]+) passed;'
$runnerTests = [int]$Matches[1]
if ($runnerTests -le 0) { throw "Rust runner reported an empty test population: $runnerTests" }

$events = @(Get-Content $trace | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
if ($events.Count -le 0) { throw 'Rust correlation trace has zero events' }
$roots = @($events | Where-Object { $_.isHarness -eq $true -and [int]$_.callDepth -eq 0 })
$subject = @($events | Where-Object { $_.isHarness -ne $true })
if ($roots.Count -le 0 -or $subject.Count -le 0) {
    throw "Rust correlation populations are empty: events=$($events.Count) roots=$($roots.Count) subject=$($subject.Count)"
}
if ($roots.Count -ne $runnerTests) {
    throw "Rust runner/derived test counts differ: runner=$runnerTests derived=$($roots.Count)"
}

$byCall = @{}
foreach ($event in $events) { $byCall[[long]$event.callId] = $event }
$orphans = @($events | Where-Object {
    $parent = $_.PSObject.Properties['parentCallId']
    $null -ne $parent -and -not $byCall.ContainsKey([long]$parent.Value)
})
$subjectRoots = @($subject | Where-Object { [int]$_.callDepth -eq 0 })
$noTest = @($subject | Where-Object testId -ceq '(no-test)')
$rootIds = @($roots | ForEach-Object testId | Sort-Object -Unique)
$subjectTestIds = @($subject | ForEach-Object testId | Sort-Object -Unique)
if (($rootIds | ConvertTo-Json -Compress) -cne ($subjectTestIds | ConvertTo-Json -Compress)) {
    throw "Rust structural test identities differ: roots=$($rootIds.Count) subject=$($subjectTestIds.Count)"
}

$keys = @($subject | Group-Object testId, methodFullName)
$methods = @($subject | ForEach-Object methodFullName | Sort-Object -Unique)
$ordinalFailures = @($keys | Where-Object {
    $ordinals = @($_.Group | ForEach-Object { [int]$_.ordinal } | Sort-Object)
    $expected = @(0..($ordinals.Count - 1))
    ($ordinals | ConvertTo-Json -Compress) -cne ($expected | ConvertTo-Json -Compress)
})
if ($keys.Count -lt 300 -or $methods.Count -lt 50) {
    throw "Rust reference volume is below target: keys=$($keys.Count) methods=$($methods.Count)"
}
if ($orphans.Count -ne 0 -or $subjectRoots.Count -ne 0 -or $noTest.Count -ne 0 -or $ordinalFailures.Count -ne 0) {
    throw "Rust correlation guard failed from events=$($events.Count) subject=$($subject.Count) keys=$($keys.Count): orphans=$($orphans.Count) subjectRoots=$($subjectRoots.Count) noTest=$($noTest.Count) ordinals=$($ordinalFailures.Count)"
}

[pscustomobject]@{
    SourceFiles = $rewrite.sourceFiles
    RustFiles = $rewrite.rustFiles
    RunnerTests = $runnerTests
    DerivedTests = $roots.Count
    Events = $events.Count
    SubjectEvents = $subject.Count
    MatchedKeyCandidates = $keys.Count
    SubjectMethods = $methods.Count
    Orphans = $orphans.Count
    SubjectRoots = $subjectRoots.Count
    NoTestSubjectEvents = $noTest.Count
    OrdinalFailures = $ordinalFailures.Count
} | Format-List
Write-Host 'RUST_TEST_CORRELATION: PASS'
