#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/packages',
    [string]$TracerOutputDirectory = 'artifacts/cross-language-tracers',
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

& (Join-Path $PSScriptRoot 'Stage-CrossLanguageTracers.ps1') -OutputDirectory $tracers
if ($LASTEXITCODE -ne 0) {
    throw "Tracer staging failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $packages -Force | Out-Null
& dotnet pack (Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj') `
    -c $Configuration -o $packages --nologo `
    "-p:CrossLanguageTracerRoot=$tracers"
if ($LASTEXITCODE -ne 0) {
    throw "CLI pack failed with exit code $LASTEXITCODE"
}

Write-Host "BehaviorDiff CLI packages written to $packages" -ForegroundColor Green
