#requires -Version 7.0
[CmdletBinding()]
param([string]$RealDiffCommand)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/RealDiff.Cli/RealDiff.Cli.csproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ("realdiff-language-detection-{0}" -f [Guid]::NewGuid().ToString('N'))

function Invoke-Detect([string]$directory) {
    $output = if ([string]::IsNullOrWhiteSpace($RealDiffCommand)) {
        @(& dotnet run --project $project -c Release --no-build -- detect $directory 2>&1)
    } else {
        @(& $RealDiffCommand detect $directory 2>&1)
    }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $output -join "`n" }
}

function Assert-Contains([string]$text, [string]$literal, [string]$label) {
    if (-not $text.Contains($literal, [StringComparison]::Ordinal)) {
        throw "$label omitted '$literal': $text"
    }
}

function Assert-Language([string]$name, [string]$marker, [string]$expected, [string]$build, [string]$test) {
    $directory = Join-Path $work $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $entryPoint = Join-Path $directory $marker
    New-Item -ItemType Directory -Path (Split-Path -Parent $entryPoint) -Force | Out-Null
    New-Item -ItemType File -Path $entryPoint -Force | Out-Null
    $result = Invoke-Detect $directory
    if ($result.Exit -ne 0) { throw "$name detection exited $($result.Exit): $($result.Text)" }
    Assert-Contains $result.Text "language: $expected" "$name detection"
    Assert-Contains $result.Text "entry_point: $($marker.Replace('\', '/'))" "$name detection"
    Assert-Contains $result.Text "build: $build" "$name detection"
    Assert-Contains $result.Text "test: $test" "$name detection"
    Assert-Contains $result.Text 'source: auto-detection' "$name detection"
}

function Assert-NodeManager(
    [string]$name,
    [string]$lockfile,
    [string]$build,
    [string]$test,
    [switch]$YarnBerry) {
    $directory = Join-Path $work $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    '{"scripts":{"build":"node build.js","test":"node --test"}}' | Set-Content (Join-Path $directory 'package.json')
    New-Item -ItemType File -Path (Join-Path $directory $lockfile) -Force | Out-Null
    if ($YarnBerry) { New-Item -ItemType File -Path (Join-Path $directory '.yarnrc.yml') -Force | Out-Null }
    $result = Invoke-Detect $directory
    if ($result.Exit -ne 0) { throw "$name detection exited $($result.Exit): $($result.Text)" }
    Assert-Contains $result.Text "build: $build" "$name detection"
    Assert-Contains $result.Text "test: $test" "$name detection"
}

try {
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

    Assert-Language 'dotnet' 'sample.sln' 'dotnet' 'dotnet build sample.sln -c Release --nologo' 'dotnet test -c Release --no-build --nologo'
    Assert-Language 'java' 'pom.xml' 'java' 'mvn --batch-mode --no-transfer-progress package -DskipTests' 'mvn --batch-mode --no-transfer-progress test'
    Assert-Language 'java-gradle' 'build.gradle' 'java' 'gradlew build -x test' 'gradlew test'
    Assert-Language 'java-gradle-kts' 'build.gradle.kts' 'java' 'gradlew build -x test' 'gradlew test'
    Assert-Language 'node' 'package.json' 'node' 'npm ci' 'npm run test'
    Assert-Language 'go' 'go.mod' 'go' 'go build ./...' 'go test ./...'
    Assert-Language 'rust' 'Cargo.toml' 'rust' 'cargo build' 'cargo test -- --test-threads=1'

    Assert-NodeManager 'node-npm' 'package-lock.json' 'npm ci && npm run build' 'npm run test'
    Assert-NodeManager 'node-pnpm' 'pnpm-lock.yaml' 'pnpm install --frozen-lockfile && pnpm run build' 'pnpm run test'
    Assert-NodeManager 'node-yarn-classic' 'yarn.lock' 'yarn install --frozen-lockfile && yarn run build' 'yarn run test'
    Assert-NodeManager 'node-yarn-berry' 'yarn.lock' 'yarn install --immutable && yarn run build' 'yarn run test' -YarnBerry
    Assert-NodeManager 'node-bun' 'bun.lock' 'bun install --frozen-lockfile && bun run build' 'bun run test'
    Assert-NodeManager 'node-bun-legacy' 'bun.lockb' 'bun install --frozen-lockfile && bun run build' 'bun run test'

    $conflictingNode = Join-Path $work 'node-conflicting-locks'
    New-Item -ItemType Directory -Path $conflictingNode -Force | Out-Null
    '{}' | Set-Content (Join-Path $conflictingNode 'package.json')
    New-Item -ItemType File -Path (Join-Path $conflictingNode 'package-lock.json') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $conflictingNode 'pnpm-lock.yaml') -Force | Out-Null
    $conflictingNodeResult = Invoke-Detect $conflictingNode
    if ($conflictingNodeResult.Exit -ne 3) { throw "conflicting Node lockfile exit was $($conflictingNodeResult.Exit), expected 3" }
    Assert-Contains $conflictingNodeResult.Text 'Multiple Node lockfiles were detected' 'conflicting Node lockfile refusal'

    $conflictingBun = Join-Path $work 'node-conflicting-bun-locks'
    New-Item -ItemType Directory -Path $conflictingBun -Force | Out-Null
    '{}' | Set-Content (Join-Path $conflictingBun 'package.json')
    New-Item -ItemType File -Path (Join-Path $conflictingBun 'bun.lock') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $conflictingBun 'bun.lockb') -Force | Out-Null
    $conflictingBunResult = Invoke-Detect $conflictingBun
    if ($conflictingBunResult.Exit -ne 3) { throw "conflicting Bun lockfile exit was $($conflictingBunResult.Exit), expected 3" }
    Assert-Contains $conflictingBunResult.Text 'Multiple Node lockfiles were detected' 'conflicting Bun lockfile refusal'

    $customJava = Join-Path $work 'java-custom-source-root'
    New-Item -ItemType Directory -Path (Join-Path $customJava '.realdiff') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $customJava 'code/production/com/example') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $customJava 'build.gradle') -Force | Out-Null
    'package com.example; public class Subject {}' | Set-Content (Join-Path $customJava 'code/production/com/example/Subject.java')
    @'
