param([string] $SPTPath = 'F:\SPT')

# Replaces the Fika server mod with the official 2.4.2 release, which the installed 2.4.6 client requires.
# The old copy is kept next to the other parked mods, so this can be undone by moving it back.
$ErrorActionPreference = 'Stop'
$url = 'https://github.com/project-fika/Fika-Server-CSharp/releases/download/v2.4.2/Fika.Server.Release.2.4.2.zip'
$mods = Join-Path $SPTPath 'SPT_Runtime\user\mods'
$current = Join-Path $mods 'fika-server'
$parked = Join-Path $SPTPath '_mods_removed_20261105\fika-server-before-update'

if (Get-Process -Name 'SPT.Server', 'EscapeFromTarkov' -ErrorAction SilentlyContinue) { throw 'Close the SPT server and the game first.' }

$work = Join-Path $env:TEMP ('fika-server-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'fika.zip'
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'x')
    $dll = Get-ChildItem (Join-Path $work 'x') -Recurse -Filter 'FikaServer.dll' | Select-Object -First 1
    if (-not $dll) { throw 'The download has no FikaServer.dll in it.' }
    if (Test-Path $current) {
        if (Test-Path $parked) { Remove-Item $parked -Recurse -Force }
        Move-Item $current $parked
    }
    Copy-Item $dll.Directory.FullName $current -Recurse
    Write-Host "Fika server is now $((Get-Item (Join-Path $current 'FikaServer.dll')).VersionInfo.FileVersion)."
}
finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
