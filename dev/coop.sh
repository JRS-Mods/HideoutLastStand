#!/bin/bash
# Two copies of the game on this machine: a host and a client that joins it. Waits for the host to leave the raid.
cd "/d/Tushonka - Hideout Siege" || exit 1
export SIEGE_GOD=1 SIEGE_FAST=1 SIEGE_AUTOKILL=${SIEGE_AUTOKILL:-12} SIEGE_WAIT_PEERS=1
unset SIEGE_ROLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\make_second_copy.ps1' | tail -1
bash dev/cycle.sh "Menu is up|siege map failed" 200 | grep -E "MARKER|FAILED|error|TIMED|EXITED" | cut -c1-200
rm -f /f/SPT/BepInEx/plugins/HideoutSiege.Dev/shots_client/*
echo "--- starting client"
powershell.exe -NoProfile -Command "\$env:SIEGE_ROLE='client'; \$env:SIEGE_GOD='1'; Remove-Item Env:SIEGE_AUTOKILL -ErrorAction SilentlyContinue; Remove-Item Env:SIEGE_WAIT_PEERS -ErrorAction SilentlyContinue; \$cfg='{\\\"BackendUrl\\\":\\\"https://127.0.0.1:6969\\\",\\\"Version\\\":\\\"live\\\",\\\"MatchingVersion\\\":\\\"live\\\"}'; Start-Process 'F:\SPT_CoopTest\EscapeFromTarkov.exe' -WorkingDirectory 'F:\SPT_CoopTest' -ArgumentList \"-token=bbbbbbbbbbbbbbbbbbbb5678 -config=\$cfg -screen-fullscreen 0 -screen-width 1280 -screen-height 720\""
start=$SECONDS
L=/f/SPT/BepInEx/LogOutput.log
while [ $((SECONDS-start)) -lt 540 ]; do
  grep -aqE "Leaving the raid|siege map failed|Local game matching failed" "$L" && { echo "HOST DONE after $((SECONDS-start))s"; break; }
  [ "$(tasklist | grep -ci EscapeFrom)" -lt 1 ] && { echo "ALL GAMES GONE"; break; }
  sleep 5
done
sleep 25
tasklist | grep -iE "EscapeFrom" | cut -c1-70
