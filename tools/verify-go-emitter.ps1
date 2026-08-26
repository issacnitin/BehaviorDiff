#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$toolModule = Join-Path $repo 'src/RealDiff.Go'
$artifactRoot = Join-Path ([IO.Path]::GetTempPath()) "realdiff-go-emitter-$([guid]::NewGuid().ToString('N'))"

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

try {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
    $env:REALDIFF_GO_EMITTER_OUTPUT = $artifactRoot
    Push-Location $toolModule
    try {
        Write-Host "Go toolchain: $(& go version)"
        $output = @(& go test -count=1 -run TestEmitterFixture -v ./internal/rewriter 2>&1)
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) {
            throw "Go emitter unit failed with exit code $exitCode"
        }
    } finally {
        Pop-Location
    }

    $expected = 'GO_EMITTER_SUMMARY events=55 members=45 modules=2 patched=38 skipped=7 roots=5 values=310 unreadableFields=15 ambiguousMapEntries=0 enqueued=55 written=55 dropped=0'
    if (-not (($output -join "`n").Contains($expected, [StringComparison]::Ordinal))) {
        throw "Required emitter summary was not found: $expected"
    }

    $manifest = @(Get-ChildItem -Path $artifactRoot -Filter '*.manifest.ndjson' -File)
    if ($manifest.Count -ne 1) {
        throw "Expected one process manifest, found $($manifest.Count)."
    }
    & dotnet run --project (Join-Path $repo 'tools/ManifestParseProof') -c Release -- $manifest[0].FullName
    if ($LASTEXITCODE -ne 0) {
        throw 'Shared contract parser rejected the Go manifest.'
    }

    Write-Host $expected -ForegroundColor Green
    Write-Host 'GO_EMITTER_PROOF returns=true panicNoReturn=true recovered=true roots=true goroutineParent=true source=true ordinals=true reconciliation=true parser=true' -ForegroundColor Green
} finally {
    Remove-Item Env:REALDIFF_GO_EMITTER_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item -Path $artifactRoot -Recurse -Force -ErrorAction SilentlyContinue
}