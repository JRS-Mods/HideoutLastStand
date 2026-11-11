# Makes the Defences tab's models (the steel barricade and the barbed wire) and their textures in assets\models.
# The mod reads them directly.
param(
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $root 'assets\models'
New-Item -ItemType Directory -Force $out | Out-Null
& $Blender -b --factory-startup --python (Join-Path $PSScriptRoot 'make_defences.py') -- $out | Select-String 'DEFENCES|Traceback|Error'
if ($LASTEXITCODE -ne 0) { throw 'Blender failed.' }
& $Python (Join-Path $PSScriptRoot 'textures.py') $out
if ($LASTEXITCODE -ne 0) { throw 'Making the textures failed.' }
Get-ChildItem $out -Include 'steel*', 'wire*' -Recurse | ForEach-Object { "$($_.Name): $([math]::Round($_.Length / 1KB)) KB" }
