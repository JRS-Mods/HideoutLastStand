param([switch]$Remove, [string]$HeadlessDll)
# A throwaway Fika headless launch folder: links to the real game files, with single-instance switched off,
# and its own plugins folder so Fika.Headless.dll never reaches the real game. -Remove deletes only the links.
$ErrorActionPreference = 'Stop'
$src = 'F:\SPT'
$dst = 'F:\SPT_Headless'

function Remove-Links([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return }
    foreach ($item in Get-ChildItem -LiteralPath $dir -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { cmd /c rmdir "$($item.FullName)" | Out-Null }
        elseif ($item.PSIsContainer) { Remove-Links $item.FullName; Remove-Item -LiteralPath $item.FullName -Force }
        else { Remove-Item -LiteralPath $item.FullName -Force }
    }
}

if ($Remove) {
    Remove-Links $dst
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Force }
    Write-Host 'Removed.'
    return
}
if (-not $HeadlessDll -or -not (Test-Path -LiteralPath $HeadlessDll)) { throw 'Pass -HeadlessDll with the path to Fika.Headless.dll' }
if (Test-Path -LiteralPath $dst) { Remove-Links $dst; Remove-Item -LiteralPath $dst -Force }

New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $src -Force) {
    # SPT_Runtime is linked too: the game finds the server mods' bundles through it.
    if ($item.Name -in 'Logs', 'BepInEx', 'EscapeFromTarkov_Data') { continue }
    if ($item.Name -like '_*' -or $item.Extension -in '.log', '.lnk') { continue }
    $target = Join-Path $dst $item.Name
    if ($item.PSIsContainer) { cmd /c mklink /J "$target" "$($item.FullName)" | Out-Null }
    else { cmd /c mklink /H "$target" "$($item.FullName)" | Out-Null }
}
New-Item -ItemType Directory -Force (Join-Path $dst 'Logs') | Out-Null

$data = Join-Path $dst 'EscapeFromTarkov_Data'
New-Item -ItemType Directory -Force $data | Out-Null
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $src 'EscapeFromTarkov_Data') -Force) {
    $target = Join-Path $data $item.Name
    if ($item.Name -eq 'boot.config') {
        Get-Content -LiteralPath $item.FullName | Where-Object { $_ -notmatch '^single-instance' } | Set-Content -LiteralPath $target -Encoding ascii
    }
    elseif ($item.PSIsContainer) { cmd /c mklink /J "$target" "$($item.FullName)" | Out-Null }
    else { cmd /c mklink /H "$target" "$($item.FullName)" | Out-Null }
}

# BepInEx: its own logs, config and plugins folder. Every plugin is linked except Fika's, which gets the headless dll too.
$bep = Join-Path $dst 'BepInEx'
New-Item -ItemType Directory -Force $bep | Out-Null
foreach ($name in 'core', 'patchers') { cmd /c mklink /J "$(Join-Path $bep $name)" "$(Join-Path $src "BepInEx\$name")" | Out-Null }
Copy-Item (Join-Path $src 'BepInEx\config') (Join-Path $bep 'config') -Recurse
$plugins = Join-Path $bep 'plugins'
New-Item -ItemType Directory -Force $plugins | Out-Null
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $src 'BepInEx\plugins') -Force) {
    $target = Join-Path $plugins $item.Name
    if ($item.Name -eq 'Fika') {
        New-Item -ItemType Directory -Force $target | Out-Null
        foreach ($f in Get-ChildItem -LiteralPath $item.FullName -Force) { cmd /c mklink /H "$(Join-Path $target $f.Name)" "$($f.FullName)" | Out-Null }
        Copy-Item -LiteralPath $HeadlessDll (Join-Path $target 'Fika.Headless.dll')
    }
    elseif ($item.PSIsContainer) { cmd /c mklink /J "$target" "$($item.FullName)" | Out-Null }
    else { cmd /c mklink /H "$target" "$($item.FullName)" | Out-Null }
}
Write-Host "Made $dst"
