param([string]$DotNet = 'dotnet', [string]$Iscc = 'ISCC.exe', [string]$Cargo = 'cargo', [switch]$Offline)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$native = Join-Path $root 'src/OpenRevo.Native/OpenRevo.Native.csproj'
$checks = Join-Path $root 'src/OpenRevo.Checks/OpenRevo.Checks.csproj'
$version = ([xml](Get-Content -LiteralPath $native -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$app = Join-Path $root ('app-' + $version + '-final')
$evidence = Join-Path $root ('evidence/package-' + $version)
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$offlineRestore = @(); $offlineCargo = @()
if ($Offline) { $offlineRestore = @('--ignore-failed-sources', '-p:NuGetAudit=false'); $offlineCargo = @('--offline') }
& $DotNet restore $native -p:Platform=x64 -p:SelfContained=false --locked-mode @offlineRestore
if ($LASTEXITCODE -ne 0) { throw 'Native restore failed.' }
& $DotNet restore $checks --locked-mode @offlineRestore
if ($LASTEXITCODE -ne 0) { throw 'Checks restore failed.' }
& $DotNet build $checks -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Checks build failed.' }
& $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') | Set-Content -LiteralPath (Join-Path $evidence 'core-checks.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
& $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --diagnostics-checks (Join-Path $evidence 'diagnostics-checks.json')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics checks failed.' }
& $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --editor-fixes-checks (Join-Path $evidence 'editor-checks.json')
if ($LASTEXITCODE -ne 0) { throw 'Fan editor checks failed.' }
& $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --responsive-layout-checks (Join-Path $evidence 'layout-checks.json')
if ($LASTEXITCODE -ne 0) { throw 'Responsive layout checks failed.' }
& $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --osd-frame-checks (Join-Path $evidence 'osd-frame-checks.json')
if ($LASTEXITCODE -ne 0) { throw 'OSD native frame checks failed.' }
& $DotNet publish $native -c Release -p:Platform=x64 ("-p:OutputPath=bin-package-" + $version + "/") -p:DebugType=None -p:DebugSymbols=false --no-restore --self-contained false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (-not $env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR = Join-Path $root 'core-proof/build' }
& $Cargo build --release --locked @offlineCargo --manifest-path (Join-Path $root 'src/OpenRevo.CoreBridge/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Core bridge build failed.' }
& $Cargo test --release --locked @offlineCargo --manifest-path (Join-Path $root 'src/OpenRevo.CoreBridge/Cargo.toml') | Set-Content -LiteralPath (Join-Path $evidence 'bridge-checks.txt') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Core bridge checks failed.' }
Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'release/openrevo_native_bridge.dll') -Destination (Join-Path $app 'openrevo_native_bridge.dll') -Force
foreach ($required in @('OpenRevo.Native.exe','OpenRevo.Native.dll','OpenRevo.Core.dll','App.xbf','OpenRevo.Native.pri','openrevo_native_bridge.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw ('Missing publish artifact: ' + $required) }
}
Add-Type -AssemblyName System.IO.Compression
$zipPath = Join-Path $root ('native-source-' + $version + '.zip')
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    # Do not recurse into previous output directories: SDK content globs can
    # otherwise collect an unbounded chain of old review images and binaries.
    $sourceFiles = [Collections.Generic.List[string]]::new()
    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push((Join-Path $root 'src'))
    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($file in [IO.Directory]::EnumerateFiles($directory)) { $sourceFiles.Add($file) }
        foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
            if ([IO.Path]::GetFileName($child) -match '^(bin(?:-[^/]+)?|obj|target|node_modules|\.git)$') { continue }
            if (([IO.File]::GetAttributes($child) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Source link is not allowed: ' + $child) }
            $pendingDirectories.Push($child)
        }
    }
    foreach ($sourceFile in ($sourceFiles | Sort-Object)) {
        $file = Get-Item -LiteralPath $sourceFile
        $name = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if ($name -match '^src/OpenRevo\.(Native|Core|Checks)/(bin(?:-[^/]+)?|obj)/' -or $name -match '/(target|node_modules|\.git)/') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name) | Out-Null
    }
    foreach ($name in @('LICENSE','build.ps1','installer.iss','legacy-files.iss','INSTALL-NOTES.txt','SCOPE.md','README.md','CORE-INTEGRATION.md','DIAGNOSTICS.md','TUNING-INTEGRATION.md','TUNING-PRESETS.md','DESKTOP-SERVICES.md','SETTINGS-AND-TRAY.md','UI-FUNCTION-MAP.md','PAWNIO-FEASIBILITY.md','RESPONSIVE-OPTIONS.md',('RELEASE-' + $version + '.md'))) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $name), $name) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }
& $Iscc /Q ("/DAppVersion=" + $version) (Join-Path $root 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed.' }
$setup = Join-Path (Split-Path -Parent $root) ('Open-Revo-' + $version + '-Setup-x64.exe')
Get-FileHash -LiteralPath $setup -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ascii
