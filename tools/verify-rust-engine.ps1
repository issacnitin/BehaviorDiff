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
    [string]$FixtureFault = 'none',
    [switch]$CompareFindings,
    [string]$BaseSha = 'proof-base',
    [string]$PrSha = 'proof-pr',
    [string]$MergeBaseSha = 'proof-merge-base'
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

function Compare-JsonText(
    [Parameter(Mandatory)] [string]$DotNetPath,
    [Parameter(Mandatory)] [string]$RustPath,
    [switch]$NormalizeGeneratedUtc
) {
    $dotnetText = Get-Content $DotNetPath -Raw
    $rustText = Get-Content $RustPath -Raw
    if ($NormalizeGeneratedUtc) {
        $pattern = '(?m)^(\s*"generatedUtc"\s*:\s*)"[^"]*"'
        $dotnetText = [regex]::Replace($dotnetText, $pattern, '$1"<normalized>"')
        $rustText = [regex]::Replace($rustText, $pattern, '$1"<normalized>"')
    }

    if ($dotnetText -ceq $rustText) {
        return
    }

    $limit = [Math]::Min($dotnetText.Length, $rustText.Length)
    $index = 0
    while ($index -lt $limit -and $dotnetText[$index] -ceq $rustText[$index]) {
        $index++
    }

    $contextStart = [Math]::Max(0, $index - 100)
    $dotnetContext = $dotnetText.Substring(
        $contextStart,
        [Math]::Min(300, $dotnetText.Length - $contextStart))
    $rustContext = $rustText.Substring(
        $contextStart,
        [Math]::Min(300, $rustText.Length - $contextStart))
    throw "JSON text differs at character $index.`n.NET: $dotnetContext`nRust: $rustContext"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Remove-Item $dotnetOutput, $rustOutput -Force -ErrorAction SilentlyContinue

$dotnetArguments = New-DiffArguments $dotnetOutput
$dotnetLog = @(& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- @dotnetArguments 2>&1)
$dotnetExit = $LASTEXITCODE
$dotnetLog | ForEach-Object { Write-Host $_ }

$rustManifest = Join-Path $repo 'src/BehaviorDiff.Engine.Rust/Cargo.toml'
$rustLog = @(& cargo run --quiet --manifest-path $rustManifest -- @(New-DiffArguments $rustOutput) 2>&1)
$rustExit = $LASTEXITCODE
$rustLog | ForEach-Object { Write-Host $_ }

if ($dotnetExit -ne $rustExit) {
    throw "diff exit codes differ: .NET=$dotnetExit Rust=$rustExit"
}

if ($dotnetExit -ne 0) {
    if ((Test-Path $dotnetOutput) -or (Test-Path $rustOutput)) {
        throw "a refused diff emitted an artifact: .NET=$(Test-Path $dotnetOutput) Rust=$(Test-Path $rustOutput)"
    }

    if (-not $CompareFindings -or $dotnetExit -ne 4) {
        Write-Host "PASS: both engines refused with exit $dotnetExit and emitted no artifact."
        exit 0
    }

    function Get-RefusalReason([object[]]$Lines) {
        $reasons = @($Lines | ForEach-Object { [string]$_ } | Where-Object { $_.StartsWith('  - ') } |
            ForEach-Object { $_.Substring(4) })
        if ($reasons.Count -eq 0) {
            throw 'a refused diff emitted no structured refusal reasons'
        }

        return $reasons -join [Environment]::NewLine
    }

    $dotnetReason = Get-RefusalReason $dotnetLog
    $rustReason = Get-RefusalReason $rustLog
    if ($dotnetReason -cne $rustReason) {
        throw "diff refusal reasons differ.`n.NET: $dotnetReason`nRust: $rustReason"
    }

    $dotnetFindings = Join-Path $OutputDirectory 'dotnet-findings.json'
    $rustFindings = Join-Path $OutputDirectory 'rust-findings.json'
    foreach ($findings in @($dotnetFindings, $rustFindings)) {
        & dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
            findings-invalid --status refused --exit-code 3 --reason $dotnetReason --out $findings `
            --base-sha $BaseSha --pr-sha $PrSha --merge-base $MergeBaseSha
        if ($LASTEXITCODE -ne 0) {
            throw "failed to write refused findings: $findings"
        }
    }

    Compare-JsonText -DotNetPath $dotnetFindings -RustPath $rustFindings -NormalizeGeneratedUtc
    Write-Host "PASS: both engines refused with direct exit $dotnetExit, mapped to CLI exit 3, and refused findings are byte-equivalent (generatedUtc excluded)."
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

if (-not $CompareFindings) {
    Write-Host 'PASS: exit codes and divergence sets are equivalent (generatedUtc excluded).'
    exit 0
}

if (-not $ChangedFiles) {
    $ChangedFiles = Join-Path $OutputDirectory 'changed-files.txt'
    Set-Content $ChangedFiles -Value @()
}

$dotnetFrontier = Join-Path $OutputDirectory 'dotnet-frontier-report.json'
$rustFrontier = Join-Path $OutputDirectory 'rust-frontier-report.json'
Remove-Item $dotnetFrontier, $rustFrontier -Force -ErrorAction SilentlyContinue

& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
    frontier --in $dotnetOutput --changed-files $ChangedFiles --out $dotnetFrontier
$dotnetFrontierExit = $LASTEXITCODE
& dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
    frontier --in $rustOutput --changed-files $ChangedFiles --out $rustFrontier
$rustFrontierExit = $LASTEXITCODE
if ($dotnetFrontierExit -ne $rustFrontierExit) {
    throw "frontier exit codes differ: .NET=$dotnetFrontierExit Rust=$rustFrontierExit"
}

if ($dotnetFrontierExit -ne 0) {
    if ((Test-Path $dotnetFrontier) -or (Test-Path $rustFrontier)) {
        throw "a refused frontier emitted an artifact: .NET=$(Test-Path $dotnetFrontier) Rust=$(Test-Path $rustFrontier)"
    }

    Write-Host "PASS: diff artifacts are equivalent and both frontier runs refused with exit $dotnetFrontierExit."
    exit 0
}

Compare-JsonText -DotNetPath $dotnetFrontier -RustPath $rustFrontier -NormalizeGeneratedUtc
$frontier = Get-Content $dotnetFrontier -Raw | ConvertFrom-Json
$findingsExit = if ($frontier.counts.unexpected -gt 0) { 1 } else { 0 }
$dotnetFindings = Join-Path $OutputDirectory 'dotnet-findings.json'
$rustFindings = Join-Path $OutputDirectory 'rust-findings.json'
Remove-Item $dotnetFindings, $rustFindings -Force -ErrorAction SilentlyContinue

foreach ($item in @(
    @{ Divergences = $dotnetOutput; Frontier = $dotnetFrontier; Findings = $dotnetFindings },
    @{ Divergences = $rustOutput; Frontier = $rustFrontier; Findings = $rustFindings }
)) {
    & dotnet run --project (Join-Path $repo 'src/BehaviorDiff.Engine') -c Release --no-build -- `
        findings --divergences $item.Divergences --frontier $item.Frontier --out $item.Findings `
        --exit-code $findingsExit --base-sha $BaseSha --pr-sha $PrSha --merge-base $MergeBaseSha
    if ($LASTEXITCODE -ne 0) {
        throw "findings generation failed for $($item.Divergences): $LASTEXITCODE"
    }
}

Compare-JsonText -DotNetPath $dotnetFindings -RustPath $rustFindings -NormalizeGeneratedUtc
Write-Host 'PASS: diff, frontier, and findings are byte-equivalent (generatedUtc excluded).'