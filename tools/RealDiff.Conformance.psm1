Set-StrictMode -Version Latest

$script:RequiredDigestProofs = @(
    'NoUserCodeInvoked',
    'CyclesTerminate',
    'ReferenceTopology',
    'UnorderedCollectionsStable',
    'TimeAndIdentityNormalized',
    'BlocklistBeforeRecursion',
    'DepthMarker',
    'TruncationMarker',
    'UnreadableFieldMarker',
    'BeyondRenderedCap'
)

function Get-PropertyValue {
    param(
        [Parameter(Mandatory)] [object]$InputObject,
        [Parameter(Mandatory)] [string]$Name,
        [object]$Default = $null
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Read-RealDiffConformanceRun {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string]$Path)

    $resolvedPath = (Resolve-Path $Path -ErrorAction Stop).Path
    $traceFiles = @(Get-ChildItem $resolvedPath -File -Filter '*.ndjson' |
        Where-Object { $_.Name -notlike '*.manifest.ndjson' } |
        Sort-Object FullName)
    $manifestFiles = @(Get-ChildItem $resolvedPath -File -Filter '*.manifest.ndjson' | Sort-Object FullName)

    if ($traceFiles.Count -eq 0) { throw "Conformance run has no trace files: $resolvedPath" }

    $events = @($traceFiles | ForEach-Object {
        Get-Content $_.FullName | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json }
    })
    $manifestRecords = @($manifestFiles | ForEach-Object {
        Get-Content $_.FullName | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json }
    })

    return [pscustomobject]@{
        Path = $resolvedPath
        Events = $events
        ManifestRecords = $manifestRecords
        TraceFiles = @($traceFiles | ForEach-Object { $_.FullName })
        ManifestFiles = @($manifestFiles | ForEach-Object { $_.FullName })
    }
}

function Get-SubjectKeyMap {
    param([Parameter(Mandatory)] [object[]]$Events)

    $keys = @{}
    foreach ($event in $Events) {
        if ([bool](Get-PropertyValue $event 'isHarness' $false)) { continue }

        $testId = [string](Get-PropertyValue $event 'testId' '')
        $method = [string](Get-PropertyValue $event 'methodFullName' '')
        $key = @($testId, $method) | ConvertTo-Json -Compress
        if (-not $keys.ContainsKey($key)) {
            $keys[$key] = [Collections.Generic.List[object]]::new()
        }

        $keys[$key].Add($event)
    }

    return $keys
}

function Test-SequenceEqual {
    param(
        [Parameter(Mandatory)] [object[]]$Left,
        [Parameter(Mandatory)] [object[]]$Right
    )

    if ($Left.Count -ne $Right.Count) { return $false }
    for ($index = 0; $index -lt $Left.Count; $index++) {
        if ($Left[$index] -cne $Right[$index]) { return $false }
    }

    return $true
}

function Test-SourcePathMatch {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$Patterns
    )

    foreach ($pattern in $Patterns) {
        if ($Path -match $pattern) { return $true }
    }

    return $false
}

function Assert-DigestProofs {
    param(
        [Parameter(Mandatory)] [object]$Run,
        [Parameter(Mandatory)] [scriptblock]$Evaluator,
        [Parameter(Mandatory)] [string]$Label
    )

    $results = @(& $Evaluator $Run)
    foreach ($proofName in $script:RequiredDigestProofs) {
        $proof = @($results | Where-Object { [string](Get-PropertyValue $_ 'Name' '') -ceq $proofName })
        if ($proof.Count -ne 1) {
            throw "Digest proofs ($Label): expected exactly one '$proofName' result, got $($proof.Count)"
        }

        if (-not [bool](Get-PropertyValue $proof[0] 'Passed' $false)) {
            $detail = [string](Get-PropertyValue $proof[0] 'Detail' 'no detail')
            throw "Digest proof failed ($Label/$proofName): $detail"
        }
    }

    return $results
}

