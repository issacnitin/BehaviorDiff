#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-redaction-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$fixture = Join-Path $work 'repo'
$runWork = Join-Path $work 'analysis'
$findingsPath = Join-Path $runWork 'findings.json'
$commentPath = Join-Path $runWork 'comment.md'
$baseToken = 'AKIA1234567890ABCDEF'
$prToken = 'AKIAFEDCBA0987654321'
$password = 'correct-horse-battery-staple'

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$label failed with exit $LASTEXITCODE" }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'App'), (Join-Path $fixture 'App.Tests') -Force | Out-Null
    $expiredWork = Join-Path $work 'expired-analysis'
    $expiredRun = Join-Path $expiredWork 'base_run1'
    New-Item -ItemType Directory -Path $expiredRun -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $expiredRun 'run.1.ndjson'), "sensitive trace")
    [IO.File]::WriteAllText((Join-Path $expiredWork 'trace-retention.json'),
        '{"schema":"realdiff.trace-retention/1","expiresUtc":"2000-01-01T00:00:00.0000000+00:00"}')
    [IO.File]::WriteAllText((Join-Path $fixture 'App/App.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><RootNamespace>App</RootNamespace></PropertyGroup>
</Project>
'@)
    [IO.File]::WriteAllText((Join-Path $fixture 'App/SecretConfig.cs'), @"
namespace App;
public static class SecretConfig
{
    public const string Token = `"$baseToken`";
    public static void Touch() { }
}
"@)
    [IO.File]::WriteAllText((Join-Path $fixture 'App/SecretService.cs'), @'
using System;
using System.IO;
namespace App;
public static class SecretService
{
    public static string Authenticate(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("password required", nameof(password));
        SecretConfig.Touch();
        return SecretConfig.Token;
    }
}
'@)
    [IO.File]::WriteAllText((Join-Path $fixture 'App.Tests/App.Tests.csproj'), @'
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
    $tests = @((1..120) | ForEach-Object {
        "    [Fact] public void ReadsToken$_() => Assert.StartsWith(`"AKIA`", SecretService.Authenticate(`"$password`"));"
    }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $fixture 'App.Tests/SecretServiceTests.cs'), @"
using Xunit;
namespace App.Tests;
public sealed class SecretServiceTests
{
$tests
}
"@)
    Push-Location $fixture
    try {
        Invoke-Checked 'solution creation' { & dotnet new sln -n RedactionFixture }
        Invoke-Checked 'solution projects' { & dotnet sln RedactionFixture.sln add App/App.csproj App.Tests/App.Tests.csproj }
    } finally { Pop-Location }

    Invoke-Checked 'git init' { & git -C $fixture init --initial-branch=main --quiet }
    Invoke-Checked 'git identity' { & git -C $fixture config user.email 'redaction-proof@realdiff.invalid' }
    Invoke-Checked 'git identity' { & git -C $fixture config user.name 'RealDiff Redaction Proof' }
    Invoke-Checked 'git add' { & git -C $fixture add . }
    Invoke-Checked 'base commit' { & git -C $fixture commit --quiet -m 'base secret' }
    $base = (& git -C $fixture rev-parse HEAD).Trim()
    $configPath = Join-Path $fixture 'App/SecretConfig.cs'
    $config = (Get-Content $configPath -Raw).Replace($baseToken, $prToken, [StringComparison]::Ordinal)
    [IO.File]::WriteAllText($configPath, $config)
    Invoke-Checked 'PR add' { & git -C $fixture add App/SecretConfig.cs }
    Invoke-Checked 'PR commit' { & git -C $fixture commit --quiet -m 'rotate token' }
    $pr = (& git -C $fixture rev-parse HEAD).Trim()

    Invoke-Checked 'solution build' { & dotnet build (Join-Path $repo 'RealDiff.sln') -c Release --nologo -v quiet }
    $cli = Join-Path $repo 'src/RealDiff.Cli/bin/Release/net8.0/realdiff.dll'
    $output = @(& dotnet $cli $fixture --base $base --pr $pr --work $runWork --findings $findingsPath --no-cache 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 1) { throw "Expected one or more redacted findings, got exit $exitCode" }

    $divergence = Get-Content (Join-Path $runWork 'divergence-set.json') -Raw
    $findings = Get-Content $findingsPath -Raw
    $comment = @(& dotnet run --project (Join-Path $repo 'tools/CommentPreview/RealDiff.CommentPreview.csproj') `
        -c Release -- $findingsPath)
    if ($LASTEXITCODE -ne 0) { throw 'Comment rendering failed' }
    $commentText = $comment -join "`n"
    [IO.File]::WriteAllText($commentPath, $commentText)
    $published = $divergence + "`n" + $findings + "`n" + $commentText
    foreach ($secret in @($baseToken, $prToken, $password)) {
        if ($published.Contains($secret, [StringComparison]::Ordinal)) {
            throw 'A fixture secret was present in a rendered analysis artifact'
        }
    }
    if ($commentText -notmatch '<redacted>' -or $divergence -notmatch '"baseReturnDigest"\s*:\s*"sha256:' `
        -or $divergence -notmatch '"prReturnDigest"\s*:\s*"sha256:') {
        throw 'Redacted output or real-value digest evidence was missing'
    }
    $artifact = $findings | ConvertFrom-Json
    if ($artifact.status -ne 'analyzed' -or $artifact.summary.unexpectedMembers -lt 1) {
        throw 'Secret rotation did not produce an analyzed divergence'
    }
    $remainingTraces = @(Get-ChildItem $runWork -Recurse -Filter 'run.*.ndjson' -ErrorAction SilentlyContinue)
    if ($remainingTraces.Count -ne 0) {
        throw "Default retention left $($remainingTraces.Count) trace file(s) behind"
    }
    if (Test-Path $expiredRun) {
        throw 'Expired retained traces were not pruned on the next CLI run'
    }

    Write-Host '--- REDACTED RENDERED COMMENT ---'
    Write-Host $commentText
    Write-Host '--- END REDACTED RENDERED COMMENT ---'
    Write-Host ("REDACTION_RETENTION_PROOF matched={0} divergences={1} redacted=true secretsLeaked=0 tracesRemaining=0 expiredPruned=true" -f `
        $artifact.summary.observedCallSites, $artifact.summary.unexpectedCallSites)
    Write-Host 'verify-redaction-retention: PASS' -ForegroundColor Green
}
finally {
    if ($ownsWork -and -not $KeepWork) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Redaction proof work kept at $work"
    }
}