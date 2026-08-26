#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$module = Join-Path $repo 'src/RealDiff.Go'
$package = './internal/runtime/canonical'

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs/RealDiffGo/go/bin'),
        (Join-Path $HOME '.realdiff-tools/go/bin')
    )
    $localGoBin = $candidates | Where-Object { Test-Path (Join-Path $_ 'go.exe') -PathType Leaf } | Select-Object -First 1
    if (-not $localGoBin) {
        throw 'Go was not found on PATH or in a RealDiff local tool directory.'
    }
    $env:PATH = "$localGoBin;$env:PATH"
}

function Invoke-Go {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [hashtable]$Environment = @{}
    )

    $saved = @{}
    foreach ($name in $Environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $Environment[$name], 'Process')
    }
    try {
        $output = @(& go @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) {
            throw "go $($Arguments -join ' ') failed with exit code $exitCode"
        }
        return $output
    } finally {
        foreach ($name in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
        }
    }
}

function Get-ProofLine {
    param(
        [Parameter(Mandatory)]
        [object[]]$Output,
        [Parameter(Mandatory)]
        [string]$Prefix
    )

    $line = $Output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -like "*$Prefix*" } | Select-Object -First 1
    if (-not $line) {
        throw "Missing verifier proof line: $Prefix"
    }
    return $line.Substring($line.IndexOf($Prefix, [StringComparison]::Ordinal))
}

Push-Location $module
try {
    Write-Host "Go toolchain: $(& go version)"

    $formatDiff = @(& gofmt -d internal/runtime/canonical 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "gofmt inspection failed with exit code $LASTEXITCODE"
    }
    if ($formatDiff.Count -ne 0) {
        $formatDiff | ForEach-Object { Write-Host $_ }
        throw 'Canonicalizer sources are not gofmt-clean.'
    }

    $metadata = (& go list -json $package | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) {
        throw 'go list failed for the canonicalizer package.'
    }
    $directImports = @($metadata.Imports) + @($metadata.TestImports)
    if ($metadata.PSObject.Properties['XTestImports']) {
        $directImports += @($metadata.XTestImports)
    }
    if ($directImports -contains 'unsafe') {
        throw 'The canonicalizer package or its tests directly import unsafe.'
    }

    $null = Invoke-Go @('test', '-count=1', $package)

    $firstOutput = Invoke-Go @('test', '-count=1', '-run', '^TestCanonicalProcessHelper$', '-v', $package) @{ REALDIFF_CANONICAL_HELPER = 'first' }
    $secondOutput = Invoke-Go @('test', '-count=1', '-run', '^TestCanonicalProcessHelper$', '-v', $package) @{ REALDIFF_CANONICAL_HELPER = 'second' }
    $firstProof = Get-ProofLine $firstOutput 'CANONICAL_MAP_PROOF'
    $secondProof = Get-ProofLine $secondOutput 'CANONICAL_MAP_PROOF'
    if ($firstProof -ne $secondProof) {
        throw "Cross-process map proof mismatch:`n$firstProof`n$secondProof"
    }
    $firstTieProof = Get-ProofLine $firstOutput 'CANONICAL_MAP_TIE_PROOF'
    $secondTieProof = Get-ProofLine $secondOutput 'CANONICAL_MAP_TIE_PROOF'
    if ($firstTieProof -ne $secondTieProof) {
        throw "Cross-process map tie proof mismatch:`n$firstTieProof`n$secondTieProof"
    }
    if ($firstTieProof -notmatch '^CANONICAL_MAP_TIE_PROOF sha256=[0-9a-f]{64} values=20 blocklisted=1 mapTies=2 partial=true$') {
        throw "Unexpected map tie proof counters: $firstTieProof"
    }

    $counterOutput = Invoke-Go @('test', '-count=1', '-run', '^TestCanonicalCounterProof$', '-v', $package) @{ REALDIFF_CANONICAL_PROOF = '1' }
    $counterProof = Get-ProofLine $counterOutput 'CANONICAL_COUNTER_PROOF'
    $expectedCounterProof = 'CANONICAL_COUNTER_PROOF values=3 depthLimited=0 entryLimited=0 blocklisted=1 mapTies=0 errored=0 renderedTruncated=0 unexported=1 partial=true'
    if ($counterProof -ne $expectedCounterProof) {
        throw "Unexpected canonical counter proof:`nExpected: $expectedCounterProof`nActual:   $counterProof"
    }

    Write-Host 'GO_CANONICALIZER_TESTS passed=true' -ForegroundColor Green
    Write-Host 'GO_CANONICALIZER_IMPORTS unsafe=false methods=false' -ForegroundColor Green
    Write-Host "GO_CANONICALIZER_MAP processes=2 $firstProof" -ForegroundColor Green
    Write-Host "GO_CANONICALIZER_MAP_TIES processes=2 $firstTieProof" -ForegroundColor Green
    Write-Host "GO_CANONICALIZER_COUNTERS $counterProof" -ForegroundColor Green
} finally {
    Pop-Location
}