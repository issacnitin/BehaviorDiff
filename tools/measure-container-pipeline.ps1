#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Image,
    [Parameter(Mandatory)] [string]$Repository,
    [Parameter(Mandatory)] [string]$BaseSha,
    [Parameter(Mandatory)] [string]$PrSha,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [ValidateSet('cold', 'warm')] [string[]]$Temperatures = @('cold', 'warm')
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

function Read-Markers([string]$Path, [ref]$EngineActive, [ref]$FrontierCompleted) {
    if (-not (Test-Path $Path -PathType Leaf)) { return }
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $reader = [IO.StreamReader]::new($stream)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    $started = $text.Contains('=== 9. engine part 1 ===', [StringComparison]::Ordinal)
    $completed = $text.Contains('Frontier report written:', [StringComparison]::Ordinal)
    $FrontierCompleted.Value = $completed
    $EngineActive.Value = $started -and -not $completed
}

function Read-ContainerMemory([string]$Name) {
    $script = @'
root=1
descendants=" $root "
changed=1
while [ "$changed" -eq 1 ]; do
  changed=0
  for status in /proc/[0-9]*/status; do
    [ -r "$status" ] || continue
    pid=$(awk '/^Pid:/{print $2}' "$status")
    ppid=$(awk '/^PPid:/{print $2}' "$status")
    case "$descendants" in
      *" $ppid "*) case "$descendants" in *" $pid "*) ;; *) descendants="$descendants$pid "; changed=1 ;; esac ;;
    esac
  done
done
rss_kib=0
processes=0
for pid in $descendants; do
  [ -r "/proc/$pid/status" ] || continue
  value=$(awk '/^VmRSS:/{print $2}' "/proc/$pid/status")
  rss_kib=$((rss_kib + ${value:-0}))
  processes=$((processes + 1))
