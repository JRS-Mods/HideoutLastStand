#!/bin/bash
# Temporarily moves bot mods out of the parked folder for a compatibility test, or back again. compat.sh on|off
P=/f/SPT/_mods_removed_20261105; G=/f/SPT
PLUGINS=(WTT-ArmoryClient WTT-ClientCommonLib WTT-ContentBackportClient SAIN DrakiaXYZ-BigBrain.dll DrakiaXYZ-Waypoints ORBIT acidphantasm-botplacementsystem BlackDiv MoreBotsPlugin UnityToolkit)
PATCHERS=(WTT-ContentBackportPatcher BlackDiv MoreBotsPrepatch UnityToolkit)
SERVER=(WTT-Armory WTT-ServerCommonLib WTT-ContentBackport Solarint-SAIN-ServerMod ORBIT acidphantasm-botplacementsystem BlackDivServer MoreBotsServer)
move() { # from to name
  if [ -e "$1/$3" ] && [ ! -e "$2/$3" ]; then mv "$1/$3" "$2/$3" && echo "moved $3"; else echo "skip $3"; fi
}
if [ "$1" = on ]; then
  for x in "${PLUGINS[@]}"; do move "$P/BepInEx/plugins" "$G/BepInEx/plugins" "$x"; done
  for x in "${PATCHERS[@]}"; do move "$P/BepInEx/patchers" "$G/BepInEx/patchers" "$x"; done
  for x in "${SERVER[@]}"; do move "$P/SPT_Runtime/user/mods" "$G/SPT_Runtime/user/mods" "$x"; done
  move "$P/BepInEx" "$G/BepInEx" ORBIT
else
  for x in "${PLUGINS[@]}"; do move "$G/BepInEx/plugins" "$P/BepInEx/plugins" "$x"; done
  for x in "${PATCHERS[@]}"; do move "$G/BepInEx/patchers" "$P/BepInEx/patchers" "$x"; done
  for x in "${SERVER[@]}"; do move "$G/SPT_Runtime/user/mods" "$P/SPT_Runtime/user/mods" "$x"; done
  move "$G/BepInEx" "$P/BepInEx" ORBIT
fi
