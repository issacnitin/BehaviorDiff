#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-rust-generic-identity-gate'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'samples/RustReference'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$cache = Join-Path $work 'cache'
$run = Join-Path $work 'run'
$trace = Join-Path $run 'run.rust.ndjson'
$traceManifest = Join-Path $run 'run.rust.manifest.ndjson'
$tracerManifest = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/Cargo.toml'
$binary = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/target/release/behaviordiff-rust-rewrite.exe'
if (-not $IsWindows) { $binary = $binary.Substring(0, $binary.Length - 4) }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $run -ItemType Directory -Force | Out-Null
& cargo build --release --manifest-path $tracerManifest
if ($LASTEXITCODE -ne 0) { throw "Rust tracer build failed: $LASTEXITCODE" }
$rewrite = (& $binary --source $source --cache-root $cache | ConvertFrom-Json)
if ($rewrite.sourceFiles -le 0 -or $rewrite.rustFiles -le 0) {
    throw "Rust generic identity inputs are empty: source=$($rewrite.sourceFiles) rust=$($rewrite.rustFiles)"
}
$env:BEHAVIORDIFF_RUST_EXIT_TRACE = $trace
try {
    & cargo run --quiet --manifest-path (Join-Path $rewrite.output 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw "Rewritten Rust reference failed: $LASTEXITCODE" }
} finally {
    Remove-Item Env:BEHAVIORDIFF_RUST_EXIT_TRACE -ErrorAction SilentlyContinue
}
& $binary finalize --origin (Join-Path $rewrite.output '.behaviordiff-rust-origin.json') --trace $trace --out $traceManifest | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Rust generic manifest finalization failed: $LASTEXITCODE" }

$events = @(Get-Content $trace | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
$members = @(Get-Content $traceManifest | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object kind -ceq 'member')
if ($events.Count -le 0 -or $members.Count -le 0) {
    throw "Rust generic identity populations are empty: events=$($events.Count) members=$($members.Count)"
}
$genericEvents = @($events | Where-Object methodFullName -like '*generic_identity<*')
$genericMethods = @($genericEvents | ForEach-Object methodFullName | Sort-Object -Unique)
$genericMembers = @($members | Where-Object { $_.method -in $genericMethods -and $_.status -ceq 'Patched' })
$genericTemplate = @($members | Where-Object { $_.method -like '*generic_identity' -and $_.status -ceq 'Skipped' })
if ($genericEvents.Count -ne 2 -or $genericMethods.Count -ne 2 -or $genericMembers.Count -ne 2 -or $genericTemplate.Count -ne 1) {
    throw "Rust generic identity counts differ: events=$($genericEvents.Count) identities=$($genericMethods.Count) concreteMembers=$($genericMembers.Count) templates=$($genericTemplate.Count)"
}
$missingMembers = @($events | Where-Object { $_.methodFullName -notin $members.method })
if ($missingMembers.Count -ne 0) {
    throw "Rust manifest omitted $($missingMembers.Count) event identities from $($events.Count) events and $($members.Count) members"
}

[pscustomobject]@{
    SourceFiles = $rewrite.sourceFiles
    RustFiles = $rewrite.rustFiles
    Events = $events.Count
    ManifestMembers = $members.Count
    GenericEvents = $genericEvents.Count
    ConcreteGenericIdentities = $genericMethods.Count
    ConcreteGenericMembers = $genericMembers.Count
    GenericTemplateMembers = $genericTemplate.Count
    MissingEventMembers = $missingMembers.Count
    Identities = ($genericMethods -join '; ')
} | Format-List
Write-Host 'RUST_GENERIC_IDENTITIES: PASS'