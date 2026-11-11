# Makes the sandbag wall's model and textures in assets\models. The mod reads them directly.
param(
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $root 'assets\models'
New-Item -ItemType Directory -Force $out | Out-Null
& $Blender -b --factory-startup --python (Join-Path $PSScriptRoot 'make_sandbags.py') -- $out | Select-String 'SANDBAGS|Traceback|Error'
if ($LASTEXITCODE -ne 0) { throw 'Blender failed.' }
& $Python (Join-Path $PSScriptRoot 'textures.py') $out
if ($LASTEXITCODE -ne 0) { throw 'Making the textures failed.' }
Get-ChildItem $out -Filter 'sandbags*' | ForEach-Object { "$($_.Name): $([math]::Round($_.Length / 1KB)) KB" }
