#requires -Version 7.0
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/BehaviorDiff.Java.Agent/pom.xml'
$target = Join-Path $repo 'src/BehaviorDiff.Java.Agent/target'
$agent = Join-Path $target 'behaviordiff-java-agent-0.2.0-SNAPSHOT.jar'
$work = Join-Path ([IO.Path]::GetTempPath()) 'behaviordiff-java-emitter'

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $work | Out-Null

& mvn -f $project clean package
if ($LASTEXITCODE -ne 0) { throw 'Java agent package failed' }

$trace = Join-Path $work 'run.ndjson'
& java `
    --add-opens java.base/java.util=ALL-UNNAMED `
    "-javaagent:$agent=include=sample.emitter;trace=$trace" `
    -cp (Join-Path $target 'test-classes') `
    sample.emitter.EmitterMain
if ($LASTEXITCODE -ne 0) { throw "Java emitter fixture failed: $LASTEXITCODE" }

$traceFile = Get-ChildItem $work -Filter 'run.*.ndjson' |
    Where-Object Name -NotLike '*.manifest.ndjson' | Select-Object -First 1
$manifestFile = Get-ChildItem $work -Filter 'run.*.manifest.ndjson' | Select-Object -First 1
if (-not $traceFile -or -not $manifestFile) { throw 'Java trace or manifest was not produced' }

$events = @(Get-Content $traceFile.FullName | ForEach-Object { $_ | ConvertFrom-Json })
$records = @(Get-Content $manifestFile.FullName | ForEach-Object { $_ | ConvertFrom-Json })
$run = @($records | Where-Object kind -eq 'run')
$modules = @($records | Where-Object kind -eq 'assembly')
$members = @($records | Where-Object kind -eq 'member')
$writer = $records | Where-Object kind -eq 'writer' | Select-Object -First 1

if ($run.Count -ne 1 -or $run[0].schema -ne 'behaviordiff.trace/1' -or $run[0].language -ne 'java') {
    throw 'Java run metadata is invalid'
}
foreach ($module in $modules) {
    $moduleMembers = @($members | Where-Object assembly -eq $module.assembly)
    if ($module.discoveredMembers -ne $module.patchedMembers + $module.skippedMembers `
        -or $module.discoveredMembers -ne $moduleMembers.Count `
        -or $module.patchFailedMembers -ne 0) {
        throw "Java module does not reconcile: $($module.assembly)"
    }
}
if ($writer.enqueued -ne $events.Count -or $writer.written -ne $events.Count -or $writer.dropped -ne 0) {
    throw 'Java writer accounting does not reconcile'
}

$root = @($events | Where-Object { $_.methodFullName -like '*EmitterMain.root*' })
$nested = @($events | Where-Object { $_.methodFullName -like '*EmitterMain.nested*' })
$thrown = @($events | Where-Object { $_.methodFullName -like '*EmitterMain.throwsNow*' })
$future = @($events | Where-Object { $_.methodFullName -like '*EmitterMain.future*' })
if ($root.Count -ne 1 -or $nested.Count -ne 1 -or $root[0].testId -ne $nested[0].testId) {
    throw 'Java structural test correlation failed'
}
if ($thrown.Count -ne 1 -or $thrown[0].exceptionType -ne 'java.lang.IllegalStateException' `
    -or $null -ne $thrown[0].returnDigest) {
    throw 'Java exceptional exit contract failed'
}
if ($future.Count -ne 1 -or $future[0].returnRendered -notlike '*settled*') {
    throw 'Java future settlement contract failed'
}
if (@($events | Where-Object { $_.filePathResolution -ne 'debugInfo' -or $_.line -le 0 }).Count -ne 0) {
    throw 'Java source resolution tripwire failed'
}

$engine = Join-Path $repo 'src/BehaviorDiff.Engine/BehaviorDiff.Engine.csproj'
& dotnet run --project $engine -c Release --no-build -- read $traceFile.FullName | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Shared engine could not read Java trace events' }

Write-Host 'Java emitter proof: PASS' -ForegroundColor Green
Write-Host "  events=$($events.Count) members=$($members.Count) modules=$($modules.Count)"
Write-Host "  writer=$($writer.written) thrown=$($thrown.Count) futures=$($future.Count) sourceFailures=0"