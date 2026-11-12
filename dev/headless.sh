#!/bin/bash
# Fika headless test: builds, starts the server and a headless copy of the game, then the real game asks the headless
# to host the siege and joins it. Waits for the siege to end. Needs dev/make_headless_copy.ps1 run first.
P="/d/Tushonka - Hideout Siege"; D="$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"
L=/f/SPT/BepInEx/LogOutput.log; HL=/f/SPT_Headless/BepInEx/LogOutput.log
HEADLESS_ID=${HEADLESS_ID:-6af6130057cb0e4f64e4bd00}
cd "$P" || exit 1
[ -d /f/SPT_Headless ] || { echo "No F:\\SPT_Headless: run dev/make_headless_copy.ps1 first"; exit 1; }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' -Stop >/dev/null 2>&1
sleep 3
for p in client/HideoutSiege.csproj brains/HideoutSiege.Brains.csproj server/HideoutSiege.Server.csproj dev/HideoutSiege.Dev.csproj fika/HideoutSiege.Fika.csproj; do
  out=$("$D" build "$p" -c Release --nologo 2>&1)
  if ! echo "$out" | grep -q "Build succeeded"; then echo "BUILD FAILED $p"; echo "$out" | grep -E "error" | sort -u | cut -c1-260 | head -25; exit 2; fi
done
cp fika/bin/Release/HideoutSiege.Fika.dll client/bin/Release/HideoutSiege.dll brains/bin/Release/HideoutSiege.Brains.dll /f/SPT/BepInEx/plugins/HideoutSiege/
mkdir -p /f/SPT/BepInEx/plugins/HideoutSiege.Dev; cp dev/bin/Release/HideoutSiege.Dev.dll /f/SPT/BepInEx/plugins/HideoutSiege.Dev/
cp server/bin/Release/HideoutSiege.Server.dll /f/SPT/SPT_Runtime/user/mods/HideoutSiege.Server/
rm -f /f/SPT/BepInEx/plugins/HideoutSiege.Dev/shots/* /f/SPT/BepInEx/siege_server.log
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' -ServerOnly >/dev/null 2>&1
echo "--- server up"

# The headless: no window, no sound, its own token. Only the test timings and auto-kill from the driver.
rm -f "$HL"
powershell.exe -NoProfile -Command "\$env:SIEGE_ROLE='headless'; \$env:SIEGE_FAST='1'; \$env:SIEGE_AUTOKILL='${SIEGE_AUTOKILL:-12}'; \$cfg='{\\\"BackendUrl\\\":\\\"https://127.0.0.1:6969\\\",\\\"Version\\\":\\\"live\\\",\\\"MatchingVersion\\\":\\\"live\\\"}'; Start-Process 'F:\SPT_Headless\EscapeFromTarkov.exe' -WorkingDirectory 'F:\SPT_Headless' -WindowStyle Minimized -ArgumentList \"-token=$HEADLESS_ID -config=\$cfg -batchmode -nographics --enable-console true\""
start=$SECONDS
until grep -aqiE "headless.*(connected|ready|websocket)|Fika.Headless loaded" "$HL" 2>/dev/null; do
  [ $((SECONDS-start)) -gt 300 ] && { echo "HEADLESS NEVER CAME UP"; tail -20 "$HL"; exit 3; }
  sleep 5
done
echo "--- headless plugin loaded after $((SECONDS-start))s; waiting for it to reach its menu"
# Ready once it has opened its headless socket with the server, which is what makes it show up as available.
until grep -aqE "WebSocket Request\] /fika/headless" /f/SPT/BepInEx/siege_server.log 2>/dev/null; do
  [ $((SECONDS-start)) -gt 600 ] && { echo "HEADLESS NEVER READY"; grep -avE "RequestHandler|Missing bundle" "$HL" | tail -15 | cut -c1-200; exit 3; }
  tasklist | grep -qi EscapeFrom || { echo "HEADLESS EXITED"; grep -avE "RequestHandler" "$HL" | tail -15 | cut -c1-200; exit 3; }
  sleep 5
done
sleep 10
echo "--- headless ready after $((SECONDS-start))s; starting the game"

rm -f "$L"
powershell.exe -NoProfile -Command "\$env:SIEGE_HEADLESS='1'; \$env:SIEGE_GOD='1'; & 'D:\Tushonka - Hideout Siege\dev\run.ps1'" >/dev/null 2>&1
start=$SECONDS
while [ $((SECONDS-start)) -lt ${SIEGE_WAIT:-900} ]; do
  grep -aqE "Leaving the raid|siege map failed|Local game matching failed|No headless is available" "$L" 2>/dev/null && { echo "CLIENT DONE after $((SECONDS-start))s"; break; }
  tasklist | grep -qi EscapeFrom || { echo "ALL GAMES GONE after $((SECONDS-start))s"; break; }
  sleep 5
done
sleep 25
echo "=== game (client) log"
grep -anE "\[siege\] (Tier|Won|Lost|Leaving|Wave)|\[map\] Ready|\[test\]|Fika found|Joined|headless|\[Error  :Tushonka|\[Error  :Hideout Siege|\[Error  :Fika" "$L" | cut -d: -f2- | cut -c1-200 | tail -40
echo "=== headless log"
grep -anE "\[siege\] (Tier|Won|Lost|Leaving|Wave|t phase)|\[map\]|Fika found|Test driver|\[Error|\[Fatal|Exception" "$HL" | cut -d: -f2- | cut -c1-220 | tail -50
