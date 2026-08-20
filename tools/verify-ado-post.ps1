#requires -Version 7.0
<#
  Local transport proof for `behaviordiff post`. A TCP listener implements the small Azure DevOps
  REST surface the provider calls and records every request body. This validates HTTP method/route,
  idempotent update behavior, summary ordering, file thread position, refusals, and gate exits.

  It cannot prove Azure DevOps authorization or that the live service accepts/tracks an UNEXPECTED
  file anchor; that requires a real organization and PR.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$runId = [Guid]::NewGuid().ToString('N')
$proofWork = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-ado-$runId"
$proofPrTree = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-ado-pr-$runId"
$findings = Join-Path $proofWork 'findings.json'
$recording = Join-Path $proofWork 'ado-mock.ndjson'
$refusal = Join-Path $proofWork 'ado-refusal.json'
$clean = Join-Path $proofWork 'ado-clean.json'
$ready = Join-Path $proofWork 'ado-mock.ready'
$port = 0

try {
    & (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change config `
        -WorkDirectory $proofWork -PrTreeDirectory $proofPrTree
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $findings)) {
        throw "could not generate isolated SampleApp findings: $LASTEXITCODE"
    }
}
catch {
    Remove-Item $proofWork, $proofPrTree -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

try {
Remove-Item $recording, $refusal, $clean, $ready -Force -ErrorAction SilentlyContinue
@{
    schema = 'behaviordiff.findings/1'; status = 'refused'; verdict = 'could_not_analyze'
    isCleanResult = $false; exitCode = 3; exitReason = 'analysis_refused'
    refs = @{ baseSha = 'base'; prSha = 'pr'; mergeBaseSha = 'merge-base' }
    refusal = @{ reason = 'CALL TREE: parent links were incomplete; no safety verdict was produced.' }
} | ConvertTo-Json -Depth 5 | Set-Content $refusal

$cleanDocument = Get-Content $findings -Raw | ConvertFrom-Json
$cleanDocument.verdict = 'clean'
$cleanDocument.isCleanResult = $true
$cleanDocument.exitCode = 0
$cleanDocument.exitReason = 'analyzed_no_unexpected'
$cleanDocument.summary.unexpectedMembers = 0
$cleanDocument.summary.unexpectedCallSites = 0
$cleanDocument.members = @($cleanDocument.members | Where-Object attribution -ne 'unexpected')
$cleanDocument | ConvertTo-Json -Depth 20 | Set-Content $clean

$server = Start-Job -ArgumentList $port, $recording, $ready -ScriptBlock {
    param($port, $recording, $ready)
    $ErrorActionPreference = 'Stop'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
    $listener.Start()
    Set-Content $ready ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $threads = @()
    $nextThread = 100

    function Send-Response($stream, $status, $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $head = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
        $stream.Write($head, 0, $head.Length)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }

    try {
        while ($true) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
                $requestLine = $reader.ReadLine()
                if ([string]::IsNullOrWhiteSpace($requestLine)) { continue }
                $parts = $requestLine.Split(' ')
                $method = $parts[0]
                $path = $parts[1]
                $length = 0
                while ($true) {
                    $line = $reader.ReadLine()
                    if ([string]::IsNullOrEmpty($line)) { break }
                    if ($line.StartsWith('Content-Length:', [StringComparison]::OrdinalIgnoreCase)) {
                        $length = [int]$line.Substring($line.IndexOf(':') + 1).Trim()
                    }
                }

                $body = ''
                if ($length -gt 0) {
                    $chars = [char[]]::new($length)
                    $read = 0
                    while ($read -lt $length) { $read += $reader.Read($chars, $read, $length - $read) }
                    $body = -join $chars
                }

                @{ method = $method; path = $path; body = $body } | ConvertTo-Json -Compress | Add-Content $recording

                if ($method -eq 'GET') {
                    @{ count = $threads.Count; value = $threads } | ConvertTo-Json -Depth 8 -Compress | ForEach-Object { Send-Response $stream '200 OK' $_ }
                }
                elseif ($method -eq 'POST') {
                    $payload = $body | ConvertFrom-Json
                    $thread = @{
                        id = $nextThread
                        comments = @(@{ id = 1; content = $payload.comments[0].content; isDeleted = $false })
                        threadContext = $payload.threadContext
                        status = 'active'
                    }
                    $threads += $thread
                    $nextThread++
                    $thread | ConvertTo-Json -Depth 8 -Compress | ForEach-Object { Send-Response $stream '200 OK' $_ }
                }
                elseif ($method -eq 'PATCH') {
                    if ($path -notmatch '/threads/(\d+)/comments/(\d+)') { throw "unexpected PATCH $path" }
                    $threadId = [int]$Matches[1]
                    $commentId = [int]$Matches[2]
                    $payload = $body | ConvertFrom-Json
                    $thread = $threads | Where-Object { $_.id -eq $threadId } | Select-Object -First 1
                    $comment = $thread.comments | Where-Object { $_.id -eq $commentId } | Select-Object -First 1
                    $comment.content = $payload.content
                    $comment | ConvertTo-Json -Depth 5 -Compress | ForEach-Object { Send-Response $stream '200 OK' $_ }
                }
                else {
                    Send-Response $stream '405 Method Not Allowed' '{}'
                }
            }
            finally { $client.Dispose() }
        }
    }
    finally { $listener.Stop() }
}

try {
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path $ready)) {
        if ([DateTime]::UtcNow -gt $readyDeadline) { throw 'local ADO mock did not become ready' }
    }
    $port = [int](Get-Content $ready -Raw)

    dotnet build $cli -c Release --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

    $env:SYSTEM_ACCESSTOKEN = 'mock-token-not-a-secret'
    $env:SYSTEM_COLLECTIONURI = "http://127.0.0.1:$port/"
    $env:SYSTEM_TEAMPROJECT = 'Example Project'
    $env:BUILD_REPOSITORY_ID = '00000000-0000-0000-0000-000000000314'
    $env:SYSTEM_PULLREQUEST_PULLREQUESTID = '314'

    Write-Host '=== first post creates summary and anchored member ===' -ForegroundColor Cyan
    dotnet run --project $cli -c Release --no-build -- post --provider=azuredevops --findings $findings
    if ($LASTEXITCODE -ne 0) { throw "warn-only first post returned $LASTEXITCODE" }

    Write-Host '=== second post updates, never appends ===' -ForegroundColor Cyan
    dotnet run --project $cli -c Release --no-build -- post --provider=azuredevops --findings $findings
    if ($LASTEXITCODE -ne 0) { throw "warn-only second post returned $LASTEXITCODE" }

    Write-Host '=== refusal updates summary and still posts ===' -ForegroundColor Cyan
    dotnet run --project $cli -c Release --no-build -- post --provider=azuredevops --findings $refusal
    if ($LASTEXITCODE -ne 0) { throw "refusal post returned $LASTEXITCODE" }

    Write-Host '=== fail-on-findings is opt-in ===' -ForegroundColor Cyan
    dotnet run --project $cli -c Release --no-build -- post --provider=azuredevops --findings $findings --gate fail-on-findings
    if ($LASTEXITCODE -ne 1) { throw "fail-on-findings expected 1, got $LASTEXITCODE" }

    Write-Host '=== script-only fallback updates the same summary ===' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Post-AdoFallback.ps1') -Findings $refusal
    if ($LASTEXITCODE -ne 0) { throw "fallback poster returned $LASTEXITCODE" }

    Write-Host '=== clean result remains coverage-qualified ===' -ForegroundColor Cyan
    dotnet run --project $cli -c Release --no-build -- post --provider=azuredevops --findings $clean
    if ($LASTEXITCODE -ne 0) { throw "clean post returned $LASTEXITCODE" }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $requests = if (Test-Path $recording) { @(Get-Content $recording | ConvertFrom-Json) } else { @() }
    } while ($requests.Count -lt 15 -and [DateTime]::UtcNow -lt $deadline)

    $posts = @($requests | Where-Object method -eq 'POST')
    $patches = @($requests | Where-Object method -eq 'PATCH')
    $gets = @($requests | Where-Object method -eq 'GET')
    if ($posts.Count -ne 2) { throw "idempotency failed: expected exactly 2 creates total, got $($posts.Count)" }
    if ($patches.Count -ne 7) { throw "expected 7 updates across re-push/refusal/gate/fallback/clean, got $($patches.Count)" }
    if ($gets.Count -ne 6) { throw "expected one list call per invocation, got $($gets.Count)" }

    $summary = $posts | Where-Object { $_.body -match 'behaviordiff:pr:314:summary' } | Select-Object -First 1
    $member = $posts | Where-Object { $_.body -match 'behaviordiff:pr:314:member:' } | Select-Object -First 1
    if (-not $summary -or -not $member) { throw 'summary/member marker missing from create payloads' }
    if ($summary.body.IndexOf('Edited-code coverage') -gt $summary.body.IndexOf('UNASSERTED')) { throw 'coverage did not precede gap count' }
    if ($summary.body.IndexOf('UNASSERTED') -gt $summary.body.IndexOf('EXPECTED')) { throw 'summary did not put unasserted gaps first' }
    $memberBody = $member.body | ConvertFrom-Json
    if ($memberBody.threadContext.filePath -ne '/samples/SampleApp/ShippingCalculator.cs') { throw 'wrong anchored file path' }
    if ($memberBody.threadContext.rightFileStart.line -ne 10) { throw 'wrong one-based source line' }
    if ($memberBody.threadContext.rightFileStart.offset -ne 1) { throw 'wrong source offset' }

    $refusalPatch = $patches | Where-Object { $_.body -match 'analysis could not complete' } | Select-Object -First 1
    if (-not $refusalPatch -or $refusalPatch.body -notmatch 'no safety verdict') { throw 'refusal did not overwrite summary with an explicit non-verdict' }

    $cleanPatch = $patches | Where-Object { $_.body -match 'No unexpected behavior changes across 1 edited files \(1 member, 5 call sites observed\)' } | Select-Object -First 1
    if (-not $cleanPatch) { throw 'clean summary omitted coverage-qualified wording' }
    if ($cleanPatch.body -match '\bNo findings\b') { throw 'clean summary used an unqualified all-clear' }

    Write-Host 'PASS: first run POSTed one summary and one line-anchored member thread' -ForegroundColor Green
    Write-Host 'PASS: re-push PATCHed existing comments; total POST count stayed at 2' -ForegroundColor Green
    Write-Host 'PASS: refusal PATCHed the summary with the reason; never posted silence' -ForegroundColor Green
    Write-Host 'PASS: warn-only returned 0; fail-on-findings returned 1' -ForegroundColor Green
    Write-Host 'PASS: script-only fallback PATCHed the existing summary; no duplicate POST' -ForegroundColor Green
    Write-Host 'PASS: coverage precedes findings and clean wording remains coverage-qualified' -ForegroundColor Green
}
finally {
    Stop-Job $server -ErrorAction SilentlyContinue
    Remove-Job $server -Force -ErrorAction SilentlyContinue
    Remove-Item Env:SYSTEM_ACCESSTOKEN, Env:SYSTEM_COLLECTIONURI, Env:SYSTEM_TEAMPROJECT, Env:BUILD_REPOSITORY_ID, Env:SYSTEM_PULLREQUEST_PULLREQUESTID -ErrorAction SilentlyContinue
    Remove-Item $proofWork, $proofPrTree -Recurse -Force -ErrorAction SilentlyContinue
}
}
catch {
    if ($null -ne $server) {
        Stop-Job $server -ErrorAction SilentlyContinue
        Remove-Job $server -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $proofWork, $proofPrTree -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host ''
Write-Host 'verify-ado-post: PASS (local mock; live Azure DevOps remains unverified)' -ForegroundColor Green