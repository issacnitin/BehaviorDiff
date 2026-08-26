#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$WorkDirectory,
    [string]$BehaviorDiffCommand
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-config-proof-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$fixture = Join-Path $work 'repository'
$analysisWork = Join-Path $work 'analysis'
$findings = Join-Path $work 'findings.json'
$project = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'
$previousNodeTracer = $env:BEHAVIORDIFF_NODE_TRACER

function Invoke-BehaviorDiff([string[]]$arguments) {
    if ([string]::IsNullOrWhiteSpace($BehaviorDiffCommand)) {
        & dotnet $cli @arguments
    } else {
        & $BehaviorDiffCommand @arguments
    }
}

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$label failed with exit code $LASTEXITCODE" }
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    Get-ChildItem (Join-Path $repo 'samples/NodeReference') -Force | Where-Object Name -notin @('node_modules', 'dist') |
        ForEach-Object { Copy-Item $_.FullName $fixture -Recurse -Force }
    Push-Location $fixture
    try {
        Invoke-Checked 'lockfile generation' { & npm install --package-lock-only --ignore-scripts --no-audit --no-fund }
        New-Item -ItemType Directory -Path '.behaviordiff' -Force | Out-Null
        @'
language: node
build: node -e "require('fs').writeFileSync('build.marker','configured')" && npm ci && npm run build --if-present
test: npm test
include_namespaces:
  - src
exclude_namespaces:
  - src/generated
redaction:
  names:
    - custom_password
  types:
    - SecretEnvelope
  paths:
    - generated
baseline:
  schema: behaviordiff.baseline/2
  acknowledgements: []
  ignorePaths: []
  ignoreMembers: []
'@ | Set-Content '.behaviordiff/config.yml'
        Invoke-Checked 'git init' { & git init --initial-branch=main --quiet }
        Invoke-Checked 'git identity' { & git config user.email 'config-proof@example.invalid' }
        Invoke-Checked 'git identity' { & git config user.name 'BehaviorDiff Config Proof' }
        Invoke-Checked 'git add' { & git add . }
        Invoke-Checked 'base commit' { & git commit --quiet -m 'configured base' }
        $base = (& git rev-parse HEAD).Trim()
        Invoke-Checked 'empty PR commit' { & git commit --quiet --allow-empty -m 'configured pr' }
        $pr = (& git rev-parse HEAD).Trim()
    }
    finally { Pop-Location }

    Invoke-Checked 'CLI build' { & dotnet build $project -c Release --nologo -v quiet }
    $nodeTracer = Join-Path $repo 'src/BehaviorDiff.Node'
    Invoke-Checked 'Node tracer install' { & npm ci --prefix $nodeTracer --ignore-scripts --no-audit --no-fund }
    $env:BEHAVIORDIFF_NODE_TRACER = $nodeTracer
    $output = @(Invoke-BehaviorDiff @($fixture, '--base', $base, '--pr', $pr, '--work', $analysisWork,
        '--findings', $findings, '--no-cache', '--keep', '--keep-traces', '1d') 2>&1)
    $exit = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exit -ne 0) { throw "configured analysis exited $exit" }
    $configuredBuilds = @($output | Where-Object { $_ -match '^  (base|pr) configured command:' }).Count
    if ($configuredBuilds -ne 2) {
        throw "expected two configured build invocations, got $configuredBuilds"
    }
    $document = Get-Content $findings -Raw | ConvertFrom-Json
    if ($document.status -ne 'analyzed' -or -not $document.isCleanResult) {
        throw "configured findings were not clean analyzed output"
    }
    $eventCount = @(Get-ChildItem $analysisWork -Recurse -File -Filter 'run.*.ndjson' |
        Where-Object Name -NotLike '*.manifest.ndjson' |
        ForEach-Object { Get-Content $_.FullName }).Count
    if ($eventCount -le 0) { throw 'configured test command produced no trace events' }

    (Get-Content (Join-Path $fixture '.behaviordiff/config.yml') -Raw).Replace('test: npm test', 'test: node -e "process.exit(0)"') |
        Set-Content (Join-Path $fixture '.behaviordiff/config.yml')
    Push-Location $fixture
    try {
        Invoke-Checked 'bypass base commit' { & git add .behaviordiff/config.yml; git commit --quiet -m 'bypass tests' }
        $bypassBase = (& git rev-parse HEAD).Trim()
        Invoke-Checked 'bypass PR commit' { & git commit --quiet --allow-empty -m 'bypass pr' }
        $bypassPr = (& git rev-parse HEAD).Trim()
    }
    finally { Pop-Location }
    $bypassFindings = Join-Path $work 'bypass-findings.json'
    $bypassOutput = @(Invoke-BehaviorDiff @($fixture, '--base', $bypassBase, '--pr', $bypassPr,
        '--work', (Join-Path $work 'bypass-work'), '--findings', $bypassFindings, '--no-cache') 2>&1)
    $bypassExit = $LASTEXITCODE
    if ($bypassExit -ne 3) {
        throw "instrumentation bypass exit was $bypassExit, expected 3: $($bypassOutput -join "`n")"
    }
    $bypassText = $bypassOutput -join "`n"
    if ($bypassText -notmatch 'NO EVENTS: base_run1 produced 1 trace file\(s\), 0 event\(s\), and 1 manifest\(s\)' `
        -or $bypassText -notmatch 'configured command: node -e "process.exit\(0\)"') {
        throw "instrumentation bypass refusal omitted the zero-event/configured-command evidence: $bypassText"
    }

    Write-Host 'Repository custom command execution: PASS' -ForegroundColor Green
    Write-Host "  builds=$configuredBuilds tracedEvents=$eventCount bypassExit=$bypassExit"
}
finally {
    $env:BEHAVIORDIFF_NODE_TRACER = $previousNodeTracer
    if ($ownsWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