done
current=$(cat /sys/fs/cgroup/memory.current)
anon=$(awk '$1=="anon"{print $2}' /sys/fs/cgroup/memory.stat)
file=$(awk '$1=="file"{print $2}' /sys/fs/cgroup/memory.stat)
shmem=$(awk '$1=="shmem"{print $2}' /sys/fs/cgroup/memory.stat)
file_mapped=$(awk '$1=="file_mapped"{print $2}' /sys/fs/cgroup/memory.stat)
kernel=$(awk '$1=="kernel"{print $2}' /sys/fs/cgroup/memory.stat)
printf 'rss=%s processes=%s current=%s anon=%s file=%s shmem=%s file_mapped=%s kernel=%s\n' "$((rss_kib * 1024))" "$processes" "$current" "$anon" "$file" "$shmem" "$file_mapped" "$kernel"
'@
    $script = $script.Replace("`r", '')
    $line = @(& docker exec $Name /bin/sh -c $script 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -or $line.Count -ne 1) {
        Add-Content $script:MemoryProbeFailureLog ("{0:o} exit={1} lines={2} raw={3}" -f `
            [DateTimeOffset]::UtcNow, $exitCode, $line.Count, ($line -join ' | '))
        return $null
    }
    $values = @{}
    foreach ($part in $line[0] -split ' ') {
        if ($part -match '^([^=]+)=([0-9]+)$') { $values[$Matches[1]] = [long]$Matches[2] }
    }
    foreach ($required in @('rss', 'processes', 'current', 'anon', 'file', 'shmem', 'file_mapped', 'kernel')) {
        if (-not $values.ContainsKey($required)) {
            Add-Content $script:MemoryProbeFailureLog ("{0:o} missing={1} raw={2}" -f `
                [DateTimeOffset]::UtcNow, $required, $line[0])
            return $null
        }
    }
    return [pscustomobject]$values
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
    $engineActive = $false
    $frontierCompleted = $false
    $memorySamples = [Collections.Generic.List[object]]::new()
    $markerLog = Join-Path $output "$Temperature.marker-transitions.log"
    $script:MemoryProbeFailureLog = Join-Path $output "$Temperature.memory-probe-failures.log"
    $lastEngineActive = $false
    $lastFrontierCompleted = $false
    $peakProcessTreeRss = 0L
    $peakCgroupCurrent = 0L
    $peakCgroupAnon = 0L
    $peakCgroupFile = 0L
    $peakCgroupShmem = 0L
    $peakCgroupFileMapped = 0L
    $peakCgroupKernel = 0L
    try {
        do {
            Read-Markers $stdout ([ref]$engineActive) ([ref]$frontierCompleted)
            if ($engineActive -ne $lastEngineActive -or $frontierCompleted -ne $lastFrontierCompleted) {
                Add-Content $markerLog ("{0:o} elapsedMs={1:N3} engineActive={2} frontierCompleted={3} stdoutBytes={4}" -f `
                    [DateTimeOffset]::UtcNow, $watch.Elapsed.TotalMilliseconds, $engineActive, $frontierCompleted,
                    (Get-Item $stdout).Length)
                $lastEngineActive = $engineActive
                $lastFrontierCompleted = $frontierCompleted
            }
            if ($engineActive) {
                $memory = Read-ContainerMemory $name
                if ($null -ne $memory) {
                    $memorySamples.Add([pscustomobject][ordered]@{
                        elapsedMilliseconds = $watch.Elapsed.TotalMilliseconds
                        processTreeRssBytes = $memory.rss
                        processCount = $memory.processes
                        cgroupCurrentBytes = $memory.current
                        cgroupAnonBytes = $memory.anon
                        cgroupFileBytes = $memory.file
                        cgroupShmemBytes = $memory.shmem
                        cgroupFileMappedBytes = $memory.file_mapped
                        cgroupKernelBytes = $memory.kernel
                    })
                    $peakProcessTreeRss = [Math]::Max($peakProcessTreeRss, $memory.rss)
                    $peakCgroupCurrent = [Math]::Max($peakCgroupCurrent, $memory.current)
                    $peakCgroupAnon = [Math]::Max($peakCgroupAnon, $memory.anon)
                    $peakCgroupFile = [Math]::Max($peakCgroupFile, $memory.file)
                    $peakCgroupShmem = [Math]::Max($peakCgroupShmem, $memory.shmem)
                    $peakCgroupFileMapped = [Math]::Max($peakCgroupFileMapped, $memory.file_mapped)
                    $peakCgroupKernel = [Math]::Max($peakCgroupKernel, $memory.kernel)
                }
            }
        } while (-not $process.WaitForExit(25))
        $process.WaitForExit()
        Read-Markers $stdout ([ref]$engineActive) ([ref]$frontierCompleted)
    } finally {
        $watch.Stop()
        & docker cp "${name}:/proof/findings-$Temperature.json" $findings 2>$null | Out-Null
        & docker rm -f $name 2>$null | Out-Null
    }

    if ($process.ExitCode -notin @(0, 1)) { throw "Container $Temperature run failed with exit $($process.ExitCode): $(Get-Content $stderr -Raw)" }
    if (-not $frontierCompleted) { throw "Container $Temperature run did not complete frontier analysis" }
    if ($memorySamples.Count -le 0 -or $peakProcessTreeRss -le 0 -or $peakCgroupCurrent -le 0) {
        throw "Container $Temperature engine interval had no memory samples"
    }
    if (-not (Test-Path $findings)) { throw "Container $Temperature findings are missing" }
    $artifact = Get-Content $findings -Raw | ConvertFrom-Json
    $expectedCache = if ($Temperature -eq 'cold') { 'miss' } else { 'hit' }
    if ($artifact.baseTraceCache.status -cne $expectedCache) {
        throw "Container $Temperature cache status was $($artifact.baseTraceCache.status), expected $expectedCache"
    }
    $diff = [long]$artifact.timings.diffMilliseconds
    $frontier = [long]$artifact.timings.frontierMilliseconds
    if ($diff -le 0 -or $frontier -le 0) { throw "Container $Temperature timings are empty: diff=$diff frontier=$frontier" }

    $memorySampleFile = Join-Path $output "$Temperature.memory-samples.csv"
    $memorySamples | Export-Csv $memorySampleFile -NoTypeInformation
    [pscustomobject][ordered]@{
        temperature = $Temperature
        exitCode = $process.ExitCode
        cacheStatus = $artifact.baseTraceCache.status
        wallMilliseconds = $watch.Elapsed.TotalMilliseconds
        diffMilliseconds = $diff
        frontierMilliseconds = $frontier
        engineMilliseconds = $diff + $frontier
        peakProcessTreeRssBytes = $peakProcessTreeRss
        peakProcessTreeRssMiB = [Math]::Round($peakProcessTreeRss / 1MB, 3)
        peakCgroupCurrentBytes = $peakCgroupCurrent
        peakCgroupCurrentMiB = [Math]::Round($peakCgroupCurrent / 1MB, 3)
        peakCgroupAnonBytes = $peakCgroupAnon
        peakCgroupAnonMiB = [Math]::Round($peakCgroupAnon / 1MB, 3)
        peakCgroupFileBytes = $peakCgroupFile
        peakCgroupFileMiB = [Math]::Round($peakCgroupFile / 1MB, 3)
        peakCgroupShmemBytes = $peakCgroupShmem
        peakCgroupFileMappedBytes = $peakCgroupFileMapped
        peakCgroupKernelBytes = $peakCgroupKernel
        memorySamples = $memorySamples.Count
        memorySampleFile = $memorySampleFile
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
    $results = @($Temperatures | ForEach-Object { Measure-Container $_ })
} catch {
    Write-Host "Retained failed benchmark volume: $proofVolume"
    throw
}
& docker volume rm $proofVolume | Out-Null
$report = [pscustomobject][ordered]@{
    schema = 'behaviordiff.container-pipeline-cost/2'
    generatedUtc = [DateTimeOffset]::UtcNow
    image = $Image
    repository = $subject
    baseSha = $BaseSha
    prSha = $PrSha
    results = $results
}
$reportPath = Join-Path $output 'container-pipeline-cost.json'
$report | ConvertTo-Json -Depth 10 | Set-Content $reportPath
$results | Format-Table temperature, cacheStatus, wallMilliseconds, engineMilliseconds, peakProcessTreeRssMiB, peakCgroupCurrentMiB, peakCgroupAnonMiB, peakCgroupFileMiB, memorySamples -AutoSize
Write-Host "Report: $reportPath"
