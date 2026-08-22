#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-language-invocation-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$cliProject = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'
$agentProject = Join-Path $repo 'src/BehaviorDiff.Java.Agent/pom.xml'
$nodeTracer = Join-Path $repo 'src/BehaviorDiff.Node'
$previousJavaAgent = $env:BEHAVIORDIFF_JAVA_AGENT
$previousNodeTracer = $env:BEHAVIORDIFF_NODE_TRACER

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$label failed with exit code $LASTEXITCODE" }
}

function New-ReferenceRepository([string]$name, [string]$sample, [bool]$createNodeLock) {
    $directory = Join-Path $work "$name-repository"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Get-ChildItem $sample -Force | ForEach-Object {
        Copy-Item $_.FullName -Destination $directory -Recurse -Force
    }
    Get-ChildItem $directory -Directory -Recurse -Force |
        Where-Object Name -In @('target', 'node_modules', 'dist') |
        Sort-Object { $_.FullName.Length } -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    if ($createNodeLock) {
        Push-Location $directory
        try {
            Invoke-Checked 'Node reference lockfile generation' {
                & npm install --package-lock-only --ignore-scripts --no-audit --no-fund
            }
        } finally { Pop-Location }
    }

    Invoke-Checked "$name git init" { & git -C $directory init --initial-branch=main --quiet }
    Invoke-Checked "$name git identity" { & git -C $directory config user.email 'behaviordiff-proof@example.invalid' }
    Invoke-Checked "$name git identity" { & git -C $directory config user.name 'BehaviorDiff Proof' }
    Invoke-Checked "$name git add" { & git -C $directory add . }
    Invoke-Checked "$name base commit" { & git -C $directory commit --quiet -m 'reference base' }
    $base = (& git -C $directory rev-parse HEAD).Trim()
    Invoke-Checked "$name no-op PR commit" { & git -C $directory commit --quiet --allow-empty -m 'reference pr (no semantic mutation)' }
    $pr = (& git -C $directory rev-parse HEAD).Trim()
    return [pscustomobject]@{ Directory = $directory; Base = $base; Pr = $pr }
}

