<#
  Proves --ci=azuredevops against a real temporary git graph:

      common --- target-only --- merge (BUILD_SOURCEVERSION)
             \              /
              --- two PR files

  A direct target..source diff contains all three files. The merge-base diff contains only the two
  PR files. No test project is needed: ref resolution and its guard deliberately run before scanning.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) 'behaviordiff-ci-refs'
$cli = Join-Path $repoRoot 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'

Remove-Item $fixture -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $fixture | Out-Null
Push-Location $fixture
try {
    git init -q -b target
    git config user.email 'behaviordiff@example.invalid'
    git config user.name 'BehaviorDiff proof'

    Set-Content common.txt 'common'
    git add .
    git commit -q -m common
    $common = git rev-parse HEAD

    git switch -q -c source
    Set-Content pr-one.txt 'one'
    Set-Content pr-two.txt 'two'
    git add .
    git commit -q -m 'PR changes'
    $source = git rev-parse HEAD

    git switch -q target
    Set-Content target-only.txt 'unrelated target change after the branch point'
    git add .
    git commit -q -m 'target advanced'
    $target = git rev-parse HEAD
    git switch -q -c ado-merge
    git merge -q --no-ff source -m 'synthetic ADO merge'
    $merge = git rev-parse HEAD

    $direct = @(git diff --name-only $target $source)
    $fromMergeBase = @(git diff --name-only $common $source)
    if ($direct.Count -ne 3) { throw "fixture is wrong: direct diff expected 3 files, got $($direct.Count)" }
    if ($fromMergeBase.Count -ne 2) { throw "fixture is wrong: merge-base diff expected 2 files, got $($fromMergeBase.Count)" }

    dotnet build $cli -c Release --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

    Write-Host '=== explicit refs remain the default ===' -ForegroundColor Cyan
    $failedFindings = Join-Path $fixture 'failed-findings.json'
    $explicitOutput = dotnet run --project $cli -c Release --no-build -- $fixture --base refs/heads/target --pr refs/heads/source --findings $failedFindings 2>&1
    $explicitExit = $LASTEXITCODE
    $explicitText = $explicitOutput -join "`n"
    $explicitOutput | Select-Object -First 9
    if ($explicitExit -ne 3) { throw "explicit path expected early no-test-project refusal (3), got $explicitExit" }
    if ($explicitText -notmatch 'changed from merge base: 2') { throw 'explicit path did not use the merge base' }
    if ($explicitText -match 'CI provider') { throw 'explicit path unexpectedly entered CI resolution' }
    if ($explicitText -match '=== 3\. repo builds unmodified ===') { throw 'no-test refusal happened after build work began' }
    $failedDocument = Get-Content $failedFindings -Raw | ConvertFrom-Json
    if ($failedDocument.status -ne 'refused' -or $failedDocument.isCleanResult -ne $false) { throw 'no-test findings arm is not structurally refused' }
    if ($null -ne $failedDocument.members) { throw 'refused findings must omit members rather than serialize an empty array' }
    Write-Host 'PASS: explicit <repo> --base <ref> --pr <ref> remains the default' -ForegroundColor Green
    Write-Host 'PASS: no-test repository refused with exit 3 before either build' -ForegroundColor Green

    Write-Host ''

    $env:SYSTEM_PULLREQUEST_SOURCEBRANCH = 'refs/heads/source'
    $env:SYSTEM_PULLREQUEST_TARGETBRANCH = 'refs/heads/target'
    $env:SYSTEM_PULLREQUEST_PULLREQUESTID = '314'
    $env:BUILD_SOURCEVERSION = $merge
    $env:BUILD_SOURCEBRANCH = 'refs/pull/314/merge'
    $env:BUILD_REPOSITORY_LOCALPATH = $fixture
    $env:BUILD_REPOSITORY_ID = '00000000-0000-0000-0000-000000000314'
    $env:BUILD_REPOSITORY_NAME = 'BehaviorDiff.RefFixture'
    $env:BUILD_REPOSITORY_PROVIDER = 'TfsGit'
    $env:BUILD_REPOSITORY_URI = 'https://dev.azure.com/example/project/_git/BehaviorDiff.RefFixture'
    Remove-Item Env:BEHAVIORDIFF_MAX_CHANGED_FILES -ErrorAction SilentlyContinue

    Write-Host '=== merge-base resolution ===' -ForegroundColor Cyan
    $output = dotnet run --project $cli -c Release --no-build -- --ci=azuredevops 2>&1
    $exit = $LASTEXITCODE
    $text = $output -join "`n"
    $output | Select-Object -First 20

    # Exit 3 is expected because this deliberately tiny git fixture has no xunit project. Ref resolution
    # has already completed and printed its measured changed-file set before the scan refuses.
    if ($exit -ne 3) { throw "expected the early no-test-project refusal (3), got $exit" }
    if ($text -notmatch [regex]::Escape("base       : refs/heads/target -> $target")) { throw 'target SHA was not resolved from merge parent one' }
    if ($text -notmatch [regex]::Escape("pr         : refs/heads/source -> $source")) { throw 'source SHA was not resolved from merge parent two' }
    if ($text -notmatch [regex]::Escape("merge base : $common")) { throw 'wrong merge base' }
    if ($text -notmatch 'changed from merge base: 2') { throw 'changed-file set did not use merge base' }
    Write-Host 'PASS: direct target/source diff=3; BehaviorDiff merge-base changed set=2' -ForegroundColor Green

    Write-Host ''
    Write-Host '=== plausibility guard ===' -ForegroundColor Cyan
    $env:BEHAVIORDIFF_MAX_CHANGED_FILES = '1'
    $refusedFindings = Join-Path $fixture 'refused-findings.json'
    $guardOutput = dotnet run --project $cli -c Release --no-build -- --ci=azuredevops --findings $refusedFindings 2>&1
    $guardExit = $LASTEXITCODE
    $guardText = $guardOutput -join "`n"
    $guardOutput | Select-Object -Last 8
    if ($guardExit -ne 3) { throw "plausibility guard expected exit 3, got $guardExit" }
    if ($guardText -notmatch 'IMPLAUSIBLE CHANGED-FILE SET') { throw 'guard did not explain the refusal' }
    $refusedDocument = Get-Content $refusedFindings -Raw | ConvertFrom-Json
    if ($refusedDocument.status -ne 'refused' -or $refusedDocument.isCleanResult -ne $false) { throw 'refused findings arm is not structurally invalid' }
    if ($null -ne $refusedDocument.members) { throw 'refused findings must omit members rather than serialize an empty array' }
    if ($refusedDocument.refusal.reason -notmatch 'IMPLAUSIBLE CHANGED-FILE SET') { throw 'refusal reason was not preserved in findings' }
    Write-Host 'PASS: 1-commit/2-file PR refused against an explicit limit of 1' -ForegroundColor Green
    Write-Host 'PASS: refusal is structurally distinct from clean and preserves its reason' -ForegroundColor Green
}
finally {
    Pop-Location
    Remove-Item $fixture -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:BEHAVIORDIFF_MAX_CHANGED_FILES -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'verify-ci-refs: PASS' -ForegroundColor Green