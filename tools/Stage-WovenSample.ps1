#requires -Version 7
<#
Stages an already-built SampleApp test output and weaves it, so the proofs can run against Cecil.
Weaving always runs on a fresh copy of the build output: weaving an already-woven assembly would
nest the instrumentation, and there is no marker to detect that.

Writes to $OutDir and returns nothing; callers use $OutDir directly.
#>
param(
    [Parameter(Mandatory)][string]$TreeRoot,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$InstrumentationTreeRoot = $TreeRoot,
    [string]$Tfm = 'net8.0'
)

$ErrorActionPreference = 'Stop'

$built = Join-Path $TreeRoot "samples/SampleApp.Tests/bin/Release/$Tfm"
if (-not (Test-Path (Join-Path $built 'SampleApp.Tests.dll'))) {
    throw "SampleApp.Tests is not built at $built"
}

Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Copy-Item "$built\*" $OutDir -Recurse -Force

foreach ($instrumentation in @(
    'src/BehaviorDiff.Contracts/bin/Release/netstandard2.0/BehaviorDiff.Contracts',
    'src/BehaviorDiff.Tracer/bin/Release/netstandard2.0/BehaviorDiff.Tracer',
    'src/BehaviorDiff.Tracer.Xunit/bin/Release/netstandard2.0/BehaviorDiff.Tracer.Xunit')) {
    foreach ($extension in @('.dll', '.pdb')) {
        $source = Join-Path $InstrumentationTreeRoot ($instrumentation + $extension)
        if (Test-Path $source) { Copy-Item $source $OutDir -Force }
    }
}

$weaver = Join-Path $InstrumentationTreeRoot 'tools/Weaver/Weaver.csproj'
foreach ($target in @(
    @{ Name = 'Infrastructure.Collections'; Test = $false },
    @{ Name = 'Commerce.Pricing'; Test = $false },
        @{ Name = 'SampleApp'; Test = $false },
        @{ Name = 'SampleApp.Plugin'; Test = $false },
        @{ Name = 'SampleApp.Tests'; Test = $true })) {

    $dll = Join-Path $OutDir "$($target.Name).dll"
    if (-not (Test-Path $dll)) { continue }

    $weaveArgs = @(
        '--assembly', $dll,
        '--include', 'SampleApp,Commerce.Pricing,Infrastructure.Collections',
        '--exclude', 'SampleApp.Diagnostics,SampleApp.Persistence,Infrastructure.Collections')
    if ($target.Test) { $weaveArgs += '--test-assembly' }

    $out = dotnet run --project $weaver -c Release -v quiet --no-build -- @weaveArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $out | Select-Object -Last 6 | ForEach-Object { Write-Host "    $_" }
        throw "weave failed: $($target.Name)"
    }

    Move-Item "$dll.woven" $dll -Force
}
