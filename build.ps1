param(
    [string] $SPTPath = 'F:\SPT',
    [switch] $NoInstall
)

# Builds the client plugin, the server mod and the Fika link, packs dist\HideoutLastStand-<version>.zip, and installs them to the game.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pluginName = 'HideoutSiege'
$serverName = 'HideoutSiege.Server'

if (-not (Test-Path (Join-Path $SPTPath 'BepInEx\core\BepInEx.dll'))) {
    throw "$SPTPath doesn't look like an SPT folder (no BepInEx\core\BepInEx.dll)."
}

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }
$projects = @("client\$pluginName.csproj", "server\$serverName.csproj")
# The Fika link is built against Fika, never ships Fika itself, and is only loaded where Fika is installed.
$fikaLink = $null
if ((Test-Path (Join-Path $SPTPath 'BepInEx\plugins\Fika\Fika.Core.dll')) -or (Test-Path (Join-Path $SPTPath '_mods_disabled\BepInEx\plugins\Fika\Fika.Core.dll'))) {
    $projects += "fika\$pluginName.Fika.csproj"
    $fikaLink = Join-Path $root "fika\bin\Release\$pluginName.Fika.dll"
}
else { Write-Warning 'Fika.Core.dll not found: packing without the Fika link.' }
# The same for the AI layer: built against BigBrain (which SAIN needs), and only loaded where BigBrain is installed.
$brainsLink = $null
if ((Test-Path (Join-Path $SPTPath 'BepInEx\plugins\DrakiaXYZ-BigBrain.dll')) -or (Test-Path (Join-Path $SPTPath '_mods_disabled\BepInEx\plugins\DrakiaXYZ-BigBrain.dll'))) {
    $projects += "brains\$pluginName.Brains.csproj"
    $brainsLink = Join-Path $root "brains\bin\Release\$pluginName.Brains.dll"
}
else { Write-Warning 'DrakiaXYZ-BigBrain.dll not found: packing without the AI layer.' }
foreach ($project in $projects) {
    & $dotnet build (Join-Path $root $project) -c Release "-p:SPTPath=$SPTPath" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build of $project failed." }
}

$version = ([xml](Get-Content (Join-Path $root "client\$pluginName.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$clientDll = Join-Path $root "client\bin\Release\$pluginName.dll"
$serverDll = Join-Path $root "server\bin\Release\$serverName.dll"
$sounds = @(Get-ChildItem (Join-Path $root 'assets\sounds') -Filter '*.wav' -File)
if ($sounds.Count -eq 0) { throw 'No sounds in assets\sounds (tools\make_sounds.py makes them).' }
$models = @(Get-ChildItem (Join-Path $root 'assets\models') -File)
if ($models.Count -eq 0) { throw 'No charge model in assets\models (tools\charge\make_charge.ps1 makes it).' }

# The same layout zipped and installed.
$stage = Join-Path $root 'dist\stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$pluginStage = Join-Path $stage "BepInEx\plugins\$pluginName"
$serverStage = Join-Path $stage "SPT_Runtime\user\mods\$serverName"
New-Item -ItemType Directory -Force (Join-Path $pluginStage 'sounds'), (Join-Path $pluginStage 'models'), $serverStage | Out-Null
Copy-Item $clientDll, (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE.txt') $pluginStage
if ($fikaLink) { Copy-Item $fikaLink $pluginStage }
if ($brainsLink) { Copy-Item $brainsLink $pluginStage }
Copy-Item $sounds.FullName (Join-Path $pluginStage 'sounds')
Copy-Item $models.FullName (Join-Path $pluginStage 'models')
Copy-Item $serverDll, (Join-Path $root 'LICENSE.txt') $serverStage

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $root "dist\HideoutLastStand-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Write-Host "Packed $zip"

if ($NoInstall) { return }
if (Get-Process -Name 'EscapeFromTarkov' -ErrorAction SilentlyContinue) { throw 'The game is running: close it before installing, or use -NoInstall.' }
if (Get-Process -Name 'SPT.Server' -ErrorAction SilentlyContinue) { throw 'The SPT server is running: close it before installing, or use -NoInstall.' }
Copy-Item (Join-Path $stage '*') $SPTPath -Recurse -Force
Write-Host "Installed to $SPTPath"
