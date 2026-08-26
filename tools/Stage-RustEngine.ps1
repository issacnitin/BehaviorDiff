#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/rust-engine'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
}
$manifest = Join-Path $repo 'src/RealDiff.Engine.Rust/Cargo.toml'

& cargo build --release --locked --manifest-path $manifest
if ($LASTEXITCODE -ne 0) {
    throw "Rust engine build failed with exit code $LASTEXITCODE"
}

$os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else {
    throw 'The Rust engine cannot be staged for this operating system.'
}
$architectureName = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$architecture = switch ($architectureName) {
    'x64' { 'x64' }
    'arm64' { 'arm64' }
    default { throw "The Rust engine cannot be staged for architecture $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)." }
}
$fileName = if ($IsWindows) { 'realdiff-engine.exe' } else { 'realdiff-engine' }
$source = Join-Path $repo "src/RealDiff.Engine.Rust/target/release/$fileName"
if (-not (Test-Path $source -PathType Leaf)) {
    throw "The Rust engine binary was not found: $source"
}

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}
$destination = Join-Path $output "$os-$architecture"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item $source (Join-Path $destination $fileName) -Force
Write-Host "Rust engine staged: $destination" -ForegroundColor Green