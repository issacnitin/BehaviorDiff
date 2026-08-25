#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Base1Directory,
    [string]$Base2Directory,
    [string]$PrDirectory,
    [string]$BaseRoot,
    [string]$PrRoot,
    [string]$Base3Directory,
    [string]$ChangedFiles,
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-rust-engine-equivalence'),
    [ValidateSet('none', 'writer', 'ordinal')]
    [string]$FixtureFault = 'none'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnetOutput = Join-Path $OutputDirectory 'dotnet-divergence-set.json'
$rustOutput = Join-Path $OutputDirectory 'rust-divergence-set.json'

$explicitInputs = @($Base1Directory, $Base2Directory, $PrDirectory, $BaseRoot, $PrRoot) |
    Where-Object { $_ }
if ($explicitInputs.Count -eq 0) {
    $fixture = & (Join-Path $PSScriptRoot 'New-RustEngineFixture.ps1') `
        -OutputDirectory (Join-Path $OutputDirectory 'fixture') `
        -Fault $FixtureFault
    $Base1Directory = $fixture.Base1Directory
    $Base2Directory = $fixture.Base2Directory
    $PrDirectory = $fixture.PrDirectory
    $BaseRoot = $fixture.BaseRoot
    $PrRoot = $fixture.PrRoot
}
elseif ($explicitInputs.Count -ne 5) {
    throw 'Specify all of -Base1Directory, -Base2Directory, -PrDirectory, -BaseRoot, and -PrRoot, or specify none to use the generated fixture.'
}
elseif ($FixtureFault -ne 'none') {
    throw '-FixtureFault can only be used with the generated fixture.'
}

function ConvertTo-CanonicalValue([object]$Value) {
    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $canonical = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties | Sort-Object Name) {
            $canonical[$property.Name] = ConvertTo-CanonicalValue $property.Value
        }

        return $canonical
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return @($Value | ForEach-Object { ConvertTo-CanonicalValue $_ })
    }

    return $Value
}

function New-DiffArguments([string]$Output) {
    $arguments = @(
        'diff',
        '--base1', $Base1Directory,
        '--base2', $Base2Directory,
        '--pr', $PrDirectory,
        '--base-root', $BaseRoot,
        '--pr-root', $PrRoot,
        '--out', $Output
    )
    if ($Base3Directory) {
        $arguments += @('--base3', $Base3Directory)
    }

    if ($ChangedFiles) {
        $arguments += @('--changed-files', $ChangedFiles)
    }

    return $arguments
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Remove-Item $dotnetOutput, $rustOutput -Force -ErrorAction SilentlyContinue

$dotnetArguments = New-DiffArguments $dotnetOutput
& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- @dotnetArguments
$dotnetExit = $LASTEXITCODE

$rustManifest = Join-Path $repo 'src/BehaviorDiff.Engine.Rust/Cargo.toml'
& cargo run --quiet --manifest-path $rustManifest -- @(New-DiffArguments $rustOutput)
$rustExit = $LASTEXITCODE

if ($dotnetExit -ne $rustExit) {
    throw "diff exit codes differ: .NET=$dotnetExit Rust=$rustExit"
}

if ($dotnetExit -ne 0) {
    if ((Test-Path $dotnetOutput) -or (Test-Path $rustOutput)) {
        throw "a refused diff emitted an artifact: .NET=$(Test-Path $dotnetOutput) Rust=$(Test-Path $rustOutput)"
    }

    Write-Host "PASS: both engines refused with exit $dotnetExit and emitted no artifact."
    exit 0
}

$dotnetArtifact = Get-Content $dotnetOutput -Raw | ConvertFrom-Json
$rustArtifact = Get-Content $rustOutput -Raw | ConvertFrom-Json
$dotnetArtifact.PSObject.Properties.Remove('generatedUtc')
$rustArtifact.PSObject.Properties.Remove('generatedUtc')

$dotnetCanonical = ConvertTo-CanonicalValue $dotnetArtifact | ConvertTo-Json -Depth 100 -Compress
$rustCanonical = ConvertTo-CanonicalValue $rustArtifact | ConvertTo-Json -Depth 100 -Compress
if ($dotnetCanonical -cne $rustCanonical) {
    throw "normalized divergence sets differ: $dotnetOutput vs $rustOutput"
}

Write-Host 'PASS: exit codes and divergence sets are equivalent (generatedUtc excluded).'