using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.InRaid;
using ItemHelper = SPTarkov.Server.Core.Helpers.Items.ItemHelper;

namespace HideoutSiege.Server;

/// <summary>
/// Dying in the siege costs more than the kit you had on you. While you were down they went through your stash and took
/// the most valuable things they found, and you come round badly hurt, so you'll want to heal before your next raid. A
/// better Security station keeps more of your stash safe; a better Medstation leaves you less badly hurt. It's done as the
/// raid's result is saved, so the game shows it the moment you're back in the menu.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 51)]
public sealed class SiegeDefeat(ItemHelper itemHelper, HandbookHelper handbook, MailSendService mail, SiegeStore store, ISptLogger<SiegeDefeat> logger) : IOnLoad
{
    private static SiegeDefeat? instance;
    private void Warn(string text) => logger.Warning(text);

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        instance = this;
        var target = AccessTools.Method(typeof(LocationLifecycleService), "HandlePostRaidPmc");
        if (target is null) { logger.Warning("Hideout Last Stand: couldn't find where raids end, so dying in a siege costs only what you carried."); return Task.CompletedTask; }
        new Harmony("jrs.hideoutsiege.server.defeat").Patch(target, postfix: new HarmonyMethod(typeof(SiegeDefeat), nameof(AfterPmcRaid)));
        return Task.CompletedTask;
    }

    private static void AfterPmcRaid(MongoId sessionId, SptProfile fullServerProfile, bool isDead, string locationName)
    {
        if (!isDead || !string.Equals(locationName, SiegeIds.LocationId, StringComparison.OrdinalIgnoreCase)) return;
        try { instance?.Punish(sessionId, fullServerProfile.CharacterData?.PmcData); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't settle your defeat: " + e); }
    }

    /// <summary>
    /// How many things they take: more the tougher they were (the threat level, 1 to 4), fewer with a better Security
    /// station (one fewer at level 1 or 2, two fewer at 3), never fewer than two.
    /// </summary>
    public static int MostTaken(int tier, int security) => Math.Max(2, 2 + Math.Clamp(tier, 1, 4) - (Math.Clamp(security, 0, 3) + 1) / 2);

    private void Punish(MongoId session, PmcData? pmc)
    {
        if (pmc?.Inventory?.Items is null) return;
        int security = Level(pmc, HideoutAreas.Security), medstation = Level(pmc, HideoutAreas.MedStation);
        int tier = store.LastTier(session.ToString());
        int take = MostTaken(tier, security);
        double health = .12 + .08 * medstation;

        var stolen = Steal(pmc, take);
        var wrecked = Wreck(pmc);
        Hurt(pmc, health);
        store.RecordDefeat(session.ToString(), new SiegeDefeatReport
        {
            Taken = stolen.Select(t => new SiegeTaken { Name = t.Name, Worth = Math.Round(t.Worth) }).ToList(),
            Wrecked = wrecked, Tier = tier, MostTaken = take,
            Health = health, Energy = LeftOver, Hydration = LeftOver, Security = security, Medstation = medstation,
        });

        string what = stolen.Count == 0 ? "nothing worth taking" : string.Join(", ", stolen.Select(t => t.Name));
        string broke = wrecked.Count == 0 ? "" : " On the way out they wrecked your " + string.Join(" and ", wrecked.Select(w => $"{w.Name} ({w.Was} to {w.Now})")) + ".";
        mail.SendDirectNpcMessageToPlayer(session, "54cb50c76803fa8b248b4571", MessageType.MessageWithItems,
            $"They overran your hideout. While you were down they went through your stash and took {what}.{broke} You came round badly hurt: patch yourself up before your next raid.",
            new List<Item>(), 604800);
        logger.Info($"Hideout Last Stand: lost at tier {tier}. Taken from the stash ({take} at most): {what}.{broke} Health left at {health:P0}.");
    }

    private static int Level(PmcData pmc, HideoutAreas area) => pmc.Hideout?.Areas?.FirstOrDefault(a => a.Type == area)?.Level ?? 0;

    // What they can knock down a level: only stations that hold nothing, make nothing and give no bonus a profile keeps
    // (so nothing's lost but the level), and the front door's own upgrades. Never below level 1.
    private static readonly (HideoutAreas Area, string Name)[] Wreckable =
    {
        (HideoutAreas.Security, "Security"), (HideoutAreas.Vents, "Vents"), (HideoutAreas.Illumination, "Illumination"),
    };

    private readonly Random random = new();

    // One thing, picked at random from what they could get at: you rebuild it as you built it.
    private List<SiegeWrecked> Wreck(PmcData pmc)
    {
        var choices = new List<(SiegeWrecked What, Action Do)>();
        int door = DoorUpgrades.Level(pmc);
        if (door > 0 && pmc.Hideout?.Improvements is not null)
        {
            var top = DoorUpgrades.All[door - 1];
            string was = door == 1 ? "Reinforced" : "Barred", now = door == 1 ? "Plain" : "Reinforced";
            choices.Add((new SiegeWrecked { Name = "Front door", Was = was, Now = now }, () => pmc.Hideout.Improvements.Remove(top.Key)));
        }
        foreach (var (type, name) in Wreckable)
        {
            var area = pmc.Hideout?.Areas?.FirstOrDefault(a => a.Type == type);
            if (area?.Level is not int level || level < 2 || area.Constructing == true) continue;
            choices.Add((new SiegeWrecked { Name = name, Was = $"level {level}", Now = $"level {level - 1}" }, () => area.Level = level - 1));
        }
        if (choices.Count == 0) return [];
        var (what, wreck) = choices[random.Next(choices.Count)];
        wreck();
        return [what];
    }

    // The most valuable things in the stash, each with whatever's fitted to it: not money, and not a case with things in it.
    private List<(string Name, double Worth)> Steal(PmcData pmc, int count)
    {
        var items = pmc.Inventory!.Items!;
        string stash = pmc.Inventory.Stash?.ToString() ?? "";
        var children = items.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!);
        var byId = items.ToDictionary(i => i.Id.ToString());
        bool InStash(Item item)
        {
            for (int depth = 0; depth < 12 && item.ParentId is not null; depth++)
            {
                if (item.ParentId == stash) return true;
                if (!byId.TryGetValue(item.ParentId, out var up)) return false;
                item = up;
            }
            return false;
        }
        IEnumerable<Item> Fitted(Item item) => children[item.Id.ToString()].SelectMany(c => new[] { c }.Concat(Fitted(c)));
        double Value(Item item) => handbook.GetTemplatePrice(item.Template) * (item.Upd?.StackObjectsCount ?? 1) + Fitted(item).Sum(c => handbook.GetTemplatePrice(c.Template));

        var picked = items
            .Where(i => i.Location is not null && InStash(i))
            .Where(i => !itemHelper.IsOfBaseclass(i.Template, BaseClasses.MONEY))
            .Where(i => !children[i.Id.ToString()].Any(c => c.Location is not null))
            .Select(i => (Item: i, Worth: Value(i)))
            .Where(p => p.Worth > 0)
            .OrderByDescending(p => p.Worth)
            .Take(count)
            .ToList();
        var gone = new HashSet<string>();
        foreach (var (item, _) in picked)
        {
            gone.Add(item.Id.ToString());
            foreach (var c in Fitted(item)) gone.Add(c.Id.ToString());
        }
        items.RemoveAll(i => gone.Contains(i.Id.ToString()));
        return picked.Select(p => (itemHelper.GetItemName(p.Item.Template), p.Worth)).ToList();
    }

    // How much energy and water you come round with.
    private const double LeftOver = .3;

    private static void Hurt(PmcData pmc, double share)
    {
        var parts = pmc.Health?.BodyParts;
        if (parts is not null)
            foreach (var part in parts.Values)
                if (part.Health?.Maximum is double max) part.Health.Current = Math.Max(1, Math.Round(max * share));
        if (pmc.Health?.Hydration?.Maximum is double water) pmc.Health.Hydration.Current = Math.Round(water * LeftOver);
        if (pmc.Health?.Energy?.Maximum is double energy) pmc.Health.Energy.Current = Math.Round(energy * LeftOver);
    }
}