function Assert-RunArtifacts(
    [string]$language,
    [string]$runWork,
    [string]$findingsPath,
    [object[]]$output) {
    $text = $output -join "`n"
    if ($text -notmatch "language\s+: $language") {
        throw "$language CLI output did not report its detected language"
    }
    if ($language -eq 'java' -and ($text -notmatch 'command: mvn(?:w(?:\.cmd)?)? package' `
        -or $text -notmatch 'command: mvn(?:w(?:\.cmd)?)? test')) {
        throw 'Java CLI output did not prove Maven package and test invocation'
    }
    if ($language -eq 'node' -and ($text -notmatch 'command: npm ci' -or $text -notmatch 'command: npm test')) {
        throw 'Node CLI output did not prove npm ci and npm test invocation'
    }

    $allEvents = @()
    foreach ($runName in @('base_run1', 'base_run2', 'base_run3', 'pr_run')) {
        $runDirectory = Join-Path $runWork $runName
        if (-not (Test-Path $runDirectory -PathType Container)) {
            throw "$language run directory is missing: $runName"
        }
        $traces = @(Get-ChildItem $runDirectory -Filter 'run.*.ndjson' |
            Where-Object Name -NotLike '*.manifest.ndjson')
        $manifests = @(Get-ChildItem $runDirectory -Filter 'run.*.manifest.ndjson')
        if ($traces.Count -eq 0 -or $manifests.Count -eq 0) {
            throw "$language $runName did not contain trace and manifest output"
        }
        $runLanguages = @($manifests | ForEach-Object {
            Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } |
                Where-Object kind -eq 'run' | ForEach-Object language
        } | Sort-Object -Unique)
        if ($runLanguages.Count -ne 1 -or $runLanguages[0] -ne $language) {
            throw "$language $runName manifest language mismatch: $($runLanguages -join ', ')"
        }
        $allEvents += @($traces | ForEach-Object {
            Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json }
        })
    }

    $sourcePaths = @($allEvents | Where-Object {
        $null -ne $_.PSObject.Properties['filePath'] -and
            -not [string]::IsNullOrWhiteSpace([string]$_.filePath)
    } | ForEach-Object { [string]$_.filePath } | Sort-Object -Unique)
    $sourcePattern = if ($language -eq 'java') { '^src/(main|test)/java/.+\.java$' } else { '^src/.+\.(c?js|mjs)$' }
    $wrongPaths = @($sourcePaths | Where-Object { $_ -notmatch $sourcePattern })
    if ($sourcePaths.Count -eq 0 -or $wrongPaths.Count -ne 0) {
        throw "$language source-root mismatch: sources=$($sourcePaths.Count) wrong=$($wrongPaths -join ', ')"
    }

    $changedFiles = @(Get-Content (Join-Path $runWork 'changed-files.txt'))
    if ($changedFiles.Count -ne 0) {
        throw "$language no-op refs unexpectedly reported changed files: $($changedFiles -join ', ')"
    }
    $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json
    if ($findings.status -ne 'analyzed' -or -not [bool]$findings.isCleanResult `
        -or [int]$findings.summary.unexpectedMembers -ne 0) {
        throw "$language findings were not analyzed cleanly: status=$($findings.status) unexpected=$($findings.summary.unexpectedMembers)"
    }

    Write-Host ("  {0}: runs=4 events={1} sources={2} unexpected=0" -f $language, $allEvents.Count, $sourcePaths.Count)
}

function Invoke-LanguageProof([string]$language, [object]$reference) {
    $runWork = Join-Path $work "$language-work"
    $findings = Join-Path $work "$language-findings.json"
    $output = @(& dotnet $cli $reference.Directory --base $reference.Base --pr $reference.Pr `
        --work $runWork --findings $findings --keep-traces 1d 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "$language CLI invocation failed with exit code $exitCode"
    }
    Assert-RunArtifacts $language $runWork $findings $output
}

try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Write-Host '=== Build CLI ===' -ForegroundColor Cyan
    Invoke-Checked 'CLI build' { & dotnet build $cliProject -c Release --nologo -v quiet }

    Write-Host '=== Build Java agent ===' -ForegroundColor Cyan
    Invoke-Checked 'Java agent build' {
        & mvn --batch-mode --no-transfer-progress -f $agentProject package -DskipTests
    }
    $agent = Get-ChildItem (Join-Path $repo 'src/BehaviorDiff.Java.Agent/target') `
        -Filter 'behaviordiff-java-agent-*.jar' |
        Where-Object Name -NotLike 'original-*' |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $agent) { throw 'Built Java agent jar was not found' }

    Write-Host '=== Install Node tracer ===' -ForegroundColor Cyan
    Push-Location $nodeTracer
    try {
        Invoke-Checked 'Node tracer install' { & npm ci --no-audit --no-fund }
    } finally { Pop-Location }

    $env:BEHAVIORDIFF_JAVA_AGENT = $agent.FullName
    $env:BEHAVIORDIFF_NODE_TRACER = $nodeTracer
    $java = New-ReferenceRepository 'java' (Join-Path $repo 'samples/JavaReference') $false
    $node = New-ReferenceRepository 'node' (Join-Path $repo 'samples/NodeReference') $true

    Write-Host '=== Java CLI invocation ===' -ForegroundColor Cyan
    Invoke-LanguageProof 'java' $java
    Write-Host '=== Node CLI invocation ===' -ForegroundColor Cyan
    Invoke-LanguageProof 'node' $node

    Write-Host 'Per-language CLI invocation: PASS' -ForegroundColor Green
    Write-Host '  Java Maven and Node npm; identical refs; four runs each; analyzed with zero unexpected findings'
}
finally {
    $env:BEHAVIORDIFF_JAVA_AGENT = $previousJavaAgent
    $env:BEHAVIORDIFF_NODE_TRACER = $previousNodeTracer
    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Language invocation work kept at $work"
    }
}