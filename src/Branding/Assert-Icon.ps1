param([string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$expected = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'App.ico') -Algorithm SHA256).Hash
$paths = @(Join-Path $PSScriptRoot '../OpenRevo.Native/App.ico')
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot '../OpenRevo.Cpp')) { $paths += Join-Path $PSScriptRoot '../OpenRevo.Cpp/App.ico' }
if ($PayloadDirectory) { $paths += Join-Path $PayloadDirectory 'App.ico' }
foreach ($path in $paths) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) {
        throw "Formal OpenRevo icon mismatch: $path. See src/Branding/README.md."
    }
}

