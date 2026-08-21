#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-node-attribution-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$changedFile = 'samples/NodeReference/src/config.js'
$subjectFile = 'samples/NodeReference/src/subject.js'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
Import-Module (Join-Path $PSScriptRoot 'BehaviorDiff.Conformance.psm1') -Force

function Copy-ReferenceTree([string]$destination) {
    $sample = Join-Path $destination 'samples/NodeReference'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sample) -Force | Out-Null
    Copy-Item (Join-Path $repo 'samples/NodeReference') $sample -Recurse -Force
    foreach ($generated in @('node_modules', 'generated')) {
        Get-ChildItem $sample -Directory -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object Name -eq $generated |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Add-SuffixConfig([string]$tree, [string]$suffix) {
    $configPath = Join-Path $tree $changedFile
    [IO.File]::WriteAllText($configPath, @"
'use strict';

function create() {
  function config() {
    return function suffix() {
      return '$suffix';
    };
  }
  return config();
}

module.exports = { suffix: create() };
"@)

    $subjectPath = Join-Path $tree $subjectFile
    $subject = Get-Content $subjectPath -Raw
    $subject = $subject.Replace("'use strict';", "'use strict';`r`n`r`nconst config = require('./config.js');")
    $subject = $subject.Replace("new AsyncSettlement('done')", 'new AsyncSettlement(config.suffix())')
    Set-Content $subjectPath $subject -NoNewline
}

function Get-PropertyValue([object]$inputObject, [string]$name, [object]$default = $null) {
    $property = $inputObject.PSObject.Properties[$name]
    if ($null -eq $property) { return $default }
    return $property.Value
}

function Assert-RunAccounting([object]$run, [string]$label) {
    $writers = @($run.ManifestRecords | Where-Object kind -eq 'writer')
    if ($writers.Count -ne 1) { throw "Node manifest ($label): expected one writer record, got $($writers.Count)" }
    $writer = $writers[0]
    if ([long]$writer.enqueued -ne $run.Events.Count -or
        [long]$writer.written -ne $run.Events.Count -or
        [long]$writer.dropped -ne 0 -or
        [long]$writer.capacity -le 0) {
        throw "Node writer reconciliation failed ($label): events=$($run.Events.Count) enqueued=$($writer.enqueued) written=$($writer.written) dropped=$($writer.dropped) capacity=$($writer.capacity)"
    }

    $assemblies = @($run.ManifestRecords | Where-Object kind -eq 'assembly')
    $members = @($run.ManifestRecords | Where-Object kind -eq 'member')
    if ($assemblies.Count -eq 0) { throw "Node manifest ($label): no module records" }
    foreach ($module in $assemblies) {
        $moduleMembers = @($members | Where-Object assembly -CEQ $module.assembly)
        if ([int]$module.discoveredMembers -ne $moduleMembers.Count -or
            [int]$module.discoveredMembers -ne [int]$module.patchedMembers + [int]$module.skippedMembers -or
            [int]$module.patchFailedMembers -ne 0) {
            throw "Node module reconciliation failed ($label/$($module.assembly))"
        }
    }
    $moduleCalls = ($assemblies | Measure-Object -Property tracedCalls -Sum).Sum
    if ([long]$moduleCalls -ne $run.Events.Count) {
        throw "Node module call reconciliation failed ($label): events=$($run.Events.Count) moduleCalls=$moduleCalls"
    }

    $subjectEvents = @($run.Events | Where-Object { -not [bool](Get-PropertyValue $_ 'isHarness' $false) })
    $wrongPaths = @($subjectEvents | Where-Object {
        $_.filePathResolution -cne 'debugInfo' -or
        [string]::IsNullOrWhiteSpace([string]$_.filePath) -or
        [string]$_.filePath -notmatch '^samples/NodeReference/src/[^/]+\.js$'
    })
    if ($wrongPaths.Count -ne 0) {
        throw "Run emitted $($wrongPaths.Count) non-harness event(s) without a direct Node source path ($label)"
    }

    $configEvents = @($subjectEvents | Where-Object methodFullName -Like "$changedFile#*")
    if ($configEvents.Count -ne 0) { throw "Excluded config emitted $($configEvents.Count) event(s) ($label)" }
    $configMembers = @($members | Where-Object {
        $_.assembly -ceq $changedFile -and $_.method -like "$changedFile#*"
    })
    $matchingConfigMembers = @($configMembers | Where-Object method -eq "$changedFile#create.config.suffix")
    if ($configMembers.Count -lt 1 -or $matchingConfigMembers.Count -ne 1 -or
        @($configMembers | Where-Object {
            $_.status -cne 'Skipped' -or $_.skipReason -cne 'ExcludedByScope' -or
            $_.sourceResolution -cne 'debugInfo'
        }).Count -ne 0) {
        throw "Excluded config member coverage was not recorded correctly ($label)"
    }

    $configModules = @($assemblies | Where-Object assembly -CEQ $changedFile)
    if ($configModules.Count -ne 1 -or [long]$configModules[0].tracedCalls -ne 0 -or
        [int]$configModules[0].patchedMembers -ne 0) {
        throw "Excluded config module accounting was not zero-call/skipped ($label)"
    }

    $rootEvents = @($run.Events | Where-Object { [bool](Get-PropertyValue $_ 'isHarness' $false) })
    return [pscustomobject]@{
        Events = $run.Events.Count
        SubjectEvents = $subjectEvents.Count
        ExactSourceEvents = $subjectEvents.Count - $wrongPaths.Count
        RootEvents = $rootEvents.Count
        Writer = $writer
        Modules = $assemblies.Count
        ConfigMembers = $configMembers.Count
    }
}

function Run-Reference(
    [string]$tree,
    [string]$runDirectory,
    [bool]$expectSuccess,
    [string]$label) {
    Remove-Item $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $trace = Join-Path $runDirectory 'run.ndjson'
    $report = Join-Path $runDirectory 'runner-report.json'
    $register = Join-Path $repo 'src/BehaviorDiff.Node/register.cjs'
    $runner = Join-Path $tree 'samples/NodeReference/test/run.cjs'
    $environmentNames = @(
        'BEHAVIORDIFF_TRACE',
        'BEHAVIORDIFF_NAMESPACES',
        'BEHAVIORDIFF_EXCLUDE_NAMESPACES',
        'BEHAVIORDIFF_REPOSITORY_ROOT',
        'BEHAVIORDIFF_RUNNER_REPORT'
    )
    $previous = @{}
    foreach ($name in $environmentNames) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    $exitCode = -1
    try {
        $env:BEHAVIORDIFF_TRACE = $trace
        $env:BEHAVIORDIFF_NAMESPACES = 'samples/NodeReference/src'
        $env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = $changedFile
        $env:BEHAVIORDIFF_REPOSITORY_ROOT = $tree
        $env:BEHAVIORDIFF_RUNNER_REPORT = $report
        Push-Location $tree
        try {
            & node --require $register $runner 2>&1 | ForEach-Object { Write-Host $_ }
            $exitCode = $LASTEXITCODE
        } finally { Pop-Location }
    } finally {
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
        }
    }

    if ($expectSuccess -and $exitCode -ne 0) { throw "Expected Node reference tests to pass ($label), exit=$exitCode" }
    if (-not $expectSuccess -and $exitCode -eq 0) { throw "Mutated Node reference tests unexpectedly passed ($label)" }
    if ($expectSuccess) {
        if (-not (Test-Path $report -PathType Leaf)) { throw "Node runner report missing ($label)" }
        $runnerTests = [int](Get-Content $report -Raw | ConvertFrom-Json).runnerTests
        if ($runnerTests -ne 120) { throw "Node runner count mismatch ($label): expected=120 actual=$runnerTests" }
    } elseif (Test-Path $report -PathType Leaf) {
        throw 'Mutated Node runner reached its success report after the expected final assertion failure'
    }

    $run = Read-BehaviorDiffConformanceRun $runDirectory
    $accounting = Assert-RunAccounting $run $label
    if ($accounting.SubjectEvents -lt 100 -or $accounting.RootEvents -ne 120) {
        throw "Node comparison volume too small ($label): subjectEvents=$($accounting.SubjectEvents) roots=$($accounting.RootEvents)"
    }
    return [pscustomobject]@{
        Run = $run
        Accounting = $accounting
        TestExitCode = $exitCode
    }
}

try {
    if (-not (Test-Path $dotnet -PathType Leaf)) { throw "Local dotnet was not found: $dotnet" }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $baseTree = Join-Path $work 'base'
    $prTree = Join-Path $work 'pr'
    Copy-ReferenceTree $baseTree
    Copy-ReferenceTree $prTree
    Add-SuffixConfig $baseTree 'done'
    Add-SuffixConfig $prTree 'changed'

    $baseRun1 = Join-Path $work 'base-run-1'
    $baseRun2 = Join-Path $work 'base-run-2'
    $prRun = Join-Path $work 'pr-run'
    Write-Host '=== Node base run 1 ===' -ForegroundColor Cyan
    $base1 = Run-Reference $baseTree $baseRun1 $true 'base run 1'
    Write-Host '=== Node base run 2 ===' -ForegroundColor Cyan
    $base2 = Run-Reference $baseTree $baseRun2 $true 'base run 2'
    Write-Host '=== Node mutated PR run ===' -ForegroundColor Cyan
    $pr = Run-Reference $prTree $prRun $false 'PR run'

    $engineProject = Join-Path $repo 'src/BehaviorDiff.Engine/BehaviorDiff.Engine.csproj'
    & $dotnet build $engineProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed' }

    $divergencesPath = Join-Path $work 'divergence-set.json'
    & $dotnet run --project $engineProject -c Release --no-build -- diff `
        --base1 $baseRun1 --base2 $baseRun2 --pr $prRun --out $divergencesPath `
        --base-root $baseTree --pr-root $prTree
    if ($LASTEXITCODE -ne 0) { throw "Node attribution diff failed: $LASTEXITCODE" }
    $divergences = Get-Content $divergencesPath -Raw | ConvertFrom-Json
    if ($divergences.counts.matchedKeys -lt 100 -or $divergences.counts.remainingDivergences -lt 1) {
        throw "Node diff volume/divergence guard failed: matched=$($divergences.counts.matchedKeys) diverged=$($divergences.counts.remainingDivergences)"
    }

    $changedFilesPath = Join-Path $work 'changed-files.txt'
    $changedFile | Set-Content $changedFilesPath
    $frontierPath = Join-Path $work 'frontier.json'
    & $dotnet run --project $engineProject -c Release --no-build -- frontier `
        --in $divergencesPath --changed-files $changedFilesPath --out $frontierPath
    if ($LASTEXITCODE -ne 0) { throw "Node attribution frontier failed (path-attribution refusal): $LASTEXITCODE" }

    $frontier = Get-Content $frontierPath -Raw | ConvertFrom-Json
    $inputFiles = @($frontier.attributionInputs.changedFiles)
    $coverage = @($frontier.changedFileCoverage.files | Where-Object filePath -eq $changedFile)
    if ($inputFiles.Count -ne 1 -or $inputFiles[0] -cne $changedFile) {
        throw "Expected exactly changed file '$changedFile', got '$($inputFiles -join ', ')'"
    }
    if ($frontier.counts.unexpected -lt 1) {
        throw "Attribution mismatch: expected=$($frontier.counts.expected) unexpected=$($frontier.counts.unexpected)"
    }
    if ($coverage.Count -ne 1 -or [bool]$coverage[0].exercised -or
        $frontier.attributionInputs.changedPathsMatchingATracedFile -ne 0 -or
        $frontier.attributionInputs.changedPathsInTracePathNamespace -ne 1) {
        throw "Excluded changed-file coverage was not represented honestly for '$changedFile'"
    }

    $findingsPath = Join-Path $work 'findings.json'
    & $dotnet run --project $engineProject -c Release --no-build -- findings `
        --divergences $divergencesPath --frontier $frontierPath --out $findingsPath --exit-code 0 `
        --base-sha node-proof-base --pr-sha node-proof-pr --merge-base node-proof-base
    if ($LASTEXITCODE -ne 0) { throw "Node attribution findings failed: $LASTEXITCODE" }
    $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json
    if ($findings.summary.unexpectedMembers -lt 1) {
        throw "Findings attribution mismatch: expected=$($findings.summary.expectedMembers) unexpected=$($findings.summary.unexpectedMembers)"
    }
    $downstream = @($findings.members | Where-Object {
        $_.attribution -ceq 'unexpected' -and $_.filePath -ceq $subjectFile
    })
    if ($downstream.Count -lt 1) { throw 'Findings lost the downstream subject.js behavior change' }

    $rendered = @($downstream | Where-Object memberName -Match '#(AsyncSettlement\.settle|promiseWorkflow)$')
    if ($rendered.Count -eq 0) { $rendered = @($downstream | Select-Object -First 1) }
    $renderedMember = [string]$rendered[0].memberName
    $commentPath = Join-Path $work 'comment.md'
    $comment = & $dotnet run --project (Join-Path $repo 'tools/CommentPreview/BehaviorDiff.CommentPreview.csproj') `
        -c Release -- $findingsPath
    if ($LASTEXITCODE -ne 0) { throw "Node comment rendering failed: $LASTEXITCODE" }
    $commentText = $comment -join "`n"
    $commentText | Set-Content $commentPath
    if ($commentText -notmatch 'BehaviorDiff: [1-9][0-9]* test-covered behavior change' -or
        $commentText -notmatch 'samples/NodeReference/src/subject\.js#(AsyncSettlement\.settle|promiseWorkflow)' -or
        $commentText -notmatch 'node-reference/promise-chain' -or
        $commentText -notmatch 'samples/NodeReference/src/subject\.js' -or
        $commentText -match 'SampleApp|io\.behaviordiff\.reference|\.java') {
        throw 'Node comment rendering retained a .NET/Java-shaped assumption or lost the Node evidence'
    }

    Write-Host '=== Node attribution proof ===' -ForegroundColor Green
    Write-Host "  changed files             : $($inputFiles.Count) ($($inputFiles -join ', '))"
    Write-Host "  events base1/base2/pr     : $($base1.Accounting.Events) / $($base2.Accounting.Events) / $($pr.Accounting.Events)"
    Write-Host "  matched / diverged keys   : $($divergences.counts.matchedKeys) / $($frontier.counts.divergedKeys)"
    Write-Host "  expected / unexpected     : $($frontier.counts.expected) / $($frontier.counts.unexpected)"
    Write-Host "  path-attribution refusals : 0"
    Write-Host "  exercised edited files    : $($frontier.changedFileCoverage.summary.exercisedEditedFiles) / $($frontier.changedFileCoverage.summary.editedFiles) (intentionally skipped helper)"
    Write-Host "  findings member count     : $($findings.members.Count) (unexpected=$($findings.summary.unexpectedMembers))"
    Write-Host "  rendered comment member   : $renderedMember"
    Write-Host 'verify-node-attribution: PASS' -ForegroundColor Green
} finally {
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "attribution work retained at $work" }
}