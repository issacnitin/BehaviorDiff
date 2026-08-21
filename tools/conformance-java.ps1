#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-java-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
Import-Module (Join-Path $PSScriptRoot 'BehaviorDiff.Conformance.psm1') -Force
$expectedRunnerCount = 120

function Copy-CleanTree([string]$destination) {
    Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem $repo -Force | Where-Object Name -NotIn @('.git', '.vs') | ForEach-Object {
        Copy-Item $_.FullName -Destination $destination -Recurse -Force
    }
    Get-ChildItem $destination -Include bin, obj, target -Recurse -Directory -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Build-Agent([string]$tree) {
    & mvn -f (Join-Path $tree 'src/BehaviorDiff.Java.Agent/pom.xml') clean package
    if ($LASTEXITCODE -ne 0) { throw "Java agent build failed: $tree" }
}

function Run-Reference([string]$tree, [string]$agent, [string]$runDirectory) {
    Remove-Item $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $trace = Join-Path $runDirectory 'run.ndjson'
    $argLine = "--add-opens java.base/java.util=ALL-UNNAMED -javaagent:$agent=include=io.behaviordiff.reference;trace=$trace"
    & mvn -f (Join-Path $tree 'samples/JavaReference/pom.xml') test "-DargLine=$argLine" |
        ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Java reference tests failed with $agent" }

    $runnerCount = (Get-ChildItem (Join-Path $tree 'samples/JavaReference/target/surefire-reports') -Filter 'TEST-*.xml' |
        ForEach-Object { [int]([xml](Get-Content $_.FullName -Raw)).testsuite.tests } | Measure-Object -Sum).Sum
    $records = @(Get-ChildItem $runDirectory -Filter '*.manifest.ndjson' | ForEach-Object {
        Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json }
    })
    $rootMethods = @($records | Where-Object {
        $_.kind -eq 'member' -and $null -ne $_.PSObject.Properties['isTestRoot'] -and [bool]$_.isTestRoot
    } | ForEach-Object method)
    $events = @(Get-ChildItem $runDirectory -Filter '*.ndjson' | Where-Object Name -NotLike '*.manifest.ndjson' |
        ForEach-Object { Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } })
    $digest = $records | Where-Object kind -eq 'digest' | Select-Object -First 1
    foreach ($counter in @('depthLimited', 'blocklisted', 'errored', 'renderedTruncated')) {
        if ([long]$digest.$counter -le 0) { throw "Java digest counter was not exercised: $counter" }
    }
    $derivedCount = @($events | Where-Object methodFullName -In $rootMethods).Count
    if ($runnerCount -ne $expectedRunnerCount) {
        throw "Java runner count mismatch: expected=$expectedRunnerCount actual=$runnerCount"
    }
    if ($runnerCount -ne $derivedCount) {
        throw "Java test count mismatch: runner=$runnerCount derived=$derivedCount"
    }
    return [pscustomobject]@{ Runner = [int]$runnerCount; Derived = [int]$derivedCount }
}

function Events([object]$run, [string]$method) {
    return @($run.Events | Where-Object { $_.testId -like '*digestProofs*' -and $_.methodFullName -like "*$method*" })
}
function Proof([string]$name, [bool]$passed, [string]$detail) {
    [pscustomobject]@{ Name = $name; Passed = $passed; Detail = $detail }
}

