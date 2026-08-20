$ErrorActionPreference = 'Stop'
$root = "$env:USERPROFILE\.nuget\packages"
$asms = @(
    (Get-ChildItem "$root\modelcontextprotocol\2.1.0\lib" -Recurse -Filter 'ModelContextProtocol.dll' | Select-Object -First 1),
    (Get-ChildItem "$root\modelcontextprotocol.core\2.1.0\lib" -Recurse -Filter '*.dll' | Select-Object -First 1)
) | Where-Object { $_ }

foreach ($a in $asms) {
    Write-Host "=== $($a.Name) ==="
    $asm = [System.Reflection.Assembly]::LoadFrom($a.FullName)
    $asm.GetExportedTypes() |
        Where-Object { $_.Name -match 'McpServer|Tool|Stdio|Transport' } |
        Sort-Object FullName | Select-Object -First 25 | ForEach-Object { "  $($_.FullName)" }
}

Write-Host ''
Write-Host '=== extension methods for registering a stdio server ==='
foreach ($a in $asms) {
    $asm = [System.Reflection.Assembly]::LoadFrom($a.FullName)
    $asm.GetExportedTypes() | Where-Object { $_.IsAbstract -and $_.IsSealed } | ForEach-Object {
        $t = $_
        $t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static) |
            Where-Object { $_.Name -match 'AddMcpServer|WithStdio|WithTools|WithToolsFromAssembly' } |
            ForEach-Object { "  {0}.{1}({2})" -f $t.Name, $_.Name, (($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ', ') }
    }
}

Write-Host ''
Write-Host '=== tool attributes ==='
foreach ($a in $asms) {
    $asm = [System.Reflection.Assembly]::LoadFrom($a.FullName)
    $asm.GetExportedTypes() | Where-Object { $_.Name -match 'Attribute' -and $_.Name -match 'Tool|Server' } |
        ForEach-Object { "  $($_.FullName)" }
}
