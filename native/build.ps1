param([string]$DotNet = 'dotnet', [string]$Iscc = 'ISCC.exe', [string]$Cargo = 'cargo', [switch]$Offline, [switch]$Compact = $true, [ValidatePattern('^[a-z0-9-]+$')][string]$Variant = 'shared', [switch]$RunChecks, [ValidatePattern('^[A-Za-z0-9._-]+$')][string]$OutputName, [switch]$NativePluginFolderEntry = $true, [switch]$Gm7tg0mDiagnostics, [switch]$ResidentLayout, [switch]$StageOnly, [switch]$NativeResident)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ($NativeResident -and !$ResidentLayout) { throw 'NativeResident requires ResidentLayout.' }
if ($StageOnly -and (!$ResidentLayout -or $RunChecks)) { throw 'StageOnly requires ResidentLayout and excludes RunChecks.' }
if ($ResidentLayout -and $Gm7tg0mDiagnostics) { throw 'The GM7TG0M maintenance entry requires the legacy executable.' }
& (Join-Path $root 'src/Branding/Assert-Icon.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$native = Join-Path $root 'src/OpenRevo.Native/OpenRevo.Native.csproj'
$launcher = Join-Path $root 'src/OpenRevo.Start/OpenRevo.Start.csproj'
$checks = Join-Path $root 'src/OpenRevo.Checks/OpenRevo.Checks.csproj'
$version = ([xml](Get-Content -LiteralPath $native -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$app = Join-Path $root ('app-' + $version + '-' + $Variant)
$evidence = Join-Path $root ('evidence/package-' + $version + '-' + $Variant)
$nativeOutput = $app
$consoleExe = 'OpenRevo.Native.exe'
$installerTools = Join-Path $root 'installer'
$residentProperties = @()
$nativeVersionProperties = @()
if ($NativeResident) {
    [xml]$identity = Get-Content -LiteralPath (Join-Path $root 'src/OpenRevo.Release.props') -Raw
    $public = $identity.SelectSingleNode('//PublicReleaseVersion').InnerText
    $internal = $identity.SelectSingleNode('//InternalBuildVersion').InnerText
    if ($public -ne $version -or $internal -notmatch '^\d+\.\d+\.\d+$') { throw 'Native pair version identity is inconsistent.' }
    $nativeVersionProperties = @("-p:PublicReleaseVersion=$public", "-p:InternalBuildVersion=$internal", "-p:AssemblyVersion=$internal.0", "-p:FileVersion=$internal.0", "-p:InformationalVersion=$public", "-p:IncludeSourceRevisionInInformationalVersion=false")
}
if ($ResidentLayout) {
    $stage = Join-Path (Split-Path -Parent $root) ('.build/resident-layout/' + $version + '-' + $Variant + '-' + [Guid]::NewGuid().ToString('N'))
    $app = Join-Path $stage 'app'
    $nativeOutput = Join-Path $app 'panel-native'
    $evidence = Join-Path $stage 'evidence'
    $installerTools = Join-Path $stage 'tools'
    $consoleExe = 'OpenRevo.ResidentNativePanel.exe'
    $residentProperties = @('-p:ResidentPanelHost=true')
    if ($NativeResident) {
        $consoleExe = 'Open Revo.exe'
        $residentProperties += '-p:NativeResidentCore=true'
    }
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
[ordered]@{ testsRequested=[bool]$RunChecks; packagingOnly=(-not ($RunChecks -or $StageOnly)); stageOnly=[bool]$StageOnly; nativeResident=[bool]$NativeResident; residentLayout=[bool]$ResidentLayout; softwareLaunched=$false; nativePluginFolderEntry=[bool]$NativePluginFolderEntry; gm7tg0mDiagnostics=[bool]$Gm7tg0mDiagnostics } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "build-policy.json") -Encoding utf8
$compactProperties = @()
$pluginProperties = @('-p:NativePluginFolderEntry=' + ([bool]$NativePluginFolderEntry).ToString().ToLowerInvariant())
$diagnosticProperties = @('-p:Gm7tg0mDiagnostics=' + ([bool]$Gm7tg0mDiagnostics).ToString().ToLowerInvariant())
# Shared deployment never imports the self-contained trimming experiment.
$offlineRestore = @(); $offlineCargo = @()
if ($Offline) { $offlineRestore = @('--ignore-failed-sources', '-p:NuGetAudit=false'); $offlineCargo = @('--offline') }
& $DotNet restore $native -p:Platform=x64 -p:SelfContained=false --locked-mode -m:1 @offlineRestore @compactProperties @diagnosticProperties @residentProperties
if ($LASTEXITCODE -ne 0) { throw 'Native restore failed.' }
# Tests are opt-in; packaging alone never authorizes running them.
if ($RunChecks) {
    & $DotNet restore $checks --locked-mode @offlineRestore
    if ($LASTEXITCODE -ne 0) { throw 'Checks restore failed.' }
    & $DotNet build $checks -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Checks build failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') | Set-Content -LiteralPath (Join-Path $evidence 'core-checks.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --update-checks (Join-Path $evidence 'update-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'Update checks failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --diagnostics-checks (Join-Path $evidence 'diagnostics-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostics checks failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --editor-fixes-checks (Join-Path $evidence 'editor-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'Fan editor checks failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --responsive-layout-checks (Join-Path $evidence 'layout-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'Responsive layout checks failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --osd-frame-checks (Join-Path $evidence 'osd-frame-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'OSD native frame checks failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --native-extension-checks (Join-Path $evidence 'native-extension-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'Native extension checks failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --preference-persistence-checks (Join-Path $evidence 'preference-checks.json')
    if ($LASTEXITCODE -ne 0) { throw 'Preference checks failed.' }
    & $DotNet restore $native -p:Platform=x64 -p:SelfContained=false --locked-mode @offlineRestore @compactProperties @residentProperties
    if ($LASTEXITCODE -ne 0) { throw 'Native restore failed.' }
    & $DotNet (Join-Path $root 'src/OpenRevo.Checks/bin/Release/net10.0-windows10.0.26100.0/OpenRevo.Checks.dll') --tray-lightweight-checks (Join-Path $evidence 'tray-lightweight-checks.json') (Join-Path $root 'src/Branding/App.ico')
    if ($LASTEXITCODE -ne 0) { throw 'Tray/icon checks failed.' }
}
$publishOutput = "-p:OutputPath=bin-package-$version-$Variant/"
& $DotNet publish $native @nativeVersionProperties @compactProperties @pluginProperties @diagnosticProperties @residentProperties -c Release -m:1 -p:Platform=x64 $publishOutput -p:DebugType=None -p:DebugSymbols=false --no-restore --self-contained false -o $nativeOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if ($NativeResident) {
    $tools = Join-Path $root 'src/OpenRevo.Tools/OpenRevo.Tools.csproj'
    & $DotNet publish $tools @nativeVersionProperties -c Release -m:1 -p:Platform=x64 $publishOutput -p:DebugType=None -p:DebugSymbols=false --self-contained false -o $app @offlineRestore
    if ($LASTEXITCODE -ne 0) { throw 'On-demand tools publish failed.' }
    $worker = Join-Path $root 'src/OpenRevo.PluginWorker/OpenRevo.PluginWorker.csproj'
    & $DotNet publish $worker @nativeVersionProperties -c Release -m:1 -p:Platform=x64 $publishOutput -p:DebugType=None -p:DebugSymbols=false --self-contained false -o (Join-Path $app 'PluginWorker') @offlineRestore
    if ($LASTEXITCODE -ne 0) { throw 'Plugin worker publish failed.' }
    Copy-Item -LiteralPath (Join-Path $nativeOutput 'OpenRevo.ResidentProtocol.dll') -Destination $app
    Copy-Item -LiteralPath (Join-Path $root 'src/Branding/App.ico') -Destination $app
} elseif ($ResidentLayout) {
    $resident = Join-Path $root 'src/OpenRevo.Resident/OpenRevo.Resident.csproj'
    & $DotNet publish $resident -c Release -m:1 -p:Platform=x64 -p:ResidentInstalledHost=true $publishOutput -p:DebugType=None -p:DebugSymbols=false --self-contained false -o $app @offlineRestore
    if ($LASTEXITCODE -ne 0) { throw 'Resident publish failed.' }
}
& $DotNet publish $launcher @nativeVersionProperties -c Release "-p:Version=$version" -p:DebugType=None -p:DebugSymbols=false --self-contained false -o $app @offlineRestore
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
foreach ($runtimeFile in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll')) {
    if (Test-Path -LiteralPath (Join-Path $app $runtimeFile)) { throw ('Shared deployment unexpectedly bundles .NET: ' + $runtimeFile) }
}
foreach ($retiredFile in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Core.Projection.dll','WebView2Loader.dll',
    'OpenRevo.PluginWorker.exe','OpenRevo.PluginWorker.dll','OpenRevo.PluginWorker.deps.json','OpenRevo.PluginWorker.runtimeconfig.json','Assets/Osd')) {
    if (Test-Path -LiteralPath (Join-Path $app $retiredFile)) {
        throw ('Unexpected retired payload: ' + $retiredFile + '. Publish to a fresh variant directory.')
    }
}
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& (Join-Path $root 'src/Branding/Assert-Icon.ps1') -PayloadDirectory $app
& (Join-Path $root 'src/Branding/Assert-EmbeddedIcon.ps1') -Executable (Join-Path $nativeOutput $consoleExe)
if (-not $env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR = Join-Path $root 'core-proof/build' }
& $Cargo build --release --locked @offlineCargo --manifest-path (Join-Path $root 'src/OpenRevo.CoreBridge/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Core bridge build failed.' }
if ($RunChecks) {
    & $Cargo test --release --locked @offlineCargo --manifest-path (Join-Path $root 'src/OpenRevo.CoreBridge/Cargo.toml') | Set-Content -LiteralPath (Join-Path $evidence 'bridge-checks.txt') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Core bridge checks failed.' }
}
Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'release/openrevo_native_bridge.dll') -Destination (Join-Path $app 'openrevo_native_bridge.dll') -Force
if ($NativeResident) {
    Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'release/openrevo-resident.exe') -Destination (Join-Path $app 'RevoCore.exe')

}
$consoleAssembly = [IO.Path]::GetFileNameWithoutExtension($consoleExe)
foreach ($required in @($consoleExe,($consoleAssembly + '.dll'),'OpenRevo.Core.dll','App.xbf',($consoleAssembly + '.pri'))) {
    if (-not (Test-Path -LiteralPath (Join-Path $nativeOutput $required))) { throw ('Missing publish artifact: ' + $required) }
}
& (Join-Path $root 'packaging/Copy-Licenses.ps1') -PayloadDirectory $app
& (Join-Path $root 'installer/build-filter.ps1') -ResidentBootstrap:$ResidentLayout -NativeResident:$NativeResident -OutputDirectory $installerTools
Copy-Item -LiteralPath (Join-Path $installerTools 'OpenRevo.TaskEntry.exe') -Destination $app -Force
if ($ResidentLayout) {
    & (Join-Path $root 'packaging/Assert-ResidentLayout.ps1') -PayloadDirectory $app -EvidenceDirectory $evidence -NativeHost:$NativeResident
    if ($StageOnly) {
        Write-Output "Resident payload: $app"
        return
    }
}
Add-Type -AssemblyName System.IO.Compression
$sourceDirectory = if ($ResidentLayout) { $stage } else { $root }
$zipPath = Join-Path $sourceDirectory ('native-source-' + $version + '-' + $Variant + '.zip')
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
        foreach ($file in [IO.Directory]::EnumerateFiles($directory)) {
            if([IO.Path]::GetFileName($file) -eq 'OpenRevo-square-source.png'){continue}
            $sourceFiles.Add($file)
        }
        foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
            if ([IO.Path]::GetFileName($child) -match '^(OpenRevo\.Cpp|bin(?:-[^/]+)?|obj|target|node_modules|Generated Files|\.git)$') { continue }
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
    # Ship the SDK usage, templates and sample sources alongside the framework.
    $pluginSources = [Collections.Generic.Stack[string]]::new()
    $pluginSources.Push((Join-Path $root 'plugin-dev'))
    while ($pluginSources.Count -gt 0) {
        $directory = $pluginSources.Pop()
        foreach ($file in [IO.Directory]::EnumerateFiles($directory)) {
            if (([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Plugin source link is not allowed: ' + $file) }
            $name = [IO.Path]::GetRelativePath($root, $file).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $name) | Out-Null
        }
        foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
            if ([IO.Path]::GetFileName($child) -match '^(bin(?:-[^/]+)?|obj|target|node_modules|\.git)$') { continue }
            if (([IO.File]::GetAttributes($child) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Plugin source link is not allowed: ' + $child) }
            $pluginSources.Push($child)
        }
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root 'NATIVE-PLUGIN-FRAMEWORK.md'), 'NATIVE-PLUGIN-FRAMEWORK.md') | Out-Null
    foreach ($name in @('packaging/Copy-Licenses.ps1','packaging/Assert-ResidentLayout.ps1','packaging/CompactPackage.targets','packaging/CompactRoots.xml','NATIVE-EXTENSIONS.md','PLUGIN-API-INVENTORY.md','PLUGIN-COMPATIBILITY-OPTIONS.md','RELIABILITY-FIXES.md','PANEL-SWITCH-AND-INSTALL.md','ORIGINAL-INSTALLER-BROWSER.md','PACKAGE-OPTIMIZATION.md','LICENSE','build.ps1','installer.iss','InstallerDirectoryPicker.iss','RuntimePrerequisite.iss','installer/RuntimePrerequisiteChecks.iss','SHARED-RUNTIME-INSTALL.md','installer/FolderFilter.cpp','installer/FolderFilter.def','installer/build-filter.ps1','installer/InstallerFolderFilter.dll','legacy-files.iss','INSTALL-NOTES.txt','SCOPE.md','README.md','CORE-INTEGRATION.md','DIAGNOSTICS.md','TUNING-INTEGRATION.md','TUNING-PRESETS.md','DESKTOP-SERVICES.md','SETTINGS-AND-TRAY.md','UI-FUNCTION-MAP.md','PAWNIO-FEASIBILITY.md','RESPONSIVE-OPTIONS.md',('RELEASE-' + $version + '.md'))) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $name), $name) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }
if (-not $OutputName) {
    $layoutTag = if ($ResidentLayout) { '-resident' } else { '' }
    $OutputName = "Open-Revo-$version-$Variant$layoutTag-Setup-x64"
}
$installerOptions = @()
if ($Compact) { $installerOptions = @('/DCompactPackage=1') }
$payloadOption = if ($ResidentLayout) { $app } else { [IO.Path]::GetFileName($app) }
$sourceOption = if ($ResidentLayout) { $zipPath } else { [IO.Path]::GetFileName($zipPath) }
$installerOptions += @(('/DAppPayloadDir=' + $payloadOption),('/DSourceZipName=' + $sourceOption),('/DOutputName=' + $OutputName))
if ($ResidentLayout) {
    $installerOptions += @('/DResidentLayout=1',('/DInstallerFilterPath=' + (Join-Path $installerTools 'InstallerFolderFilter.dll')))
}
if ($NativeResident) { $installerOptions += @('/DNativeResident=1',('/DInternalBuildVersion=' + $internal + '.0')) }
& $Iscc @installerOptions /Q ("/DAppVersion=" + $version) (Join-Path $root 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed.' }
$setup = Join-Path (Split-Path -Parent $root) ($OutputName + '.exe')
& (Join-Path $root 'src/Branding/Assert-EmbeddedIcon.ps1') -Executable $setup
Get-FileHash -LiteralPath $setup -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ascii

Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ascii








