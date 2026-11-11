# Remakes assets\models from the model in assets. Blender cuts it down and bakes its textures, then they're packed for the game. The mod reads the results directly.
param(
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    [string]$Python = 'python',
    [string]$Pictures = ''
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$model = Join-Path $root 'assets\d-breach explosive prop 3d model.glb'
$out = Join-Path $root 'assets\models'
$work = Join-Path $root 'dist\charge_work'
New-Item -ItemType Directory -Force $out, $work | Out-Null

$arguments = @('-b', '--factory-startup', '--python', (Join-Path $PSScriptRoot 'optimise.py'), '--', $model, $out, $work)
if ($Pictures) { $arguments += $Pictures }
& $Blender @arguments | Select-String 'OPTIMISED|Traceback|Error:'
if ($LASTEXITCODE -ne 0) { throw 'Blender failed.' }
& $Python (Join-Path $PSScriptRoot 'textures.py') $work $out
if ($LASTEXITCODE -ne 0) { throw 'Packing the textures failed.' }
Get-ChildItem $out | ForEach-Object { "$($_.Name): $([math]::Round($_.Length / 1KB)) KB" }
