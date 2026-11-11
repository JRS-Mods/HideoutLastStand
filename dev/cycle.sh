#!/bin/bash
# Rebuild, redeploy, restart server and game, then wait for a marker in the game's log. Args: [grep pattern] [max seconds]
P="/d/Tushonka - Hideout Siege"; D="$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"; L=/f/SPT/BepInEx/LogOutput.log
PAT="${1:-Siege over|never appeared}"; MAX="${2:-420}"
cd "$P" || exit 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' -Stop >/dev/null 2>&1
sleep 3
for p in client/HideoutSiege.csproj brains/HideoutSiege.Brains.csproj server/HideoutSiege.Server.csproj dev/HideoutSiege.Dev.csproj; do
  out=$("$D" build "$p" -c Release --nologo 2>&1)
  if ! echo "$out" | grep -q "Build succeeded"; then echo "BUILD FAILED $p"; echo "$out" | grep -E "error" | sort -u | cut -c1-260 | head -25; exit 2; fi
done
if [ -f fika/HideoutSiege.Fika.csproj ]; then
  out=$("$D" build fika/HideoutSiege.Fika.csproj -c Release --nologo 2>&1)
  if ! echo "$out" | grep -q "Build succeeded"; then echo "BUILD FAILED fika"; echo "$out" | grep -E "error" | sort -u | cut -c1-260 | head -25; exit 2; fi
  cp fika/bin/Release/HideoutSiege.Fika.dll /f/SPT/BepInEx/plugins/HideoutSiege/
fi
cp client/bin/Release/HideoutSiege.dll /f/SPT/BepInEx/plugins/HideoutSiege/
cp brains/bin/Release/HideoutSiege.Brains.dll /f/SPT/BepInEx/plugins/HideoutSiege/
mkdir -p /f/SPT/BepInEx/plugins/HideoutSiege/sounds; cp assets/sounds/*.wav /f/SPT/BepInEx/plugins/HideoutSiege/sounds/ 2>/dev/null
mkdir -p /f/SPT/BepInEx/plugins/HideoutSiege/models; cp assets/models/* /f/SPT/BepInEx/plugins/HideoutSiege/models/ 2>/dev/null
cp dev/bin/Release/HideoutSiege.Dev.dll /f/SPT/BepInEx/plugins/HideoutSiege.Dev/
cp server/bin/Release/HideoutSiege.Server.dll /f/SPT/SPT_Runtime/user/mods/HideoutSiege.Server/
rm -f /f/SPT/BepInEx/plugins/HideoutSiege.Dev/shots/* /f/SPT/BepInEx/siege_server.log
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' >/dev/null 2>&1
sleep 15
start=$SECONDS
while [ $((SECONDS-start)) -lt "$MAX" ]; do
  grep -aqE "$PAT" "$L" 2>/dev/null && { echo "MARKER after $((SECONDS-start))s"; break; }
  tasklist | grep -qi EscapeFrom || { echo "GAME EXITED after $((SECONDS-start))s"; break; }
  sleep 4
done
[ $((SECONDS-start)) -ge "$MAX" ] && echo "TIMED OUT waiting for: $PAT"
grep -anE "\[map\]|\[siege\]|Hosted|never appeared" "$L" | cut -c1-240 | tail -60
echo "--- errors"; grep -an "^\[Error\|^\[Fatal" "$L" | grep -v "ShowFikaMessage" | cut -c1-260 | tail -12
d=/f/SPT/Logs/$(ls /f/SPT/Logs | tail -1); grep -ah "EXCEPTION:" "$d"/*errors.log 2>/dev/null | cut -c1-240 | sort | uniq -c | sort -rn | head -10
