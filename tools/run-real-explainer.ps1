#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('sort', 'retry', 'permission', 'config')]
    [string]$Change
)

$ErrorActionPreference = 'Stop'
$apiKey = $env:ANTHROPIC_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $keyFile = Join-Path $HOME '.behaviordiff/anthropic.key'
    if (-not (Test-Path $keyFile)) {
        throw 'ANTHROPIC_API_KEY is not set and ~/.behaviordiff/anthropic.key does not exist.'
    }

    $protectedKey = (Get-Content $keyFile -Raw).Trim()
    $secureKey = ConvertTo-SecureString $protectedKey
    $protectedKey = $null
    $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try {
        $apiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
        $secureKey.Dispose()
    }
}
Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue

$repo = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
$prTree = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-live-pr-$runId"
$work = Join-Path ([IO.Path]::GetTempPath()) "behaviordiff-live-$runId"
$case = switch ($Change) {
    'sort' { @{
        File = 'src/Infrastructure.Collections/SortingExtensions.cs'
        Base = 'var list = src.ToList();'
        Pr = 'Func<T, int> key) => src.OrderBy(key).ToList();'
    } }
    'retry' { @{
        File = 'samples/SampleApp/ConfigParser.cs'
        Base = 'RetrySettings.MaxAttempts = int.Parse(raw["max_attempts"], CultureInfo.InvariantCulture);'
        Pr = 'RetrySettings.MaxAttempts = raw.TryGetValue("max_attempts", out string? value)'
    } }
    'permission' { @{
        File = 'samples/SampleApp/PermissionDefaultsParser.cs'
        Base = 'DefaultRole = "Reader"'
        Pr = 'DefaultRole = "None"'
    } }
    'config' { @{
        File = 'samples/SampleApp/SettingsParser.cs'
        Base = 'DefaultFreeShippingThreshold = 50m'
        Pr = 'DefaultFreeShippingThreshold = 30m'
    } }
}

try {
    & (Join-Path $PSScriptRoot 'verify-diff.ps1') -Mutate -Change $Change `
        -WorkDirectory $work -PrTreeDirectory $prTree
    if ($LASTEXITCODE -ne 0) { throw "$Change behavior diff failed: $LASTEXITCODE" }

    $baseText = Get-Content (Join-Path $repo $case.File) -Raw
    $prText = Get-Content (Join-Path $prTree $case.File) -Raw
    if ($baseText -notmatch [regex]::Escape($case.Base) -or $prText -notmatch [regex]::Escape($case.Pr)) {
        throw "$Change mutation does not match the expected one-line change"
    }

    $patch = Join-Path $work "$Change.patch"
    $diff = @(& git diff --no-index --no-prefix --unified=3 -- `
        (Join-Path $repo $case.File) (Join-Path $prTree $case.File) 2>$null)
    if ($LASTEXITCODE -ne 1) { throw "$Change expected git diff --no-index to report one changed file" }
    $hunkStart = 0
    while ($hunkStart -lt $diff.Count -and -not $diff[$hunkStart].StartsWith('@@', [StringComparison]::Ordinal)) {
        $hunkStart++
    }
    if ($hunkStart -eq $diff.Count) { throw "$Change mutation did not produce a unified diff hunk" }
    $hunk = $diff[$hunkStart..($diff.Count - 1)]
    @(
        "--- a/$($case.File)"
        "+++ b/$($case.File)"
        $hunk
    ) | Set-Content $patch

    $liveOutput = Join-Path $work 'live'
    $liveArtifacts = Join-Path $work 'live-artifacts'
    & dotnet publish (Join-Path $PSScriptRoot 'AnthropicLive/BehaviorDiff.AnthropicLive.csproj') `
        -c Release --artifacts-path $liveArtifacts -o $liveOutput --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "AnthropicLive publish failed: $LASTEXITCODE" }
    $liveDll = Join-Path $liveOutput 'BehaviorDiff.AnthropicLive.dll'

    $env:ANTHROPIC_API_KEY = $apiKey
    & dotnet $liveDll (Join-Path $work 'findings.json') $case.File $patch
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
    $apiKey = $null
    Remove-Item $work, $prTree -Recurse -Force -ErrorAction SilentlyContinue
}

