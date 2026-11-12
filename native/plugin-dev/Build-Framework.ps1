param([string]$DotNet='dotnet', [string]$OutputDirectory, [switch]$BuildPreview)
$ErrorActionPreference='Stop'
$nativeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $nativeRoot '../../work/native-plugin-framework' }
$frameworkOutput = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($frameworkOutput) -and ([IO.File]::GetAttributes($frameworkOutput) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Output is a reparse point.' }
New-Item -ItemType Directory -Path $frameworkOutput -Force | Out-Null
$projects = @('src/OpenRevo.PluginHost/OpenRevo.PluginHost.csproj','src/OpenRevo.PluginWorker/OpenRevo.PluginWorker.csproj',
    'plugin-dev/SystemSummary/SystemSummary.csproj','plugin-dev/HardwareControls/HardwareControls.csproj',
    'plugin-dev/InterfaceInspector/InterfaceInspector.csproj',
    'plugin-dev/IsolationProbe/IsolationProbe.csproj','src/OpenRevo.PluginChecks/OpenRevo.PluginChecks.csproj')
foreach ($project in $projects) {
    $projectPath = Join-Path $nativeRoot $project
    $logName = [IO.Path]::GetFileNameWithoutExtension($projectPath)+'.build.log'
    & $DotNet build $projectPath -c Release -m:1 --nologo --ignore-failed-sources -p:NuGetAudit=false 2>&1 |
        Tee-Object -FilePath (Join-Path $frameworkOutput $logName)
    if ($LASTEXITCODE -ne 0) { throw ('Build failed: '+$project) }
}
$sdkOutput = Join-Path $frameworkOutput 'sdk'
$workerOutput = Join-Path $frameworkOutput 'worker'
New-Item -ItemType Directory -Path $sdkOutput,$workerOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeRoot 'src/OpenRevo.PluginSdk/bin/Release/net10.0/OpenRevo.PluginSdk.dll') -Destination $sdkOutput -Force
$workerBuild = Join-Path $nativeRoot 'src/OpenRevo.PluginWorker/bin/Release/net10.0-windows10.0.26100.0/win-x64'
foreach ($name in @('OpenRevo.PluginWorker.exe','OpenRevo.PluginWorker.dll','OpenRevo.PluginWorker.deps.json','OpenRevo.PluginWorker.runtimeconfig.json','OpenRevo.PluginSdk.dll')) {
    Copy-Item -LiteralPath (Join-Path $workerBuild $name) -Destination $workerOutput -Force
}
foreach ($example in @('SystemSummary','HardwareControls','InterfaceInspector')) {
    $exampleOutput = Join-Path $frameworkOutput ('examples/'+$example)
    New-Item -ItemType Directory -Path $exampleOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $nativeRoot ('plugin-dev/'+$example+'/bin/Release/net10.0/'+$example+'.dll')) -Destination $exampleOutput -Force
    Copy-Item -LiteralPath (Join-Path $nativeRoot ('plugin-dev/'+$example+'/plugin.json')) -Destination $exampleOutput -Force
}
$checksOutput = Join-Path $frameworkOutput 'checks'
$themeOutput = Join-Path $frameworkOutput 'examples/NaiwaTheme'
New-Item -ItemType Directory -Path $themeOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeRoot 'plugin-dev/NaiwaTheme/plugin.json') -Destination $themeOutput -Force
$probeOutput = Join-Path $checksOutput 'probe'
New-Item -ItemType Directory -Path $probeOutput -Force | Out-Null
$checksBuild = Join-Path $nativeRoot 'src/OpenRevo.PluginChecks/bin/Release/net10.0-windows10.0.26100.0'
Get-ChildItem -LiteralPath $checksBuild -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $checksOutput -Force }
Copy-Item -LiteralPath (Join-Path $nativeRoot 'plugin-dev/IsolationProbe/bin/Release/net10.0-windows10.0.26100.0/IsolationProbe.dll') -Destination $probeOutput -Force
Copy-Item -LiteralPath (Join-Path $nativeRoot 'plugin-dev/IsolationProbe/plugin.json') -Destination $probeOutput -Force
if ($BuildPreview) {
    $previewOutput = Join-Path $frameworkOutput 'preview'
    & $DotNet publish (Join-Path $nativeRoot 'src/OpenRevo.Native/OpenRevo.Native.csproj') -c Release -m:1 --nologo `
        -p:Platform=x64 -p:SelfContained=false -p:NativePluginPreviewHost=true -p:IntermediateOutputPath=obj/plugin-preview/ `
        --ignore-failed-sources -p:NuGetAudit=false -o $previewOutput 2>&1 | Tee-Object -FilePath (Join-Path $frameworkOutput 'preview.build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Native plugin design preview build failed.' }
}
[ordered]@{ built='2026-11-07'; previewBuilt=[bool]$BuildPreview; softwareLaunched=$false; pluginsExecuted=$false;
    automatedTestsRun=$false; hardwareActionsRun=$false; installed=$false; published=$false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $frameworkOutput 'build-policy.json') -Encoding utf8
Write-Output ('Framework build output: '+$frameworkOutput)
Write-Output 'Build only: no preview, plugin, test suite or hardware command was executed.'
