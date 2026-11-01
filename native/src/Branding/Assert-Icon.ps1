param([string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$expected = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'App.ico') -Algorithm SHA256).Hash
$paths = @('../OpenRevo.Native/App.ico','../OpenRevo.Cpp/App.ico') | ForEach-Object { Join-Path $PSScriptRoot $_ }
if ($PayloadDirectory) { $paths += Join-Path $PayloadDirectory 'App.ico' }
foreach ($path in $paths) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) {
        throw "Formal OpenRevo icon mismatch: $path. See src/Branding/README.md."
    }
}
