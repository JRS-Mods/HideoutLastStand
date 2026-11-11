param([string]$Token = 'aaaaaaaaaaaaaaaaaaaa1234', [switch]$ServerOnly, [switch]$Stop)
# Starts the SPT server and the game for a silent test run. Everything it starts, -Stop closes.
$spt = 'F:\SPT'
if ($Stop) {
    Get-Process EscapeFromTarkov, SPT.Server -ErrorAction SilentlyContinue | Stop-Process -Force
    return
}
if (-not (Get-Process SPT.Server -ErrorAction SilentlyContinue)) {
    Start-Process (Join-Path $spt 'SPT_Runtime\SPT.Server.exe') -WorkingDirectory (Join-Path $spt 'SPT_Runtime') -WindowStyle Minimized -RedirectStandardOutput (Join-Path $spt 'BepInEx\siege_server.log')
}
$deadline = (Get-Date).AddMinutes(4)
while ((Get-Date) -lt $deadline) {
    if (Select-String -Path (Join-Path $spt 'BepInEx\siege_server.log') -Pattern 'Server has started' -Quiet -ErrorAction SilentlyContinue) { break }
    Start-Sleep 3
}
if ($ServerOnly) { return }
$cfg = '{\"BackendUrl\":\"https://127.0.0.1:6969\",\"Version\":\"live\",\"MatchingVersion\":\"live\"}'
Start-Process (Join-Path $spt 'EscapeFromTarkov.exe') -WorkingDirectory $spt -ArgumentList "-token=$Token -config=$cfg -screen-fullscreen 0 -screen-width 1280 -screen-height 720"
