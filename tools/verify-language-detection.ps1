#requires -Version 7.0
[CmdletBinding()]
param([string]$BehaviorDiffCommand)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-language-detection-{0}" -f [Guid]::NewGuid().ToString('N'))

function Invoke-Detect([string]$directory) {
    $output = if ([string]::IsNullOrWhiteSpace($BehaviorDiffCommand)) {
        @(& dotnet run --project $project -c Release --no-build -- detect $directory 2>&1)
    } else {
        @(& $BehaviorDiffCommand detect $directory 2>&1)
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

try {
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

    Assert-Language 'dotnet' 'sample.sln' 'dotnet' 'dotnet build sample.sln -c Release --nologo' 'dotnet test -c Release --no-build --nologo'
    Assert-Language 'java' 'pom.xml' 'java' 'mvn --batch-mode --no-transfer-progress package -DskipTests' 'mvn --batch-mode --no-transfer-progress test'
    Assert-Language 'node' 'package.json' 'node' 'npm ci && npm run build --if-present' 'npm test'
    Assert-Language 'go' 'go.mod' 'go' 'go build ./...' 'go test ./...'
    Assert-Language 'rust' 'Cargo.toml' 'rust' 'cargo build' 'cargo test -- --test-threads=1'

    $monorepo = Join-Path $work 'monorepo'
    New-Item -ItemType Directory -Path (Join-Path $monorepo 'services/web/.behaviordiff') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $monorepo '.behaviordiff') -Force | Out-Null
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
'@ | Set-Content (Join-Path $monorepo '.behaviordiff/config.yml')
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
        'source: .behaviordiff/config.yml + detection')) {
        Assert-Contains $configured.Text $literal 'configured detection'
    }

    $mixed = Join-Path $work 'mixed'
    New-Item -ItemType Directory -Path $mixed -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'pom.xml') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'package.json') -Force | Out-Null
    $mixedResult = Invoke-Detect $mixed
    if ($mixedResult.Exit -ne 3) { throw "mixed repository exit was $($mixedResult.Exit), expected 3" }
    Assert-Contains $mixedResult.Text 'Repository language is ambiguous' 'mixed refusal'
    Assert-Contains $mixedResult.Text "behaviordiff detect <repo>" 'mixed refusal'
    Assert-Contains $mixedResult.Text '.behaviordiff/config.yml' 'mixed refusal'

    $solutions = Join-Path $work 'multiple-solutions'
    New-Item -ItemType Directory -Path $solutions -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $solutions 'one.sln') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $solutions 'two.sln') -Force | Out-Null
    $solutionResult = Invoke-Detect $solutions
    if ($solutionResult.Exit -ne 3) { throw "multiple-solution exit was $($solutionResult.Exit), expected 3" }
    Assert-Contains $solutionResult.Text 'Multiple dotnet build entry points' 'multiple-solution refusal'

    Write-Host 'Repository config and detection: PASS' -ForegroundColor Green
    Write-Host '  languages=5 configuredOverrides=7 ambiguityRefusals=2'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
