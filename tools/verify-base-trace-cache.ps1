#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-cache-proof-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$cache = Join-Path $work 'cache'
$cliProject = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$cli = Join-Path $repo 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.dll'

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$label failed with exit $LASTEXITCODE" }
}

function Initialize-History([string]$directory) {
    Invoke-Checked 'git init' { & git -C $directory init --initial-branch=main --quiet }
    Invoke-Checked 'git identity' { & git -C $directory config user.email 'cache-proof@behaviordiff.invalid' }
    Invoke-Checked 'git identity' { & git -C $directory config user.name 'BehaviorDiff Cache Proof' }
    Invoke-Checked 'git add' { & git -C $directory add . }
    Invoke-Checked 'base commit' { & git -C $directory commit --quiet -m 'base' }
    $base = (& git -C $directory rev-parse HEAD).Trim()
    Invoke-Checked 'PR commit' { & git -C $directory commit --quiet --allow-empty -m 'no-op PR' }
    [pscustomobject]@{ Base = $base; Pr = (& git -C $directory rev-parse HEAD).Trim() }
}

function New-DotNetFixture([string]$directory) {
    New-Item -ItemType Directory -Path (Join-Path $directory 'App'), (Join-Path $directory 'App.Tests') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'App/App.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup><TargetFramework>net8.0</TargetFramework><RootNamespace>App</RootNamespace></PropertyGroup>
</Project>
'@)
    [IO.File]::WriteAllText((Join-Path $directory 'App/Subject.cs'), @'
namespace App;
public static class Subject { public static string Echo(string value) => value; }
'@)
    [IO.File]::WriteAllText((Join-Path $directory 'App.Tests/App.Tests.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup><TargetFramework>net8.0</TargetFramework><RootNamespace>App.Tests</RootNamespace><IsPackable>false</IsPackable></PropertyGroup>
  <ItemGroup><ProjectReference Include="..\App\App.csproj" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2" />
  </ItemGroup>
</Project>
'@)
    $testMethods = @((1..120) | ForEach-Object {
        "    [Fact] public void Echoes$_() => Assert.Equal(`"value$_`", Subject.Echo(`"value$_`"));"
    }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $directory 'App.Tests/SubjectTests.cs'), @"
using Xunit;
namespace App.Tests;
public sealed class SubjectTests
{
$testMethods
}
"@)
    Push-Location $directory
    try {
        Invoke-Checked 'solution creation' { & dotnet new sln -n CacheFixture }
        Invoke-Checked 'solution projects' { & dotnet sln CacheFixture.sln add App/App.csproj App.Tests/App.Tests.csproj }
    } finally { Pop-Location }
    Initialize-History $directory
}

function New-NodeFixture([string]$directory) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item (Join-Path $repo 'samples/NodeReference/*') $directory -Recurse -Force
    Get-ChildItem $directory -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object Name -In @('node_modules', 'generated') |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $directory 'package-lock.json'))) {
        Push-Location $directory
        try {
            Invoke-Checked 'Node lockfile generation' {
                & npm install --package-lock-only --ignore-scripts --no-audit --no-fund
            }
        } finally { Pop-Location }
    }
    Initialize-History $directory
}

function Invoke-Analysis([string]$language, [string]$directory, [object]$refs, [string]$label) {
    $runWork = Join-Path $work "$language-$label"
    $findings = Join-Path $runWork 'findings.json'
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $output = @(& dotnet $cli $directory --base $refs.Base --pr $refs.Pr --cache-dir $cache `
        --work $runWork --findings $findings 2>&1)
    $exitCode = $LASTEXITCODE
    $stopwatch.Stop()
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "$language $label analysis failed with exit $exitCode" }

    $artifact = Get-Content $findings -Raw | ConvertFrom-Json
    $expectedStatus = if ($label -eq 'cold') { 'miss' } else { 'hit' }
    if ($artifact.baseTraceCache.status -cne $expectedStatus) {
        throw "$language $label findings cache status was $($artifact.baseTraceCache.status), expected $expectedStatus"
    }
    if ($label -eq 'warm' -and [long]$artifact.baseTraceCache.savedWallClockMilliseconds -le 0) {
        throw "$language warm findings did not report positive saved wall-clock time"
    }

    $text = $output -join "`n"
    $baseRuns = @([regex]::Matches($text, '(?m)^\s*(base_run[123])\s+.*(?:traces=|command:)') |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique).Count
    $prRuns = @([regex]::Matches($text, '(?m)^\s*(pr_run)\s+.*(?:traces=|command:)') |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique).Count
    $expectedBaseRuns = if ($label -eq 'cold') { 3 } else { 0 }
    if ($baseRuns -ne $expectedBaseRuns -or $prRuns -ne 1) {
        throw "$language $label instrumented run count mismatch: base=$baseRuns pr=$prRuns"
    }

    [pscustomobject]@{
        Language = $language
        Label = $label
        ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
        SavedMilliseconds = [long]$artifact.baseTraceCache.savedWallClockMilliseconds
        BaseRuns = $baseRuns
        PrRuns = $prRuns
    }
}

function Assert-WarmCommandHit([string]$language, [string]$directory, [object]$refs) {
    $warmWork = Join-Path $work "$language-nightly-warm"
    $output = @(& dotnet $cli warm $directory --target $refs.Base --cache-dir $cache --work $warmWork 2>&1)
    $exitCode = $LASTEXITCODE
    $text = $output -join "`n"
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0 -or $text -notmatch 'base trace cache: hit' `
        -or $text -match '(?m)^\s*base_run[123]\s+.*(?:traces=|command:)') {
        throw "$language nightly warm command did not produce a zero-run cache hit"
    }
}

$previousNodeTracer = $env:BEHAVIORDIFF_NODE_TRACER
try {
    New-Item -ItemType Directory -Path $work, $cache -Force | Out-Null
    Invoke-Checked 'CLI build' { & dotnet build $cliProject -c Release --nologo -v quiet }
    $env:BEHAVIORDIFF_NODE_TRACER = Join-Path $repo 'src/BehaviorDiff.Node'

    $dotnetRepo = Join-Path $work 'dotnet-repo'
    $nodeRepo = Join-Path $work 'node-repo'
    $dotnetRefs = New-DotNetFixture $dotnetRepo
    $nodeRefs = New-NodeFixture $nodeRepo

    $results = @(
        Invoke-Analysis 'dotnet' $dotnetRepo $dotnetRefs 'cold'
        Invoke-Analysis 'dotnet' $dotnetRepo $dotnetRefs 'warm'
        Invoke-Analysis 'node' $nodeRepo $nodeRefs 'cold'
        Invoke-Analysis 'node' $nodeRepo $nodeRefs 'warm'
    )
    Assert-WarmCommandHit 'dotnet' $dotnetRepo $dotnetRefs
    Assert-WarmCommandHit 'node' $nodeRepo $nodeRefs

    Write-Host 'BASE_TRACE_CACHE_PROOF' -ForegroundColor Green
    foreach ($result in $results) {
        Write-Host ("  {0} {1}: elapsed={2}ms base-runs={3} pr-runs={4} reported-saved={5}ms" -f `
            $result.Language, $result.Label, $result.ElapsedMilliseconds, $result.BaseRuns, $result.PrRuns, $result.SavedMilliseconds)
    }
    Write-Host 'verify-base-trace-cache: PASS' -ForegroundColor Green
}
finally {
    $env:BEHAVIORDIFF_NODE_TRACER = $previousNodeTracer
    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Base trace cache proof work kept at $work"
    }
}