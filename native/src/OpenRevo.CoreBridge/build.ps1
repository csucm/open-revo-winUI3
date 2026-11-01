param([string]$Cargo = 'cargo', [string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$env:CARGO_TARGET_DIR = Join-Path $root 'core-proof/build'
& $Cargo build --release --locked --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Core bridge build failed.' }
$dll = Join-Path $root 'core-proof/openrevo_native_bridge.dll'
Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'release/openrevo_native_bridge.dll') -Destination $dll -Force
$checks = Join-Path $root 'src/OpenRevo.Checks/OpenRevo.Checks.csproj'
& $DotNet build $checks -c Release
if ($LASTEXITCODE -ne 0) { throw 'Interop client build failed.' }
$client = Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll'
& $DotNet $client --probe-upstream-core $dll
if ($LASTEXITCODE -ne 0) { throw 'Interop check failed.' }
