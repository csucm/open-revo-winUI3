param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory, [switch]$NativeHost
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$panel = Join-Path $root 'panel-native'
$hostName = if ($NativeHost) { 'RevoCore.exe' } else { 'OpenRevo.Resident.exe' }
$panelName = if ($NativeHost) { 'Open Revo' } else { 'OpenRevo.ResidentNativePanel' }
$required = @(
    $hostName,
    'OpenRevo.Start.exe', 'OpenRevo.Start.dll', 'OpenRevo.Start.runtimeconfig.json', 'OpenRevo.Start.deps.json',
    'OpenRevo.TaskEntry.exe', 'OpenRevo.ResidentProtocol.dll', 'openrevo_native_bridge.dll', 'App.ico',
    'PluginWorker/OpenRevo.PluginWorker.exe', 'PluginWorker/OpenRevo.PluginWorker.dll',
    'PluginWorker/OpenRevo.PluginWorker.deps.json', 'PluginWorker/OpenRevo.PluginWorker.runtimeconfig.json',
    'PluginWorker/OpenRevo.PluginSdk.dll',
    "panel-native/$panelName.exe", "panel-native/$panelName.dll",
    "panel-native/$panelName.deps.json", "panel-native/$panelName.runtimeconfig.json",
    "panel-native/$panelName.pri", 'panel-native/App.xbf', 'panel-native/OpenRevo.ResidentProtocol.dll'
)
$managedHostFiles = @('OpenRevo.Resident.dll', 'OpenRevo.Resident.runtimeconfig.json', 'OpenRevo.Resident.deps.json')
if ($NativeHost) {
    $required += @('OpenRevo.Tools.exe','OpenRevo.Tools.dll','OpenRevo.Tools.deps.json','OpenRevo.Tools.runtimeconfig.json','OpenRevo.Core.dll')
    foreach ($file in $managedHostFiles) {
        if (Test-Path -LiteralPath (Join-Path $root $file)) { throw "Native payload contains managed resident host: $file" }
    }
} else { $required += $managedHostFiles + @('OpenRevo.Core.dll') }
foreach ($relative in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Missing resident layout file: $relative"
    }
}
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    if ($file.Name -match '^(OpenRevo\.Native\.(exe|dll|pri|.*\.json)|coreclr\.dll|hostfxr\.dll|hostpolicy\.dll|System\.Private\.CoreLib\.dll|.*WebView2.*\.dll)$' -or
        $relative -match '^panel-native/(PluginWorker/|openrevo_native_bridge\.dll$)' -or
        ($relative -match '^OpenRevo\.PluginWorker\.') -or $relative -match '(^|/)(data|resident-data|resident-storage|plugins)/') {
        throw "Unexpected resident layout file: $relative"
    }
}
if ($NativeHost) {
    $exe = Join-Path $root $hostName
    $bytes = [IO.File]::ReadAllBytes($exe)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $optional = $pe + 24
    if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664 -or [BitConverter]::ToUInt16($bytes, $optional) -ne 0x20b -or
        [BitConverter]::ToUInt64($bytes, $optional + 112 + 14*8) -ne 0) { throw 'Resident EXE is not native x64.' }
    $version = (Get-Item -LiteralPath $exe).VersionInfo
    if ($version.ProductVersion -ne '0.7.0' -or $version.FileVersion -ne '1.0.5') { throw 'Native resident version is not 0.7.0 / 1.0.5.' }
    foreach ($relative in @('OpenRevo.Start.exe','OpenRevo.Tools.exe','PluginWorker/OpenRevo.PluginWorker.exe',"panel-native/$panelName.exe")) {
        $pairedVersion=(Get-Item -LiteralPath (Join-Path $root $relative)).VersionInfo
        if ($pairedVersion.ProductVersion -ne '0.7.0' -or $pairedVersion.FileVersion -ne '1.0.5.0') { throw "Paired version is inconsistent: $relative" }
    }
    $panelVersion = (Get-Item -LiteralPath (Join-Path $panel "$panelName.exe")).VersionInfo
    if ($panelVersion.FileDescription -cne $panelName -or $panelVersion.ProductName -cne $panelName -or
        (Get-ChildItem -LiteralPath $panel -Filter 'OpenRevo.ResidentNativePanel.*')) {
        throw 'Native panel identity or retirement of the former panel is invalid.'
    }
    if ($version.FileDescription -cne "Revo Core" -or $version.ProductName -cne "Revo Core" -or
        (Test-Path -LiteralPath (Join-Path $root 'OpenRevo.Sensors.exe')) -or
        (Test-Path -LiteralPath (Join-Path $root 'OpenRevo.Resident.exe'))) {
        throw 'Native Core identity or sensor retirement is invalid.'
    }
    $hostDependencies = @()
} else {
$hostDependencies = (Get-Content -LiteralPath (Join-Path $root 'OpenRevo.Resident.deps.json') -Raw |
    ConvertFrom-Json).libraries.PSObject.Properties.Name
if ($hostDependencies -match 'WinUI|WindowsAppSDK|Microsoft\.UI\.Xaml') { throw 'The resident host must not load WinUI.' }

}

$protocol = @(foreach ($directory in $root, $panel) {
    (Get-FileHash -LiteralPath (Join-Path $directory 'OpenRevo.ResidentProtocol.dll')).Hash
})
if ($protocol[0] -ne $protocol[1]) { throw 'Host and panel protocols were not built from the same assembly.' }

$entry = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes((Join-Path $root 'OpenRevo.TaskEntry.exe')))
if (!$entry.Contains($hostName) -or !$entry.Contains('--installed-open') -or
    !$entry.Contains('--installed-logon') -or $entry.Contains('OpenRevo.Native.exe')) {
    throw 'The scheduled task entry does not target the resident host.'
}
$manifest = [ordered]@{
    PayloadDirectory = $root
    NativeHost = [bool]$NativeHost
    InstalledEntryStaticVerified = $true
    InstalledEntryRuntimeVerified = $false
    ProtocolSha256 = $protocol[0]
    ResidentDependencies = @($hostDependencies)
    Files = @($files | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            Path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash
        }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'resident-layout.json') -Encoding utf8
