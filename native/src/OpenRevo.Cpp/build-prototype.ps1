param(
    [switch]$Check,
    [switch]$SmokeCheck,
    [string]$MSBuildPath = 'D:/DevTools/VSBuildTools/MSBuild/Current/Bin/MSBuild.exe',
    [string]$NuGetPackages = 'D:/DevTools/NuGet/packages',
    [string]$SdkBin = 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64'
)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
& (Join-Path $source '../Branding/Assert-Icon.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $source '../../../..'))
$evidence = Join-Path $source '../../evidence/cpp-winrt-migration'
$cppPackage = Join-Path $root 'work/cpp-winrt-packages/microsoft.windows.cppwinrt/3.0.260818.1'
$packageFile = Join-Path $root 'work/cpp-winrt-packages/microsoft.windows.cppwinrt.3.0.260818.1.nupkg'
$packageHash = 'A8993608AE9263A8288E1A1D9EE2684A5632F4ACC59E1E89DDD86B3B889A7367'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
if (!(Test-Path -LiteralPath $packageFile)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $packageFile) | Out-Null
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.cppwinrt/3.0.260818.1/microsoft.windows.cppwinrt.3.0.260818.1.nupkg' -OutFile $packageFile
}
if ((Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash -ne $packageHash) { throw 'C++/WinRT package hash mismatch.' }
if (!(Test-Path -LiteralPath "$cppPackage/build/native/Microsoft.Windows.CppWinRT.targets")) {
    Expand-Archive -LiteralPath $packageFile -DestinationPath $cppPackage -Force
}
$pinned = [ordered]@{
    'microsoft.windowsappsdk.base' = '2.0.4'
    'microsoft.windowsappsdk.foundation' = '2.3.12'
    'microsoft.windowsappsdk.interactiveexperiences' = '2.1.9'
    'microsoft.windowsappsdk.winui' = '2.3.9'
    'microsoft.windowsappsdk.runtime' = '2.5.1'
    'microsoft.windows.sdk.buildtools' = '10.0.28000.2705'
    'microsoft.web.webview2' = '1.0.3719.77'
}
foreach ($name in $pinned.Keys) {
    if (!(Test-Path -LiteralPath (Join-Path $NuGetPackages "$name/$($pinned[$name])"))) { throw "Missing pinned native build package: $name/$($pinned[$name])" }
}
$pinned['microsoft.windows.cppwinrt'] = '3.0.260818.1'
$pinned | ConvertTo-Json | Set-Content (Join-Path $evidence 'packages.json') -Encoding utf8
foreach ($project in @('OpenRevo.Cpp.vcxproj', 'OpenRevo.Cpp.Checks.vcxproj')) {
    $log = if ($project -eq 'OpenRevo.Cpp.vcxproj') { 'build.log' } else { 'checks-build.log' }
    & $MSBuildPath (Join-Path $source $project) /p:Configuration=Release /p:Platform=x64 "/p:OpenRevoNuGetRoot=$NuGetPackages/" "/p:CppWinRTPackageRoot=$cppPackage/" /m:1 /v:quiet /nologo "/flp:logfile=$evidence/$log;verbosity=normal;encoding=UTF-8"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
$output = Join-Path $source 'bin/Release'
Copy-Item -LiteralPath (Join-Path $source 'Assets') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $source 'App.ico') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $source '../../LICENSE') -Destination $output -Force
$bridge = Join-Path $source '../../app-0.9.6-final/openrevo_native_bridge.dll'
if ((Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash -ne '92FB8E7F0A27F05B82F7C50693F7917C0CC9F21754932F5613DC902D11FB8EFC') { throw 'Baseline bridge changed. Read changes before updating the pin.' }
Copy-Item -LiteralPath $bridge -Destination $output -Force
# Fresh PRI input prevents re-indexing a previous PRI or unrelated EXE/DLL files.
$priInput = Join-Path $source ('obj/pri-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $priInput | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'Assets') -Destination $priInput -Recurse
Copy-Item -LiteralPath (Join-Path $output 'App.xbf') -Destination $priInput
Copy-Item -LiteralPath (Join-Path $source 'App.ico') -Destination $priInput
$priConfig = Join-Path $source 'obj/Release/priconfig.xml'
& "$SdkBin/makepri.exe" createconfig /cf $priConfig /dq lang-zh-CN /o *> (Join-Path $evidence 'pri-config.log')
if ($LASTEXITCODE -ne 0) { throw 'PRI configuration failed.' }
& "$SdkBin/makepri.exe" new /pr $priInput /cf $priConfig /of "$output/resources.pri" /in OpenRevo.Cpp /o *> (Join-Path $evidence 'pri-build.log')
if ($LASTEXITCODE -ne 0) { throw 'PRI generation failed.' }
if ($Check -or $SmokeCheck) {
    $checksOutput = & (Join-Path $source 'bin/Checks/OpenRevo.Cpp.Checks.exe') ([IO.Path]::GetFullPath($bridge))
    if ($LASTEXITCODE -ne 0) { throw 'Native boundary checks failed.' }
    $checksOutput | Set-Content (Join-Path $evidence 'checks.json') -Encoding utf8
    Write-Output $checksOutput
}
if ($SmokeCheck) {
    $report = [IO.Path]::GetFullPath((Join-Path $evidence ('smoke-' + [Guid]::NewGuid().ToString('N') + '.json')))
    $process = Start-Process -FilePath "$output/OpenRevo.Cpp.exe" -ArgumentList @('--smoke-check', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(30000)) { throw "Hidden check timed out; PID=$($process.Id). No process was killed." }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw "Hidden check failed with exit code $($process.ExitCode); report=$report" }
    $smoke = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (!$smoke.passed) { throw "Hidden check reported a failure: $report" }
    if (@($smoke.modules | Where-Object { $_ -match '^(coreclr|clr|hostfxr|hostpolicy|mscoree)\.dll$' }).Count) { throw 'CLR host detected in native application.' }
    Copy-Item -LiteralPath $report -Destination (Join-Path $evidence 'smoke.json') -Force
    Write-Output 'Hidden WinUI resource/frame check passed. No visible preview was opened.'
}
Write-Output "Prototype built: $output/OpenRevo.Cpp.exe"