function Assert-RealDiffConformanceRuns {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$FirstRun,
        [Parameter(Mandatory)] [string]$SecondRun,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int]$MinimumMatchedKeys,
        [Parameter(Mandatory)] [string[]]$UsableSourceResolutions,
        [Parameter(Mandatory)] [string[]]$ReferenceSourcePathPatterns,
        [Parameter(Mandatory)] [scriptblock]$DigestProofEvaluator
    )

    $first = Read-RealDiffConformanceRun $FirstRun
    $second = Read-RealDiffConformanceRun $SecondRun
    $firstKeys = Get-SubjectKeyMap $first.Events
    $secondKeys = Get-SubjectKeyMap $second.Events
    $matchedKeys = @($firstKeys.Keys | Where-Object { $secondKeys.ContainsKey($_) })

    if ($matchedKeys.Count -lt $MinimumMatchedKeys) {
        throw "Matched-keys guard failed: $($matchedKeys.Count) matched, minimum is $MinimumMatchedKeys"
    }

    $firstMethods = @($first.Events | Where-Object { -not [bool](Get-PropertyValue $_ 'isHarness' $false) } |
        ForEach-Object { [string](Get-PropertyValue $_ 'methodFullName' '') } | Sort-Object -Unique)
    $secondMethods = @($second.Events | Where-Object { -not [bool](Get-PropertyValue $_ 'isHarness' $false) } |
        ForEach-Object { [string](Get-PropertyValue $_ 'methodFullName' '') } | Sort-Object -Unique)
    if (-not (Test-SequenceEqual $firstMethods $secondMethods)) {
        throw 'Method-set guard failed: subject method sets differ'
    }

    foreach ($key in $matchedKeys) {
        $firstCalls = @($firstKeys[$key])
        $secondCalls = @($secondKeys[$key])
        if ($firstCalls.Count -ne $secondCalls.Count) {
            throw "Event-count guard failed for ${key}: $($firstCalls.Count) vs $($secondCalls.Count)"
        }

        $firstOrdinals = @($firstCalls | ForEach-Object { [int](Get-PropertyValue $_ 'ordinal' -1) } | Sort-Object)
        $secondOrdinals = @($secondCalls | ForEach-Object { [int](Get-PropertyValue $_ 'ordinal' -1) } | Sort-Object)
        $expectedOrdinals = @(0..($firstCalls.Count - 1))
        if (-not (Test-SequenceEqual $firstOrdinals $expectedOrdinals) -or
            -not (Test-SequenceEqual $secondOrdinals $expectedOrdinals) -or
            -not (Test-SequenceEqual $firstOrdinals $secondOrdinals)) {
            throw "Ordinal-sequence guard failed for $key"
        }
    }

    $tripwire = @{}
    foreach ($item in @(@{ Label = 'first'; Run = $first }, @{ Label = 'second'; Run = $second })) {
        $subject = @($item.Run.Events | Where-Object { -not [bool](Get-PropertyValue $_ 'isHarness' $false) })
        $unusable = @($subject | Where-Object {
            $resolution = [string](Get-PropertyValue $_ 'filePathResolution' '')
            $path = [string](Get-PropertyValue $_ 'filePath' '')
            $resolution -notin $UsableSourceResolutions -or [string]::IsNullOrWhiteSpace($path)
        })
        if ($unusable.Count -ne 0) {
            throw "Source-resolution tripwire failed ($($item.Label)): $($unusable.Count) unusable subject event(s)"
        }

        $subjectRoots = @($subject | Where-Object { [int](Get-PropertyValue $_ 'callDepth' -1) -eq 0 })
        if ($subjectRoots.Count -ne 0) {
            throw "Source-resolution tripwire failed ($($item.Label)): $($subjectRoots.Count) subject depth-0 event(s)"
        }

        $wrongSource = @($subject | Where-Object {
            -not (Test-SourcePathMatch ([string](Get-PropertyValue $_ 'filePath' '')) $ReferenceSourcePathPatterns)
        })
        if ($wrongSource.Count -ne 0) {
            throw "Source-resolution tripwire failed ($($item.Label)): $($wrongSource.Count) event(s) mapped outside reference sources"
        }

        $tripwire[$item.Label] = [pscustomobject]@{
            SubjectEvents = $subject.Count
            UnusableSourceEvents = $unusable.Count
            SubjectRoots = $subjectRoots.Count
            WrongSourceEvents = $wrongSource.Count
        }
    }

    $firstDigestProofs = @(Assert-DigestProofs $first $DigestProofEvaluator 'first')
    $secondDigestProofs = @(Assert-DigestProofs $second $DigestProofEvaluator 'second')

    return [pscustomobject]@{
        MatchedKeys = $matchedKeys.Count
        SubjectMethods = $firstMethods.Count
        FirstSubjectEvents = $tripwire.first.SubjectEvents
        SecondSubjectEvents = $tripwire.second.SubjectEvents
        UnusableSourceEvents = $tripwire.first.UnusableSourceEvents + $tripwire.second.UnusableSourceEvents
        SubjectRoots = $tripwire.first.SubjectRoots + $tripwire.second.SubjectRoots
        WrongSourceEvents = $tripwire.first.WrongSourceEvents + $tripwire.second.WrongSourceEvents
        DigestProofsPerRun = $firstDigestProofs.Count
    }
}

