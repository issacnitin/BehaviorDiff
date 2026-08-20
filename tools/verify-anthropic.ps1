#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
$work = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-anthropic-$runId"
$prTree = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-anthropic-pr-$runId"

try {
    & (Join-Path $PSScriptRoot 'verify-coverage.ps1') -WorkDirectory $work -PrTreeDirectory $prTree
    if ($LASTEXITCODE -ne 0) { throw "verify-coverage failed: $LASTEXITCODE" }

    $findings = Join-Path $work 'coverage-findings.json'
    & dotnet run --project (Join-Path $PSScriptRoot 'AnthropicProof/BehaviorDiff.AnthropicProof.csproj') `
        -c Release -- $findings
    if ($LASTEXITCODE -ne 0) { throw "Anthropic proof failed: $LASTEXITCODE" }
}
finally {
    Remove-Item $work, $prTree -Recurse -Force -ErrorAction SilentlyContinue
}
