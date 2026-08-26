#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/packages',
    [string]$TracerOutputDirectory = 'artifacts/cross-language-tracers',
    [string]$RustEngineOutputDirectory = 'artifacts/rust-engine',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$packages = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
}
$tracers = if ([IO.Path]::IsPathRooted($TracerOutputDirectory)) {
    [IO.Path]::GetFullPath($TracerOutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $TracerOutputDirectory))
}
$rustEngine = if ([IO.Path]::IsPathRooted($RustEngineOutputDirectory)) {
    [IO.Path]::GetFullPath($RustEngineOutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $RustEngineOutputDirectory))
}

& (Join-Path $PSScriptRoot 'Stage-CrossLanguageTracers.ps1') -OutputDirectory $tracers
if ($LASTEXITCODE -ne 0) {
    throw "Tracer staging failed with exit code $LASTEXITCODE"
}
& (Join-Path $PSScriptRoot 'Stage-RustEngine.ps1') -OutputDirectory $rustEngine
if ($LASTEXITCODE -ne 0) {
    throw "Rust engine staging failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $packages -Force | Out-Null
& dotnet pack (Join-Path $repo 'src/RealDiff.Cli/RealDiff.Cli.csproj') `
    -c $Configuration -o $packages --nologo `
    "-p:CrossLanguageTracerRoot=$tracers" `
    "-p:RustEngineRoot=$rustEngine"
if ($LASTEXITCODE -ne 0) {
    throw "CLI pack failed with exit code $LASTEXITCODE"
}

Write-Host "RealDiff CLI packages written to $packages" -ForegroundColor Green
