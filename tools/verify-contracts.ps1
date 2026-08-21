#requires -Version 7.0
<#
    Proof harness for the BehaviorDiff wire format.

    PowerShell acts as an independent producer: it hand-writes an NDJSON trace containing
    adversarial payloads, then the Engine reads it back through the real Contracts types.
    Output is validated by PowerShell's own JSON parser, not by ours.

    Run from anywhere:  pwsh -NoProfile -File tools/verify-contracts.ps1
#>
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$engine = Join-Path $repo 'src\BehaviorDiff.Engine'
$work = Join-Path ([System.IO.Path]::GetTempPath()) 'behaviordiff-verify'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work | Out-Null

$in = Join-Path $work 'in.ndjson'
$out1 = Join-Path $work 'out1.ndjson'
$out2 = Join-Path $work 'out2.ndjson'

$lines = @(
    # plain root call
    '{"testId":"Acme.Tests.Cart.AddsItem","methodFullName":"Acme.Cart.Add(System.String,System.Int32)","filePath":"C:\\src\\Acme\\Cart.cs","line":42,"callDepth":0,"callId":1,"ordinal":0,"argsDigest":"sha256:aa","returnDigest":"void","threadId":1}'
    # quotes, backslashes, embedded newline + tab, BMP escape (U+6F22), astral surrogate pair (U+1F680)
    '{"testId":"Acme.Tests.Cart.AddsItem","methodFullName":"Acme.Cart.Validate(System.String)","filePath":"C:\\src\\Acme\\Cart.cs","line":77,"callDepth":1,"callId":2,"ordinal":0,"parentCallId":1,"argsDigest":"a=\"x\\\"y\\\\z\"\nTAB:\there \u6f22 \ud83d\ude80","returnDigest":"true","threadId":1}'
    # explicit null parentCallId, exception path, optional fields absent
    '{"testId":"Acme.Tests.Cart.Throws","methodFullName":"Acme.Cart.Add(System.String,System.Int32)","line":42,"callDepth":0,"callId":3,"ordinal":0,"parentCallId":null,"exceptionType":"System.ArgumentNullException","threadId":4}'
    # unknown forward-compat field holding a nested object/array with a brace inside a string
    '{"testId":"Acme.Tests.Cart.Throws","methodFullName":"Acme.Cart.Log()","line":0,"callDepth":1,"callId":4,"ordinal":0,"parentCallId":3,"threadId":4,"futureField":{"nested":[1,2,{"deep":"}"}],"b":true}}'
    # blank line mid-stream
    ''
    '{"testId":"Acme.Tests.Cart.Throws","methodFullName":"Acme.Cart.Flush()","line":9,"callDepth":1,"callId":5,"ordinal":0,"parentCallId":3,"threadId":4}'
    # torn final line: the traced process died mid-write
    '{"testId":"Acme.Tests.Cart.Throws","methodFullName":"Acme.Cart.Tor'
)
[System.IO.File]::WriteAllText($in, ($lines -join "`n"), (New-Object System.Text.UTF8Encoding $false))

Write-Host ''
Write-Host '=== build ===' -ForegroundColor Cyan
dotnet build BehaviorDiff.sln -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Write-Host ''
Write-Host '=== 1. read: 5 events + 1 torn line, expect exit 1 ===' -ForegroundColor Cyan
dotnet run --project $engine -c Release --no-build -- read $in
Write-Host "exit code: $LASTEXITCODE"

Write-Host ''
Write-Host '=== 2. normalize: reader -> writer through the real types ===' -ForegroundColor Cyan
dotnet run --project $engine -c Release --no-build -- normalize $in -o $out1 --force
Write-Host "exit code: $LASTEXITCODE"

Write-Host ''
Write-Host "=== 3. output validated by PowerShell's JSON parser, not ours ===" -ForegroundColor Cyan
$count = 0
foreach ($line in (Get-Content $out1)) { $count++; $null = $line | ConvertFrom-Json }
Write-Host "$count line(s) accepted by ConvertFrom-Json, 0 failures"

Write-Host ''
Write-Host '=== 4. round-trip is byte-identical ===' -ForegroundColor Cyan
dotnet run --project $engine -c Release --no-build -- normalize $out1 -o $out2 --force | Out-Null
$h1 = (Get-FileHash $out1 -Algorithm SHA256).Hash
$h2 = (Get-FileHash $out2 -Algorithm SHA256).Hash
Write-Host "out1 = $h1"
Write-Host "out2 = $h2"
Write-Host "identical = $($h1 -eq $h2)"

Write-Host ''
Write-Host '=== 5. payload fidelity through the round trip ===' -ForegroundColor Cyan
$event2 = (Get-Content $out1)[1] | ConvertFrom-Json
Write-Host "argsDigest  = $($event2.argsDigest)"
Write-Host "code points = $((([int[]][char[]]$event2.argsDigest) -join ','))"

Write-Host ''
Write-Host '=== canonical output ===' -ForegroundColor Cyan
Get-Content $out1 -Raw
