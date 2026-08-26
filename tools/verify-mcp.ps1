<#
  Drives the RealDiff MCP server over its real stdio transport with raw JSON-RPC.
  No mocks: the run directory is populated with the artifacts a real engine run just produced
  (tools/verify-diff.ps1 -Mutate -Change config), and every response printed below came back
  through the same stdin/stdout pipe an MCP client would use.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$mcp = Join-Path $repo 'src/RealDiff.Mcp'
$proofId = [Guid]::NewGuid().ToString('N')
$runsRoot = Join-Path ([IO.Path]::GetTempPath()) "realdiff-mcp-runs-$proofId"
$source = Join-Path ([IO.Path]::GetTempPath()) "realdiff-mcp-source-$proofId"
$proofPrTree = Join-Path ([IO.Path]::GetTempPath()) "realdiff-mcp-pr-$proofId"

try {
    & (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change config `
        -WorkDirectory $source -PrTreeDirectory $proofPrTree
    if ($LASTEXITCODE -ne 0) { throw "could not generate isolated MCP artifacts: $LASTEXITCODE" }
    foreach ($f in 'findings.json', 'divergence-set.json') {
        if (-not (Test-Path (Join-Path $source $f))) { throw "missing generated $f in $source" }
    }
}
catch {
    Remove-Item $runsRoot, $source, $proofPrTree -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

try {
Remove-Item $runsRoot -Recurse -Force -ErrorAction SilentlyContinue
$runId = 'proof-sampleapp'
$runDir = Join-Path $runsRoot $runId
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
Copy-Item (Join-Path $source 'findings.json') $runDir
Copy-Item (Join-Path $source 'divergence-set.json') $runDir
@{
    runId = $runId; status = 'complete'; phase = 'done'; progress = 100
    repoPath = $repo; baseRef = 'HEAD'; prRef = 'mutated'; exitCode = 1
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $runDir 'status.json')

Write-Host '=== building the server ===' -ForegroundColor Cyan
dotnet build $mcp -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$exe = Get-ChildItem (Join-Path $mcp 'bin/Release') -Recurse -Filter 'RealDiff.Mcp.exe' | Select-Object -First 1
if (-not $exe) { throw 'server executable not found' }

$requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"verify-mcp","version":"1"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
    ('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_run_status","arguments":{"runId":"' + $runId + '"}}}')
    ('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_divergences","arguments":{"runId":"' + $runId + '","category":"unexpected"}}}')
    ('{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_divergence","arguments":{"runId":"' + $runId + '","memberName":"SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)"}}}')
    ('{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_call_path","arguments":{"runId":"' + $runId + '","memberName":"SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)"}}}')
    ('{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_untested_divergences","arguments":{"runId":"' + $runId + '"}}}')
    '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"get_run_status","arguments":{"runId":"no-such-run"}}}'
)

$env:REALDIFF_RUNS = $runsRoot
$stdinFile = Join-Path $runsRoot 'stdin.jsonl'
Set-Content -Path $stdinFile -Value ($requests -join "`n") -NoNewline -Encoding utf8

Write-Host '=== speaking JSON-RPC over stdio ===' -ForegroundColor Cyan
$psi = [System.Diagnostics.ProcessStartInfo]::new($exe.FullName)
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['REALDIFF_RUNS'] = $runsRoot
$p = [System.Diagnostics.Process]::Start($psi)

# stdin stays open: once the transport sees EOF it stops writing, so closing it up front
# discards every response.
$responses = @{}
foreach ($r in $requests) {
    $p.StandardInput.WriteLine($r)
    $p.StandardInput.Flush()
    if ($r -notmatch '"id"') { continue }
    $task = $p.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(20000)) { throw "timed out waiting for a response to: $r" }
    $line = $task.Result
    if ([string]::IsNullOrWhiteSpace($line)) { throw "empty response line for: $r" }
    $o = $line | ConvertFrom-Json
    if ($null -ne $o.id) { $responses[[int]$o.id] = $o }
}
$p.StandardInput.Close()
$p.WaitForExit(10000) | Out-Null
if (-not $p.HasExited) { $p.Kill() }

if ($responses.Count -eq 0) {
    Write-Host 'no JSON-RPC responses on stdout; stderr follows:' -ForegroundColor Red
    Write-Host $p.StandardError.ReadToEnd()
    throw 'MCP server returned no JSON-RPC responses'
}

$fail = 0
function Payload($id) {
    $r = $responses[$id]
    if (-not $r) { return $null }
    if ($r.error) { return $null }
    $txt = $r.result.content[0].text
    try { return ($txt | ConvertFrom-Json) } catch { Write-Host "  raw tool output: $txt" -ForegroundColor Red; return $null }
}

Write-Host ''
Write-Host '--- tools/list ---' -ForegroundColor Yellow
$names = $responses[2].result.tools | ForEach-Object { $_.name } | Sort-Object
$names | ForEach-Object { "  $_" }
$expected = 'get_call_path', 'get_divergence', 'get_run_status', 'get_untested_divergences', 'list_divergences', 'run_analysis'
if (Compare-Object $names $expected) { Write-Host '  FAIL: tool set mismatch' -ForegroundColor Red; $fail++ }
else { Write-Host "  PASS: all 6 tools advertised" -ForegroundColor Green }

Write-Host ''
Write-Host '--- list_divergences(unexpected) ---' -ForegroundColor Yellow
$list = Payload 4
$list | ConvertTo-Json -Depth 6
if ($list.total_members -ne 1 -or $list.total_call_sites -ne 2) { Write-Host '  FAIL: expected 1 member / 2 call sites' -ForegroundColor Red; $fail++ }
else { Write-Host '  PASS: 1 member across 2 call sites, matching the engine headline' -ForegroundColor Green }

Write-Host ''
Write-Host '--- get_divergence ---' -ForegroundColor Yellow
$det = Payload 5
$det | ConvertTo-Json -Depth 6
if ($det.observations.Count -lt 1) { Write-Host '  FAIL: no observations' -ForegroundColor Red; $fail++ }
else { Write-Host "  PASS: $($det.observations.Count) observation(s) with base/PR values" -ForegroundColor Green }

Write-Host ''
Write-Host '--- get_call_path ---' -ForegroundColor Yellow
$path = Payload 6
$path | ConvertTo-Json -Depth 6
if ($path.path.Count -lt 2) { Write-Host '  FAIL: path not a chain' -ForegroundColor Red; $fail++ }
else { Write-Host "  PASS: chain of $($path.path.Count) from the test root" -ForegroundColor Green }

Write-Host ''
Write-Host '--- get_untested_divergences ---' -ForegroundColor Yellow
$un = Payload 7
$un | ConvertTo-Json -Depth 6
if ($un.total_members -lt 1) { Write-Host '  FAIL: expected the untested member' -ForegroundColor Red; $fail++ }
else { Write-Host '  PASS' -ForegroundColor Green }

Write-Host ''
Write-Host '--- unknown run: must refuse, not return an empty list ---' -ForegroundColor Yellow
$bad = Payload 8
$bad | ConvertTo-Json -Depth 4
if ($bad.error -and $bad.is_clean_result -eq $false) { Write-Host '  PASS: refusal carries a reason and is_clean_result=false' -ForegroundColor Green }
else { Write-Host '  FAIL: an unknown run did not refuse' -ForegroundColor Red; $fail++ }

Write-Host ''
if ($fail -eq 0) {
    Write-Host 'verify-mcp: PASS' -ForegroundColor Green
    $proofExit = 0
}
else {
    Write-Host "verify-mcp: FAIL ($fail)" -ForegroundColor Red
    $proofExit = 1
}

}
finally {
    if ($null -ne $p -and -not $p.HasExited) {
        $p.Kill()
        $p.WaitForExit(10000) | Out-Null
    }
    if ($null -ne $p) { $p.Dispose() }
    Remove-Item Env:REALDIFF_RUNS -ErrorAction SilentlyContinue
    Remove-Item $runsRoot, $source, $proofPrTree -Recurse -Force -ErrorAction SilentlyContinue
}

exit $proofExit