source_roots:
    - code/production
'@ | Set-Content (Join-Path $customJava '.realdiff/config.yml')
    $customJavaResult = Invoke-Detect $customJava
    if ($customJavaResult.Exit -ne 0) { throw "custom Java detection exited $($customJavaResult.Exit): $($customJavaResult.Text)" }
    Assert-Contains $customJavaResult.Text '- code/production' 'custom Java source roots'
    Assert-Contains $customJavaResult.Text '- com.example' 'custom Java package scope'

    @'
source_roots:
    - ../outside
'@ | Set-Content (Join-Path $customJava '.realdiff/config.yml')
    $escapingJavaResult = Invoke-Detect $customJava
    if ($escapingJavaResult.Exit -ne 3) { throw "escaping Java source root exit was $($escapingJavaResult.Exit), expected 3" }
    Assert-Contains $escapingJavaResult.Text 'Java source root escapes the repository root' 'escaping Java source root refusal'

    $monorepo = Join-Path $work 'monorepo'
    New-Item -ItemType Directory -Path (Join-Path $monorepo 'services/web/.realdiff') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $monorepo '.realdiff') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $monorepo 'services/web/package.json') -Force | Out-Null
    @'
language: node
workdir: services/web
build: npm run generate && npm run compile
test: npm run test:behavior
test_projects:
  - test/behavior/**/*.test.ts
include_namespaces:
  - src/domain
exclude_namespaces:
  - src/generated
'@ | Set-Content (Join-Path $monorepo '.realdiff/config.yml')
    $configured = Invoke-Detect $monorepo
    if ($configured.Exit -ne 0) { throw "configured detection exited $($configured.Exit): $($configured.Text)" }
    foreach ($literal in @(
        'language: node',
        'workdir: services/web',
        'build: npm run generate && npm run compile',
        'test: npm run test:behavior',
        '- test/behavior/**/*.test.ts',
        '- src/domain',
        '- src/generated',
        'source: .realdiff/config.yml + detection')) {
        Assert-Contains $configured.Text $literal 'configured detection'
    }

    $mixed = Join-Path $work 'mixed'
    New-Item -ItemType Directory -Path $mixed -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'pom.xml') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'package.json') -Force | Out-Null
    $mixedResult = Invoke-Detect $mixed
    if ($mixedResult.Exit -ne 3) { throw "mixed repository exit was $($mixedResult.Exit), expected 3" }
    Assert-Contains $mixedResult.Text 'Repository language is ambiguous' 'mixed refusal'
    Assert-Contains $mixedResult.Text "realdiff detect <repo>" 'mixed refusal'
    Assert-Contains $mixedResult.Text '.realdiff/config.yml' 'mixed refusal'

    $solutions = Join-Path $work 'multiple-solutions'
    New-Item -ItemType Directory -Path $solutions -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $solutions 'one.sln') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $solutions 'two.sln') -Force | Out-Null
    $solutionResult = Invoke-Detect $solutions
    if ($solutionResult.Exit -ne 3) { throw "multiple-solution exit was $($solutionResult.Exit), expected 3" }
    Assert-Contains $solutionResult.Text 'Multiple dotnet build entry points' 'multiple-solution refusal'

    Write-Host 'Repository config and detection: PASS' -ForegroundColor Green
    Write-Host '  languages=7 nodeManagers=6 configuredOverrides=8 ambiguityRefusals=4'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
