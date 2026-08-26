#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$toolModule = Join-Path $repo 'src/RealDiff.Go'
$fixture = Join-Path $toolModule 'testdata/rewrite'

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

function Get-TreeHashes {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $hashes = [ordered]@{}
    Get-ChildItem -Path $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        $hashes[$relative] = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash
    }
    return $hashes
}

function Assert-HashesEqual {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Expected,
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Actual
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "Source file count changed: expected $($Expected.Count), actual $($Actual.Count)"
    }
    foreach ($path in $Expected.Keys) {
        if (-not $Actual.Contains($path) -or $Actual[$path] -ne $Expected[$path]) {
            throw "Source hash changed: $path"
        }
    }
}

function Invoke-Go {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& go @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "go $($Arguments -join ' ') failed with exit code $exitCode"
    }
    return $output
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "realdiff-go-rewriter-$([guid]::NewGuid().ToString('N'))"
$source = Join-Path $tempRoot 'source'
$cache = Join-Path $tempRoot 'cache'

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Copy-Item -Path $fixture -Destination $source -Recurse
    $before = Get-TreeHashes -Root $source

    Push-Location $toolModule
    try {
        Write-Host "Go toolchain: $(& go version)"
        $rewriteOutput = Invoke-Go @('run', './cmd/realdiff-go-rewrite', '--source', $source, '--out', $cache)
    } finally {
        Pop-Location
    }

    $after = Get-TreeHashes -Root $source
    Assert-HashesEqual -Expected $before -Actual $after

    Push-Location $cache
    try {
        $null = Invoke-Go @('test', './...')
    } finally {
        Pop-Location
    }

    $reportPath = Join-Path $cache 'realdiff-rewrite-report.json'
    if (-not (Test-Path $reportPath -PathType Leaf)) {
        throw "Rewrite report was not created: $reportPath"
    }
    $report = Get-Content -Path $reportPath -Raw | ConvertFrom-Json
    $expected = [ordered]@{
        packages = 2
        files = 3
        functions = 29
        methods = 5
        companions = 34
        testRoots = 5
        patched = 31
        skipped = 7
        genericTemplates = 3
        directCalls = 38
        goStatements = 8
        boundaries = 4
    }
    foreach ($name in $expected.Keys) {
        if ($report.metrics.$name -ne $expected[$name]) {
            throw "Metric $name mismatch: expected $($expected[$name]), actual $($report.metrics.$name)"
        }
    }

    $actualBoundaryKinds = @($report.boundaries.kind | Sort-Object)
    $expectedBoundaryKinds = @('cross-package-call', 'function-value-call', 'function-value-go', 'interface-call')
    if (($actualBoundaryKinds -join ',') -ne ($expectedBoundaryKinds -join ',')) {
        throw "Boundary kinds mismatch: $($actualBoundaryKinds -join ',')"
    }

    $templates = @($report.genericTemplates)
    if ($templates.Count -ne 3 -or @($templates | Where-Object {
        $_.skipReason -ne 'Unobservable' -or $_.detail -ne 'Go: GenericTemplate'
    }).Count -ne 0 -or [int]$report.metrics.skipped -ne [int]$report.metrics.boundaries + $templates.Count) {
        throw "Generic template report mismatch: $($templates | ConvertTo-Json -Compress)"
    }

    $expectedSummary = 'GO_REWRITE_SUMMARY methods=5 companions=34 roots=5 patched=31 skipped=7 templates=3 direct=38 go=8 boundaries=4 report=realdiff-rewrite-report.json'
    if (-not (($rewriteOutput -join "`n").Contains($expectedSummary, [StringComparison]::Ordinal))) {
        throw "Required CLI summary was not found: $expectedSummary"
    }

    Write-Host 'GO_REWRITER_SOURCE unchanged=true files=5' -ForegroundColor Green
    Write-Host 'GO_REWRITER_CACHE go_test=passed parseable=true formatted=true' -ForegroundColor Green
    Write-Host 'GO_REWRITER_SUMMARY methods=5 companions=34 roots=5 patched=31 skipped=7 templates=3 direct=38 go=8 boundaries=4' -ForegroundColor Green
    Write-Host 'GO_REWRITER_TEMPLATES count=3 status=Skipped reason=Unobservable detail=Go:GenericTemplate' -ForegroundColor Green
    Write-Host "GO_REWRITER_BOUNDARIES kinds=$($actualBoundaryKinds -join ',')" -ForegroundColor Green
} finally {
    Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}