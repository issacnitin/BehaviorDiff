#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'realdiff-rust-exit-hook-gate')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$source = if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    Join-Path $repo 'samples/RustReference'
} else {
    [IO.Path]::GetFullPath($SourceDirectory)
}
$work = [IO.Path]::GetFullPath($WorkDirectory)
$cache = Join-Path $work 'cache'
$trace = Join-Path $work 'exit-hooks.ndjson'
$manifest = Join-Path $repo 'src/RealDiff.Rust.Tracer/Cargo.toml'
$binary = Join-Path $repo 'src/RealDiff.Rust.Tracer/target/release/realdiff-rust-rewrite.exe'
if (-not $IsWindows) { $binary = $binary.Substring(0, $binary.Length - 4) }

function Get-SourceHashes([string]$Root) {
    $files = @(Get-ChildItem $Root -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/]target[\\/]' -and $_.FullName -notmatch '[\\/]\.git[\\/]'
    } | Sort-Object FullName)
    if ($files.Count -eq 0) { throw "Rust exit-hook source input is empty: $Root" }
    @($files | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
            Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
    })
}

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $work -ItemType Directory -Force | Out-Null
$before = @(Get-SourceHashes $source)

& cargo build --release --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw "Rust rewriter build failed: $LASTEXITCODE" }
$rewrite = (& $binary --source $source --cache-root $cache | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw "Rust exit-hook rewrite failed: $LASTEXITCODE" }
if ($rewrite.sourceFiles -le 0 -or $rewrite.rustFiles -le 0) {
    throw "Rust exit-hook rewrite input is empty: source=$($rewrite.sourceFiles) rust=$($rewrite.rustFiles)"
}

$previousTrace = $env:REALDIFF_RUST_EXIT_TRACE
$env:REALDIFF_RUST_EXIT_TRACE = $trace
try {
    & cargo run --quiet --manifest-path (Join-Path $rewrite.output 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw "Rewritten Rust reference failed: $LASTEXITCODE" }
} finally {
    $env:REALDIFF_RUST_EXIT_TRACE = $previousTrace
}

$after = @(Get-SourceHashes $source)
if (($before | ConvertTo-Json -Compress) -cne ($after | ConvertTo-Json -Compress)) {
    throw 'Rust source hashes changed while exercising exit hooks'
}
if (-not (Test-Path $trace)) { throw 'Rust exit-hook trace was not created' }
$lines = @(Get-Content $trace | Where-Object { $_.Trim().Length -gt 0 })
if ($lines.Count -eq 0) { throw 'Rust exit-hook trace has zero non-empty records' }
$events = @($lines | ForEach-Object { $_ | ConvertFrom-Json })

$normal = @($events | Where-Object outcome -ceq 'normal')
$panic = @($events | Where-Object outcome -ceq 'panic')
$cancelled = @($events | Where-Object outcome -ceq 'cancelled')
$question = @($events | Where-Object methodFullName -like '*question_mark_return')
$asyncCompletion = @($events | Where-Object methodFullName -like '*async_completion')
$asyncCancellation = @($events | Where-Object methodFullName -like '*async_cancellation')
if ($events.Count -ne 16 -or $normal.Count -ne 14 -or $panic.Count -ne 1 -or $cancelled.Count -ne 1) {
    throw "Rust exit counts differ: total=$($events.Count) normal=$($normal.Count) panic=$($panic.Count) cancelled=$($cancelled.Count)"
}
if ($question.Count -ne 2 -or @($question | Where-Object outcome -cne 'normal').Count -ne 0) {
    throw "Rust ?-return exits differ: total=$($question.Count) nonNormal=$(@($question | Where-Object outcome -cne 'normal').Count)"
}
if ($asyncCompletion.Count -ne 1 -or $asyncCompletion[0].outcome -cne 'normal' -or $asyncCancellation.Count -ne 1 -or $asyncCancellation[0].outcome -cne 'cancelled') {
    throw 'Rust async completion/cancellation outcomes differ'
}
$asyncSynchronousReturns = @($asyncCancellation | Where-Object outcome -ceq 'normal').Count
if ($asyncSynchronousReturns -ne 0) {
    throw "Rust async cancellation emitted $asyncSynchronousReturns synchronous return event(s) from $($asyncCancellation.Count) cancellation event(s)"
}
$panicReturnFields = @($panic | Where-Object { $null -ne $_.PSObject.Properties['returnDigest'] }).Count
if ($panicReturnFields -ne 0) { throw "Rust panic records carried $panicReturnFields return digest field(s)" }

[pscustomobject]@{
    SourceFiles = $rewrite.sourceFiles
    RustFiles = $rewrite.rustFiles
    TraceRecords = $events.Count
    Normal = $normal.Count
    Panic = $panic.Count
    Cancelled = $cancelled.Count
    QuestionMarkNormal = $question.Count
    AsyncCompleted = $asyncCompletion.Count
    AsyncCancelled = $asyncCancellation.Count
    AsyncSynchronousReturns = $asyncSynchronousReturns
    PanicReturnFields = $panicReturnFields
    SourceHashChanges = 0
} | Format-List
Write-Host 'RUST_EXIT_HOOKS: PASS'
