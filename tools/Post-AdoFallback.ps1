#requires -Version 7.0
<#
  Emergency summary-only poster used when realdiff.dll itself is unavailable.
  Normal posting belongs to `realdiff post`; this script exists solely to preserve the invariant
  that a broken tool build still leaves a visible non-verdict on the pull request.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Findings
)

$ErrorActionPreference = 'Stop'
foreach ($name in 'SYSTEM_ACCESSTOKEN', 'SYSTEM_COLLECTIONURI', 'SYSTEM_TEAMPROJECT', 'BUILD_REPOSITORY_ID', 'SYSTEM_PULLREQUEST_PULLREQUESTID') {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "fallback posting requires $name"
    }
}
if (-not (Test-Path $Findings)) { throw "findings file does not exist: $Findings" }

$document = Get-Content $Findings -Raw | ConvertFrom-Json
$reason = if ($document.refusal.reason) { $document.refusal.reason } else { 'No reason was recorded.' }
$prId = $env:SYSTEM_PULLREQUEST_PULLREQUESTID
$marker = "<!-- realdiff:pr:${prId}:summary -->"
$content = @"
## RealDiff: analysis could not complete

**No safety verdict was produced.** This is not a clean result.

> $($reason -replace "`n", "`n> ")

$marker
"@

$project = [Uri]::EscapeDataString($env:SYSTEM_TEAMPROJECT)
$repository = [Uri]::EscapeDataString($env:BUILD_REPOSITORY_ID)
$root = $env:SYSTEM_COLLECTIONURI.TrimEnd('/') + "/$project/_apis/git/repositories/$repository/pullRequests/$prId"
$headers = @{ Authorization = "Bearer $env:SYSTEM_ACCESSTOKEN" }
$threads = Invoke-RestMethod -Method Get -Uri "$root/threads?api-version=7.1" -Headers $headers
$existing = foreach ($thread in $threads.value) {
    foreach ($comment in $thread.comments) {
        if (-not $comment.isDeleted -and $comment.content.Contains($marker, [StringComparison]::Ordinal)) {
            [pscustomobject]@{ ThreadId = $thread.id; CommentId = $comment.id }
        }
    }
}

$body = @{ content = $content } | ConvertTo-Json
if ($existing) {
    $match = @($existing)[0]
    Invoke-RestMethod -Method Patch `
        -Uri "$root/threads/$($match.ThreadId)/comments/$($match.CommentId)?api-version=7.1" `
        -Headers $headers -ContentType 'application/json' -Body $body | Out-Null
    Write-Host "updated fallback Azure DevOps PR comment $($match.ThreadId)/$($match.CommentId)"
}
else {
    $thread = @{
        comments = @(@{ parentCommentId = 0; content = $content; commentType = 1 })
        status = 1
    } | ConvertTo-Json -Depth 5
    $created = Invoke-RestMethod -Method Post -Uri "$root/threads?api-version=7.1" `
        -Headers $headers -ContentType 'application/json' -Body $thread
    Write-Host "created fallback Azure DevOps PR thread $($created.id)"
}

exit 0
