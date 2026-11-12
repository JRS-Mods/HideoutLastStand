using System;
using System.Reflection;
using HarmonyLib;

namespace HideoutSiege
{
    /// <summary>
    /// Keeps other bot mods out of each other's way in a siege. Each one is optional and found by name, so nothing here
    /// matters if the mod isn't installed, and a mod that changes shape only loses its own fix.
    /// </summary>
    internal static class SiegeCompat
    {
        private static bool applied;

        internal static void Apply()
        {
            if (applied) return;
            applied = true;
            var harmony = new Harmony(Plugin.Id + ".compat");
            Patch(harmony, "SAIN's location check", "SAIN.Components.LocationClass", "parseLocation", nameof(SainLocation));
            Patch(harmony, "ORBIT's bot brain", "Orbit.Brain.OrbitBrainLayer", "IsActive", nameof(OrbitLayer));
            // ORBIT also puts bots to "sleep" and moves them itself, by its own map of waypoints. The hideout has none (its
            // grid is 1x1), so a sleeping attacker's every route was swapped for one from ORBIT's waypoint 12 m off, and he
            // stood still with a route to you. And it skips the legs of any bot it counts as its own.
            Patch(harmony, "ORBIT's sleeping bots", "Orbit.Systems.NativeGhostSystem", "CanSleep", nameof(OrbitNoSleep));
            Patch(harmony, "ORBIT's bot legs", "Orbit.Core.BotRoster", "IsOrbitActive", nameof(OrbitNotActive));
            // That check is small enough to be folded into ORBIT's own patch, out of reach of the one above: so its patch too.
            Patch(harmony, "ORBIT's skipped steps", "Orbit.Patches.ManualFixedUpdateSkipPatch", "PatchPrefix", nameof(OrbitLegsRun));
            // SAIN skips the game's walking for any bot it counts as in a fight, as its own fighting moves it. But the siege's
            // layer is above SAIN's, so while it's walking an attacker to you, SAIN's isn't running and nothing moved him: he
            // stood with a route to you and never took a step (most of those "isn't getting anywhere"). Its legs run then.
            Patch(harmony, "SAIN's walking", "SAIN.Patches.Movement.BotMoverManualFixedUpdatePatch", "PatchPrefix", nameof(SainLegs));
        }

        private static bool earlyApplied;

        /// <summary>For mods that act as the raid is being created, before the siege's map is built: once every plugin is loaded.</summary>
        internal static void ApplyEarly()
        {
            if (earlyApplied) return;
            earlyApplied = true;
            var harmony = new Harmony(Plugin.Id + ".compat.early");
            // Tushonka Territories looks the raid's map up in its own table of maps as the raid is created, and the siege's
            // isn't in it: "Specified argument was out of the range of valid values", and Fika dropped the raid. Its own
            // "no state for this map" path leaves its spawner switched off, which its other patches already allow for.
            Patch(harmony, "Tushonka Territories' spawns", "TerritoryClient.Spawns.TerritoriesSpawnScenario", "Init", nameof(TerritoriesNoSpawns));
            // Its kill counter would send the siege's kills to its server as a battle on a map it doesn't have, which throws
            // there; and one left running by a raid that never loaded made the next raid's start log an error.
            Patch(harmony, "Tushonka Territories' kill counter", "TerritoryClient.Services.KillCounter", "StartRaid", nameof(TerritoriesNoCount));
            // HollywoodFX leaves the camera, lighting and effects alone in the hideout, and it tells by the kind of game world.
            // The siege's is a raid's, so it put its raid camera work on the hideout: the screen went black behind the security
            // camera, and the wave timer and the pause menu broke. To it the siege is the hideout.
            // Its flag is set as the world wakes, so this goes after it.
            HollywoodInHideout(harmony);
            // HollywoodGraphics, which comes with it, puts a bloom effect on the camera as a raid starts. On the hideout's camera
            // it fails half way, after adding it: the screen was black, and it threw an error every frame after. Not in a siege.
            Patch(harmony, "HollywoodGraphics' camera effects", "HollywoodGraphics.GraphicsControllerInitPatch", "Postfix", nameof(NotInSiege));
        }

