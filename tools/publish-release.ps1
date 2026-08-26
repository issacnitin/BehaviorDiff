#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64', 'win-x64')]
    [string]$RuntimeIdentifier,
    [string]$Version = '0.3.0',
    [string]$OutputDirectory = 'artifacts/release',
    [switch]$Trimmed
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
}
$hostOs = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else {
    throw 'Self-contained releases are supported only on Windows, Linux, and macOS.'
}
$hostArchitecture = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    ([Runtime.InteropServices.Architecture]::X64) { 'x64' }
    ([Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
    default { throw "Unsupported release architecture: $($_)." }
}
$hostRid = "$hostOs-$hostArchitecture"
if ($RuntimeIdentifier -ne $hostRid) {
    throw "Release RID $RuntimeIdentifier must be built on a matching native host; this host is $hostRid."
}

$staging = Join-Path $output "staging/$RuntimeIdentifier"
$layout = Join-Path $output "layout/$RuntimeIdentifier"
$tracers = Join-Path $staging 'tracers'
$engines = Join-Path $staging 'engines/rust'
$launcher = Join-Path $staging 'launcher'
Remove-Item $staging, $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $staging, $layout, $output -Force | Out-Null

& (Join-Path $PSScriptRoot 'Stage-CrossLanguageTracers.ps1') -OutputDirectory $tracers
if ($LASTEXITCODE -ne 0) { throw "Tracer staging failed with exit code $LASTEXITCODE" }
& (Join-Path $PSScriptRoot 'Stage-RustEngine.ps1') -OutputDirectory $engines
if ($LASTEXITCODE -ne 0) { throw "Rust engine staging failed with exit code $LASTEXITCODE" }
& (Join-Path $PSScriptRoot 'Stage-RustLauncher.ps1') -OutputDirectory $launcher
if ($LASTEXITCODE -ne 0) { throw "Rust launcher staging failed with exit code $LASTEXITCODE" }

$trim = if ($Trimmed) { 'true' } else { 'false' }
& dotnet publish (Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj') `
    -c Release -r $RuntimeIdentifier --self-contained true -o $layout --nologo `
    '-p:SelfContainedRelease=true' "-p:PublishTrimmed=$trim"
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE" }

Copy-Item $tracers (Join-Path $layout 'tracers') -Recurse -Force
Copy-Item $engines (Join-Path $layout 'engines/rust') -Recurse -Force

$executable = Join-Path $layout $(if ($IsWindows) { 'behaviordiff.exe' } else { 'behaviordiff' })
$managed = Join-Path $layout $(if ($IsWindows) { 'behaviordiff-managed.exe' } else { 'behaviordiff-managed' })
Move-Item $executable $managed -Force
Copy-Item (Join-Path $launcher "$RuntimeIdentifier/$(Split-Path -Leaf $executable)") $executable -Force
& $executable --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Published CLI --help failed with exit code $LASTEXITCODE" }

$required = @(
    'behaviordiff-weaver.dll',
    'behaviordiff-weaver.deps.json',
    'behaviordiff-weaver.runtimeconfig.json',
    $(Split-Path -Leaf $managed),
    'BehaviorDiff.Contracts.dll',
    'BehaviorDiff.Tracer.dll',
    'Mono.Cecil.dll',
    'tracers/java/behaviordiff-java-agent.jar',
    'tracers/node/register.cjs',
    "tracers/go/$RuntimeIdentifier/$(if ($IsWindows) { 'behaviordiff-go-rewrite.exe' } else { 'behaviordiff-go-rewrite' })",
    "tracers/rust/$RuntimeIdentifier/$(if ($IsWindows) { 'behaviordiff-rust-rewrite.exe' } else { 'behaviordiff-rust-rewrite' })",
    "engines/rust/$RuntimeIdentifier/$(if ($IsWindows) { 'behaviordiff-engine.exe' } else { 'behaviordiff-engine' })"
)
foreach ($relative in $required) {
    if (-not (Test-Path (Join-Path $layout $relative) -PathType Leaf)) {
        throw "Release payload is missing $relative"
    }
}

$publicRid = $RuntimeIdentifier.Replace('osx-', 'darwin-')
$baseName = "behaviordiff-v$Version-$publicRid"
$archive = if ($IsWindows) { Join-Path $output "$baseName.zip" } else { Join-Path $output "$baseName.tar.gz" }
Remove-Item $archive, "$archive.sha256" -Force -ErrorAction SilentlyContinue
if ($IsWindows) {
    Compress-Archive -Path (Join-Path $layout '*') -DestinationPath $archive -CompressionLevel Optimal
} else {
    & tar -C $layout -czf $archive .
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
}

$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
$files = @(Get-ChildItem $layout -Recurse -File)
$layoutBytes = ($files | Measure-Object Length -Sum).Sum
$metrics = [ordered]@{
    version = $Version
    runtimeIdentifier = $RuntimeIdentifier
    publicRuntimeIdentifier = $publicRid
    trimmed = $Trimmed.IsPresent
    executableBytes = (Get-Item $executable).Length
    managedBytes = (Get-Item $managed).Length
    layoutBytes = $layoutBytes
    fileCount = $files.Count
    archive = [IO.Path]::GetFileName($archive)
    archiveBytes = (Get-Item $archive).Length
    sha256 = $hash
}
$metrics | ConvertTo-Json | Set-Content (Join-Path $output "$baseName.metrics.json") -Encoding utf8
Write-Host 'BehaviorDiff self-contained release: PASS' -ForegroundColor Green
Write-Host "  rid=$RuntimeIdentifier trimmed=$($Trimmed.IsPresent.ToString().ToLowerInvariant())"
Write-Host "  executableBytes=$((Get-Item $executable).Length) layoutBytes=$layoutBytes files=$($files.Count)"
Write-Host "  archive=$archive bytes=$((Get-Item $archive).Length) sha256=$hash"