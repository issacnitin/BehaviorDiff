#requires -Version 7.0
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/BehaviorDiff.Cli/BehaviorDiff.Cli.csproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-language-detection-{0}" -f [Guid]::NewGuid().ToString('N'))

function Assert-Language([string]$name, [string]$marker, [string]$expected) {
    $directory = Join-Path $work $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $entryPoint = Join-Path $directory $marker
    New-Item -ItemType Directory -Path (Split-Path -Parent $entryPoint) -Force | Out-Null
    New-Item -ItemType File -Path $entryPoint -Force | Out-Null
    $output = @(& dotnet run --project $project -c Release --no-build -- detect-language $directory)
    $relative = $marker.Replace('\', '/')
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 3 -or $output[0] -ne $expected `
        -or $output[1] -ne $relative -or $output[2] -ne [IO.Path]::GetFullPath($entryPoint)) {
        throw "$name detection failed: exit=$LASTEXITCODE output=$($output -join ' | ') expected=$expected/$relative/$entryPoint"
    }
}

try {
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
    Assert-Language 'dotnet' 'sample.sln' 'dotnet'
    Assert-Language 'java' 'pom.xml' 'java'
    Assert-Language 'node' 'package.json' 'node'
    Assert-Language 'nested-java' 'services/reference/pom.xml' 'java'

    $mixed = Join-Path $work 'mixed'
    New-Item -ItemType Directory -Path $mixed -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'pom.xml') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $mixed 'package.json') -Force | Out-Null
    & dotnet run --project $project -c Release --no-build -- detect-language $mixed 2>$null
    if ($LASTEXITCODE -ne 3) { throw "mixed repository exit was $LASTEXITCODE, expected 3" }

    Write-Host 'Repository language detection: PASS' -ForegroundColor Green
    Write-Host '  dotnet / java / node root markers; nested relative evidence; absolute entry point; mixed root refused'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}