        private static bool NotInSiege() => !SiegeLauncher.Selected;

        private static FieldInfo hollywoodHideout;

        private static void HollywoodInHideout(Harmony harmony)
        {
            try
            {
                hollywoodHideout = AccessTools.TypeByName("HollywoodFX.Patches.GameWorldAwakePrefixPatch") is Type type ? AccessTools.Field(type, "IsHideout") : null;
                if (hollywoodHideout == null) return;
                var awake = AccessTools.Method(typeof(EFT.GameWorld), "Awake");
                harmony.Patch(awake, new HarmonyMethod(typeof(SiegeCompat), nameof(HollywoodSiegeIsHideout)) { priority = Priority.Last });
                Plugin.Log.LogInfo("[compat] Handling HollywoodFX's raid effects.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[compat] HollywoodFX's raid effects: " + e.Message); }
        }

        private static void HollywoodSiegeIsHideout()
        {
            if (!SiegeLauncher.Selected) return;
            hollywoodHideout.SetValue(null, true);
            Plugin.Log.LogInfo("[compat] HollywoodFX is treating the siege as the hideout.");
        }

        private static bool IsSiegeMap(string locationId) => string.Equals(locationId, SiegeIds.LocationId, StringComparison.OrdinalIgnoreCase);

        private static bool TerritoriesNoSpawns(JsonType.LocationSettings.Location ____location) => !IsSiegeMap(____location?.Id);

        private static bool TerritoriesNoCount(string __0) => !IsSiegeMap(__0);

        private static void Patch(Harmony harmony, string what, string typeName, string method, string prefix, string fallbackTypeName = null)
        {
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null && fallbackTypeName != null)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name != "ORBIT") continue;
                        foreach (Type t in AccessTools.GetTypesFromAssembly(assembly))
                            if (t.Name == fallbackTypeName) type = t;
                    }
                }
                if (type == null) return;
                MethodInfo target = AccessTools.Method(type, method);
                if (target == null) { Plugin.Log.LogWarning($"[compat] {what}: {typeName}.{method} has moved, so this isn't handled."); return; }
                harmony.Patch(target, new HarmonyMethod(typeof(SiegeCompat), prefix));
                Plugin.Log.LogInfo($"[compat] Handling {what}.");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[compat] {what}: {e.Message}"); }
        }

        // SAIN logs an error every frame for a map it doesn't know. Factory is its closest match: indoors and close quarters.
        private static bool SainLocation(ref object __result, MethodBase __originalMethod)
        {
            if (!SiegeLauncher.Selected) return true;
            __result = Enum.Parse(((MethodInfo)__originalMethod).ReturnType, "Factory");
            return false;
        }

        // ORBIT plans routes across zones the hideout doesn't have, and its bots would stand idle. Bots stay under SAIN or the game's own AI here.
        private static bool OrbitLayer(ref bool __result)
        {
            if (!SiegeLauncher.Selected) return true;
            __result = false;
            return false;
        }

        private static bool OrbitNoSleep(ref bool __result)
        {
            if (!SiegeLauncher.Selected) return true;
            __result = false;
            return false;
        }

        private static bool SainLegs(object __0, ref bool __result)
        {
            if (!SiegeLauncher.Selected || !(__0 is BotMover mover) || !SiegePush.Walking(mover._owner)) return true;
            __result = true;
            return false;
        }

        // ORBIT's prefix says whether the game's own step runs: in a siege, always.
        private static bool OrbitLegsRun(ref bool __result)
        {
            if (!SiegeLauncher.Selected) return true;
            __result = true;
            return false;
        }

        private static bool OrbitNotActive(ref bool __result)
        {
            if (!SiegeLauncher.Selected) return true;
            __result = false;
            return false;
        }
    }
}
