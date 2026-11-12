param(
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9_-]{0,63}$')][string]$Id,
    [Parameter(Mandatory)][ValidateLength(1,80)][string]$Name,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{0,63}$')][string]$ProjectName = 'MyPlugin'
)
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination already exists; no files were overwritten.' }
for ($ancestor = [IO.DirectoryInfo]::new($destinationPath); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not accepted.' }
}
New-Item -ItemType Directory -Path $destinationPath | Out-Null
foreach ($extension in @('csproj','cs')) {
    $templateFile = Join-Path $PSScriptRoot ('template/PluginStarter.' + $extension)
    $text = [IO.File]::ReadAllText($templateFile).Replace('PluginStarter', $ProjectName)
    [IO.File]::WriteAllText((Join-Path $destinationPath ($ProjectName + '.' + $extension)), $text, [Text.UTF8Encoding]::new($false))
}
[ordered]@{
    apiMajor=1; id=$Id; name=$Name; version='1.0.0'; entryAssembly=($ProjectName+'.dll');
    entryType=($ProjectName+'.Plugin'); permissions=@('ui.view'); background=$false
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destinationPath 'plugin.json') -Encoding utf8
Write-Output ('Created plugin source: ' + $destinationPath)
Write-Output 'Build with dotnet build -c Release -p:OpenRevoSdkPath=<absolute path to OpenRevo.PluginSdk.dll>.'
Write-Output 'This command does not install, enable or execute the plugin.'
