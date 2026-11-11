#!/bin/bash
# One combined run, with Fika as the player has it: hold out in the first siege (the stash, a bag from the racks, your own
# gear left lying), go down in the second (what they take and wreck, the defeat screen), then open the hideout.
# Back up the profiles first: losing takes things from the test profile's stash and knocks a station down a level.
P="/d/Tushonka - Hideout Siege"; L=/f/SPT/BepInEx/LogOutput.log
cd "$P" || exit 1
mkdir -p /f/SPT/BepInEx/plugins/HideoutSiege.Dev
export SIEGE_GOD=1 SIEGE_FAST=1 SIEGE_AUTOKILL=${SIEGE_AUTOKILL:-60} SIEGE_LOOPS=2 SIEGE_DIE=second SIEGE_STASH=1 SIEGE_RESULTS=next SIEGE_OPENHIDEOUT=1 SIEGE_SNAPS=1
bash dev/cycle.sh "HIDEOUT CHECK|never appeared" "${SIEGE_WAIT:-2100}" | grep -E "BUILD FAILED|MARKER|GAME EXITED|TIMED OUT|error" | head -20
sleep 5
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Tushonka - Hideout Siege\dev\run.ps1' -Stop >/dev/null 2>&1
echo "stopped"
