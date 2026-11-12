param([switch]$ResidentBootstrap, [switch]$NativeResident, [string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference='Stop'
$OutputDirectory = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$sdk='C:/Program Files (x86)/Windows Kits/10'
$vswhere='C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $installation){throw 'MSVC x86 build tools are required.'}
$vc=(Get-ChildItem -LiteralPath (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$kit=(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'um/windows.h')} | Sort-Object Name -Descending | Select-Object -First 1).Name
$savedPath=$env:PATH;$savedInclude=$env:INCLUDE;$savedLib=$env:LIB
try {
    $env:PATH="$vc/bin/Hostx64/x86;"+$env:PATH
    $env:INCLUDE="$vc/include;$sdk/Include/$kit/ucrt;$sdk/Include/$kit/um;$sdk/Include/$kit/shared"
    $env:LIB="$vc/lib/x86;$sdk/Lib/$kit/ucrt/x86;$sdk/Lib/$kit/um/x86"
    & "$vc/bin/Hostx64/x86/cl.exe" /nologo /std:c++20 /O2 /MT /W4 /WX /LD /guard:cf /Brepro (Join-Path $PSScriptRoot 'FolderFilter.cpp') "/Fo$OutputDirectory/FolderFilter.obj" /link "/DEF:$PSScriptRoot/FolderFilter.def" "/OUT:$OutputDirectory/InstallerFolderFilter.dll" "/IMPLIB:$OutputDirectory/FolderFilter.lib" /DYNAMICBASE /NXCOMPAT /Brepro
    if($LASTEXITCODE -ne 0){throw 'Installer directory filter build failed.'}
    $env:PATH="$vc/bin/Hostx64/x64;$sdk/bin/$kit/x64;"+$savedPath
    $env:LIB="$vc/lib/x64;$sdk/Lib/$kit/ucrt/x64;$sdk/Lib/$kit/um/x64"
    $taskEntrySource=Join-Path $PSScriptRoot '../src/OpenRevo.Start/TaskEntry.cpp'
    $bootstrap = if ($ResidentBootstrap) { '/DREVOCORE_RESIDENT_BOOTSTRAP' } else { '/UREVOCORE_RESIDENT_BOOTSTRAP' }
    $native = if ($NativeResident) { '/DREVOCORE_NATIVE_HOST' } else { '/UREVOCORE_NATIVE_HOST' }
    & "$vc/bin/Hostx64/x64/cl.exe" /nologo /std:c++20 /O2 /MT /EHsc /W4 /WX /utf-8 /guard:cf /Brepro $bootstrap $native $taskEntrySource "/Fo$OutputDirectory/TaskEntry.obj" /link "/OUT:$OutputDirectory/OpenRevo.TaskEntry.exe" /SUBSYSTEM:WINDOWS /MANIFEST:EMBED /DYNAMICBASE /NXCOMPAT /Brepro user32.lib advapi32.lib shell32.lib ole32.lib uuid.lib
    if($LASTEXITCODE -ne 0){throw 'Scheduled task entry build failed.'}
} finally {$env:PATH=$savedPath;$env:INCLUDE=$savedInclude;$env:LIB=$savedLib}
