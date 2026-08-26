#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-cli-package-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$tracers = Join-Path $work 'tracers'
$rustEngine = Join-Path $work 'rust-engine'
$packages = Join-Path $work 'packages'
$toolPath = Join-Path $work 'tool'
$cliProject = Join-Path $repo 'src/RealDiff.Cli/RealDiff.Cli.csproj'
$previousJavaAgent = $env:REALDIFF_JAVA_AGENT
$previousNodeTracer = $env:REALDIFF_NODE_TRACER
$previousGoRewriter = $env:REALDIFF_GO_REWRITER
$previousRustTracer = $env:REALDIFF_RUST_TRACER

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$label failed with exit code $LASTEXITCODE" }
}

function New-ReferenceRepository([string]$name, [string]$sample) {
    $directory = Join-Path $work "$name-repository"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Get-ChildItem $sample -Force | ForEach-Object {
        Copy-Item $_.FullName -Destination $directory -Recurse -Force
    }
    Get-ChildItem $directory -Directory -Recurse -Force |
        Where-Object Name -In @('target', 'node_modules', 'dist') |
        Sort-Object { $_.FullName.Length } -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    if ($name -eq 'node' -and -not (Test-Path (Join-Path $directory 'package-lock.json'))) {
        Push-Location $directory
        try {
            Invoke-Checked 'Node reference lockfile generation' {
                & npm install --package-lock-only --ignore-scripts --no-audit --no-fund
            }
        } finally { Pop-Location }
    }

    Invoke-Checked "$name git init" { & git -C $directory init --initial-branch=main --quiet }
    Invoke-Checked "$name git identity" { & git -C $directory config user.email 'realdiff-proof@example.invalid' }
    Invoke-Checked "$name git identity" { & git -C $directory config user.name 'RealDiff Proof' }
    Invoke-Checked "$name git add" { & git -C $directory add . }
    Invoke-Checked "$name base commit" { & git -C $directory commit --quiet -m 'reference base' }
    $base = (& git -C $directory rev-parse HEAD).Trim()
    Invoke-Checked "$name no-op PR commit" { & git -C $directory commit --quiet --allow-empty -m 'reference pr (no semantic mutation)' }
    $pr = (& git -C $directory rev-parse HEAD).Trim()
    [pscustomobject]@{ Directory = $directory; Base = $base; Pr = $pr }
}

function Assert-RunArtifacts(
    [string]$language,
    [string]$runWork,
    [string]$findingsPath,
    [object[]]$output) {
    $text = $output -join "`n"
    $label = switch ($language) {
        'java' { 'java agent' }
        'node' { 'node tracer' }
        'go' { 'go rewriter' }
        'rust' { 'rust tracer' }
    }
    $pathMatch = [regex]::Match($text, "(?m)^\s*$label\s*:\s*(.+)$")
    if (-not $pathMatch.Success) {
        throw "$language CLI output did not report the selected packaged tracer path"
    }
    $selectedPath = [IO.Path]::GetFullPath($pathMatch.Groups[1].Value.Trim())
    $installedRoot = [IO.Path]::GetFullPath($toolPath).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $selectedPath.StartsWith($installedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$language selected a tracer outside the installed tool directory: $selectedPath"
    }
    if ($selectedPath.StartsWith([IO.Path]::GetFullPath($repo), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$language selected a source-tree tracer: $selectedPath"
    }

    $events = @()
    foreach ($runName in @('base_run1', 'base_run2', 'base_run3', 'pr_run')) {
        $runDirectory = Join-Path $runWork $runName
        if (-not (Test-Path $runDirectory -PathType Container)) {
            throw "$language run directory is missing: $runName"
        }
        $traces = @(Get-ChildItem $runDirectory -Filter 'run.*.ndjson' |
            Where-Object Name -NotLike '*.manifest.ndjson')
        $manifests = @(Get-ChildItem $runDirectory -Filter 'run.*.manifest.ndjson')
        if ($traces.Count -eq 0 -or $manifests.Count -eq 0) {
            throw "$language $runName did not contain trace and manifest output"
        }
        $events += @($traces | ForEach-Object {
            Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json }
        })
    }

    $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json
    if ($findings.status -ne 'analyzed' -or -not [bool]$findings.isCleanResult `
        -or [int]$findings.summary.unexpectedMembers -ne 0) {
        throw "$language findings were not analyzed cleanly: status=$($findings.status) unexpected=$($findings.summary.unexpectedMembers)"
    }

    [pscustomobject]@{ Language = $language; Runs = 4; Events = $events.Count; Tracer = $selectedPath }
}

function Invoke-LanguageProof([string]$language, [object]$reference, [string]$cli) {
    $runWork = Join-Path $work "$language-work"
    $findings = Join-Path $work "$language-findings.json"
    $output = @(& $cli $reference.Directory --base $reference.Base --pr $reference.Pr `
        --work $runWork --findings $findings --keep-traces 1d 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "$language installed CLI invocation failed with exit code $exitCode" }
    Assert-RunArtifacts $language $runWork $findings $output
}

