#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$BehaviorDiffCommand,
    [string]$WorkDirectory,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = Split-Path -Parent $PSScriptRoot
$cli = if ([string]::IsNullOrWhiteSpace($BehaviorDiffCommand)) {
    Join-Path $source 'src/BehaviorDiff.Cli/bin/Release/net8.0/behaviordiff.exe'
} else {
    [IO.Path]::GetFullPath($BehaviorDiffCommand)
}
$root = if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    Join-Path ([IO.Path]::GetTempPath()) ("behaviordiff-rust-cli-{0}" -f [Guid]::NewGuid().ToString('N'))
} else {
    [IO.Path]::GetFullPath($WorkDirectory)
}
$preview = Join-Path $source 'tools/CommentPreview/BehaviorDiff.CommentPreview.csproj'
$previousExclusions = $env:BEHAVIORDIFF_EXCLUDE_NAMESPACES

if (-not (Test-Path $cli -PathType Leaf)) {
    throw "BehaviorDiff CLI was not found: $cli"
}

$cases = @(
    [pscustomobject]@{
        Name = 'sort'
        File = 'src/Infrastructure.Collections/SortingExtensions.cs'
        Pattern = '(?s)public static List<T> ByPriority<T>\(\s*this IEnumerable<T> src,\s*Func<T, int> key\)\s*\{\s*var list = src\.ToList\(\);\s*list\.Sort\(\(a, b\) => key\(a\)\.CompareTo\(key\(b\)\)\);\s*return list;\s*\}'
        Replacement = @'
public static List<T> ByPriority<T>(
        this IEnumerable<T> src,
        Func<T, int> key) => src.OrderBy(key).ToList();
'@
        Headline = 'Commerce.Pricing.DiscountEngine.SelectDiscount(System.Decimal)'
    },
    [pscustomobject]@{
        Name = 'retry'
        File = 'samples/SampleApp/ConfigParser.cs'
        Pattern = 'RetrySettings\.MaxAttempts = int\.Parse\(raw\["max_attempts"\], CultureInfo\.InvariantCulture\);'
        Replacement = @'
RetrySettings.MaxAttempts = raw.TryGetValue("max_attempts", out string? value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : 3;
'@
        Headline = 'SampleApp.RetryPolicy.ShouldRetry(System.Int32,System.Int32)'
    },
    [pscustomobject]@{
        Name = 'config'
        File = 'samples/SampleApp/SettingsParser.cs'
        Pattern = 'DefaultFreeShippingThreshold = 50m'
        Replacement = 'DefaultFreeShippingThreshold = 30m'
        Headline = 'SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)'
    }
)

$archivePaths = @(
    'Directory.Build.props',
    'global.json',
    'samples/SampleApp',
    'samples/SampleApp.Plugin',
    'samples/SampleApp.Tests',
    'src/BehaviorDiff.Contracts',
    'src/BehaviorDiff.Tracer',
    'src/BehaviorDiff.Tracer.Xunit',
    'src/Commerce.Pricing',
    'src/Infrastructure.Collections'
)

function Invoke-Checked([string]$label, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$label failed with exit code $LASTEXITCODE"
    }
}

function Initialize-DemoRepository([string]$repository) {
    $archive = Join-Path $repository 'source.zip'
    New-Item -ItemType Directory -Path $repository -Force | Out-Null
    Invoke-Checked 'Source archive' { & git -C $source archive --format=zip --output=$archive HEAD @archivePaths }
    Expand-Archive $archive $repository
    Remove-Item $archive
    Invoke-Checked 'Solution creation' { & dotnet new sln --name Demo --output $repository }
    Invoke-Checked 'Solution project add' {
        & dotnet sln (Join-Path $repository 'Demo.sln') add `
            (Join-Path $repository 'samples/SampleApp.Tests/SampleApp.Tests.csproj')
    }
    Invoke-Checked 'Git initialization' { & git -C $repository init --initial-branch=main }
    Invoke-Checked 'Git identity' { & git -C $repository config user.email behaviordiff@example.invalid }
    Invoke-Checked 'Git identity' { & git -C $repository config user.name BehaviorDiff }
    Invoke-Checked 'Base commit' { & git -C $repository add .; & git -C $repository commit -m base }
}

if (Test-Path $root) {
    Remove-Item $root -Recurse -Force
}
New-Item -ItemType Directory -Path $root -Force | Out-Null
$env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = 'SampleApp.Diagnostics,SampleApp.Persistence,Infrastructure.Collections'

try {
    foreach ($case in $cases) {
        Write-Host "=== Rust CLI demo: $($case.Name) ===" -ForegroundColor Cyan
        $caseRoot = Join-Path $root $case.Name
        $repository = Join-Path $caseRoot 'repo'
        $analysis = Join-Path $caseRoot 'analysis'
        Initialize-DemoRepository $repository
        $baseSha = (& git -C $repository rev-parse HEAD).Trim()

        $target = Join-Path $repository $case.File
        $text = Get-Content $target -Raw
        $mutated = $text -replace $case.Pattern, $case.Replacement
        if ($mutated -ceq $text) {
            throw "$($case.Name) mutation did not match $($case.File)"
        }

        [IO.File]::WriteAllText($target, $mutated)
        Invoke-Checked 'PR commit' { & git -C $repository add $case.File; & git -C $repository commit -m $case.Name }
        $prSha = (& git -C $repository rev-parse HEAD).Trim()

        $findingsPath = Join-Path $analysis 'findings.json'
        $output = @(& $cli $repository --base $baseSha --pr $prSha `
            --work $analysis --findings $findingsPath --no-baseline --keep --keep-traces 1d 2>&1)
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 1) {
            throw "$($case.Name) CLI exited $exitCode instead of analyzed-findings exit 1"
        }
        if (($output -join "`n") -notmatch 'engine:\s+rust') {
            throw "$($case.Name) CLI output did not confirm Rust dispatch"
        }

        $findings = Get-Content $findingsPath -Raw | ConvertFrom-Json -Depth 100
        $members = @($findings.members | Where-Object memberName -eq $case.Headline)
        if ($findings.status -ne 'analyzed' -or $findings.verdict -ne 'findings' -or $members.Count -ne 1) {
            throw "$($case.Name) findings lost expected headline $($case.Headline)"
        }

        $comment = @(& dotnet run --project $preview -c Release -- $findingsPath 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "$($case.Name) comment rendering failed: $LASTEXITCODE"
        }
        $commentText = $comment -join "`n"
        $commentText | Set-Content (Join-Path $analysis 'comment.md')
        if ($commentText -notmatch [regex]::Escape($case.Headline.Split('(')[0])) {
            throw "$($case.Name) rendered comment omitted the expected headline"
        }

        Write-Host ("PASS {0}: members={1} callSites={2} comment={3}" -f `
            $case.Name,
            $findings.summary.unexpectedMembers,
            $findings.summary.unexpectedCallSites,
            (Join-Path $analysis 'comment.md')) -ForegroundColor Green
    }
}
finally {
    $env:BEHAVIORDIFF_EXCLUDE_NAMESPACES = $previousExclusions
    if (-not $KeepWork -and (Test-Path $root)) {
        Remove-Item $root -Recurse -Force
    }
}