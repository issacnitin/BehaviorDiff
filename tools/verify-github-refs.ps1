#requires -Version 7.0
<#
  Proves --ci=github against a real temporary git graph and an event payload. It also creates a
  depth-1 clone to prove the guard fails before worktree/build work with fetch-depth: 0 guidance.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-github-refs'
$shallow = Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-github-shallow'
$cli = Join-Path $repoRoot 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'

Remove-Item $fixture, $shallow -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $fixture | Out-Null
Push-Location $fixture
try {
    git init -q -b main
    git config user.email 'behaviordiff@example.invalid'
    git config user.name 'BehaviorDiff proof'
    Set-Content common.txt 'common'
    git add .
    git commit -q -m common
    $base = git rev-parse HEAD

    git switch -q -c feature
    Set-Content changed.txt 'changed'
    git add .
    git commit -q -m feature
    $head = git rev-parse HEAD

    $event = Join-Path $fixture 'event.json'
    @{
        number = 271
        pull_request = @{
            base = @{ sha = $base; repo = @{ full_name = 'example/fixture'; fork = $false } }
            head = @{ sha = $head; repo = @{ full_name = 'example/fixture'; fork = $false } }
        }
    } | ConvertTo-Json -Depth 8 | Set-Content $event

    dotnet build $cli -c Release --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

    $env:GITHUB_WORKSPACE = $fixture
    $env:GITHUB_EVENT_PATH = $event
    $env:GITHUB_REPOSITORY = 'example/fixture'
    $findings = Join-Path $fixture 'findings.json'

    Write-Host '=== immutable event SHAs ===' -ForegroundColor Cyan
    $output = dotnet run --project $cli -c Release --no-build -- --ci=github --findings $findings 2>&1
    $exit = $LASTEXITCODE
    $text = $output -join "`n"
    $output | Select-Object -First 16
    if ($exit -ne 3) { throw "expected early no-test-project refusal 3, got $exit" }
    if ($text -match '=== 3\. repo builds unmodified ===') { throw 'no-test refusal happened after build work began' }
    if ($text -notmatch [regex]::Escape("base       : github.event.pull_request.base.sha -> $base")) { throw 'base SHA did not come from event payload' }
    if ($text -notmatch [regex]::Escape("pr         : github.event.pull_request.head.sha -> $head")) { throw 'head SHA did not come from event payload' }
    if ($text -notmatch 'changed from merge base: 1') { throw 'wrong merge-base changed set' }
    Write-Host 'PASS: GitHub refs came from event payload SHAs, not branch names' -ForegroundColor Green
    Write-Host 'PASS: no-test repository refused with exit 3 before either build' -ForegroundColor Green

    Write-Host ''
    Write-Host '=== fork posting refusal ===' -ForegroundColor Cyan
    $forkEvent = Get-Content $event -Raw | ConvertFrom-Json
    $forkEvent.pull_request.head.repo.full_name = 'contributor/fixture'
    $forkEvent.pull_request.head.repo.fork = $true
    $forkEvent | ConvertTo-Json -Depth 8 | Set-Content $event
    $env:GITHUB_API_URL = 'http://127.0.0.1:1'
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    $forkOutput = dotnet run --project $cli -c Release --no-build -- post --provider=github --findings $findings 2>&1
    $forkExit = $LASTEXITCODE
    $forkText = $forkOutput -join "`n"
    $forkOutput | ForEach-Object { Write-Host $_ }
    if ($forkExit -ne 4 -or $forkText -notmatch 'FORK PULL REQUEST' -or $forkText -notmatch 'read-only GITHUB_TOKEN') {
        throw 'fork did not refuse before token lookup/network access'
    }
    Write-Host 'PASS: fork refused before token lookup or network access' -ForegroundColor Green

    # Restore the same-repository event before copying it into the shallow checkout.
    $forkEvent.pull_request.head.repo.full_name = 'example/fixture'
    $forkEvent.pull_request.head.repo.fork = $false
    $forkEvent | ConvertTo-Json -Depth 8 | Set-Content $event

    Write-Host ''
    Write-Host '=== shallow checkout refusal ===' -ForegroundColor Cyan
    Pop-Location
    $uri = 'file:///' + ($fixture.Replace('\', '/'))
    git clone -q --depth 1 --branch feature $uri $shallow
    Push-Location $shallow
    $shallowEvent = Join-Path $shallow 'event.json'
    Copy-Item $event $shallowEvent
    $env:GITHUB_WORKSPACE = $shallow
    $env:GITHUB_EVENT_PATH = $shallowEvent
    $shallowFindings = Join-Path $shallow 'findings.json'
    $shallowOutput = dotnet run --project $cli -c Release --no-build -- --ci=github --findings $shallowFindings 2>&1
    $shallowExit = $LASTEXITCODE
    $shallowText = $shallowOutput -join "`n"
    $shallowOutput | Select-Object -Last 6
    if ($shallowExit -ne 3) { throw "shallow clone expected exit 3, got $shallowExit" }
    if ($shallowText -notmatch 'SHALLOW CLONE' -or $shallowText -notmatch 'fetch-depth: 0') { throw 'shallow refusal did not explain actions/checkout fix' }
    $document = Get-Content $shallowFindings -Raw | ConvertFrom-Json
    if ($document.status -ne 'refused' -or $document.isCleanResult -ne $false) { throw 'shallow refusal did not produce invalid findings' }
    Write-Host 'PASS: depth-1 checkout refused before build with fetch-depth: 0 guidance' -ForegroundColor Green
}
finally {
    Pop-Location
    Remove-Item $fixture, $shallow -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:GITHUB_WORKSPACE, Env:GITHUB_EVENT_PATH, Env:GITHUB_REPOSITORY, Env:GITHUB_API_URL, Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'verify-github-refs: PASS' -ForegroundColor Green
