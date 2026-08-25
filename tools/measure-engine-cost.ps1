#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$WorkDirectory,

    [int]$Iterations = 3,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Iterations -lt 1) {
    throw 'Iterations must be at least 1.'
}

$repo = Split-Path -Parent $PSScriptRoot
$work = [IO.Path]::GetFullPath($WorkDirectory)
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $work 'engine-cost'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$csharpEngine = Join-Path $repo 'src/BehaviorDiff.Engine/bin/Release/net8.0/BehaviorDiff.Engine.exe'
$rustEngine = Join-Path $repo 'src/BehaviorDiff.Engine.Rust/target/release/behaviordiff-engine.exe'

foreach ($path in @(
    (Join-Path $work 'base_run1'),
    (Join-Path $work 'base_run2'),
    (Join-Path $work 'base_run3'),
    (Join-Path $work 'pr_run'),
    (Join-Path $work 'base'),
    (Join-Path $work 'pr'),
    (Join-Path $work 'changed-files.txt')
)) {
    if (-not (Test-Path $path)) {
        throw "Required benchmark input is missing: $path"
    }
}

& dotnet build (Join-Path $repo 'src/BehaviorDiff.Engine/BehaviorDiff.Engine.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "C# engine build failed: $LASTEXITCODE" }
& cargo build --release --manifest-path (Join-Path $repo 'src/BehaviorDiff.Engine.Rust/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw "Rust engine build failed: $LASTEXITCODE" }

New-Item -ItemType Directory -Path $output -Force | Out-Null

$traceBytes = 0L
$manifestBytes = 0L
foreach ($run in 'base_run1', 'base_run2', 'base_run3', 'pr_run') {
    $files = @(Get-ChildItem (Join-Path $work $run) -File -Filter '*.ndjson')
    $traceBytes += [long](($files | Where-Object Name -NotLike '*.manifest.ndjson' |
        Measure-Object Length -Sum).Sum)
    $manifestBytes += [long](($files | Where-Object Name -Like '*.manifest.ndjson' |
        Measure-Object Length -Sum).Sum)
}
if ($traceBytes -le 0) { throw 'No NDJSON trace bytes were found.' }

function Measure-Process {
    param(
        [Parameter(Mandatory)] [string]$Label,
        [Parameter(Mandatory)] [string]$Executable,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [int]$Iteration
    )

    $stdout = Join-Path $output "$Label-$Iteration.stdout.log"
    $stderr = Join-Path $output "$Label-$Iteration.stderr.log"
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $peakBytes = 0L
    do {
        try {
            $process.Refresh()
            $peakBytes = [Math]::Max($peakBytes, $process.WorkingSet64)
        }
        catch { }
    } while (-not $process.WaitForExit(10))
    $process.WaitForExit()
    $stopwatch.Stop()

    if ($process.ExitCode -ne 0) {
        throw "$Label iteration $Iteration failed with exit $($process.ExitCode): $(Get-Content $stderr -Raw)"
    }

    [pscustomobject][ordered]@{
        label = $Label
        iteration = $Iteration
        wallMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
        peakRssBytes = $peakBytes
        traceBytes = $traceBytes
        amplification = $peakBytes / $traceBytes
    }
}

function Get-DiffArguments([string]$path) {
    @(
        'diff',
        '--base1', (Join-Path $work 'base_run1'),
        '--base2', (Join-Path $work 'base_run2'),
        '--base3', (Join-Path $work 'base_run3'),
        '--pr', (Join-Path $work 'pr_run'),
        '--base-root', (Join-Path $work 'base'),
        '--pr-root', (Join-Path $work 'pr'),
        '--changed-files', (Join-Path $work 'changed-files.txt'),
        '--out', $path)
}

$measurements = @()
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $csharpDivergences = Join-Path $output "csharp-divergences-$iteration.json"
    $rustDivergences = Join-Path $output "rust-divergences-$iteration.json"
    $measurements += Measure-Process 'csharp-diff' $csharpEngine (Get-DiffArguments $csharpDivergences) $iteration
    $measurements += Measure-Process 'rust-diff' $rustEngine (Get-DiffArguments $rustDivergences) $iteration

    $measurements += Measure-Process 'csharp-frontier' $csharpEngine @(
        'frontier', '--in', $csharpDivergences,
        '--changed-files', (Join-Path $work 'changed-files.txt'),
        '--out', (Join-Path $output "csharp-frontier-$iteration.json")) $iteration
    $measurements += Measure-Process 'rust-output-frontier' $csharpEngine @(
        'frontier', '--in', $rustDivergences,
        '--changed-files', (Join-Path $work 'changed-files.txt'),
        '--out', (Join-Path $output "rust-output-frontier-$iteration.json")) $iteration
}

$summary = @($measurements | Group-Object label | ForEach-Object {
    $orderedWall = @($_.Group.wallMilliseconds | Sort-Object)
    $medianWall = $orderedWall[[int][Math]::Floor($orderedWall.Count / 2)]
    $peakRss = [long]($_.Group.peakRssBytes | Measure-Object -Maximum).Maximum
    [pscustomobject][ordered]@{
        stage = $_.Name
        iterations = $_.Count
        medianWallMilliseconds = [Math]::Round($medianWall, 3)
        peakRssBytes = $peakRss
        peakRssMiB = [Math]::Round($peakRss / 1MB, 3)
        traceBytes = $traceBytes
        amplification = [Math]::Round($peakRss / $traceBytes, 4)
    }
})

$report = [pscustomobject][ordered]@{
    schema = 'behaviordiff.engine-cost/1'
    generatedUtc = [DateTimeOffset]::UtcNow
    workDirectory = $work
    traceBytes = $traceBytes
    manifestBytes = $manifestBytes
    totalInputBytes = $traceBytes + $manifestBytes
    frontierImplementation = 'csharp-shared'
    summary = $summary
    measurements = $measurements
}
$reportPath = Join-Path $output 'engine-cost.json'
$report | ConvertTo-Json -Depth 10 | Set-Content $reportPath
$summary | Format-Table stage, iterations, medianWallMilliseconds, peakRssMiB, amplification -AutoSize
Write-Host "Report: $reportPath"
