$ErrorActionPreference='Stop'
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
    & "$vc/bin/Hostx64/x86/cl.exe" /nologo /std:c++20 /O2 /MT /W4 /WX /LD /guard:cf /Brepro (Join-Path $PSScriptRoot 'FolderFilter.cpp') "/Fo$PSScriptRoot/FolderFilter.obj" /link "/DEF:$PSScriptRoot/FolderFilter.def" "/OUT:$PSScriptRoot/InstallerFolderFilter.dll" "/IMPLIB:$PSScriptRoot/FolderFilter.lib" /DYNAMICBASE /NXCOMPAT /Brepro
    if($LASTEXITCODE -ne 0){throw 'Installer directory filter build failed.'}
} finally {$env:PATH=$savedPath;$env:INCLUDE=$savedInclude;$env:LIB=$savedLib}
