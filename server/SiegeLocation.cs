using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Utils;

using SPTarkov.Server.Core.Utils.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

using SPTarkov.Server.Core.Utils.Cloners;

namespace HideoutSiege.Server;

/// <summary>
/// Turns the unused "Suburbs" location into the siege map: a normal raid's settings, with the hideout's scene and spawns.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 50)]
public sealed class SiegeLocation(LocationTable locations, LocaleTable locales, LocationConfig config, BotConfig bots, ICloner cloner, ISptLogger<SiegeLocation> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var slot = locations.GetLocation(SiegeIds.LocationId);
        var factory = locations.GetLocation("factory4_day");
        var hideout = locations.GetLocation("hideout");
        if (slot is null || factory is null || hideout is null)
        {
            logger.Error("Hideout Last Stand: a location it needs is missing, so the siege can't start.");
            return Task.CompletedTask;
        }

        var keep = (slot.Base.Id, slot.Base.IdField);
        var b = cloner.Clone(factory.Base)!;
        b.Id = keep.Id;
        b.IdField = keep.IdField;
        b.Name = "Hideout";
        b.Scene = cloner.Clone(hideout.Base.Scene)!;
        b.Scene.Path = SiegeIds.HideoutPreset;
        b.Scene.Rcid = "bunker.ScenesPreset.asset";
        b.SpawnPointParams = cloner.Clone(hideout.Base.SpawnPointParams)!;
        b.Waves = [];
        b.BossLocationSpawn = [];
        b.Exits = [];
        b.Transits = [];
        b.Loot = [];
        b.Doors = [];
        b.EscapeTimeLimit = 60;
        b.EscapeTimeLimitCoop = 60;
        b.EscapeTimeLimitPVE = 60;
        b.OldSpawn = false;
        b.NewSpawn = false;
        b.OfflineOldSpawn = false;
        b.OfflineNewSpawn = false;
        b.BotMax = 30;
        b.BotMaxPvE = 30;
        b.Enabled = false; // Keeps it off the normal map list.
        b.Locked = false;
        b.AccessKeys = [];
        b.AccessKeysPvE = [];
        b.MinPlayerLvlAccessKeys = 0;
        b.RequiredPlayerLevelMin = 0;
        b.RequiredPlayerLevelMax = 100;

        b.Events = new LocationEvents();
        slot.Base = b;
        if (locales.Global.TryGetValue("en", out var english))
            english.AddTransformer(text =>
            {
                if (text == null) return text;
                text[$"{b.IdField} Name"] = "Your Hideout";
                text[$"{b.IdField} Description"] = "Your hideout is under attack. Hold it.";
                return text;
            });
        // The box that stands in for your stash in the siege, in every language: nobody has one of these otherwise.
        foreach (var table in locales.Global.Values)
            table.AddTransformer(text =>
            {
                if (text == null) return text;
                text[$"{SiegeIds.StashBoxTemplate} Name"] = "Stash";
                text[$"{SiegeIds.StashBoxTemplate} ShortName"] = "Stash";
                text[$"{SiegeIds.StashBoxTemplate} Description"] = "Your stash. Whatever you put in here during the siege is in your real stash afterwards.";
                return text;
            });
        string key = SiegeIds.LocationId.ToLowerInvariant();
        // SPT looks these up by map name for every scav it spawns.
        bots.AssaultBrainType[key] = bots.AssaultBrainType["factory4_day"];
        bots.PlayerScavBrainType[key] = bots.PlayerScavBrainType["factory4_day"];
        config.LooseLootMultiplier[key] = 0;
        config.StaticLootMultiplier[key] = 0;
        slot.LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot { Spawnpoints = [], SpawnpointsForced = [], SpawnpointCount = new SpawnpointCount { Mean = 1, Std = .1 } });
        slot.StaticLoot = new LazyLoad<Dictionary<SPTarkov.Server.Core.Models.Common.MongoId, StaticLootDetails>>(() => []);
        slot.StaticContainers = new LazyLoad<StaticContainerDetails>(() => new StaticContainerDetails { StaticWeapons = [], StaticContainers = [], StaticForced = [] });
        slot.StaticAmmo = [];
        slot.AllExtracts = [];
        logger.Info("Hideout Last Stand: the hideout is ready to be attacked.");
        return Task.CompletedTask;
    }
}
