#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-rust-rewriter-cache-gate')
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
$manifest = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/Cargo.toml'
$binary = Join-Path $repo 'src/BehaviorDiff.Rust.Tracer/target/release/behaviordiff-rust-rewrite.exe'
if (-not $IsWindows) {
    $binary = $binary.Substring(0, $binary.Length - 4)
}

function Get-SourceHashes([string]$Root) {
    $files = @(Get-ChildItem $Root -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/]target[\\/]' -and $_.FullName -notmatch '[\\/]\.git[\\/]'
    } | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Rust source hash input is empty: $Root"
    }

    return @($files | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
            Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
    })
}

function Assert-HashSetsEqual([object[]]$Expected, [object[]]$Actual, [string]$Label) {
    $expectedText = $Expected | ConvertTo-Json -Compress
    $actualText = $Actual | ConvertTo-Json -Compress
    if ($expectedText -cne $actualText) {
        throw "Rust source hashes changed after $Label"
    }
}

if (Test-Path $work) {
    Remove-Item $work -Recurse -Force
}
New-Item $work -ItemType Directory -Force | Out-Null

$before = @(Get-SourceHashes $source)
& cargo build --release --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw "Rust rewriter build failed: $LASTEXITCODE" }

$first = (& $binary --source $source --cache-root $work | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw "Rust rewrite miss failed: $LASTEXITCODE" }
$afterMiss = @(Get-SourceHashes $source)
Assert-HashSetsEqual $before $afterMiss 'cache miss'

$second = (& $binary --source $source --cache-root $work | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw "Rust rewrite hit failed: $LASTEXITCODE" }
$afterHit = @(Get-SourceHashes $source)
Assert-HashSetsEqual $before $afterHit 'cache hit'

if ($first.cacheStatus -cne 'miss' -or $second.cacheStatus -cne 'hit') {
    throw "Unexpected cache statuses: $($first.cacheStatus)/$($second.cacheStatus)"
}
if ($first.cacheKey -cne $second.cacheKey -or $first.sourceHash -cne $second.sourceHash) {
    throw 'Rust cache hit did not address the same source content'
}
if ($first.sourceFiles -le 0 -or $first.rustFiles -le 0) {
    throw "Rust rewrite reported empty inputs: source=$($first.sourceFiles) rust=$($first.rustFiles)"
}

$origin = Get-Content (Join-Path $first.output '.behaviordiff-rust-origin.json') -Raw | ConvertFrom-Json
if (@($origin.rustFiles).Count -ne $first.rustFiles -or @($origin.rustFiles).Count -le 0) {
    throw "Rust origin manifest count mismatch: report=$($first.rustFiles) manifest=$(@($origin.rustFiles).Count)"
}
if ($origin.generatedReaders -le 0) {
    throw "Rust rewrite generated no local type readers from $($first.rustFiles) non-empty Rust input file(s)"
}

& cargo check --manifest-path (Join-Path $first.output 'Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw "Rewritten Rust project failed cargo check: $LASTEXITCODE" }

[pscustomobject]@{
    SourceFiles = $first.sourceFiles
    RustFiles = $first.rustFiles
    SourceHash = $first.sourceHash
    CacheKey = $first.cacheKey
    Miss = $first.cacheStatus
    Hit = $second.cacheStatus
    SourceHashChanges = 0
    GeneratedReaders = $origin.generatedReaders
    Output = $first.output
} | Format-List
Write-Host 'RUST_REWRITER_CACHE: PASS'