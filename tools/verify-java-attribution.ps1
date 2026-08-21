#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-java-attribution-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$changedFile = 'samples/JavaReference/src/main/java/io/behaviordiff/reference/Subject.java'

function Copy-ReferenceTree([string]$destination) {
    $sample = Join-Path $destination 'samples/JavaReference'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sample) -Force | Out-Null
    Copy-Item (Join-Path $repo 'samples/JavaReference') $sample -Recurse -Force
    Remove-Item (Join-Path $sample 'target') -Recurse -Force -ErrorAction SilentlyContinue
}

function Run-Reference(
    [string]$tree,
    [string]$agent,
    [string]$runDirectory,
    [bool]$expectSuccess) {
    Remove-Item $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $trace = Join-Path $runDirectory 'run.ndjson'
    $argLine = "--add-opens java.base/java.util=ALL-UNNAMED -javaagent:$agent=include=io.behaviordiff.reference;trace=$trace"
    $previousRepositoryRoot = [Environment]::GetEnvironmentVariable('BEHAVIORDIFF_REPOSITORY_ROOT', 'Process')
    try {
        $env:BEHAVIORDIFF_REPOSITORY_ROOT = $tree
        & mvn -f (Join-Path $tree 'samples/JavaReference/pom.xml') test "-DargLine=$argLine" |
            ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable(
            'BEHAVIORDIFF_REPOSITORY_ROOT', $previousRepositoryRoot, 'Process')
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

    $prSubject = Join-Path $prTree $changedFile
    $source = Get-Content $prSubject -Raw
    $before = 'public static int observe(int value) { return value * 2 + 1; }'
    $after = 'public static int observe(int value) { return value * 2 + 2; }'
    if ($source.IndexOf($before, [StringComparison]::Ordinal) -lt 0) {
        throw 'Subject.observe mutation anchor was not found'
    }
    Set-Content $prSubject ($source.Replace($before, $after)) -NoNewline

    Write-Host '=== Java agent build ===' -ForegroundColor Cyan
    & mvn -f (Join-Path $repo 'src/BehaviorDiff.Java.Agent/pom.xml') clean package
    if ($LASTEXITCODE -ne 0) { throw 'Java agent build failed' }
    $agent = Join-Path $repo 'src/BehaviorDiff.Java.Agent/target/behaviordiff-java-agent-0.2.0-SNAPSHOT.jar'

    $baseRun1 = Join-Path $work 'base-run-1'
    $baseRun2 = Join-Path $work 'base-run-2'
    $prRun = Join-Path $work 'pr-run'
    Write-Host '=== Java base run 1 ===' -ForegroundColor Cyan
    $base1 = Run-Reference $baseTree $agent $baseRun1 $true
    Write-Host '=== Java base run 2 ===' -ForegroundColor Cyan
    $base2 = Run-Reference $baseTree $agent $baseRun2 $true
    Write-Host '=== Java mutated PR run ===' -ForegroundColor Cyan
    $pr = Run-Reference $prTree $agent $prRun $false

    $engineProject = Join-Path $repo 'src/BehaviorDiff.Engine/BehaviorDiff.Engine.csproj'
    & dotnet build $engineProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed' }

    $divergencesPath = Join-Path $work 'divergence-set.json'
    & dotnet run --project $engineProject -c Release --no-build -- diff `
        --base1 $baseRun1 --base2 $baseRun2 --pr $prRun --out $divergencesPath
    if ($LASTEXITCODE -ne 0) { throw "Java attribution diff failed: $LASTEXITCODE" }

    $changedFilesPath = Join-Path $work 'changed-files.txt'
    $changedFile | Set-Content $changedFilesPath
    $frontierPath = Join-Path $work 'frontier.json'
    & dotnet run --project $engineProject -c Release --no-build -- frontier `
        --in $divergencesPath --changed-files $changedFilesPath --out $frontierPath
    if ($LASTEXITCODE -ne 0) { throw "Java attribution frontier failed: $LASTEXITCODE" }

    $frontier = Get-Content $frontierPath -Raw | ConvertFrom-Json
    $inputFiles = @($frontier.attributionInputs.changedFiles)
    $coverage = @($frontier.changedFileCoverage.files | Where-Object filePath -eq $changedFile)
    if ($inputFiles.Count -ne 1 -or $inputFiles[0] -cne $changedFile) {
        throw "Expected exactly changed file '$changedFile', got '$($inputFiles -join ', ')'"
    }
    if ($frontier.counts.expected -lt 1 -or $frontier.counts.unexpected -ne 0) {
        throw "Attribution mismatch: expected=$($frontier.counts.expected) unexpected=$($frontier.counts.unexpected)"
    }
    if ($coverage.Count -ne 1 -or -not [bool]$coverage[0].exercised) {
        throw "Changed-file coverage did not exercise '$changedFile'"
    }

    $findingsPath = Join-Path $work 'findings.json'
    & dotnet run --project $engineProject -c Release --no-build -- findings `
        --divergences $divergencesPath --frontier $frontierPath --out $findingsPath --exit-code 0 `
        --base-sha java-proof-base --pr-sha java-proof-pr --merge-base java-proof-base
    if ($LASTEXITCODE -ne 0) { throw "Java attribution findings failed: $LASTEXITCODE" }
    $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json
    if ($findings.summary.expectedMembers -lt 1 -or $findings.summary.unexpectedMembers -ne 0) {
        throw "Findings attribution mismatch: expected=$($findings.summary.expectedMembers) unexpected=$($findings.summary.unexpectedMembers)"
    }

    Write-Host '=== Java attribution proof ===' -ForegroundColor Green
    Write-Host "  changed files             : $($inputFiles.Count) ($($inputFiles -join ', '))"
    Write-Host "  events base1/base2/pr     : $($base1.Events) / $($base2.Events) / $($pr.Events)"
    Write-Host "  exact source events       : $($base1.ExactSourceEvents + $base2.ExactSourceEvents + $pr.ExactSourceEvents)"
    Write-Host "  remaining divergences     : $($frontier.counts.divergedKeys)"
    Write-Host "  expected / unexpected     : $($frontier.counts.expected) / $($frontier.counts.unexpected)"
    Write-Host "  exercised edited files    : $($frontier.changedFileCoverage.summary.exercisedEditedFiles) / $($frontier.changedFileCoverage.summary.editedFiles)"
    Write-Host "  findings expected members : $($findings.summary.expectedMembers)"
    Write-Host 'verify-java-attribution: PASS' -ForegroundColor Green
} finally {
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "attribution work retained at $work" }
}