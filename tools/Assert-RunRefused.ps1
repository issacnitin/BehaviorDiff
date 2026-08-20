#requires -Version 7.0
<#
.SYNOPSIS
    Asserts that a command refuses to run, for the stated reason.

.DESCRIPTION
    "Given this condition, the run must refuse, with this message" is a category, not a one-off. A
    negative test that only checks a non-zero exit code is worthless: the run may have failed to compile,
    failed to find a file, or crashed. Both halves are required - the refusal AND the specific diagnostic.

    Runs the command in a subprocess so a throw inside it cannot be confused with a throw in the harness.

.OUTPUTS
    A result object with Passed, ExitCode, MatchedMessage and Output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [Parameter(Mandatory)][string[]]$ExpectedMessages,
    [string]$WorkingDirectory = (Get-Location).Path,
    [hashtable]$Environment = @{}
)

$ErrorActionPreference = 'Stop'

$stdout = [System.IO.Path]::GetTempFileName()
$stderr = [System.IO.Path]::GetTempFileName()

$previous = @{}
foreach ($key in $Environment.Keys) {
    $previous[$key] = [System.Environment]::GetEnvironmentVariable($key)
    [System.Environment]::SetEnvironmentVariable($key, $Environment[$key])
}

try {
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $output = (Get-Content $stdout -Raw) + "`n" + (Get-Content $stderr -Raw)
    $exitCode = $process.ExitCode
}
finally {
    foreach ($key in $Environment.Keys) {
        [System.Environment]::SetEnvironmentVariable($key, $previous[$key])
    }

    Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
}

$refused = $exitCode -ne 0

# Every expected message must be present. A refusal for an unrelated reason must not pass.
$missing = @()
foreach ($message in $ExpectedMessages) {
    if ($output -notlike "*$message*") { $missing += $message }
}

$passed = $refused -and ($missing.Count -eq 0)

Write-Host ""
Write-Host "NEGATIVE TEST: $Name" -ForegroundColor Cyan
Write-Host "  refused (exit != 0) : $refused (exit=$exitCode)"
Write-Host "  expected diagnostics: $($ExpectedMessages.Count) required, $($missing.Count) missing"
foreach ($message in $missing) {
    Write-Host "    MISSING: $message" -ForegroundColor Red
}

if ($refused -and $missing.Count -gt 0) {
    Write-Host "  the run refused, but NOT for the stated reason - this is a false pass" -ForegroundColor Red
}

Write-Host ("  result              : {0}" -f $(if ($passed) { 'PASS' } else { 'FAIL' })) `
    -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })

if (-not $passed) {
    Write-Host "  ---- captured output (last 30 lines) ----"
    ($output -split "`n" | Select-Object -Last 30) | ForEach-Object { Write-Host "  $_" }
}

[pscustomobject]@{
    Name            = $Name
    Passed          = $passed
    Refused         = $refused
    ExitCode        = $exitCode
    MissingMessages = $missing
    Output          = $output
}
