#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'realdiff-rust-sort-demo-gate'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repo 'samples/RustSortDemo'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$repository = Join-Path $work 'repository'
$analysis = Join-Path $work 'analysis'
$findingsPath = Join-Path $analysis 'findings.json'
$commentPath = Join-Path $analysis 'comment.md'
$preview = Join-Path $repo 'tools/CommentPreview/RealDiff.CommentPreview.csproj'
$tracer = Join-Path $repo 'src/RealDiff.Rust.Tracer/target/release/realdiff-rust-rewrite.exe'
if (-not $IsWindows) { $tracer = $tracer.Substring(0, $tracer.Length - 4) }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item $repository -ItemType Directory -Force | Out-Null
Get-ChildItem $fixture -Force | Where-Object Name -ne 'target' | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $repository $_.Name) -Recurse -Force
}
& git -C $repository init --initial-branch=main --quiet
& git -C $repository config user.email realdiff-proof@example.invalid
& git -C $repository config user.name 'RealDiff Proof'
& git -C $repository add .
& git -C $repository commit --quiet -m base
$base = (& git -C $repository rev-parse HEAD).Trim()
$changedFile = 'src/config.rs'
$config = Join-Path $repository $changedFile
$text = Get-Content $config -Raw
$mutated = $text.Replace('PRIORITY_BIAS: i32 = 0', 'PRIORITY_BIAS: i32 = 5')
if ($mutated -ceq $text) { throw 'Rust demo mutation did not match config.rs' }
[IO.File]::WriteAllText($config, $mutated)
& git -C $repository add $changedFile
& git -C $repository commit --quiet -m pr
$pr = (& git -C $repository rev-parse HEAD).Trim()
$changed = @(& git -C $repository diff --name-only "$base..$pr")
if ($changed.Count -ne 1 -or $changed[0] -cne $changedFile) {
    throw "Rust demo changed-file input differs: count=$($changed.Count) files=$($changed -join ',')"
}

& cargo build --release --locked --manifest-path (Join-Path $repo 'src/RealDiff.Rust.Tracer/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw "Rust tracer build failed: $LASTEXITCODE" }
$env:REALDIFF_RUST_TRACER = $tracer
try {
    $output = @(& dotnet run --project (Join-Path $repo 'src/RealDiff.Cli') -c Release --no-build -- `
        $repository --base $base --pr $pr --work $analysis --findings $findingsPath `
        --no-baseline --strict --keep --keep-traces 1d 2>&1)
    $exitCode = $LASTEXITCODE
} finally {
    Remove-Item Env:REALDIFF_RUST_TRACER -ErrorAction SilentlyContinue
}
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 1) { throw "Rust demo CLI exited $exitCode instead of findings exit 1" }

$findings = Get-Content $findingsPath -Raw | ConvertFrom-Json -Depth 100
$frontier = Get-Content (Join-Path $analysis 'frontier-report.json') -Raw | ConvertFrom-Json -Depth 100
$divergence = Get-Content (Join-Path $analysis 'divergence-set.json') -Raw | ConvertFrom-Json -Depth 100
$baseEvents = @(Get-Content (Join-Path $analysis 'base_run1/run.rust.ndjson') | Where-Object { $_.Trim().Length -gt 0 })
if ($baseEvents.Count -le 0 -or [int]$divergence.counts.matchedKeys -lt 100) {
    throw "Rust demo comparison input is empty or below floor: events=$($baseEvents.Count) matched=$($divergence.counts.matchedKeys)"
}
$coverage = $frontier.changedFileCoverage.files | Where-Object filePath -ceq $changedFile
$member = @($findings.members)
$collapse = [double]$frontier.counts.divergedKeys / [double]$frontier.counts.frontierNodes
if ($findings.status -cne 'analyzed' -or $findings.verdict -cne 'findings' -or
    $findings.summary.editedFiles -ne 1 -or $findings.summary.exercisedEditedFiles -ne 0 -or
    $findings.summary.tracedMembers -ne 0 -or $findings.summary.untestedMembers -ne 1 -or
    $coverage.tracedMembers -ne 0 -or $member.Count -ne 1 -or
    $member[0].filePath -ceq $changedFile -or $member[0].untestedCallSiteCount -le 0 -or
    $collapse -le 1) {
    throw "Rust demo shape differs: status=$($findings.status) edited=$($findings.summary.editedFiles) exercised=$($findings.summary.exercisedEditedFiles) traced=$($findings.summary.tracedMembers) untested=$($findings.summary.untestedMembers) members=$($member.Count) collapse=$collapse"
}

$comment = @(& dotnet run --project $preview -c Release -- $findingsPath 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Rust demo comment rendering failed: $LASTEXITCODE" }
$commentText = $comment -join "`n"
$commentText | Set-Content $commentPath
if ($commentText -notmatch 'RealDiff:' -or
    $commentText -notmatch [regex]::Escape($member[0].memberName) -or
    $commentText -notmatch 'did not assert|none asserted|unasserted') {
    throw 'Rust demo rendered comment omitted heading, headline, or untested explanation'
}

[pscustomobject]@{
    ChangedFiles = $changed.Count
    BaseEvents = $baseEvents.Count
    MatchedKeys = [int]$divergence.counts.matchedKeys
    RemainingDivergences = [int]$divergence.counts.remainingDivergences
    DivergedKeys = [int]$frontier.counts.divergedKeys
    FrontierNodes = [int]$frontier.counts.frontierNodes
    Collapse = [Math]::Round($collapse, 2)
    EditedTracedMembers = [int]$coverage.tracedMembers
    Headline = $member[0].memberName
    HeadlineFile = $member[0].filePath
    UntestedMembers = [int]$findings.summary.untestedMembers
    Evidence = [int]$member[0].evidenceTotalCount
    CommentBytes = [Text.Encoding]::UTF8.GetByteCount($commentText)
    Comment = $commentPath
} | Format-List
Write-Host 'RUST_SORT_DEMO: PASS'
