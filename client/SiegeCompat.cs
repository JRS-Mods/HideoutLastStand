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
        }

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
    }
}
