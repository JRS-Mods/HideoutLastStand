#!/bin/bash
# Solo without Fika, two sieges in a row in one session. Fika goes back where it was afterwards.
cd "/d/Tushonka - Hideout Siege" || exit 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' -Stop >/dev/null 2>&1
sleep 3
T=/f/SPT/_fika_parked_for_test; mkdir -p "$T"
mv /f/SPT/BepInEx/plugins/Fika "$T/client" && mv /f/SPT/SPT_Runtime/user/mods/fika-server "$T/server"
mv /f/SPT/BepInEx/plugins/HideoutSiege/HideoutSiege.Fika.dll "$T/" 2>/dev/null
sed -i 's#^if \[ -f fika/HideoutSiege.Fika.csproj \]; then#if false; then#' dev/cycle.sh
export SIEGE_GOD=1 SIEGE_FAST=1 SIEGE_AUTOKILL=${SIEGE_AUTOKILL:-8} SIEGE_LOOPS=${SIEGE_LOOPS:-2}
rm -f /f/SPT/SPT_Runtime/user/mods/HideoutSiege.Server/state.json
bash dev/cycle.sh "NEVERMATCHES" 1 | grep -E "FAILED|error" | cut -c1-200
L=/f/SPT/BepInEx/LogOutput.log
start=$SECONDS
while [ $((SECONDS-start)) -lt ${SIEGE_WAIT:-780} ]; do
  [ "$(grep -acE 'Won, wave|Lost, wave' "$L")" -ge "$SIEGE_LOOPS" ] && { echo "SIEGES DONE after $((SECONDS-start))s"; break; }
  tasklist | grep -qi EscapeFrom || { echo "GAME GONE after $((SECONDS-start))s"; break; }
  sleep 5
done
sleep 20
grep -anE "No Fika|Back to the menu|\[siege\] (Tier|Won|Lost|Leaving)|\[map\] Ready|customised|\[Error  :Tushonka|\[Error  :Hideout Siege" "$L" | cut -d: -f2- | cut -c1-130 | tail -24
d=$(ls -dt /f/SPT/Logs/*/ | head -1); grep -ahE "^  at " "$d"*errors.log | cut -c1-110 | sort | uniq -c | sort -rn | head -6
sed -i 's#^if false; then#if [ -f fika/HideoutSiege.Fika.csproj ]; then#' dev/cycle.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' -Stop >/dev/null 2>&1
sleep 3
mv "$T/client" /f/SPT/BepInEx/plugins/Fika && mv "$T/server" /f/SPT/SPT_Runtime/user/mods/fika-server
mv "$T/HideoutSiege.Fika.dll" /f/SPT/BepInEx/plugins/HideoutSiege/ 2>/dev/null
rmdir "$T"
ls /f/SPT/BepInEx/plugins | tr '\n' ' '; ls /f/SPT/SPT_Runtime/user/mods | tr '\n' ' '; echo
