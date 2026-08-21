#requires -Version 7.0
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-language-detection-{0}" -f [Guid]::NewGuid().ToString('N'))

function Assert-Language([string]$name, [string]$marker, [string]$expected) {
    $directory = Join-Path $work $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $directory $marker) -Force | Out-Null
    $output = @(& dotnet run --project $project -c Release --no-build -- detect-language $directory)
    if ($LASTEXITCODE -ne 0 -or $output[0] -ne $expected) {
        throw "$name detection failed: exit=$LASTEXITCODE output=$($output -join ' | ') expected=$expected"
    }
}

try {
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
    Assert-Language 'dotnet' 'sample.sln' 'dotnet'
    Assert-Language 'java' 'pom.xml' 'java'
    Assert-Language 'node' 'package.json' 'node'

    $mixed = Join-Path $work 'mixed'
    New-Item -ItemType Directory -Path $mixed -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'pom.xml') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'package.json') -Force | Out-Null
    & dotnet run --project $project -c Release --no-build -- detect-language $mixed 2>$null
    if ($LASTEXITCODE -ne 3) { throw "mixed repository exit was $LASTEXITCODE, expected 3" }

    Write-Host 'Repository language detection: PASS' -ForegroundColor Green
    Write-Host '  dotnet / java / node root markers; mixed root refused'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}