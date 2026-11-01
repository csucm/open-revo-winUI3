param([Parameter(Mandatory=$true)][string]$PayloadDirectory)
$ErrorActionPreference='Stop'
$packages=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}
$groups=@(
 @('microsoft.netcore.app.runtime.win-x64','10.0.13','dotnet','LICENSE.TXT','THIRD-PARTY-NOTICES.TXT'),
 @('microsoft.windowsappsdk.winui','2.3.9','winui','license.txt','NOTICE.txt'),
 @('microsoft.windowsappsdk.interactiveexperiences','2.1.9','interactive-experiences','license.txt'),
 @('microsoft.windowsappsdk.runtime','2.5.1','windows-app-sdk','license.txt','NOTICE.txt')
)
foreach($group in $groups){
 $destination=Join-Path $PayloadDirectory ('licenses/'+$group[2]);New-Item -ItemType Directory -Force $destination|Out-Null
 foreach($name in $group[3..($group.Count-1)]){Copy-Item -LiteralPath (Join-Path $packages ($group[0]+'/'+$group[1]+'/'+$name)) -Destination $destination}
}
'Copyright Microsoft Corporation. Microsoft.Windows.SDK.NET.Ref projection license: https://aka.ms/WinSDKLicenseURL' | Set-Content (Join-Path $PayloadDirectory 'licenses/Windows-SDK-projection.txt') -Encoding utf8
