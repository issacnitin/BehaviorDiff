#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Image,
    [Parameter(Mandatory)] [string]$Repository,
    [Parameter(Mandatory)] [string]$BaseSha,
    [Parameter(Mandatory)] [string]$PrSha,
    [Parameter(Mandatory)] [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$subject = [IO.Path]::GetFullPath($Repository)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$nugetFeed = Join-Path $output 'nuget-feed'
$proofVolume = "behaviordiff-fv-proof-$([Guid]::NewGuid().ToString('N').Substring(0, 10))"
$nugetPackages = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path $HOME '.nuget/packages'
} else {
    [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
}
if (-not (Test-Path (Join-Path $subject '.git'))) { throw "Container benchmark repository is not a Git checkout: $subject" }
if (-not (& docker image inspect $Image 2>$null)) { throw "Container benchmark image was not found: $Image" }
if (-not (Test-Path $nugetPackages) -or @(Get-ChildItem $nugetPackages -Directory).Count -le 0) {
    throw "Container benchmark host NuGet cache is empty: $nugetPackages"
}
& git -C $subject worktree prune
if ($LASTEXITCODE -ne 0) { throw "Container benchmark could not prune stale subject worktrees: $LASTEXITCODE" }

function Docker-Path([string]$Path) {
    $cygpath = Get-Command cygpath -ErrorAction SilentlyContinue
    if ($null -ne $cygpath) { return (& $cygpath.Source -w $Path).Trim() }
    return $Path
}

function Convert-MemoryBytes([string]$Text) {
    $value = ($Text -split '/')[0].Trim()
    if ($value -notmatch '^([0-9.]+)([KMG]iB|B)$') { throw "Unrecognized docker memory value: $Text" }
    $number = [double]$Matches[1]
    $multiplier = switch ($Matches[2]) { 'KiB' { 1KB } 'MiB' { 1MB } 'GiB' { 1GB } default { 1 } }
    return [long]($number * $multiplier)
}

function Read-NewLines([IO.StreamReader]$Reader, [ref]$EngineActive, [ref]$FrontierCompleted) {
    while (-not $Reader.EndOfStream) {
        $line = $Reader.ReadLine()
        if ($line -eq '=== 9. engine part 1 ===') { $EngineActive.Value = $true }
        elseif ($line -like 'Frontier report written:*') {
            $FrontierCompleted.Value = $true
            $EngineActive.Value = $false
        }
    }
}

function Measure-Container([string]$Temperature) {
    $work = Join-Path $output "work-$Temperature"
    $findings = Join-Path $output "findings-$Temperature.json"
    $stdout = Join-Path $output "$Temperature.stdout.log"
    $stderr = Join-Path $output "$Temperature.stderr.log"
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $findings, $stdout, $stderr -Force -ErrorAction SilentlyContinue
    New-Item $work -ItemType Directory -Force | Out-Null

    $name = "behaviordiff-fv-$Temperature-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $arguments = @(
        'run', '--name', $name,
        '--entrypoint', '/usr/local/bin/behaviordiff',
        '--volume', "$(Docker-Path $subject):/subject",
        '--volume', "${proofVolume}:/proof",
        '--volume', "$(Docker-Path $nugetFeed):/nuget-feed:ro",
        '--volume', "$(Docker-Path $nugetPackages):/root/.nuget/packages:ro",
        '--env', 'NuGetAudit=false',
        '--env', 'RestoreIgnoreFailedSources=true',
        '--env', 'WarningsNotAsErrors=NU1801',
        '--env', 'RestoreSources=/nuget-feed',
        $Image,
        '/subject', '--base', $BaseSha, '--pr', $PrSha,
        '--work', "/proof/work-$Temperature",
        '--findings', "/proof/findings-$Temperature.json",
        '--cache-dir', '/proof/cache', '--cache-retention', '30d',
        '--no-baseline', '--strict', '--keep', '--keep-traces', '1d'
    )
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process docker -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    while (-not (Test-Path $stdout)) { $process.WaitForExit(10) | Out-Null }
    $stream = [IO.File]::Open($stdout, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    $engineActive = $false
    $frontierCompleted = $false
    $rssSamples = 0
    $peak = 0L
    try {
        do {
            Read-NewLines $reader ([ref]$engineActive) ([ref]$frontierCompleted)
            if ($engineActive) {
                $memory = @(& docker stats --no-stream --format '{{.MemUsage}}' $name 2>$null)
                if ($LASTEXITCODE -eq 0 -and $memory.Count -eq 1) {
                    $peak = [Math]::Max($peak, (Convert-MemoryBytes $memory[0]))
                    $rssSamples++
                }
            }
        } while (-not $process.WaitForExit(25))
        $process.WaitForExit()
        Read-NewLines $reader ([ref]$engineActive) ([ref]$frontierCompleted)
    } finally {
        $reader.Dispose()
        $stream.Dispose()
        $watch.Stop()
        & docker cp "${name}:/proof/findings-$Temperature.json" $findings 2>$null | Out-Null
        & docker rm -f $name 2>$null | Out-Null
    }

    if ($process.ExitCode -notin @(0, 1)) { throw "Container $Temperature run failed with exit $($process.ExitCode): $(Get-Content $stderr -Raw)" }
    if (-not $frontierCompleted) { throw "Container $Temperature run did not complete frontier analysis" }
    if ($rssSamples -le 0 -or $peak -le 0) { throw "Container $Temperature engine interval had no RSS samples" }
    if (-not (Test-Path $findings)) { throw "Container $Temperature findings are missing" }
    $artifact = Get-Content $findings -Raw | ConvertFrom-Json
    $expectedCache = if ($Temperature -eq 'cold') { 'miss' } else { 'hit' }
    if ($artifact.baseTraceCache.status -cne $expectedCache) {
        throw "Container $Temperature cache status was $($artifact.baseTraceCache.status), expected $expectedCache"
    }
    $diff = [long]$artifact.timings.diffMilliseconds
    $frontier = [long]$artifact.timings.frontierMilliseconds
    if ($diff -le 0 -or $frontier -le 0) { throw "Container $Temperature timings are empty: diff=$diff frontier=$frontier" }

    [pscustomobject][ordered]@{
        temperature = $Temperature
        exitCode = $process.ExitCode
        cacheStatus = $artifact.baseTraceCache.status
        wallMilliseconds = $watch.Elapsed.TotalMilliseconds
        diffMilliseconds = $diff
        frontierMilliseconds = $frontier
        engineMilliseconds = $diff + $frontier
        peakEngineIntervalRssBytes = $peak
        peakEngineIntervalRssMiB = [Math]::Round($peak / 1MB, 3)
        rssSamples = $rssSamples
        savedWallClockMilliseconds = [long]$artifact.baseTraceCache.savedWallClockMilliseconds
        findings = $findings
    }
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item $output, $nugetFeed -ItemType Directory -Force | Out-Null
$sourceLinkPackages = @(Get-ChildItem (Join-Path $nugetPackages 'microsoft.sourcelink.github') -Recurse -Filter '*.nupkg' -File)
if ($sourceLinkPackages.Count -le 0) {
    throw "Container benchmark local SourceLink feed input is empty under $nugetPackages"
}
$sourceLinkPackages | Copy-Item -Destination $nugetFeed
& docker volume create $proofVolume | Out-Null
$results = @()
try {
    $results = @(
        Measure-Container 'cold'
        Measure-Container 'warm'
    )
} catch {
    Write-Host "Retained failed benchmark volume: $proofVolume"
    throw
}
& docker volume rm $proofVolume | Out-Null
$report = [pscustomobject][ordered]@{
    schema = 'behaviordiff.container-pipeline-cost/1'
    generatedUtc = [DateTimeOffset]::UtcNow
    image = $Image
    repository = $subject
    baseSha = $BaseSha
    prSha = $PrSha
    results = $results
}
$reportPath = Join-Path $output 'container-pipeline-cost.json'
$report | ConvertTo-Json -Depth 10 | Set-Content $reportPath
$results | Format-Table temperature, cacheStatus, wallMilliseconds, engineMilliseconds, peakEngineIntervalRssMiB, rssSamples, savedWallClockMilliseconds -AutoSize
Write-Host "Report: $reportPath"
