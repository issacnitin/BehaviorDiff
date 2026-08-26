#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Repository,

    [Parameter(Mandatory)]
    [string]$BaseSha,

    [Parameter(Mandatory)]
    [string]$PrSha,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [ValidateSet('csharp', 'rust', 'both')]
    [string]$Engine = 'both'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$subject = [IO.Path]::GetFullPath($Repository)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$cliProject = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'
$rustManifest = Join-Path $repo 'src/BehaviorDiff.Engine.Rust/Cargo.toml'
$rustEngine = Join-Path $repo 'src/BehaviorDiff.Engine.Rust/target/release/behaviordiff-engine.exe'

if (-not (Test-Path (Join-Path $subject '.git'))) {
    throw "Repository is not a git checkout: $subject"
}

& dotnet build $cliProject -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "CLI build failed: $LASTEXITCODE" }
& cargo build --release --manifest-path $rustManifest
if ($LASTEXITCODE -ne 0) { throw "Rust engine build failed: $LASTEXITCODE" }
if (-not (Test-Path $rustEngine)) { throw "Rust engine is missing: $rustEngine" }

New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:BEHAVIORDIFF_RUST_ENGINE = $rustEngine

function Read-NewLines([IO.StreamReader]$Reader, [ref]$EngineActive, [ref]$FrontierCompleted) {
    while (-not $Reader.EndOfStream) {
        $line = $Reader.ReadLine()
        if ($line -eq '=== 9. engine part 1 ===') {
            $EngineActive.Value = $true
        }
        elseif ($line -like 'Frontier report written:*') {
            $FrontierCompleted.Value = $true
            $EngineActive.Value = $false
        }
    }
}

function Get-ProcessTreeWorkingSet([Diagnostics.Process]$Root) {
    $total = 0L
    try {
        $Root.Refresh()
        $total += $Root.WorkingSet64
    }
    catch {
        return $total
    }

    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Root.Id)" -Property ProcessId)
    foreach ($child in $children) {
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$child.ProcessId)
            $process.Refresh()
            $total += $process.WorkingSet64
            $process.Dispose()
        }
        catch { }
    }

    return $total
}

function Measure-Analysis([string]$EngineName, [string]$Temperature) {
    $cache = Join-Path $output "$EngineName-cache"
    $work = Join-Path $output "$EngineName-$Temperature"
    $findings = Join-Path $work 'findings.json'
    $stdout = Join-Path $output "$EngineName-$Temperature.stdout.log"
    $stderr = Join-Path $output "$EngineName-$Temperature.stderr.log"
    if ($Temperature -eq 'cold') {
        Remove-Item $cache -Recurse -Force -ErrorAction SilentlyContinue
    }

    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $cache -Force | Out-Null

    $arguments = @(
        $cli,
        $subject,
        '--base', $BaseSha,
        '--pr', $PrSha,
        "--engine=$EngineName",
        '--work', $work,
        '--findings', $findings,
        '--cache-dir', $cache,
        '--cache-retention', '30d',
        '--no-baseline',
        '--strict')
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process dotnet -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not (Test-Path $stdout)) {
        $process.WaitForExit(10) | Out-Null
    }

    $stream = [IO.File]::Open($stdout, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    $engineActive = $false
    $frontierCompleted = $false
    $peakEngineTreeRss = 0L
    try {
        do {
            Read-NewLines $reader ([ref]$engineActive) ([ref]$frontierCompleted)
            if ($engineActive) {
                $peakEngineTreeRss = [Math]::Max($peakEngineTreeRss, (Get-ProcessTreeWorkingSet $process))
            }
        } while (-not $process.WaitForExit(25))
        $process.WaitForExit()
        Read-NewLines $reader ([ref]$engineActive) ([ref]$frontierCompleted)
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
        $stopwatch.Stop()
    }

    if ($process.ExitCode -notin @(0, 1)) {
        throw "$EngineName $Temperature failed with exit $($process.ExitCode): $(Get-Content $stderr -Raw)"
    }
    if (-not $frontierCompleted) {
        throw "$EngineName $Temperature did not complete frontier analysis."
    }

    $artifact = Get-Content $findings -Raw | ConvertFrom-Json
    $expectedCacheStatus = if ($Temperature -eq 'cold') { 'miss' } else { 'hit' }
    if ($artifact.baseTraceCache.status -cne $expectedCacheStatus) {
        throw "$EngineName $Temperature cache status was $($artifact.baseTraceCache.status), expected $expectedCacheStatus."
    }

    [pscustomobject][ordered]@{
        engine = $EngineName
        temperature = $Temperature
        exitCode = $process.ExitCode
        cacheStatus = $artifact.baseTraceCache.status
        wallMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
        diffMilliseconds = [long]$artifact.timings.diffMilliseconds
        frontierMilliseconds = [long]$artifact.timings.frontierMilliseconds
        engineMilliseconds = [long]$artifact.timings.diffMilliseconds + [long]$artifact.timings.frontierMilliseconds
        peakEngineTreeRssBytes = $peakEngineTreeRss
        peakEngineTreeRssMiB = [Math]::Round($peakEngineTreeRss / 1MB, 3)
        findings = $findings
    }
}

$engines = if ($Engine -eq 'both') { @('csharp', 'rust') } else { @($Engine) }
$results = foreach ($engineName in $engines) {
    Measure-Analysis $engineName 'cold'
    Measure-Analysis $engineName 'warm'
}

$report = [pscustomobject][ordered]@{
    schema = 'behaviordiff.pipeline-engine-cost/1'
    generatedUtc = [DateTimeOffset]::UtcNow
    repository = $subject
    baseSha = $BaseSha
    prSha = $PrSha
    results = $results
}
$reportPath = Join-Path $output "pipeline-engine-cost-$Engine.json"
$report | ConvertTo-Json -Depth 10 | Set-Content $reportPath
$results | Format-Table engine, temperature, cacheStatus, wallMilliseconds, engineMilliseconds, peakEngineTreeRssMiB -AutoSize
Write-Host "Report: $reportPath"