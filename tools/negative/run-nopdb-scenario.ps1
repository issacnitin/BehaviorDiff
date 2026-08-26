#requires -Version 7.0
<#
    Runs the no-PDB scenario and applies the SourceUnavailable gate. Exits non-zero when the gate fires.
    Invoked as a subprocess by tools/verify-negative-tests.ps1 so the refusal can be asserted.
#>
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repo

$work = Join-Path ([System.IO.Path]::GetTempPath()) 'realdiff-nopdb'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work | Out-Null

$env:DOTNET_JITMinOpts = '1'
$env:REALDIFF_TRACE = Join-Path $work 'run.ndjson'
$env:REALDIFF_NAMESPACES = 'SampleApp.NoPdb'
$env:REALDIFF_EXCLUDE_NAMESPACES = ''
$env:REALDIFF_BACKEND = 'cecil'

# The gate is about an assembly with no PDB, so it has to be woven like any other subject.
$staged = Join-Path $work 'bin'
New-Item -ItemType Directory -Path $staged -Force | Out-Null
Copy-Item 'samples/SampleApp.NoPdb.Tests/bin/Release/net8.0/*' $staged -Recurse -Force

foreach ($pair in @(@('SampleApp.NoPdb', $false), @('SampleApp.NoPdb.Tests', $true))) {
    $dll = Join-Path $staged "$($pair[0]).dll"
    if (-not (Test-Path $dll)) { continue }

    $weaveArgs = @('--assembly', $dll, '--include', 'SampleApp.NoPdb')
    if ($pair[1]) { $weaveArgs += '--test-assembly' }
    dotnet run --project tools/Weaver/Weaver.csproj -c Release -v quiet --no-build -- @weaveArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "weave failed: $($pair[0])" }
    Move-Item "$dll.woven" $dll -Force
}

dotnet test (Join-Path $staged 'SampleApp.NoPdb.Tests.dll') --nologo | Out-Null

$manifestFile = Get-ChildItem $work -Filter 'run.*.manifest.ndjson' | Select-Object -First 1
if (-not $manifestFile) { throw 'no manifest produced' }

$assemblies = Get-Content $manifestFile.FullName |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.kind -eq 'assembly' -and $_.instrumented }

foreach ($a in $assemblies) {
    "  {0,-24} patched={1,-3} exactSource={2,-3} pct={3,-4} tracedCalls={4,-4} unavailable={5}" -f `
        $a.assembly, $a.patchedMembers, $a.membersWithExactSource, $a.exactSourcePercent, $a.tracedCalls, [bool]$a.sourceUnavailable
}

$dead = $assemblies | Where-Object { $_.sourceUnavailable -and $_.tracedCalls -gt 0 }
if ($dead.Count -gt 0) {
    foreach ($a in $dead) {
        Write-Host "RUN INVALID - SourceUnavailable: assembly '$($a.assembly)' produced $($a.tracedCalls) traced call(s) but only $($a.exactSourcePercent)% of its patched members resolved a source line."
    }

    Write-Host "Divergences in it cannot be attributed to a changed file and would be silently classified EXPECTED."
    Write-Host "Remedy: build it with <DebugType>portable</DebugType>."
    exit 3
}

Write-Host 'no SourceUnavailable assemblies'
exit 0
