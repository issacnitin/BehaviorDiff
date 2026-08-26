#requires -Version 7.0
<#
  Local proof for the CI-specific seams. It does not claim to run an Azure-hosted agent:
  - verify-ci-refs creates a real merge graph and mocks ADO predefined variables;
  - verify-ado-post sends real HTTP requests to a local service and checks every REST payload;
  - this wrapper checks the YAML preserves full history, always posts, defaults to warn, and cleans up.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pipeline = Join-Path $repo 'azure-pipelines.yml'
$yaml = Get-Content $pipeline -Raw
$githubWorkflow = Get-Content (Join-Path $repo '.github/workflows/blastradius.yml') -Raw

$required = @{
    'no duplicate push trigger' = 'trigger: none'
    'Azure Repos branch-policy trigger declaration' = 'Build validation policy'
    'full history for merge-base resolution' = 'fetchDepth: 0'
    'Azure DevOps ref mode' = '--ci=azuredevops'
    'canonical findings output' = '--findings'
    'posting command' = 'post --provider=azuredevops'
    'explicit OAuth token mapping' = 'SYSTEM_ACCESSTOKEN: $(System.AccessToken)'
    'warn-only default' = 'behaviorDiffGate: warn-only'
    'repository-owned namespace exclusions' = 'behaviorDiffExcludeNamespaces: SampleApp.Diagnostics,SampleApp.Persistence,Infrastructure.Collections'
    'always-run cleanup/posting' = 'condition: always()'
    'trace cleanup' = 'Remove-Item $work -Recurse -Force'
    'hosted measurement output' = 'HOSTED MEASUREMENT'
    'fallback non-verdict artifact' = 'pipeline_failed_before_analysis_artifact'
    'fallback summary poster' = 'Post-AdoFallback.ps1'
}

foreach ($entry in $required.GetEnumerator()) {
    if (-not $yaml.Contains($entry.Value, [StringComparison]::Ordinal)) {
        throw "pipeline is missing $($entry.Key): $($entry.Value)"
    }
}

if (-not $githubWorkflow.Contains(
    'if: always() && github.event.pull_request.head.repo.fork == false',
    [StringComparison]::Ordinal)) {
    throw 'GitHub posting is not guarded against fork pull requests'
}

$program = Get-Content (Join-Path $repo 'src/RealDiff.Cli/Program.cs') -Raw
if ((-not $program.Contains('RunTests("base_run3"', [StringComparison]::Ordinal)) -or
    (-not $program.Contains('Base3 = base3', [StringComparison]::Ordinal))) {
    throw 'the generic CLI is not supplying the third base run to the engine'
}
if (-not $program.Contains('arguments.Add("--exclude")', [StringComparison]::Ordinal)) {
    throw 'the generic CLI is not forwarding repository-owned namespace exclusions to the weaver'
}

Write-Host 'PASS: pipeline has full-history refs, canonical output, always-post, warn default, measurements, and cleanup' -ForegroundColor Green
Write-Host 'PASS: generic CLI runs three base samples plus one PR sample' -ForegroundColor Green

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-ci-refs.ps1')
if ($LASTEXITCODE -ne 0) { throw "verify-ci-refs failed: $LASTEXITCODE" }

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-ado-post.ps1')
if ($LASTEXITCODE -ne 0) { throw "verify-ado-post failed: $LASTEXITCODE" }

Write-Host ''
Write-Host 'verify-pipeline: PASS (ADO variables and REST mocked; hosted agent and live REST unverified)' -ForegroundColor Green
