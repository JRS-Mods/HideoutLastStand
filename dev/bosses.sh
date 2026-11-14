#!/bin/bash
# Every boss in one siege, a wave each, with a Smuggler and Rogue wave second (the first to use the second way in).
# You can't be hurt and don't shoot: each attacker has SIEGE_AUTOKILL seconds to get to you, and every few seconds the
# log says where each one is and what his AI is doing. Solo, without Fika; the profile, the invasion state, the settings
# and Fika go back as they were afterwards, and the clean build is put back in.
# Args: [profile id]. Refuses to start while the game or the server is open.
cd "/d/Tushonka - Hideout Siege" || exit 1
TOKEN="${1:-6af7924dd7b8324f00e1f0ab}"
SPT=/f/SPT; L=$SPT/BepInEx/LogOutput.log; KEEP=$SPT/_siege_test_keep; T=$SPT/_fika_parked_for_test
PROFILE=$SPT/SPT_Runtime/user/profiles/$TOKEN.json; STATE=$SPT/SPT_Runtime/user/mods/HideoutSiege.Server/state.json; CFG=$SPT/BepInEx/config/jrs.hideoutsiege.cfg
tasklist | grep -qiE "EscapeFromTarkov|SPT.Server" && { echo "The game or the SPT server is open: close them first."; exit 1; }
[ -f "$PROFILE" ] || { echo "No profile $TOKEN."; exit 1; }
[ -e "$KEEP" ] && { echo "$KEEP is still there from a run that didn't finish: put its files back first."; exit 1; }

mkdir -p "$KEEP" "$T"
cp "$PROFILE" "$KEEP/profile.json"; cp "$CFG" "$KEEP/config.cfg"; [ -f "$STATE" ] && cp "$STATE" "$KEEP/state.json"
mv $SPT/BepInEx/plugins/Fika "$T/client" && mv $SPT/SPT_Runtime/user/mods/fika-server "$T/server"
mv $SPT/BepInEx/plugins/HideoutSiege/HideoutSiege.Fika.dll "$T/" 2>/dev/null
sed -i 's#^if \[ -f fika/HideoutSiege.Fika.csproj \]; then#if false; then#' dev/cycle.sh
rm -f "$STATE" "$L"

export SIEGE_TOKEN=$TOKEN SIEGE_GOD=1 SIEGE_FAST=1 SIEGE_BREAK=8 SIEGE_LOOPS=1 SIEGE_TELEMETRY=4 SIEGE_AUTOKILL=${SIEGE_AUTOKILL:-60}
export SIEGE_WAVES="${SIEGE_WAVES:-RESHALA,2 smugglers + 2 rogues,TAGILLA,KILLA,SANITAR,GLUKHAR,KABAN,KOLLONTAY,THE GOONS}"
bash dev/cycle.sh "NEVERMATCHES" 1 | grep -E "FAILED|error" | cut -c1-200
start=$SECONDS
while [ $((SECONDS-start)) -lt ${SIEGE_WAIT:-1500} ]; do
  [ "$(grep -acE 'Won, wave|Lost, wave' "$L")" -ge 1 ] && { echo "SIEGE DONE after $((SECONDS-start))s"; break; }
  tasklist | grep -qi EscapeFrom || { echo "GAME GONE after $((SECONDS-start))s"; break; }
  sleep 5
done
sleep 20
cp "$L" dev/last_test.log
rm -rf dev/last_test_shots; cp -r $SPT/BepInEx/plugins/HideoutSiege.Dev/shots dev/last_test_shots 2>/dev/null

# Everything back as it was.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' -Stop >/dev/null 2>&1
sleep 5
sed -i 's#^if false; then#if [ -f fika/HideoutSiege.Fika.csproj ]; then#' dev/cycle.sh
cp "$KEEP/profile.json" "$PROFILE"; cp "$KEEP/config.cfg" "$CFG"
rm -f "$STATE"; [ -f "$KEEP/state.json" ] && cp "$KEEP/state.json" "$STATE"
mv "$T/client" $SPT/BepInEx/plugins/Fika && mv "$T/server" $SPT/SPT_Runtime/user/mods/fika-server
mv "$T/HideoutSiege.Fika.dll" $SPT/BepInEx/plugins/HideoutSiege/ 2>/dev/null
rmdir "$T"
rm -rf $SPT/BepInEx/plugins/HideoutSiege.Dev
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\build.ps1' 2>&1 | grep -E "error|Installed|failed"
cmp -s "$KEEP/profile.json" "$PROFILE" && cmp -s "$KEEP/config.cfg" "$CFG" && [ -d $SPT/BepInEx/plugins/Fika ] && rm -rf "$KEEP" && echo "Profile, settings and Fika are back as they were."
grep -aE "\[siege\] (Tier|Won|Lost)|\[Error  :Tushonka" dev/last_test.log | cut -d: -f2- | cut -c1-200 | tail -8
