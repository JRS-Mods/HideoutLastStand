using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace HideoutSiege.Server;

/// <summary>
/// Keeps other server mods from choking on the siege's map. Mods that keep their own table per map know nothing of "Suburbs",
/// throw on it, and either stop the siege starting or leave it with no bots. Optional mods are found by name, so nothing here
/// matters if one isn't installed, and a mod that changes shape only loses its own fix.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 50)]
public sealed class SiegeCompat(ISptLogger<SiegeCompat> logger) : IOnLoad
{
    // The siege is fought indoors at close quarters, so where a mod wants a real map, it gets Factory.
    private const string StandIn = "factory4_day";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var harmony = new Harmony("jrs.hideoutsiege.server.compat");
        // The siege has no loot of its own (its multipliers are 0 anyway), so the game's loot generation is skipped for it
        // outright, and with it every loot mod hooked in there: LotsofLoot threw "the given key 'suburbs' was not present".
        Patch(harmony, "loot mods", typeof(LocationLootGenerator), nameof(LocationLootGenerator.GenerateLocationLoot), nameof(NoLootForSiege), Priority.First);
        // APBS picks bots' weapons by map and throws "Map 'Suburbs' not found", so no bot spawned and the siege stood empty.
        Patch(harmony, "APBS's map weights", AccessTools.TypeByName("ProgressiveBotSystem.Models.MapRangeWeights"), "get_Item", nameof(ApbsMap));
        return Task.CompletedTask;
    }

    private void Patch(Harmony harmony, string what, Type? type, string method, string prefix, int priority = Priority.Normal)
    {
        if (type == null) return;
        try
        {
            MethodInfo? target = AccessTools.Method(type, method);
            if (target == null) { logger.Warning($"Hideout Last Stand: {type.Name}.{method} has moved, so {what} aren't handled in the siege."); return; }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(SiegeCompat), prefix) { priority = priority });
        }
        catch (Exception e) { logger.Warning($"Hideout Last Stand: couldn't handle {what} in the siege: {e.Message}"); }
    }

    private static bool IsSiege(string? location) => string.Equals(location, SiegeIds.LocationId, StringComparison.OrdinalIgnoreCase);

    private static bool NoLootForSiege(string locationId, ref List<SpawnpointTemplate> __result)
    {
        if (!IsSiege(locationId)) return true;
        __result = [];
        return false;
    }

    private static void ApbsMap(ref string __0)
    {
        if (IsSiege(__0)) __0 = StandIn;
    }
}
