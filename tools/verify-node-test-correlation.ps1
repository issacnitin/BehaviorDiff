#requires -Version 7.0
[CmdletBinding()]
param([string]$WorkDirectory, [switch]$KeepWork)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$ownsWork = [string]::IsNullOrWhiteSpace($WorkDirectory)
$work = if ($ownsWork) {
    Join-Path ([IO.Path]::GetTempPath()) ("realdiff-node-test-correlation-{0}" -f [Guid]::NewGuid().ToString('N'))
} else { [IO.Path]::GetFullPath($WorkDirectory) }
$sampleSource = Join-Path $repo 'samples/NodeTestFrameworks'
$sample = Join-Path $work 'sample'
$results = Join-Path $work 'results'
$tracer = Join-Path $repo 'src/RealDiff.Node'
$register = Join-Path $tracer 'register.cjs'

function Copy-Sample([string]$destination) {
    Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem $sampleSource -Force |
        Where-Object Name -notin @('node_modules', 'results') |
        ForEach-Object { Copy-Item $_.FullName -Destination $destination -Recurse -Force }
}

function New-JestIntegration([string]$directory) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $bridge = Join-Path $directory 'bridge.cjs'
        $setup = Join-Path $directory 'setup.cjs'
        $transformer = Join-Path $directory 'transformer.cjs'
        $config = Join-Path $directory 'jest.config.cjs'
        [IO.File]::WriteAllText($bridge, @'
'use strict';
const register = require(process.env.REALDIFF_NODE_ROOT + '/register.cjs');
process[Symbol.for('realdiff.runtime')] = register.runtime;
'@)
        [IO.File]::WriteAllText($setup, @'
'use strict';
globalThis[Symbol.for('realdiff.runtime')] = process[Symbol.for('realdiff.runtime')];
'@)
        [IO.File]::WriteAllText($transformer, @'
'use strict';
const path = require('node:path');
const { transform } = require(path.join(process.env.REALDIFF_NODE_ROOT, 'src/transform.cjs'));
module.exports = {
    process(source, filename) {
        return { code: transform(source, filename, { repositoryRoot: process.cwd() }).code };
    }
};
'@)
        $setupLiteral = $setup.Replace('\', '/').Replace("'", "\'")
        $transformerLiteral = $transformer.Replace('\', '/').Replace("'", "\'")
        $rootLiteral = $sample.Replace('\', '/').Replace("'", "\'")
        [IO.File]::WriteAllText($config, @"
'use strict';
module.exports = {
    rootDir: '$rootLiteral',
    testEnvironment: 'node',
    testMatch: ['<rootDir>/test/jest.test.cjs'],
    setupFiles: ['$setupLiteral'],
    transform: { '^.+\\.js$': '$transformerLiteral' }
};
"@)
        return [pscustomobject]@{ Bridge = $bridge; Config = $config }
}

function New-VitestIntegration([string]$directory) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $bridge = Join-Path $directory 'bridge.cjs'
        $setup = Join-Path $directory 'setup.mjs'
        $config = Join-Path $directory 'vitest.config.mjs'
        [IO.File]::WriteAllText($bridge, @'
'use strict';
const register = require(process.env.REALDIFF_NODE_ROOT + '/register.cjs');
process[Symbol.for('realdiff.runtime')] = register.runtime;
'@)
        [IO.File]::WriteAllText($setup, @'
    import { afterAll } from 'vitest';

    const runtime = process[Symbol.for('realdiff.runtime')];
    globalThis[Symbol.for('realdiff.runtime')] = runtime;
    afterAll(() => runtime.shutdown());
'@)
        $rootLiteral = $sample.Replace('\', '/').Replace("'", "\'")
        $setupLiteral = $setup.Replace('\', '/').Replace("'", "\'")
        $tracerLiteral = $tracer.Replace('\', '/').Replace("'", "\'")
        [IO.File]::WriteAllText($config, @"
import { createRequire } from 'node:module';
import { defineConfig } from 'vitest/config';

const require = createRequire(import.meta.url);
    const { transform } = require('$tracerLiteral/src/transform.cjs');

export default defineConfig({
    root: '$rootLiteral',
    plugins: [{
        name: 'realdiff-subject-transform',
        enforce: 'pre',
        transform(source, id) {
            const filename = id.split('?')[0];
            if (!filename.replaceAll('\\\\', '/').endsWith('/src/subject.js')) return null;
            return { code: transform(source, filename, { repositoryRoot: process.cwd() }).code, map: null };
        }
    }],
    test: {
        include: ['test/vitest.test.mjs'],
        setupFiles: ['$setupLiteral'],
        fileParallelism: false,
        maxWorkers: 1
    }
});
"@)
        return [pscustomobject]@{ Bridge = $bridge; Config = $config }
}

function Read-Ndjson([string]$path) {
    return @(Get-Content $path | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object {
        $_ | ConvertFrom-Json
    })
}

function Get-RunnerCounts([string]$path, [string]$framework) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "$framework runner report was not written: $path"
    }
    $report = Get-Content $path -Raw | ConvertFrom-Json
    foreach ($property in @('numTotalTests', 'numPassedTests')) {
        if ($null -eq $report.PSObject.Properties[$property]) {
            throw "$framework runner report has no authoritative $property field"
        }
    }
    $executed = [int]$report.numTotalTests
    $passed = [int]$report.numPassedTests
    if ($executed -ne 3 -or $passed -ne 3) {
        throw "$framework runner count mismatch: executed=$executed passed=$passed expected=3"
    }
    return [pscustomobject]@{ Executed = $executed; Passed = $passed }
}

function Assert-ProcessManifest(
    [string]$tracePath,
    [string]$manifestPath,
    [string]$framework
) {
    if (-not (Test-Path $manifestPath -PathType Leaf)) {
        throw "$framework process trace has no manifest: $manifestPath"
    }
    $events = @(Read-Ndjson $tracePath)
    $records = @(Read-Ndjson $manifestPath)
    $runs = @($records | Where-Object kind -eq 'run')
    $writers = @($records | Where-Object kind -eq 'writer')
    $digests = @($records | Where-Object kind -eq 'digest')
    if ($runs.Count -ne 1 -or $runs[0].language -cne 'node') {
        throw "$framework process manifest run record mismatch: count=$($runs.Count)"
    }
    if ($writers.Count -ne 1 -or $digests.Count -ne 1) {
        throw "$framework process manifest terminal records mismatch: writers=$($writers.Count) digests=$($digests.Count)"
    }
    $writer = $writers[0]
    if ([long]$writer.enqueued -ne $events.Count -or
        [long]$writer.written -ne $events.Count -or
        [long]$writer.dropped -ne 0 -or
        [long]$writer.capacity -le 0) {
        throw "$framework writer mismatch for $tracePath`: events=$($events.Count) enqueued=$($writer.enqueued) written=$($writer.written) dropped=$($writer.dropped) capacity=$($writer.capacity)"
    }

    $assemblies = @($records | Where-Object kind -eq 'assembly')
    $members = @($records | Where-Object kind -eq 'member')
    $duplicateAssemblies = @($assemblies | Group-Object assembly | Where-Object Count -ne 1)
    if ($duplicateAssemblies.Count -ne 0) {
        throw "$framework manifest contains duplicate assembly records"
    }
    foreach ($assembly in $assemblies) {
        $moduleMembers = @($members | Where-Object assembly -CEQ $assembly.assembly)
        $patched = @($moduleMembers | Where-Object status -CEQ 'Patched').Count
        $skipped = $moduleMembers.Count - $patched
        $methods = @($moduleMembers | ForEach-Object method)
        $traced = @($events | Where-Object methodFullName -In $methods).Count
        if ([long]$assembly.discoveredMembers -ne $moduleMembers.Count -or
            [long]$assembly.patchedMembers -ne $patched -or
            [long]$assembly.skippedMembers -ne $skipped -or
            [long]$assembly.patchFailedMembers -ne 0 -or
            [long]$assembly.tracedCalls -ne $traced) {
            throw "$framework module reconciliation failed for $($assembly.assembly): discovered=$($assembly.discoveredMembers)/$($moduleMembers.Count) patched=$($assembly.patchedMembers)/$patched skipped=$($assembly.skippedMembers)/$skipped failed=$($assembly.patchFailedMembers) traced=$($assembly.tracedCalls)/$traced"
        }
    }
    $orphanMembers = @($members | Where-Object assembly -NotIn @($assemblies | ForEach-Object assembly))
    $knownMethods = @($members | ForEach-Object method)
    $orphanEvents = @($events | Where-Object methodFullName -NotIn $knownMethods)
    if ($orphanMembers.Count -ne 0 -or $orphanEvents.Count -ne 0) {
        throw "$framework module reconciliation found orphans: members=$($orphanMembers.Count) events=$($orphanEvents.Count)"
    }

    return [pscustomobject]@{
        Events = $events
        Records = $records
        Writer = $writer
        Assemblies = $assemblies.Count
    }
}

function Read-TraceRun([string]$runDirectory, [string]$framework) {
    $traceFiles = @(Get-ChildItem $runDirectory -File -Filter '*.ndjson' |
        Where-Object Name -notlike '*.manifest.ndjson' | Sort-Object FullName)
    if ($traceFiles.Count -eq 0) { throw "$framework emitted no process traces" }
    $events = [Collections.Generic.List[object]]::new()
    $records = [Collections.Generic.List[object]]::new()
    $writerWritten = 0L
    $modules = 0
    foreach ($traceFile in $traceFiles) {
        $manifestPath = $traceFile.FullName -replace '\.ndjson$', '.manifest.ndjson'
        $process = Assert-ProcessManifest $traceFile.FullName $manifestPath $framework
        foreach ($event in $process.Events) { $events.Add($event) }
        foreach ($record in $process.Records) { $records.Add($record) }
        $writerWritten += [long]$process.Writer.written
        $modules += $process.Assemblies
    }
    if ($writerWritten -ne $events.Count) {
        throw "$framework aggregate writer mismatch: written=$writerWritten events=$($events.Count)"
    }
    return [pscustomobject]@{
        Events = @($events)
        Records = @($records)
        ProcessTraces = $traceFiles.Count
        WriterWritten = $writerWritten
        Modules = $modules
    }
}

function Assert-Correlation(
    [object]$run,
    [object]$runner,
    [string]$framework
) {
    $rootMethods = @($run.Records | Where-Object {
        $_.kind -eq 'member' -and
        $null -ne $_.PSObject.Properties['isTestRoot'] -and [bool]$_.isTestRoot
    } | ForEach-Object method | Sort-Object -Unique)
    if ($rootMethods.Count -eq 0) { throw "$framework manifest contains no test-root methods" }
    $rootEvents = @($run.Events | Where-Object methodFullName -In $rootMethods)
    $rootIds = @($rootEvents | ForEach-Object testId | Sort-Object -Unique)
    $subject = @($run.Events | Where-Object {
        $null -eq $_.PSObject.Properties['isHarness'] -or -not [bool]$_.isHarness
    })
    $expectedPrefix = "$($framework.ToLowerInvariant()):"
    $wrongPrefix = @($rootIds | Where-Object { -not $_.StartsWith($expectedPrefix, [StringComparison]::Ordinal) })
    $noTest = @($subject | Where-Object testId -CEQ '(no-test)')
    $uncorrelated = @($subject | Where-Object testId -NotIn $rootIds)
    $subjectRoots = @($subject | Where-Object { [int]$_.callDepth -eq 0 })
    $wrongResolution = @($subject | Where-Object filePathResolution -CNE 'debugInfo')
    $wrongSource = @($subject | Where-Object filePath -CNE 'src/subject.js')
    if ($rootEvents.Count -ne 3 -or $rootEvents.Count -ne $runner.Executed) {
        throw "$framework derived root count mismatch: runner=$($runner.Executed) roots=$($rootEvents.Count) expected=3"
    }
    if ($rootIds.Count -ne 3 -or $wrongPrefix.Count -ne 0) {
        throw "$framework root identity mismatch: distinct=$($rootIds.Count) wrongPrefix=$($wrongPrefix.Count) expectedPrefix=$expectedPrefix"
    }
    if ($subject.Count -eq 0 -or $noTest.Count -ne 0 -or $uncorrelated.Count -ne 0 -or
        $subjectRoots.Count -ne 0 -or $wrongResolution.Count -ne 0 -or $wrongSource.Count -ne 0) {
        throw "$framework subject correlation tripwire failed: events=$($subject.Count) noTest=$($noTest.Count) uncorrelated=$($uncorrelated.Count) depth0=$($subjectRoots.Count) wrongResolution=$($wrongResolution.Count) wrongSource=$($wrongSource.Count)"
    }
    return [pscustomobject]@{
        Framework = $framework
        RunnerExecuted = $runner.Executed
        RunnerPassed = $runner.Passed
        DerivedRoots = $rootEvents.Count
        DistinctRoots = $rootIds.Count
        SubjectEvents = $subject.Count
        NoTest = $noTest.Count
        Uncorrelated = $uncorrelated.Count
        SubjectDepthZero = $subjectRoots.Count
        WrongResolution = $wrongResolution.Count
        WrongSource = $wrongSource.Count
        ProcessTraces = $run.ProcessTraces
        WriterWritten = $run.WriterWritten
        Modules = $run.Modules
    }
}

function Invoke-Framework([ValidateSet('Jest', 'Vitest')] [string]$framework) {
    $runDirectory = Join-Path $results $framework.ToLowerInvariant()
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $trace = Join-Path $runDirectory 'run.ndjson'
    $report = Join-Path $runDirectory 'runner-report.json'
    $oldTrace = $env:REALDIFF_TRACE
    $oldReport = $env:REALDIFF_RUNNER_REPORT
    $oldNamespaces = $env:REALDIFF_NAMESPACES
    $oldNodeRoot = $env:REALDIFF_NODE_ROOT
    $oldNodeOptions = $env:NODE_OPTIONS
    try {
        $env:REALDIFF_TRACE = $trace
        $env:REALDIFF_RUNNER_REPORT = $report
        $env:REALDIFF_NAMESPACES = 'src'
        $env:REALDIFF_NODE_ROOT = $tracer
        $env:NODE_OPTIONS = "--require=$register"
        Push-Location $sample
        try {
            if ($framework -ceq 'Jest') {
                $cli = Join-Path $sample 'node_modules/jest/bin/jest.js'
                $integration = New-JestIntegration (Join-Path $sample '.realdiff-jest')
                $env:NODE_OPTIONS = "--require=$register --require=$($integration.Bridge)"
                & node $cli --config $integration.Config --runInBand --json --outputFile $report |
                    ForEach-Object { Write-Host $_ }
            } else {
                $cli = Join-Path $sample 'node_modules/vitest/vitest.mjs'
                $integration = New-VitestIntegration (Join-Path $sample '.realdiff-vitest')
                $env:NODE_OPTIONS = "--require=$register --require=$($integration.Bridge)"
                & node $cli run --config $integration.Config --maxWorkers=1 --no-file-parallelism --reporter=json --outputFile=$report |
                    ForEach-Object { Write-Host $_ }
            }
        } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "$framework failed with exit code $LASTEXITCODE" }
    } finally {
        $env:REALDIFF_TRACE = $oldTrace
        $env:REALDIFF_RUNNER_REPORT = $oldReport
        $env:REALDIFF_NAMESPACES = $oldNamespaces
        $env:REALDIFF_NODE_ROOT = $oldNodeRoot
        $env:NODE_OPTIONS = $oldNodeOptions
    }
    $runner = Get-RunnerCounts $report $framework
    $run = Read-TraceRun $runDirectory $framework
    return Assert-Correlation $run $runner $framework
}

try {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    Copy-Sample $sample
    Write-Host '=== clean Node test-framework install ===' -ForegroundColor Cyan
    & npm ci --prefix $sample --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Node test-framework npm ci failed' }

    Write-Host '=== Jest correlation proof ===' -ForegroundColor Cyan
    $jest = Invoke-Framework 'Jest'
    Write-Host '=== Vitest correlation proof ===' -ForegroundColor Cyan
    $vitest = Invoke-Framework 'Vitest'

    Write-Host '=== Node test correlation report ===' -ForegroundColor Green
    foreach ($proof in @($jest, $vitest)) {
        Write-Host "  $($proof.Framework) runner executed/passed       : $($proof.RunnerExecuted) / $($proof.RunnerPassed)"
        Write-Host "  $($proof.Framework) derived/distinct roots       : $($proof.DerivedRoots) / $($proof.DistinctRoots)"
        Write-Host "  $($proof.Framework) subject events               : $($proof.SubjectEvents)"
        Write-Host "  $($proof.Framework) tripwires no-test/uncorrelated/depth0/source/path: $($proof.NoTest) / $($proof.Uncorrelated) / $($proof.SubjectDepthZero) / $($proof.WrongResolution) / $($proof.WrongSource)"
        Write-Host "  $($proof.Framework) process traces/written/modules: $($proof.ProcessTraces) / $($proof.WriterWritten) / $($proof.Modules)"
    }
} finally {
    if ($ownsWork -and -not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "correlation work retained at $work" }
}