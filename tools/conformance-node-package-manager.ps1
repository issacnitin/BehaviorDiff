#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('npm', 'pnpm', 'yarn', 'bun')]
    [string]$Manager = 'npm',
    [switch]$YarnBerry,
    [string]$WorkDirectory,
    [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-node-$Manager-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$fixture = Join-Path $work 'repository'
$run = Join-Path $work 'run'
$lockfiles = @{
    npm = 'package-lock.json'
    pnpm = 'pnpm-lock.yaml'
    yarn = 'yarn.lock'
    bun = 'bun.lock'
}
if ($YarnBerry -and $Manager -ne 'yarn') { throw '-YarnBerry requires -Manager yarn' }

function Invoke-PackageManager([string[]]$Arguments) {
    Push-Location $fixture
    try {
        & $Manager @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Manager command failed: $($Arguments -join ' ')" }
    } finally { Pop-Location }
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $fixture -ItemType Directory -Force | Out-Null
    Get-ChildItem (Join-Path $repo 'samples/NodeReference') -Force | ForEach-Object {
        Copy-Item $_.FullName $fixture -Recurse -Force
    }

    $packagePath = Join-Path $fixture 'package.json'
    $package = Get-Content $packagePath -Raw | ConvertFrom-Json
    $package.scripts | Add-Member -NotePropertyName build -NotePropertyValue 'node build.cjs'
    if ($Manager -eq 'bun') {
        $package | Add-Member -NotePropertyName dependencies -NotePropertyValue @{ 'is-number' = '7.0.0' }
    }
    if ($YarnBerry) {
        $package | Add-Member -NotePropertyName packageManager -NotePropertyValue 'yarn@4.10.3'
    }
    $package | ConvertTo-Json -Depth 10 | Set-Content $packagePath
    'require("node:fs").mkdirSync("dist", { recursive: true });' | Set-Content (Join-Path $fixture 'build.cjs')
    if ($YarnBerry) {
        "nodeLinker: node-modules`nenableScripts: false" | Set-Content (Join-Path $fixture '.yarnrc.yml')
        $yarnVersion = (& yarn --version).Trim()
        if ($LASTEXITCODE -ne 0 -or [int]$yarnVersion.Split('.')[0] -lt 2) {
            throw "Yarn Berry requires Yarn 2 or newer; found '$yarnVersion'"
        }
    }

    switch ($Manager) {
        npm { Invoke-PackageManager @('install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund') }
        pnpm { Invoke-PackageManager @('install', '--lockfile-only', '--ignore-scripts') }
        yarn {
            if ($YarnBerry) { Invoke-PackageManager @('install') }
            else { Invoke-PackageManager @('install', '--ignore-scripts', '--non-interactive') }
        }
        bun { Invoke-PackageManager @('install', '--lockfile-only', '--ignore-scripts') }
    }
    if (-not (Test-Path (Join-Path $fixture $lockfiles[$Manager]) -PathType Leaf)) {
        throw "$Manager did not create $($lockfiles[$Manager])"
    }
    Remove-Item (Join-Path $fixture 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue

    & git -C $fixture init --initial-branch=main --quiet
    & git -C $fixture config user.email 'realdiff-proof@example.invalid'
    & git -C $fixture config user.name 'RealDiff Proof'
    & git -C $fixture add .
    & git -C $fixture commit --quiet -m "$Manager reference base"
    $base = (& git -C $fixture rev-parse HEAD).Trim()
    $subject = Join-Path $fixture 'src/subject.js'
    $updated = (Get-Content $subject -Raw).Replace('return value * 2 + 1;', 'return 1 + value * 2;')
    if ($updated -eq (Get-Content $subject -Raw)) { throw 'Behavior-neutral subject edit was not applied' }
    Set-Content $subject $updated -NoNewline
    & git -C $fixture add $subject
    & git -C $fixture commit --quiet -m 'Reorder observe arithmetic'
    $pr = (& git -C $fixture rev-parse HEAD).Trim()

    & npm ci --prefix (Join-Path $repo 'src/RealDiff.Node') --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Node tracer install failed' }
    & cargo build --release --manifest-path (Join-Path $repo 'src/RealDiff.Engine.Rust/Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Rust engine build failed' }
    & dotnet build (Join-Path $repo 'src/RealDiff.Cli/RealDiff.Cli.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

    $env:REALDIFF_NODE_TRACER = Join-Path $repo 'src/RealDiff.Node'
    $env:REALDIFF_RUST_ENGINE = Join-Path $repo 'src/RealDiff.Engine.Rust/target/release/realdiff-engine.exe'
    & dotnet run --project (Join-Path $repo 'src/RealDiff.Cli/RealDiff.Cli.csproj') -c Release --no-build -- `
        $fixture --base $base --pr $pr --work $run --findings (Join-Path $run 'findings.json') `
        --keep --keep-traces 1d
    if ($LASTEXITCODE -ne 0) { throw "$Manager CLI analysis failed with exit code $LASTEXITCODE" }

    $findings = Get-Content (Join-Path $run 'findings.json') -Raw | ConvertFrom-Json
    if ($findings.status -ne 'analyzed' -or -not [bool]$findings.isCleanResult) {
        throw "$Manager findings were not clean: $($findings.status)"
    }
    $events = @(Get-ChildItem $run -Recurse -File -Filter '*.ndjson' |
        Where-Object Name -NotLike '*.manifest.ndjson' |
        ForEach-Object { Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } } |
        Where-Object { $null -ne $_.PSObject.Properties['methodFullName'] })
    $subjectEvents = @($events | Where-Object {
        $null -ne $_.PSObject.Properties['filePath'] -and [string]$_.filePath -eq 'src/subject.js'
    })
    if ($subjectEvents.Count -eq 0) { throw "$Manager produced no subject events" }

    $label = if ($YarnBerry) { 'yarn-berry' } else { $Manager }
    Write-Host "NODE_PACKAGE_MANAGER_CONFORMANCE: PASS ($label)" -ForegroundColor Green
    Write-Host "  lockfile       : $($lockfiles[$Manager])"
    Write-Host "  subject events : $($subjectEvents.Count) across four runs"
}
finally {
    Remove-Item Env:REALDIFF_NODE_TRACER,Env:REALDIFF_RUST_ENGINE -ErrorAction SilentlyContinue
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "Node $Manager conformance work retained at $work" }
}