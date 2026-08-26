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
$javaProject = Join-Path $repo 'src/RealDiff.Java.Agent/pom.xml'
$javaTarget = Join-Path $repo 'src/RealDiff.Java.Agent/target'
$nodeSource = Join-Path $repo 'src/RealDiff.Node'
$goModule = Join-Path $repo 'src/RealDiff.Go'
$rustManifest = Join-Path $repo 'src/RealDiff.Rust.Tracer/Cargo.toml'
$pythonSource = Join-Path $repo 'src/RealDiff.Python'

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
$javaAgent = Get-ChildItem $javaTarget -Filter 'realdiff-java-agent-*.jar' -File |
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
$pythonOutput = Join-Path $output 'python'
$rustRid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { 'osx-x64' }
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) {
    $rustRid = $rustRid.Replace('x64', 'arm64')
}
$rustOutput = Join-Path $output "rust/$rustRid"
$goOutput = Join-Path $output "go/$rustRid"
New-Item -ItemType Directory -Path $javaOutput, $nodeOutput, $pythonOutput, $goOutput, $rustOutput -Force | Out-Null
Copy-Item $javaAgent.FullName (Join-Path $javaOutput 'realdiff-java-agent.jar') -Force

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
    'java/realdiff-java-agent.jar',
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

Write-Host '=== Stage Python tracer ===' -ForegroundColor Cyan
Copy-Item (Join-Path $pythonSource 'sitecustomize.py') $pythonOutput -Force
Copy-Item (Join-Path $pythonSource 'realdiff_python') $pythonOutput -Recurse -Force
Get-ChildItem $pythonOutput -Recurse -Directory -Filter '__pycache__' -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force
if (Test-Path (Join-Path $pythonOutput 'tests')) {
    throw 'The staged Python tracer unexpectedly contains tests.'
}
foreach ($relative in @(
    'python/sitecustomize.py',
    'python/realdiff_python/__init__.py',
    'python/realdiff_python/monitor.py',
    'python/realdiff_python/runtime.py',
    'python/realdiff_python/canonical.py',
    'python/realdiff_python/pytest_plugin.py'
)) {
    Assert-Path (Join-Path $output $relative) "Staged tracer file '$relative'"
}

Write-Host '=== Build Go tracer ===' -ForegroundColor Cyan
$goName = if ($IsWindows) { 'realdiff-go-rewrite.exe' } else { 'realdiff-go-rewrite' }
$goBinary = Join-Path $goOutput $goName
Push-Location $goModule
try {
    Invoke-Checked 'Go tracer build' {
        & go build -trimpath -o $goBinary ./cmd/realdiff-go-rewrite
    }
}
finally {
    Pop-Location
}
Assert-Path $goBinary 'Staged Go tracer binary'

Write-Host '=== Build Rust tracer ===' -ForegroundColor Cyan
Invoke-Checked 'Rust tracer build' { & cargo build --release --locked --manifest-path $rustManifest }
$rustName = if ($IsWindows) { 'realdiff-rust-rewrite.exe' } else { 'realdiff-rust-rewrite' }
$rustBinary = Join-Path $repo "src/RealDiff.Rust.Tracer/target/release/$rustName"
Assert-Path $rustBinary 'Rust tracer binary'
Copy-Item $rustBinary (Join-Path $rustOutput $rustName) -Force
Assert-Path (Join-Path $rustOutput $rustName) 'Staged Rust tracer binary'

Write-Host 'Cross-language tracers staged:' -ForegroundColor Green
Write-Host "  $output"