try {
    New-Item -ItemType Directory -Path $work, $packages, $toolPath -Force | Out-Null
    Write-Host '=== Stage cross-language tracers ===' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Stage-CrossLanguageTracers.ps1') -OutputDirectory $tracers
    if ($LASTEXITCODE -ne 0) { throw "Tracer staging failed with exit code $LASTEXITCODE" }
    Write-Host '=== Stage Rust engine ===' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Stage-RustEngine.ps1') -OutputDirectory $rustEngine
    if ($LASTEXITCODE -ne 0) { throw "Rust engine staging failed with exit code $LASTEXITCODE" }

    Write-Host '=== Pack CLI ===' -ForegroundColor Cyan
    Invoke-Checked 'CLI pack' {
        & dotnet pack $cliProject -c Release -o $packages --nologo `
            "-p:CrossLanguageTracerRoot=$tracers" "-p:RustEngineRoot=$rustEngine"
    }
    $package = Get-ChildItem $packages -Filter 'RealDiff.Tool.*.nupkg' -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $package) { throw 'RealDiff CLI package was not produced' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
    } finally {
        $archive.Dispose()
    }
    $rustOs = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { 'osx' }
    $rustArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $rustFile = if ($IsWindows) { 'realdiff-engine.exe' } else { 'realdiff-engine' }
    $rustPackageEntry = "tools/net8.0/any/engines/rust/$rustOs-$rustArchitecture/$rustFile"
    $rustTracerFile = if ($IsWindows) { 'realdiff-rust-rewrite.exe' } else { 'realdiff-rust-rewrite' }
    $rustTracerPackageEntry = "tools/net8.0/any/tracers/rust/$rustOs-$rustArchitecture/$rustTracerFile"
    $goTracerFile = if ($IsWindows) { 'realdiff-go-rewrite.exe' } else { 'realdiff-go-rewrite' }
    $goTracerPackageEntry = "tools/net8.0/any/tracers/go/$rustOs-$rustArchitecture/$goTracerFile"
    $requiredEntries = @(
        'tools/net8.0/any/tracers/java/realdiff-java-agent.jar',
        'tools/net8.0/any/tracers/node/register.cjs',
        'tools/net8.0/any/tracers/node/loader.mjs',
        'tools/net8.0/any/tracers/node/bootstrap.mjs',
        'tools/net8.0/any/tracers/node/src/runtime.cjs',
        'tools/net8.0/any/tracers/node/src/canonicalize.cjs',
        'tools/net8.0/any/tracers/node/src/transform.cjs',
        'tools/net8.0/any/tracers/node/src/source-map.cjs',
        'tools/net8.0/any/tracers/node/adapters/jest.cjs',
        'tools/net8.0/any/tracers/node/adapters/vitest.mjs',
        'tools/net8.0/any/tracers/node/node_modules/@babel/parser/package.json',
        'tools/net8.0/any/Mono.Cecil.dll',
        $goTracerPackageEntry,
        $rustPackageEntry,
        $rustTracerPackageEntry
    )
    $missing = @($requiredEntries | Where-Object { $_ -notin $entries })
    if ($missing.Count -ne 0) { throw "Package entries are missing: $($missing -join ', ')" }
    if (@($entries | Where-Object { $_ -match '/tracers/node/test/' }).Count -ne 0) {
        throw 'The CLI package contains Node tracer tests.'
    }

    [xml]$project = Get-Content $cliProject -Raw
    $versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "CLI package version was not found in $cliProject"
    }
    $version = $versionNode.InnerText.Trim()
    Write-Host '=== Install packed CLI ===' -ForegroundColor Cyan
    Invoke-Checked 'CLI tool install' {
        & dotnet tool install RealDiff.Tool --tool-path $toolPath --version $version `
            --add-source $packages --ignore-failed-sources
    }
    $launcher = if ($IsWindows) { 'realdiff.exe' } else { 'realdiff' }
    $cli = Join-Path $toolPath $launcher
    if (-not (Test-Path $cli -PathType Leaf)) { throw "Installed CLI launcher was not found: $cli" }

    $env:REALDIFF_JAVA_AGENT = $null
    $env:REALDIFF_NODE_TRACER = $null
    $env:REALDIFF_GO_REWRITER = $null
    $env:REALDIFF_RUST_TRACER = $null
    $java = New-ReferenceRepository 'java' (Join-Path $repo 'samples/JavaReference')
    $node = New-ReferenceRepository 'node' (Join-Path $repo 'samples/NodeReference')
    $go = New-ReferenceRepository 'go' (Join-Path $repo 'samples/GoReference')
    $rust = New-ReferenceRepository 'rust' (Join-Path $repo 'samples/RustReference')

    Write-Host '=== Installed Java CLI invocation ===' -ForegroundColor Cyan
    $javaResult = Invoke-LanguageProof 'java' $java $cli
    Write-Host '=== Installed Node CLI invocation ===' -ForegroundColor Cyan
    $nodeResult = Invoke-LanguageProof 'node' $node $cli
    Write-Host '=== Installed Go CLI invocation ===' -ForegroundColor Cyan
    $goResult = Invoke-LanguageProof 'go' $go $cli
    Write-Host '=== Installed Rust CLI invocation ===' -ForegroundColor Cyan
    $rustResult = Invoke-LanguageProof 'rust' $rust $cli

    $size = [Math]::Round($package.Length / 1MB, 2)
    Write-Host 'CLI package proof: PASS' -ForegroundColor Green
    Write-Host ("  package: {0} ({1} MiB, {2} entries)" -f $package.Name, $size, $entries.Count)
    Write-Host ("  Java: runs={0} events={1}" -f $javaResult.Runs, $javaResult.Events)
    Write-Host ("  Node : runs={0} events={1}" -f $nodeResult.Runs, $nodeResult.Events)
    Write-Host ("  Go   : runs={0} events={1}" -f $goResult.Runs, $goResult.Events)
    Write-Host ("  Rust : runs={0} events={1}" -f $rustResult.Runs, $rustResult.Events)
}
finally {
    $env:REALDIFF_JAVA_AGENT = $previousJavaAgent
    $env:REALDIFF_NODE_TRACER = $previousNodeTracer
    $env:REALDIFF_GO_REWRITER = $previousGoRewriter
    $env:REALDIFF_RUST_TRACER = $previousRustTracer
    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "CLI package proof work kept at $work"
    }
}
