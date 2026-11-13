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
    // What's in your stash box (each thing with what's in or on it), for your real stash.
    public List<Item>? Deposited { get; set; }
    // New things in copies still lying about (loot put in a bag from your racks), for your real stash.
    public List<Item>? Added { get; set; }
    // After holding out: your own things left lying about in the hideout (each with what's in it), for your real stash.
    public List<Item>? Left { get; set; }
    // The siege wasn't over when this was said (the player died and left early): what's in the box is theirs only if the hideout holds.
    public bool Pending { get; set; }
}

/// <summary>
/// What's on your real weapon racks and gear stands comes to the siege, with spare magazines from your stash that fit those guns.
/// The raid gets copies, as loose loot. The real things aren't touched until the end, when the client says which copies are still
/// where they were: the original of anything else is gone, because it's on you now or it died with you. So a crash costs nothing.
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 50)]
public sealed class SiegeRackGear(ProfileHelper profiles, SaveServer saves, SPTarkov.Server.Core.Helpers.Items.ItemHelper itemHelper, ICloner cloner,
    InventoryHelper inventoryHelper, SPTarkov.Server.Core.Services.Commerce.MailSendService mail, SiegeStore store, ISptLogger<SiegeRackGear> logger) : IOnLoad
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
        // A stash box is a Developer case nobody has, made the size the config says: that's how much each player can put
        // away in a siege. Now, before any game fetches the items.
        var (known, template) = itemHelper.GetItem(new MongoId(SiegeIds.StashBoxTemplate));
        var grid = known ? template?.Properties?.Grids?.FirstOrDefault()?.Properties : null;
        var (columns, rows) = store.StashBoxSize;
        if (grid is null) logger.Warning("Hideout Last Stand: couldn't set the size of the siege's stash boxes: they're a Developer case's size.");
        else
        {
            grid.CellsH = columns;
            grid.CellsV = rows;
        }
        return Task.CompletedTask;
    }

    private static void AfterLootGenerated(MongoId sessionId, string name, bool generateLoot, LocationBase __result)
    {
        if (!generateLoot || !string.Equals(name, SiegeIds.LocationId, StringComparison.OrdinalIgnoreCase)) return;
        // On a Fika headless it's the racks of the player it's hosting for.
        sessionId = new MongoId(SiegeOwners.Of(sessionId.ToString()));
        SiegeOwners.Began(sessionId.ToString());
        lock (held) held.Remove(sessionId.ToString());
        try { instance?.AddTo(sessionId, __result); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't bring your rack gear in: " + e); }
        try { instance?.AddStashBoxes(__result); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't put the stash boxes in the siege: " + e); }
        instance?.Opened(sessionId.ToString());
    }

    // Players whose stash box hasn't been emptied into their real stash yet: it's done once, whichever report comes first.
    private static readonly HashSet<string> open = new();

    /// <summary>This player's in a siege: what they put away in it is to go to their stash at the end.</summary>
    public void Opened(string session)
    {
        lock (open) open.Add(session);
    }

    // The boxes for the siege's Stash station, by the beds to start with: each game moves them into the station. Empty, one
    // for each player who might be there: nobody knows yet who's coming, so each player's game asks which is theirs.
    private void AddStashBoxes(LocationBase location)
    {
        var boxes = new List<SpawnpointTemplate>();
        for (int number = 0; number < SiegeIds.StashBoxes; number++)
        {
            string id = SiegeIds.StashBoxId(number);
            boxes.Add(new SpawnpointTemplate
            {
                Id = "hideoutsiege stash " + number,
                IsContainer = false,
                UseGravity = false,
                RandomRotation = false,
                Position = DropPoint,
                Rotation = new SPTarkov.Server.Core.Models.Eft.Common.Vector3(0f, 0f, 0f),
                IsGroupPosition = false,
                GroupPositions = [],
                IsAlwaysSpawn = true,
                Root = id,
                Items = [new SptLootItem { Id = new MongoId(id), Template = new MongoId(SiegeIds.StashBoxTemplate) }],
            });
        }
        location.Loot = (location.Loot ?? []).Concat(boxes).ToList();
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
        logger.Info($"Hideout Last Stand: {loot.Count} things brought in from your racks and gear stands, with spare magazines from your stash.");
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
        var tree = CopyTree(root, children, map, mannequin);
        return new SpawnpointTemplate
        {
            Id = "hideoutsiege " + tree[0].Id,
            IsContainer = false,
            UseGravity = true,
            RandomRotation = false,
            Position = DropPoint,
            Rotation = new SPTarkov.Server.Core.Models.Eft.Common.Vector3(0f, 0f, 0f),
            IsGroupPosition = false,
            GroupPositions = [],
            IsAlwaysSpawn = true,
            Root = tree[0].Id.ToString(),
            Items = tree,
        };
    }

    // A thing and everything in or on it, with copies' ids (each noted against the real one's). The root goes loose.
    private List<SptLootItem> CopyTree(Item root, ILookup<string, Item> children, Dictionary<string, MongoId> map, int mannequin)
    {
        var tree = new List<SptLootItem>();
        var newIds = new Dictionary<string, MongoId>();
        void Visit(Item item)
        {
            var id = new MongoId(SiegeIds.GearPrefix + mannequin + (copiesMade++).ToString("x15"));
            newIds[item.Id.ToString()] = id;
            map[id.ToString()] = item.Id;
            if (item.Upd?.SpawnedInSession != true) lock (notFound) notFound.Add(id.ToString());
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
        return tree;
    }

    // From the clock, so no two sieges' copies share an id, even across restarts (15 hex digits holds the ticks for centuries).
    private long copiesMade = DateTime.UtcNow.Ticks;

    private void Warn(string text) => logger.Error(text);

    public void Finish(MongoId session, SiegeGearData data)
    {
        var pmc = profiles.GetPmcProfile(session);
        var inventory = pmc?.Inventory;
        bool changed = false;
        // A friend helping has no racks here: their game says only what's in their stash box.
        if (data.Untouched is null) { }
        else if (!copies.Remove(session.ToString(), out var map))
            logger.Info("Hideout Last Stand: told what's still on your racks, but nothing was brought in for this raid (or it's been settled already).");
        else
        {
            var untouched = new HashSet<string>(data.Untouched ?? []);
            var gone = map.Where(pair => !untouched.Contains(pair.Key)).Select(pair => pair.Value.ToString()).ToHashSet();
            if (gone.Count == 0 || inventory?.Items is null)
                logger.Info($"Hideout Last Stand: everything from your racks is still there ({untouched.Count} untouched).");
            else
            {
                int before = inventory.Items.Count;
                inventory.Items.RemoveAll(item => gone.Contains(item.Id.ToString()));
                logger.Info($"Hideout Last Stand: {before - inventory.Items.Count} items taken from your racks in the siege.");
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
            foreach (var list in new[] { data.Added, data.Left }) trees.AddRange(Tops(list));
            // The stash box is only theirs if the hideout holds. Their game says so itself when it's over; said before
            // then, it waits for the result (or the result's already in).
            var box = Tops(data.Deposited);
            if (box.Count > 0)
            {
                string owner = SiegeOwners.SiegeOf(session.ToString());
                bool? result = data.Pending ? SiegeOwners.Result(owner) : true;
                if (result == true) trees.AddRange(box);
                else if (result == false) logger.Info($"Hideout Last Stand: the hideout fell, so the {box.Count} things in {session}'s stash box are gone.");
                else
                {
                    lock (held)
                    {
                        if (!held.TryGetValue(owner, out var waiting)) held[owner] = waiting = [];
                        waiting.Add((session.ToString(), box));
                    }
                    logger.Info($"Hideout Last Stand: {session} left before the siege was over: the {box.Count} things in their stash box wait to see if the hideout holds.");
                }
            }
            if (trees.Count > 0) changed |= IntoStash(session, pmc, trees);
        }
        if (changed) _ = saves.SaveProfileAsync(session);
    }

    // Stash boxes of players who left a siege before it was over, by whose siege it is.
    private static readonly Dictionary<string, List<(string Session, List<List<Item>> Box)>> held = new();

    /// <summary>The siege is over. Whoever left early gets what was in their stash box by message if the hideout held, and nothing if it fell.</summary>
    public void Settle(string owner, bool victory)
    {
        List<(string Session, List<List<Item>> Box)>? waiting;
        lock (held)
        {
            if (!held.Remove(owner, out waiting)) return;
        }
        foreach (var (session, box) in waiting)
        {
            if (!victory) { logger.Info($"Hideout Last Stand: the hideout fell, so the {box.Count} things in {session}'s stash box are gone."); continue; }
            var parcel = new MongoId();
            var items = new List<Item>();
            foreach (var tree in box.Where(tree => tree.All(item => Known(item.Template))))
            {
                var fresh = Renumbered(tree, parcel.ToString());
                fresh[0].SlotId = "main";
                fresh[0].Location = null;
                items.AddRange(fresh);
            }
            if (items.Count == 0) continue;
            mail.SendSystemMessageToPlayer(new MongoId(session), "The hideout held after you went down. Here's what you'd put in your stash box.", items, 604800);
            logger.Info($"Hideout Last Stand: the hideout held: {session} gets the {box.Count} things from their stash box by message.");
        }
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
        // A stash box itself never goes in: only what was in it.
        flat = flat.Where(i => !SiegeIds.IsStashBox(i.Id.ToString())).ToList();
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
        try { instance?.NotFoundInRaid(fullServerProfile.CharacterData?.PmcData); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't put back what wasn't found in raid: " + e); }
        try { instance?.Renumber(fullServerProfile.CharacterData?.PmcData); }
        catch (Exception e) { instance?.Warn("Hideout Last Stand: couldn't give what you came out with ordinary ids: " + e); }
    }

    // What you came out with from your racks or stash still has a copy's id. Kept, a later siege's copy could be given the
    // same one (and one of the two was lost), and it would count as a copy itself: so each gets an ordinary id.
    private void Renumber(PmcData? pmc)
    {
        var inventory = pmc?.Inventory;
        if (inventory?.Items is null) return;
        var fresh = new Dictionary<string, MongoId>();
        foreach (var item in inventory.Items)
            if (item.Id.ToString().StartsWith(SiegeIds.GearPrefix)) fresh[item.Id.ToString()] = new MongoId();
        if (fresh.Count == 0) return;
        foreach (var item in inventory.Items)
        {
            if (fresh.TryGetValue(item.Id.ToString(), out var id)) item.Id = id;
            if (item.ParentId is not null && fresh.TryGetValue(item.ParentId, out var parent)) item.ParentId = parent.ToString();
        }
        if (inventory.FastPanel is not null)
            foreach (var key in inventory.FastPanel.Keys.ToList())
                if (fresh.TryGetValue(inventory.FastPanel[key].ToString(), out var id)) inventory.FastPanel[key] = id;
        if (inventory.FavoriteItems is not null)
            inventory.FavoriteItems = inventory.FavoriteItems.Select(f => fresh.TryGetValue(f.ToString(), out var id) ? id : f).ToList();
        logger.Info($"Hideout Last Stand: {fresh.Count} things you came out with from your racks and stash are yours for good now.");
    }

    // Copies of your own things that weren't found in raid: the game counts whatever you come out with as found, and taking
    // a thing from your racks or stash in the siege would have made it so. They go back to how they were.
    private static readonly HashSet<string> notFound = new();

    private void NotFoundInRaid(PmcData? pmc)
    {
        if (pmc?.Inventory?.Items is null) return;
        int put = 0;
        lock (notFound)
            foreach (var item in pmc.Inventory.Items)
                if (item.Upd?.SpawnedInSession == true && notFound.Contains(item.Id.ToString()))
                {
                    item.Upd.SpawnedInSession = false;
                    put++;
                }
        if (put > 0) logger.Info($"Hideout Last Stand: {put} things of yours you came out with weren't found in raid, and still aren't.");
    }

    private void DropTheBox(MongoId session, PmcData? pmc)
    {
        var items = pmc?.Inventory?.Items;
        if (items is null || !items.Any(i => SiegeIds.IsStashBox(i.Id.ToString()))) return;
        var byParent = items.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!);
        var inside = Trees(items.Where(i => i.ParentId is not null && SiegeIds.IsStashBox(i.ParentId)).SelectMany(top => Descendants(top, byParent)).ToList(), SiegeIds.IsStashBox);
        var all = new HashSet<string>(inside.SelectMany(t => t).Select(i => i.Id.ToString()));
        all.UnionWith(items.Select(i => i.Id.ToString()).Where(SiegeIds.IsStashBox));
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
