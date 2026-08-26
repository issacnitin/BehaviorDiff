#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) 'realdiff-cross-language-consumer-proof'
$javaWork = Join-Path $work 'java'
$nodeWork = Join-Path $work 'node'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'

if (-not (Test-Path $dotnet -PathType Leaf)) { throw "Local dotnet was not found: $dotnet" }
$env:PATH = (Split-Path -Parent $dotnet) + [IO.Path]::PathSeparator + $env:PATH

if ($null -eq (Get-Command java -ErrorAction SilentlyContinue)) {
    $jdkRoot = Join-Path $HOME '.realdiff-tools/jdk'
    $jdk = Get-ChildItem $jdkRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $jdk) { throw 'Java was not found on PATH or under ~/.realdiff-tools/jdk' }
    $env:JAVA_HOME = $jdk.FullName
    $env:PATH = (Join-Path $jdk.FullName 'bin') + [IO.Path]::PathSeparator + $env:PATH
}

if ($null -eq (Get-Command mvn -ErrorAction SilentlyContinue)) {
    $mavenRoot = Join-Path $HOME '.realdiff-tools/maven'
    $maven = Get-ChildItem $mavenRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $maven) { throw 'Maven was not found on PATH or under ~/.realdiff-tools/maven' }
    $env:PATH = (Join-Path $maven.FullName 'bin') + [IO.Path]::PathSeparator + $env:PATH
}

if ($null -eq (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js was not found on PATH' }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work -Force | Out-Null

Write-Host '=== Regenerate Java attribution artifacts ===' -ForegroundColor Cyan
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-java-attribution.ps1') -WorkDirectory $javaWork
if ($LASTEXITCODE -ne 0) { throw "Java attribution proof failed: $LASTEXITCODE" }

Write-Host '=== Regenerate Node attribution artifacts ===' -ForegroundColor Cyan
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-node-attribution.ps1') -WorkDirectory $nodeWork
if ($LASTEXITCODE -ne 0) { throw "Node attribution proof failed: $LASTEXITCODE" }

Write-Host '=== Cross-language consumer proof ===' -ForegroundColor Cyan
$project = Join-Path $PSScriptRoot 'CrossLanguageConsumerProof/RealDiff.CrossLanguageConsumerProof.csproj'
& $dotnet run --project $project -c Release -- $javaWork $nodeWork
if ($LASTEXITCODE -ne 0) { throw "Cross-language consumer proof failed: $LASTEXITCODE" }

Write-Host "consumer proof artifacts retained at $work"
Write-Host 'verify-cross-language-consumers.ps1: PASS' -ForegroundColor Green