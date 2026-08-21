#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/cross-language-tracers'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
}
$javaProject = Join-Path $repo 'src/BehaviorDiff.Java.Agent/pom.xml'
$javaTarget = Join-Path $repo 'src/BehaviorDiff.Java.Agent/target'
$nodeSource = Join-Path $repo 'src/BehaviorDiff.Node'

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$label failed with exit code $LASTEXITCODE"
    }
}

function Assert-Path([string]$path, [string]$label, [switch]$Container) {
    $pathType = if ($Container) { 'Container' } else { 'Leaf' }
    if (-not (Test-Path $path -PathType $pathType)) {
        throw "$label was not found: $path"
    }
}

Write-Host '=== Build Java agent ===' -ForegroundColor Cyan
Invoke-Checked 'Java agent build' {
    & mvn --batch-mode --no-transfer-progress -f $javaProject package -DskipTests
}
$javaAgent = Get-ChildItem $javaTarget -Filter 'behaviordiff-java-agent-*.jar' -File |
    Where-Object Name -NotLike 'original-*' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $javaAgent) {
    throw "The shaded Java agent was not found under $javaTarget"
}

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}
$javaOutput = Join-Path $output 'java'
$nodeOutput = Join-Path $output 'node'
New-Item -ItemType Directory -Path $javaOutput, $nodeOutput -Force | Out-Null
Copy-Item $javaAgent.FullName (Join-Path $javaOutput 'behaviordiff-java-agent.jar') -Force

Write-Host '=== Stage Node tracer ===' -ForegroundColor Cyan
$nodeFiles = @('register.cjs', 'loader.mjs', 'bootstrap.mjs', 'package.json', 'package-lock.json')
foreach ($relative in $nodeFiles) {
    $source = Join-Path $nodeSource $relative
    Assert-Path $source "Node tracer file '$relative'"
    Copy-Item $source (Join-Path $nodeOutput $relative) -Force
}
foreach ($relative in @('src', 'adapters')) {
    $source = Join-Path $nodeSource $relative
    Assert-Path $source "Node tracer directory '$relative'" -Container
    Copy-Item $source (Join-Path $nodeOutput $relative) -Recurse -Force
}

Push-Location $nodeOutput
try {
    Invoke-Checked 'Staged Node production install' {
        & npm ci --omit=dev --ignore-scripts --no-audit --no-fund
    }
} finally {
    Pop-Location
}
$nodeCommandShims = Join-Path $nodeOutput 'node_modules/.bin'
if (Test-Path $nodeCommandShims) {
    Remove-Item $nodeCommandShims -Recurse -Force
}

$required = @(
    'java/behaviordiff-java-agent.jar',
    'node/register.cjs',
    'node/loader.mjs',
    'node/bootstrap.mjs',
    'node/src/runtime.cjs',
    'node/src/canonicalize.cjs',
    'node/src/transform.cjs',
    'node/src/source-map.cjs',
    'node/adapters/jest.cjs',
    'node/adapters/vitest.mjs',
    'node/package.json',
    'node/package-lock.json',
    'node/node_modules/@babel/parser/package.json'
)
foreach ($relative in $required) {
    Assert-Path (Join-Path $output $relative) "Staged tracer file '$relative'"
}
if (Test-Path (Join-Path $nodeOutput 'test')) {
    throw 'The staged Node tracer unexpectedly contains tests.'
}

Write-Host 'Cross-language tracers staged:' -ForegroundColor Green
Write-Host "  $output"
