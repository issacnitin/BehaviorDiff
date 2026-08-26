#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-java-attribution-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$changedFile = 'samples/JavaReference/src/main/java/io/realdiff/reference/config/OffsetConfig.java'

function Copy-ReferenceTree([string]$destination) {
    $sample = Join-Path $destination 'samples/JavaReference'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sample) -Force | Out-Null
    Copy-Item (Join-Path $repo 'samples/JavaReference') $sample -Recurse -Force
    Remove-Item (Join-Path $sample 'target') -Recurse -Force -ErrorAction SilentlyContinue
}

function Add-OffsetConfig([string]$tree, [int]$offset) {
    $config = Join-Path $tree $changedFile
    New-Item -ItemType Directory -Path (Split-Path -Parent $config) -Force | Out-Null
    [IO.File]::WriteAllText($config, @"
package io.realdiff.reference.config;

public final class OffsetConfig {
    private OffsetConfig() { }
    public static int offset() { return $offset; }
}
"@)

    $subjectPath = Join-Path $tree 'samples/JavaReference/src/main/java/io/realdiff/reference/Subject.java'
    $subject = Get-Content $subjectPath -Raw
    $subject = $subject.Replace(
        'import java.util.concurrent.CompletableFuture;',
        "import java.util.concurrent.CompletableFuture;`r`nimport io.realdiff.reference.config.OffsetConfig;")
    $subject = $subject.Replace(
        'public static int observe(int value) { return value * 2 + 1; }',
        'public static int observe(int value) { return value * 2 + OffsetConfig.offset(); }')
    Set-Content $subjectPath $subject -NoNewline
}

function Run-Reference(
    [string]$tree,
    [string]$agent,
    [string]$runDirectory,
    [bool]$expectSuccess) {
    Remove-Item $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $trace = Join-Path $runDirectory 'run.ndjson'
    $argLine = "--add-opens java.base/java.util=ALL-UNNAMED -javaagent:$agent=include=io.realdiff.reference;trace=$trace"
    $previousRepositoryRoot = [Environment]::GetEnvironmentVariable('REALDIFF_REPOSITORY_ROOT', 'Process')
    $previousExcludes = [Environment]::GetEnvironmentVariable('REALDIFF_EXCLUDE_NAMESPACES', 'Process')
    try {
        $env:REALDIFF_REPOSITORY_ROOT = $tree
        $env:REALDIFF_EXCLUDE_NAMESPACES = 'io.realdiff.reference.config'
        & mvn -f (Join-Path $tree 'samples/JavaReference/pom.xml') test "-DargLine=$argLine" |
            ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable(
            'REALDIFF_REPOSITORY_ROOT', $previousRepositoryRoot, 'Process')
        [Environment]::SetEnvironmentVariable(
            'REALDIFF_EXCLUDE_NAMESPACES', $previousExcludes, 'Process')
    }

    if ($expectSuccess -and $exitCode -ne 0) {
        throw "Expected Java reference tests to pass, exit=$exitCode"
    }
    if (-not $expectSuccess -and $exitCode -eq 0) {
        throw 'Mutated Java reference tests unexpectedly passed'
    }

    $events = @(Get-ChildItem $runDirectory -Filter '*.ndjson' |
        Where-Object Name -NotLike '*.manifest.ndjson' |
        ForEach-Object { Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } })
    $wrongPaths = @($events | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.filePath) -or
        $_.filePathResolution -ne 'debugInfo' -or
        [string]$_.filePath -notmatch '^samples/JavaReference/src/(main|test)/java/.+\.java$'
    })
    if ($wrongPaths.Count -ne 0) {
        throw "Run emitted $($wrongPaths.Count) unresolved or non-repository Java source path(s)"
    }

    return [pscustomobject]@{
        Events = $events.Count
        ExactSourceEvents = $events.Count - $wrongPaths.Count
        TestExitCode = $exitCode
    }
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $baseTree = Join-Path $work 'base'
    $prTree = Join-Path $work 'pr'
    Copy-ReferenceTree $baseTree
    Copy-ReferenceTree $prTree
    Add-OffsetConfig $baseTree 1
    Add-OffsetConfig $prTree 2

    Write-Host '=== Java agent build ===' -ForegroundColor Cyan
    & mvn -f (Join-Path $repo 'src/RealDiff.Java.Agent/pom.xml') clean package
    if ($LASTEXITCODE -ne 0) { throw 'Java agent build failed' }
    $agent = Join-Path $repo 'src/RealDiff.Java.Agent/target/realdiff-java-agent-0.2.0-SNAPSHOT.jar'

    $baseRun1 = Join-Path $work 'base-run-1'
    $baseRun2 = Join-Path $work 'base-run-2'
    $prRun = Join-Path $work 'pr-run'
    Write-Host '=== Java base run 1 ===' -ForegroundColor Cyan
    $base1 = Run-Reference $baseTree $agent $baseRun1 $true
    Write-Host '=== Java base run 2 ===' -ForegroundColor Cyan
    $base2 = Run-Reference $baseTree $agent $baseRun2 $true
    Write-Host '=== Java mutated PR run ===' -ForegroundColor Cyan
    $pr = Run-Reference $prTree $agent $prRun $false

    Import-Module (Join-Path $PSScriptRoot 'RealDiff.Engine.psm1') -Force
    $engine = Get-RealDiffEngine

    $divergencesPath = Join-Path $work 'divergence-set.json'
    & $engine stream-diff --base1 $baseRun1 --base2 $baseRun2 --base3 $baseRun1 `
        --pr $prRun --out $divergencesPath
    if ($LASTEXITCODE -ne 0) { throw "Java attribution diff failed: $LASTEXITCODE" }

    $changedFilesPath = Join-Path $work 'changed-files.txt'
    $changedFile | Set-Content $changedFilesPath
    $frontierPath = Join-Path $work 'frontier.json'
    & $engine frontier --in $divergencesPath --changed-files $changedFilesPath --out $frontierPath
    if ($LASTEXITCODE -ne 0) { throw "Java attribution frontier failed: $LASTEXITCODE" }

    $frontier = Get-Content $frontierPath -Raw | ConvertFrom-Json
    $inputFiles = @($frontier.attributionInputs.changedFiles)
    $coverage = @($frontier.changedFileCoverage.files | Where-Object filePath -eq $changedFile)
    if ($inputFiles.Count -ne 1 -or $inputFiles[0] -cne $changedFile) {
        throw "Expected exactly changed file '$changedFile', got '$($inputFiles -join ', ')'"
    }
    if ($frontier.counts.unexpected -lt 1) {
        throw "Attribution mismatch: expected=$($frontier.counts.expected) unexpected=$($frontier.counts.unexpected)"
    }
    if ($coverage.Count -ne 1 -or [bool]$coverage[0].exercised `
        -or $frontier.attributionInputs.changedPathsMatchingATracedFile -ne 0 `
        -or $frontier.attributionInputs.changedPathsInTracePathNamespace -ne 1) {
        throw "Excluded changed-file coverage was not represented honestly for '$changedFile'"
    }

    $findingsPath = Join-Path $work 'findings.json'
    & $engine findings --divergences $divergencesPath --frontier $frontierPath --out $findingsPath --exit-code 0 `
        --base-sha java-proof-base --pr-sha java-proof-pr --merge-base java-proof-base
    if ($LASTEXITCODE -ne 0) { throw "Java attribution findings failed: $LASTEXITCODE" }
    $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json
    if ($findings.summary.unexpectedMembers -ne 1) {
        throw "Findings attribution mismatch: expected=$($findings.summary.expectedMembers) unexpected=$($findings.summary.unexpectedMembers)"
    }

    $commentPath = Join-Path $work 'comment.md'
    $comment = & dotnet run --project (Join-Path $repo 'tools/CommentPreview/RealDiff.CommentPreview.csproj') `
        -c Release -- $findingsPath
    if ($LASTEXITCODE -ne 0) { throw "Java comment rendering failed: $LASTEXITCODE" }
    $commentText = $comment -join "`n"
    $commentText | Set-Content $commentPath
    if ($commentText -notmatch 'io\.realdiff\.reference\.Subject\.observe\(I\)I' `
        -or $commentText -notmatch 'RealDiff: 1 test-covered behavior change outside this diff' `
        -or $commentText -notmatch 'ReferenceTests\.volume\(I\)V' `
        -or $commentText -match 'SampleApp') {
        throw 'Java comment rendering retained a .NET-shaped assumption or lost the Java member'
    }

    Write-Host '=== Java attribution proof ===' -ForegroundColor Green
    Write-Host "  changed files             : $($inputFiles.Count) ($($inputFiles -join ', '))"
    Write-Host "  events base1/base2/pr     : $($base1.Events) / $($base2.Events) / $($pr.Events)"
    Write-Host "  exact source events       : $($base1.ExactSourceEvents + $base2.ExactSourceEvents + $pr.ExactSourceEvents)"
    Write-Host "  remaining divergences     : $($frontier.counts.divergedKeys)"
    Write-Host "  expected / unexpected     : $($frontier.counts.expected) / $($frontier.counts.unexpected)"
    Write-Host "  exercised edited files    : $($frontier.changedFileCoverage.summary.exercisedEditedFiles) / $($frontier.changedFileCoverage.summary.editedFiles) (excluded helper)"
    Write-Host "  findings unexpected members: $($findings.summary.unexpectedMembers)"
    Write-Host "  rendered comment member   : io.realdiff.reference.Subject.observe(I)I"
    Write-Host 'verify-java-attribution: PASS' -ForegroundColor Green
} finally {
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "attribution work retained at $work" }
}