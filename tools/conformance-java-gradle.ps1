#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-java-gradle-conformance-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$fixture = Join-Path $work 'repository'
$run = Join-Path $work 'run'
$expectedRunnerCount = 111

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $fixture -ItemType Directory -Force | Out-Null
    Get-ChildItem (Join-Path $repo 'samples/JavaGradleReference') -Force | ForEach-Object {
        Copy-Item $_.FullName $fixture -Recurse -Force
    }
    & git -C $fixture init --initial-branch=main --quiet
    & git -C $fixture config user.email 'realdiff-proof@example.invalid'
    & git -C $fixture config user.name 'RealDiff Proof'
    & git -C $fixture add .
    & git -C $fixture commit --quiet -m 'Gradle reference base'
    $base = (& git -C $fixture rev-parse HEAD).Trim()
    $subject = Join-Path $fixture 'code/main/io/realdiff/gradlereference/Subject.java'
    $updated = (Get-Content $subject -Raw).Replace('new ArrayList<>()', 'new ArrayList<>(2)')
    Set-Content $subject $updated -NoNewline
    & git -C $fixture add $subject
    & git -C $fixture commit --quiet -m 'Pre-size sum values'
    $pr = (& git -C $fixture rev-parse HEAD).Trim()

    & mvn --batch-mode --no-transfer-progress -f (Join-Path $repo 'src/RealDiff.Java.Agent/pom.xml') package -DskipTests
    if ($LASTEXITCODE -ne 0) { throw 'Java agent build failed' }
    $env:REALDIFF_JAVA_AGENT = Join-Path $repo 'src/RealDiff.Java.Agent/target/realdiff-java-agent-0.2.0-SNAPSHOT.jar'
    $env:REALDIFF_RUST_ENGINE = Join-Path $repo 'src/RealDiff.Engine.Rust/target/release/realdiff-engine.exe'
    & cargo build --release --manifest-path (Join-Path $repo 'src/RealDiff.Engine.Rust/Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Rust engine build failed' }

    & dotnet run --project (Join-Path $repo 'src/RealDiff.Cli/RealDiff.Cli.csproj') -c Release --no-build -- `
        $fixture --base $base --pr $pr --work $run --findings (Join-Path $run 'findings.json') `
        --keep --keep-traces 1d
    if ($LASTEXITCODE -ne 0) { throw "Gradle CLI analysis failed with exit code $LASTEXITCODE" }

    $events = @(Get-ChildItem $run -Recurse -File -Filter '*.ndjson' |
        Where-Object Name -NotLike '*.manifest.ndjson' |
        ForEach-Object { Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } } |
        Where-Object { $null -ne $_.PSObject.Properties['methodFullName'] })
    $manifests = @(Get-ChildItem $run -Recurse -File -Filter '*.manifest.ndjson' |
        ForEach-Object { Get-Content $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } })
    $roots = @($events | Where-Object {
        $null -ne $_.PSObject.Properties['isHarness'] -and [bool]$_.isHarness `
            -and [string]$_.methodFullName -like '*SubjectTest*'
    })
    $wrongPaths = @($events | Where-Object {
        $null -eq $_.PSObject.Properties['filePath'] `
            -or [string]$_.filePath -notmatch '^code/(main|test)/io/realdiff/gradlereference/.+\.java$'
    })
    if ($events.Count -eq 0 -or $roots.Count -ne ($expectedRunnerCount * 4) -or $wrongPaths.Count -ne 0) {
        throw "Gradle trace contract failed: events=$($events.Count) roots=$($roots.Count) wrongPaths=$($wrongPaths.Count)"
    }
    $findings = Get-Content (Join-Path $run 'findings.json') -Raw | ConvertFrom-Json
    if ($findings.status -ne 'analyzed' -or -not [bool]$findings.isCleanResult) {
        throw "Gradle findings were not clean: $($findings.status)"
    }
    if (@($manifests | Where-Object { $_.kind -eq 'member' -and $_.sourceResolution -eq 'debugInfo' }).Count -eq 0) {
        throw 'Gradle manifest has no exact-source members'
    }

    Write-Host 'JAVA_GRADLE_CONFORMANCE: PASS' -ForegroundColor Green
    Write-Host "  runner tests : $expectedRunnerCount per run"
    Write-Host "  events       : $($events.Count) across four runs"
    Write-Host '  source roots : code/main, code/test'
    Write-Host '  JPMS probe   : passed in every forked test JVM'
}
finally {
    Remove-Item Env:REALDIFF_JAVA_AGENT,Env:REALDIFF_RUST_ENGINE -ErrorAction SilentlyContinue
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "Gradle conformance work retained at $work" }
}
