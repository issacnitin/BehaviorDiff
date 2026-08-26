#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/rust-launcher')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
}
$manifest = Join-Path $repo 'src/RealDiff.Launcher.Rust/Cargo.toml'

& cargo build --release --locked --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw "Rust launcher build failed with exit code $LASTEXITCODE" }

$os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else {
    throw 'The Rust launcher cannot be staged for this operating system.'
}
$architecture = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    ([Runtime.InteropServices.Architecture]::X64) { 'x64' }
    ([Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
    default { throw "The Rust launcher cannot be staged for architecture $($_)." }
}
$fileName = if ($IsWindows) { 'realdiff.exe' } else { 'realdiff' }
$source = Join-Path $repo "src/RealDiff.Launcher.Rust/target/release/$fileName"
if (-not (Test-Path $source -PathType Leaf)) { throw "Rust launcher binary was not found: $source" }

$destination = Join-Path $output "$os-$architecture"
Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item $source (Join-Path $destination $fileName) -Force
Write-Host "Rust launcher staged: $destination" -ForegroundColor Green