$digestEvaluator = {
    param($run)
    $observed = Events $run 'Subject.observedCalls'
    Proof 'NoUserCodeInvoked' ($observed.Count -eq 1 -and $observed[0].returnRendered -eq 'String:""') "events=$($observed.Count)"
    $cycles = Events $run 'Subject.cycle'
    Proof 'CyclesTerminate' ($cycles.Count -eq 2 -and $cycles[0].argsDigest -eq $cycles[1].argsDigest) "events=$($cycles.Count)"
    $topology = Events $run 'Subject.topology'
    Proof 'ReferenceTopology' ($topology.Count -eq 2 -and $topology[0].argsDigest -ne $topology[1].argsDigest) "events=$($topology.Count)"
    $maps = Events $run 'Subject.map'; $sets = Events $run 'Subject.set'
    Proof 'UnorderedCollectionsStable' ($maps.Count -eq 2 -and $sets.Count -eq 2 -and $maps[0].returnDigest -eq $maps[1].returnDigest -and $sets[0].returnDigest -eq $sets[1].returnDigest) "maps=$($maps.Count) sets=$($sets.Count)"
    $stamps = Events $run 'Subject.stamp'
    Proof 'TimeAndIdentityNormalized' ($stamps.Count -eq 2 -and $stamps[0].argsDigest -eq $stamps[1].argsDigest) "events=$($stamps.Count)"
    $blocked = Events $run 'Subject.block'
    Proof 'BlocklistBeforeRecursion' ($blocked.Count -eq 1 -and $blocked[0].argsRendered -like '*<skipped:java.lang.Thread>*') "events=$($blocked.Count)"
    $deep = Events $run 'Subject.deep'
    Proof 'DepthMarker' ($deep.Count -eq 1 -and $deep[0].argsRendered -like '*<depth:*') "events=$($deep.Count)"
    $long = Events $run 'Subject.longText'
    Proof 'TruncationMarker' ($long.Count -eq 2 -and @($long | Where-Object argsRendered -Like '*<truncated>').Count -eq 2) "events=$($long.Count)"
    $unreadable = Events $run 'Subject.unreadable'
    Proof 'UnreadableFieldMarker' ($unreadable.Count -eq 1 -and $unreadable[0].argsRendered -like '*<error:*Inaccessible>*') "events=$($unreadable.Count)"
    Proof 'BeyondRenderedCap' ($long.Count -eq 2 -and $long[0].argsRendered -eq $long[1].argsRendered -and $long[0].argsDigest -ne $long[1].argsDigest) "events=$($long.Count)"
}

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $tree1 = Join-Path $work 'tree-1'; $tree2 = Join-Path $work 'tree-2'
    $run1 = Join-Path $work 'run-1'; $run2 = Join-Path $work 'run-2'
    Copy-CleanTree $tree1; Copy-CleanTree $tree2
    Write-Host '=== independent Java agent build 1 ===' -ForegroundColor Cyan; Build-Agent $tree1
    Write-Host '=== independent Java agent build 2 ===' -ForegroundColor Cyan; Build-Agent $tree2
    $agent1 = Join-Path $tree1 'src/BehaviorDiff.Java.Agent/target/behaviordiff-java-agent-0.2.0-SNAPSHOT.jar'
    $agent2 = Join-Path $tree2 'src/BehaviorDiff.Java.Agent/target/behaviordiff-java-agent-0.2.0-SNAPSHOT.jar'
    Write-Host '=== Java reference run 1 ===' -ForegroundColor Cyan; $count1 = Run-Reference $tree1 $agent1 $run1
    Write-Host '=== Java reference run 2 ===' -ForegroundColor Cyan; $count2 = Run-Reference $tree1 $agent2 $run2

    $guard = Assert-BehaviorDiffConformanceRuns -FirstRun $run1 -SecondRun $run2 -MinimumMatchedKeys 100 `
        -UsableSourceResolutions @('debugInfo', 'generatedState', 'declaringType') `
        -ReferenceSourcePathPatterns @('^io/behaviordiff/reference/.+\.java$') -DigestProofEvaluator $digestEvaluator
    $engineProject = Join-Path $repo 'src/BehaviorDiff.Engine/BehaviorDiff.Engine.csproj'
    & dotnet build $engineProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed' }
    $engine = Invoke-BehaviorDiffEngineConformance -FirstRun $run1 -SecondRun $run2 -EngineProject $engineProject

    Write-Host '=== Java conformance report ===' -ForegroundColor Green
    Write-Host "  runner tests / derived roots: $($count1.Runner) / $($count1.Derived)"
    Write-Host "  matched keys              : $($guard.MatchedKeys)"
    Write-Host "  identical subject methods : $($guard.SubjectMethods)"
    Write-Host "  subject events per run     : $($guard.FirstSubjectEvents) / $($guard.SecondSubjectEvents)"
    Write-Host "  unusable source events     : $($guard.UnusableSourceEvents)"
    Write-Host "  subject depth-0 events     : $($guard.SubjectRoots)"
    Write-Host "  wrong source mappings      : $($guard.WrongSourceEvents)"
    Write-Host "  digest proofs per run      : $($guard.DigestProofsPerRun)"
    Write-Host "  engine raw / divergences   : $($engine.RawDifferences) / $($engine.RemainingDivergences)"
    foreach ($proof in @(& $digestEvaluator (Read-BehaviorDiffConformanceRun $run1))) {
        Write-Host "  digest/$($proof.Name): PASS ($($proof.Detail))"
    }
} finally {
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "conformance work retained at $work" }
}