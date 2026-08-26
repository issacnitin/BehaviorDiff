Set-StrictMode -Version Latest

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

function Get-RealDiffEngine {
    [CmdletBinding()]
    param()

    $repo = Split-Path -Parent $PSScriptRoot
    $manifest = Join-Path $repo 'src/RealDiff.Engine.Rust/Cargo.toml'
    $cargo = Resolve-CargoExecutable
    & $cargo build --release --locked --manifest-path $manifest | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Rust engine build failed with exit code $LASTEXITCODE"
    }

    $fileName = if ($IsWindows) { 'realdiff-engine.exe' } else { 'realdiff-engine' }
    $engine = Join-Path $repo "src/RealDiff.Engine.Rust/target/release/$fileName"
    if (-not (Test-Path $engine -PathType Leaf)) {
        throw "Rust engine binary was not found: $engine"
    }

    return $engine
}

Export-ModuleMember -Function 'Get-RealDiffEngine'
