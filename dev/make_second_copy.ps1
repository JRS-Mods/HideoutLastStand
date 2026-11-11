param([switch]$Remove)
# A throwaway second launch folder for co-op testing on one machine: links to the real game files, with single-instance switched off.
# Uses hard links and junctions, so it takes almost no space. -Remove deletes only the links, never what they point at.
$ErrorActionPreference = 'Stop'
$src = 'F:\SPT'
$dst = 'F:\SPT_CoopTest'

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

New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $src -Force) {
    if ($item.Name -in 'Logs', 'BepInEx', 'EscapeFromTarkov_Data', 'SPT_Runtime') { continue }
    if ($item.Name -like '_*' -or $item.Extension -in '.log', '.lnk') { continue }
    $target = Join-Path $dst $item.Name
    if ($item.PSIsContainer) { cmd /c mklink /J "$target" "$($item.FullName)" | Out-Null }
    else { cmd /c mklink /H "$target" "$($item.FullName)" | Out-Null }
}

# The game's data: real folder, linked contents, and its own boot.config.
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

# BepInEx: its own logs and config, with the same plugins.
$bep = Join-Path $dst 'BepInEx'
New-Item -ItemType Directory -Force $bep | Out-Null
foreach ($name in 'core', 'patchers', 'plugins') { cmd /c mklink /J "$(Join-Path $bep $name)" "$(Join-Path $src "BepInEx\$name")" | Out-Null }
Copy-Item (Join-Path $src 'BepInEx\config') (Join-Path $bep 'config') -Recurse
Write-Host "Made $dst"