function Resolve-CargoExecutable {
    $command = Get-Command 'cargo' -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $exeName = if ($IsWindows) { 'cargo.exe' } else { 'cargo' }
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:CARGO_HOME) {
        $candidates.Add((Join-Path $env:CARGO_HOME "bin/$exeName"))
    }
    if ($env:USERPROFILE) {
        $candidates.Add((Join-Path $env:USERPROFILE ".cargo/bin/$exeName"))
    }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate -PathType Leaf)) { return $candidate }
    }
    return 'cargo'
}

function Invoke-RealDiffEngineConformance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$FirstRun,
        [Parameter(Mandatory)] [string]$SecondRun,
        [string]$BaseRoot,
        [string]$PrRoot
    )

    $output = Join-Path ([IO.Path]::GetTempPath()) ("realdiff-conformance-{0}.json" -f [Guid]::NewGuid().ToString('N'))
    try {
        $repo = Split-Path -Parent $PSScriptRoot
        $manifest = Join-Path $repo 'src/RealDiff.Engine.Rust/Cargo.toml'
        $cargo = Resolve-CargoExecutable
        & $cargo build --release --locked --manifest-path $manifest
        if ($LASTEXITCODE -ne 0) { throw "Rust engine build failed with exit code $LASTEXITCODE" }
        $fileName = if ($IsWindows) { 'realdiff-engine.exe' } else { 'realdiff-engine' }
        $engine = Join-Path $repo "src/RealDiff.Engine.Rust/target/release/$fileName"
        if (-not (Test-Path $engine -PathType Leaf)) { throw "Rust engine binary was not found: $engine" }
        $arguments = @(
            'stream-diff', '--base1', $FirstRun, '--base2', $SecondRun, '--base3', $FirstRun,
            '--pr', $SecondRun, '--out', $output
        )
        if (-not [string]::IsNullOrWhiteSpace($BaseRoot)) { $arguments += @('--base-root', $BaseRoot) }
        if (-not [string]::IsNullOrWhiteSpace($PrRoot)) { $arguments += @('--pr-root', $PrRoot) }

        $engineOutput = @(& $engine @arguments 2>&1)
        $engineOutput | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { throw "Engine conformance comparison failed with exit code $LASTEXITCODE" }

        $document = Get-Content $output -Raw | ConvertFrom-Json
        if ($document.counts.rawDifferences -ne 0 -or $document.counts.remainingDivergences -ne 0 -or $document.divergences.Count -ne 0) {
            throw "Engine conformance comparison found divergences: raw=$($document.counts.rawDifferences), remaining=$($document.counts.remainingDivergences)"
        }

        return [pscustomobject]@{
            MatchedKeys = [int]$document.counts.matchedKeys
            RawDifferences = [int]$document.counts.rawDifferences
            RemainingDivergences = [int]$document.counts.remainingDivergences
        }
    }
    finally {
        Remove-Item $output -Force -ErrorAction SilentlyContinue
    }
}

Export-ModuleMember -Function @(
    'Read-RealDiffConformanceRun',
    'Assert-RealDiffConformanceRuns',
    'Invoke-RealDiffEngineConformance'
)