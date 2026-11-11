using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils.Cloners;

namespace HideoutSiege.Server;

public sealed class SiegeGearData : SPTarkov.Server.Core.Models.Utils.IRequestData
{
    // Every copy still lying where it was put, and everything still in it.
    public List<string>? Untouched { get; set; }
    // What's in the stash box (each thing with what's in or on it), for your real stash.
    public List<Item>? Deposited { get; set; }
    // New things in copies still lying about (loot put in a bag from your racks), for your real stash.
    public List<Item>? Added { get; set; }
    // After holding out: your own things left lying about in the hideout (each with what's in it), for your real stash.
    public List<Item>? Left { get; set; }
}

/// <summary>
/// What's on your real weapon racks and gear stands comes to the siege, with spare magazines from your stash that fit those guns.
/// The raid gets copies, as loose loot. The real things aren't touched until the end, when the client says which copies are still
/// where they were: the original of anything else is gone, because it's on you now or it died with you. So a crash costs nothing.
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 50)]
public sealed class SiegeRackGear(ProfileHelper profiles, SaveServer saves, SPTarkov.Server.Core.Helpers.Items.ItemHelper itemHelper, ICloner cloner,
    InventoryHelper inventoryHelper, SPTarkov.Server.Core.Services.Commerce.MailSendService mail, ISptLogger<SiegeRackGear> logger) : IOnLoad
{
    private const string WeaponRack = "24", SecondRack = "25", GearStands = "26";
    private const int MagazinesPerGun = 4, MostMagazines = 16;
    // Beside the beds: if a copy can't be put on a rack, this is where it lands.
    private static readonly SPTarkov.Server.Core.Models.Eft.Common.Vector3 DropPoint = new(15.9f, 1.6f, -.5f);
    private static readonly HashSet<string> GearSlots = new()
    {
        "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster", "ArmorVest", "TacticalVest", "Headwear", "Backpack", "Earpiece", "FaceCover", "Eyewear",
    };

    private static SiegeRackGear? instance;
    // Shared, whichever instance the game's own loot generation and the end-of-raid route each get: kept on one, it was empty
    // on the other, so nothing taken from the racks ever went from the real ones.
    private static readonly Dictionary<string, Dictionary<string, MongoId>> copies = new();

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        instance = this;
        var harmony = new Harmony("jrs.hideoutsiege.server");
        var target = AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.GenerateLocationAndLoot));
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(SiegeRackGear), nameof(AfterLootGenerated)));
        var raidEnd = AccessTools.Method(typeof(LocationLifecycleService), "HandlePostRaidPmc");
        if (raidEnd != null) harmony.Patch(raidEnd, postfix: new HarmonyMethod(typeof(SiegeRackGear), nameof(AfterPmcRaid)));
        return Task.CompletedTask;
    }

    private static void AfterLootGenerated(MongoId sessionId, string name, bool generateLoot, LocationBase __result)
    {
        if (!generateLoot || !string.Equals(name, SiegeIds.LocationId, StringComparison.OrdinalIgnoreCase)) return;
        try { instance?.AddStashBox(sessionId, __result); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't put your stash in the siege: " + e); }
        try { instance?.AddTo(sessionId, __result); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't bring your rack gear in: " + e); }
    }

    // Sieges whose stash box hasn't been emptied into the real stash yet: it's done once, whichever report comes first.
    private static readonly HashSet<string> open = new();

    // An empty box for the siege's Stash station, by the beds to start with: the game moves it into the station.
    private void AddStashBox(MongoId session, LocationBase location)
    {
        var box = new SptLootItem { Id = new MongoId(SiegeIds.StashBox), Template = new MongoId(SiegeIds.StashBoxTemplate) };
        location.Loot = (location.Loot ?? []).Append(new SpawnpointTemplate
        {
            Id = "hideoutsiege stash",
            IsContainer = false,
            UseGravity = false,
            RandomRotation = false,
            Position = DropPoint,
            Rotation = new SPTarkov.Server.Core.Models.Eft.Common.Vector3(0f, 0f, 0f),
            IsGroupPosition = false,
            GroupPositions = [],
            IsAlwaysSpawn = true,
            Root = SiegeIds.StashBox,
            Items = [box],
        }).ToList();
        lock (open) open.Add(session.ToString());
    }

    private void AddTo(MongoId session, LocationBase location)
    {
        var inventory = profiles.GetPmcProfile(session)?.Inventory;
        if (inventory?.Items is null || inventory.HideoutAreaStashes is null) return;
        var children = inventory.Items.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!);
        var roots = new List<Item>();
        var mannequins = new Dictionary<Item, int>();
        foreach (string rack in new[] { WeaponRack, SecondRack })
            if (inventory.HideoutAreaStashes.TryGetValue(rack, out var id))
                roots.AddRange(children[id.ToString()]);
        if (inventory.HideoutAreaStashes.TryGetValue(GearStands, out var standsId))
            foreach (var stand in children[standsId.ToString()])
                foreach (var worn in children[stand.Id.ToString()].Where(i => i.SlotId is not null && GearSlots.Contains(i.SlotId)))
                {
                    roots.Add(worn);
                    // Its slot on the stand is Stand1, Stand2 or Stand3.
                    char number = (stand.SlotId ?? "").LastOrDefault();
                    mannequins[worn] = char.IsDigit(number) ? number - '0' : 1;
                }
        roots.AddRange(SpareMagazines(inventory, children, roots));

        var map = new Dictionary<string, MongoId>();
        var loot = new List<SpawnpointTemplate>();
        foreach (var root in roots) loot.Add(Copy(root, children, map, mannequins.GetValueOrDefault(root)));
        location.Loot = (location.Loot ?? []).Concat(loot).ToList();
        copies[session.ToString()] = map;
        logger.Info($"Hideout Last Stand: {loot.Count} things brought in from your racks, gear stands and stash.");
    }

    // Fullest first, from the top level of the stash: anything inside a case or a rig stays where you put it.
    private IEnumerable<Item> SpareMagazines(BotBaseInventory inventory, ILookup<string, Item> children, List<Item> roots)
    {
        var fits = new HashSet<MongoId>();
        int guns = 0;
        foreach (var root in roots)
        {
            var (known, template) = itemHelper.GetItem(root.Template);
            if (!known || template?.Properties?.Slots is null) continue;
            var slot = template.Properties.Slots.FirstOrDefault(s => s.Name == "mod_magazine");
            if (slot?.Properties?.Filters is null) continue;
            guns++;
            foreach (var filter in slot.Properties.Filters)
                if (filter.Filter is not null) fits.UnionWith(filter.Filter);
        }
        if (guns == 0 || inventory.Stash is null) return [];
        return children[inventory.Stash.Value.ToString()]
            .Where(i => fits.Contains(i.Template))
            .OrderByDescending(i => children[i.Id.ToString()].Sum(round => round.Upd?.StackObjectsCount ?? 0))
            .Take(Math.Min(guns * MagazinesPerGun, MostMagazines))
            .ToList();
    }

    private SpawnpointTemplate Copy(Item root, ILookup<string, Item> children, Dictionary<string, MongoId> map, int mannequin)
    {
        var tree = new List<SptLootItem>();
        var newIds = new Dictionary<string, MongoId>();
        void Visit(Item item)
        {
            var id = new MongoId(SiegeIds.GearPrefix + mannequin + (copiesMade++).ToString("x15"));
            newIds[item.Id.ToString()] = id;
            map[id.ToString()] = item.Id;
            bool isRoot = ReferenceEquals(item, root);
            tree.Add(new SptLootItem
            {
                Id = id,
                Template = item.Template,
                Upd = cloner.Clone(item.Upd),
                ParentId = isRoot ? null : newIds[item.ParentId!].ToString(),
                SlotId = isRoot ? null : item.SlotId,
                Location = isRoot ? null : item.Location,
            });
            foreach (var child in children[item.Id.ToString()]) Visit(child);
        }
        Visit(root);
        return new SpawnpointTemplate
        {
            Id = "hideoutsiege " + newIds[root.Id.ToString()],
            IsContainer = false,
            UseGravity = true,
            RandomRotation = false,
            Position = DropPoint,
            Rotation = new SPTarkov.Server.Core.Models.Eft.Common.Vector3(0f, 0f, 0f),
            IsGroupPosition = false,
            GroupPositions = [],
            IsAlwaysSpawn = true,
            Root = newIds[root.Id.ToString()].ToString(),
            Items = tree,
        };
    }

    private long copiesMade;

    private void Warn(string text) => logger.Error(text);

    public void Finish(MongoId session, SiegeGearData data)
    {
        var pmc = profiles.GetPmcProfile(session);
        var inventory = pmc?.Inventory;
        bool changed = false;
        if (!copies.Remove(session.ToString(), out var map))
            logger.Info("Hideout Last Stand: told what's still on your racks, but nothing was brought in for this raid (or it's been settled already).");
        else
        {
            var untouched = new HashSet<string>(data.Untouched ?? []);
            var gone = map.Where(pair => !untouched.Contains(pair.Key)).Select(pair => pair.Value.ToString()).ToHashSet();
            if (gone.Count == 0 || inventory?.Items is null)
                logger.Info($"Hideout Last Stand: everything from your racks and stash is still there ({untouched.Count} untouched).");
            else
            {
                int before = inventory.Items.Count;
                inventory.Items.RemoveAll(item => gone.Contains(item.Id.ToString()));
                logger.Info($"Hideout Last Stand: {before - inventory.Items.Count} items taken from your racks and stash in the siege.");
                changed = true;
            }
        }
        bool settle;
        lock (open) settle = open.Remove(session.ToString());
        if (settle && pmc is not null)
        {
            // What you put in the stash box, loot left in a bag from your racks, and (after holding out) your own things left
            // lying about. The game writes each thing at the top of a list without its parent: those are what go in the stash.
            var trees = new List<List<Item>>();
            foreach (var list in new[] { data.Deposited, data.Added, data.Left }) trees.AddRange(Tops(list));
            if (trees.Count > 0) changed |= IntoStash(session, pmc, trees);
        }
        if (changed) _ = saves.SaveProfileAsync(session);
    }

    private const string LooseTop = "hideoutsiege loose";

    // Each thing whose parent isn't in the list (none, or the box or bag it was in), with what's in or on it.
    private List<List<Item>> Tops(List<Item>? list)
    {
        if (list is null || list.Count == 0) return [];
        var ids = new HashSet<string>(list.Select(i => i.Id.ToString()));
        var marked = list.Select(i =>
        {
            if (i.ParentId is not null && ids.Contains(i.ParentId)) return i;
            var top = cloner.Clone(i)!;
            top.ParentId = LooseTop;
            return top;
        }).ToList();
        return Trees(marked, parent => parent == LooseTop);
    }

    // Each thing whose parent is one of these, with everything in it or fitted to it.
    private static List<List<Item>> Trees(List<Item>? flat, Func<string, bool> isTop)
    {
        var trees = new List<List<Item>>();
        if (flat is null || flat.Count == 0) return trees;
        // The stash box itself never goes in: only what was in it.
        flat = flat.Where(i => i.Id.ToString() != SiegeIds.StashBox).ToList();
        var byParent = flat.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!);
        foreach (var top in flat.Where(i => i.ParentId is not null && isTop(i.ParentId)))
        {
            var tree = new List<Item>();
            void Visit(Item item, int depth)
            {
                if (depth > 24) return;
                tree.Add(item);
                foreach (var inner in byParent[item.Id.ToString()]) Visit(inner, depth + 1);
            }
            Visit(top, 0);
            trees.Add(tree);
        }
        return trees;
    }

    private static readonly System.Reflection.MethodInfo? StashMap = AccessTools.Method(typeof(InventoryHelper), "GetStashSlotMap");

    // Into the real stash, each thing with new ids (so nothing you had can be doubled), wherever there's room. What won't
    // fit comes by message instead, so nothing's lost.
    private bool IntoStash(MongoId session, PmcData pmc, List<List<Item>> trees)
    {
        if (pmc.Inventory?.Items is null || pmc.Inventory.Stash is null || StashMap is null) return false;
        string stash = pmc.Inventory.Stash.Value.ToString();
        var room = StashMap.Invoke(inventoryHelper, [pmc]) as int[,];
        if (room is null) { logger.Warning("Hideout Last Stand: couldn't size up your stash, so what you put away comes by message."); }
        var kept = new List<string>();
        var overflow = new List<Item>();
        var parcel = new MongoId();
        foreach (var tree in trees)
        {
            if (tree.Any(item => !Known(item.Template))) { logger.Warning($"Hideout Last Stand: skipped something put away whose item isn't known ({tree[0].Template})."); continue; }
            var fresh = Renumbered(tree, stash);
            fresh[0].Location = null;
            var spot = room is null ? null : inventoryHelper.PlaceItemInContainer(room, fresh, stash, "hideout");
            if (spot?.Success == true) pmc.Inventory.Items.AddRange(fresh);
            else
            {
                fresh[0].ParentId = parcel.ToString();
                fresh[0].SlotId = "main";
                fresh[0].Location = null;
                overflow.AddRange(fresh);
            }
            kept.Add(itemHelper.GetItemName(tree[0].Template));
        }
        if (overflow.Count > 0)
            mail.SendSystemMessageToPlayer(session, "Your stash was full, so here's what you put away in the siege that wouldn't fit.", overflow, 604800);
        logger.Info($"Hideout Last Stand: put away in your stash from the siege: {(kept.Count == 0 ? "nothing" : string.Join(", ", kept))}{(overflow.Count > 0 ? " (some by message: the stash was full)" : "")}.");
        return kept.Count > 0;
    }

    private bool Known(MongoId template)
    {
        var (known, _) = itemHelper.GetItem(template);
        return known;
    }

    private List<Item> Renumbered(List<Item> tree, string parent)
    {
        var ids = new Dictionary<string, MongoId>();
        foreach (var item in tree) ids[item.Id.ToString()] = new MongoId();
        var copy = cloner.Clone(tree)!;
        for (int i = 0; i < copy.Count; i++)
        {
            var item = copy[i];
            item.Id = ids[tree[i].Id.ToString()];
            item.ParentId = i == 0 ? parent : item.ParentId is not null && ids.TryGetValue(item.ParentId, out var up) ? up.ToString() : parent;
        }
        return copy;
    }

    // Carried out of the siege on you, the stash box isn't kept: what was in it goes into the stash, and the box goes.
    private static void AfterPmcRaid(MongoId sessionId, SptProfile fullServerProfile, bool isDead, string locationName)
    {
        if (!string.Equals(locationName, SiegeIds.LocationId, StringComparison.OrdinalIgnoreCase)) return;
        try { instance?.DropTheBox(sessionId, fullServerProfile.CharacterData?.PmcData); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't put the stash box's things away: " + e); }
    }

    private void DropTheBox(MongoId session, PmcData? pmc)
    {
        var items = pmc?.Inventory?.Items;
        if (items is null || !items.Any(i => i.Id.ToString() == SiegeIds.StashBox)) return;
        var byParent = items.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!);
        var inside = Trees(items.Where(i => i.ParentId == SiegeIds.StashBox).SelectMany(top => Descendants(top, byParent)).ToList(), parent => parent == SiegeIds.StashBox);
        var all = new HashSet<string>(inside.SelectMany(t => t).Select(i => i.Id.ToString())) { SiegeIds.StashBox };
        items.RemoveAll(i => all.Contains(i.Id.ToString()));
        logger.Info("Hideout Last Stand: you carried the stash box out: its things go into your stash, and the box stays behind.");
        if (inside.Count > 0) IntoStash(session, pmc!, inside);
    }

    private static IEnumerable<Item> Descendants(Item top, ILookup<string, Item> byParent)
    {
        yield return top;
        foreach (var inner in byParent[top.Id.ToString()])
            foreach (var deeper in Descendants(inner, byParent)) yield return deeper;
    }
}
