using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// You were asleep, so your gear's where you left it: guns and magazines on the weapon racks, armour, rig, helmet and
    /// backpack on the gear rack, with everything still in them. Meds and injectors are in your pockets. A rack that isn't
    /// built leaves those things on you. Nothing needs searching and the guns are ready to fire.
    /// Each player does this for themselves, through the game's normal item drop, so everyone else sees it too.
    /// </summary>
    internal static class SiegeGear
    {
        private static readonly EquipmentSlot[] Guns = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };
        private static readonly EquipmentSlot[] Gear = { EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear };

        private static readonly Dictionary<EBoundItem, Item> binds = new Dictionary<EBoundItem, Item>();
        private static float nextBindCheck;
        private static bool laidOut, putAway;
        private static Func<LootItem, bool> placing;
        private static int placed;

        // Done, including when something went wrong with it and there's nothing more to wait for.
        internal static bool Finished { get; private set; }

        /// <summary>Your own things, as they were laid out at the start (guns, magazines, armour, rig, bag, helmet).</summary>
        internal static readonly List<Item> LaidOut = new List<Item>();

        /// <summary>Those of them still lying loose somewhere in the hideout: not on you, not on anyone, not in anything.</summary>
        internal static List<Item> LeftLying(Player me) =>
            LaidOut.Where(item => item?.Owner != null && ReferenceEquals(item.Owner.RootItem, item) && !ReferenceEquals(item.Owner, me?.InventoryController)).ToList();

        internal static IEnumerator LayOut()
        {
            if (laidOut) yield break;
            laidOut = true;
            yield return Arrange();
            Finished = true;
        }

        private static IEnumerator Arrange()
        {
            var world = Singleton<GameWorld>.Instance;
            var me = world?.MainPlayer;
            if (world == null) yield break;
            // A Fika headless has no player of its own, but the rack gear and the stash box still go where they belong.
            if (me == null) { PutUpRackGear(world, null, new SiegeRacks(null)); yield break; }
            var controller = me.InventoryController;
            var equipment = controller.Inventory.Equipment;

            bool handsFree = false;
            me.SetEmptyHands(_ => handsFree = true);
            float giveUp = Time.time + 4f;
            while (!handsFree && Time.time < giveUp) yield return null;

            var guns = Guns.Select(slot => equipment.GetSlot(slot).ContainedItem).Where(item => item != null).ToList();
            var gear = Gear.Select(slot => equipment.GetSlot(slot).ContainedItem).Where(item => item != null).ToList();
            foreach (var item in guns.Concat(gear)) MakeKnown(controller, item);
            yield return MedsToPockets(controller, gear);
            foreach (var gun in guns.OfType<Weapon>()) yield return Reload(controller, gun);

            // Quick slots as they are now: anything that fitted in your pockets just stays bound.
            binds.Clear();
            foreach (var bind in controller.Inventory.FastAccess.BoundItems)
                if (bind.Value != null) binds[bind.Key] = bind.Value;

            var racks = new SiegeRacks(me);
            // With nothing built to put them on, they stay on you: guns in hand without a weapon rack, armour, rig and bag
            // worn without a gear stand. So a siege before you've built those starts the way you came in, not with it all
            // on the floor by the bed.
            if (!racks.HaveGunRacks) guns.Clear();
            if (!racks.HaveStand) gear.Clear();

            // Out of the rig and backpack before those go anywhere (and only when there's a rack for them).
            var magazines = racks.HaveGunRacks
                ? gear.SelectMany(item => item.GetAllItems()).OfType<Magazine>().Where(m => !(m.Parent.Container is Slot)).ToList()
                : new List<Magazine>();

            var everything = new List<(Item Item, Func<LootItem, bool> Spot)>();
            foreach (var gun in guns) everything.Add((gun, racks.HangGun));
            foreach (var magazine in magazines) everything.Add((magazine, racks.HangMagazine));
            foreach (var item in gear) everything.Add((item, racks.StandUp));
            LaidOut.Clear();
            LaidOut.AddRange(everything.Select(e => e.Item));

            Vector2 facing = me.Rotation;
            world.OnThrowItem += Place;
            int thrown = 0;
            placed = 0;
            foreach (var (item, spot) in everything)
            {
                // Anything without a rack goes on the floor, spread out in front of the bed.
                float across = everything.Count > 1 ? thrown / (everything.Count - 1f) : .5f;
                me.Rotation = new Vector2(facing.x + Mathf.Lerp(-80f, 80f, across), 15f);
                placing = spot;
                bool done = false;
                controller.ThrowItem(item, false, _ => done = true);
                giveUp = Time.time + 1f;
                while (!done && Time.time < giveUp) yield return null;
                thrown++;
            }
            placing = null;
            world.OnThrowItem -= Place;
            // In co-op it's still this player's: anyone else asks before taking it.
            SiegeAsk.Announce(me.ProfileId, LaidOut.Select(item => item.Id).ToArray());
            PutUpRackGear(world, controller, racks);
            me.Rotation = facing;
            putAway = true;
            Plugin.Log.LogInfo($"[siege] Put {placed} things on racks and {thrown - placed} on the floor. {binds.Count} quick slots.");
        }

        // Copies of what's on your real racks and stands, from the server. In the same order for everyone, from the bottom of the racks.
        private static void PutUpRackGear(GameWorld world, InventoryController controller, SiegeRacks racks)
        {
            racks.FromTheBottom();
            int count = 0, onRacks = 0;
            foreach (var loot in world.LootList.OfType<LootItem>().Where(l => IsRackGear(l)).OrderBy(l => l.ItemId, StringComparer.Ordinal).ToList())
            {
                try
                {
                    count++;
                    if (controller != null) MakeKnown(controller, loot.Item);
                    int mannequin = MannequinOf(loot);
                    bool put = mannequin != 0 ? racks.AtMannequin(loot, mannequin)
                        : loot.Item is Weapon ? racks.HangGun(loot) : loot.Item is Magazine ? racks.HangMagazine(loot) : racks.StandUp(loot);
                    if (put) onRacks++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[siege] Couldn't put up {loot.Item?.ShortName}: {e.Message}"); }
            }
            if (count > 0) Plugin.Log.LogInfo($"[siege] Brought in {count} things from your racks and stash: {onRacks} put up, the rest by the beds.");
            SiegeStash.Tuck(world, controller);
            // Only the player whose hideout it is has the items they're dressed from. On other games the gear stays at the foot of the stand.
            if (SiegeOwner.IsMe && world.MainPlayer != null) Plugin.Instance.StartCoroutine(SiegeMannequins.Build(world.MainPlayer));
            // What's still on the racks is told to the server the moment you die, while it's all still here to look at (the
            // raid's end can come after the world's gone). Not when hosting for friends: they can still take things after you go down.
            if (SiegeOwner.IsMe && world.MainPlayer != null)
                world.MainPlayer.OnPlayerDeadOrUnspawn += _ => { if (!Net.HadPeers) SiegeBackend.ReportRackGear(); };
        }

        // The digit after the prefix says which mannequin it came from: 0 if it wasn't one.
        internal static int MannequinOf(LootItem loot) => IsRackGear(loot) && loot.ItemId.Length > SiegeIds.GearPrefix.Length ? loot.ItemId[SiegeIds.GearPrefix.Length] - '0' : 0;

        // Armour and rig before what goes in them, then guns.
        internal static IEnumerator TakeLoadout(Player me, int mannequin)
        {
            var world = Singleton<GameWorld>.Instance;
            if (world == null) yield break;
            var loadout = world.LootList.OfType<LootItem>().Where(l => MannequinOf(l) == mannequin).OrderBy(l => l.Item is Weapon ? 1 : 0).ToList();
            int taken = 0;
            foreach (var loot in loadout)
            {
                var item = loot.Item;
                yield return Move(me, item, mannequin);
                if (item.Owner == me.InventoryController) taken++;
            }
            me.UpdateInteractionCast();
            SiegeState.Notify(taken == loadout.Count ? "Loadout taken" : $"Took {taken} of {loadout.Count}  ·  no room for the rest");
        }

        internal static IEnumerator TakeOne(Player me, LootItem loot, int mannequin)
        {
            var item = loot.Item;
            yield return Move(me, item, mannequin);
            me.UpdateInteractionCast();
            if (item.Owner != me.InventoryController) SiegeState.Notify("No room");
        }

        private static IEnumerator Move(Player me, Item item, int mannequin)
        {
            var controller = me.InventoryController;
            var move = ItemManipulator.QuickFindAppropriatePlace(item, controller, new[] { controller.Inventory.Equipment }, ItemManipulator.EMoveItemOrder.PickUp, true);
            if (move.Failed || !controller.CanExecute(move.Value)) yield break;
            controller.RunNetworkTransaction(move.Value, null);
            float giveUp = Time.time + 1f;
            while (item.Owner != controller && Time.time < giveUp) yield return null;
            if (mannequin != 0 && item.Owner == controller) SiegeMannequins.Undress(mannequin, item);
        }

        // Your emptiest on the floor, and this one in its place.
        internal static IEnumerator SwapMagazine(Player me, Magazine spent, LootItem loot)
        {
            var controller = me.InventoryController;
            var item = loot.Item;
            bool thrown = false;
            controller.ThrowItem(spent, false, _ => thrown = true);
            float giveUp = Time.time + 1f;
            while (!thrown && Time.time < giveUp) yield return null;
            yield return Move(me, item, 0);
            me.UpdateInteractionCast();
            if (item.Owner != controller) SiegeState.Notify("No room");
        }

        internal static bool IsRackGear(LootItem loot) => loot != null && loot.ItemId != null && loot.ItemId.StartsWith(SiegeIds.GearPrefix);

        // Puts it where it belongs. Whatever has no place carries on as an ordinary drop, on to the floor.
        // Either way it's an ordinary loose item, so Take works as normal.
        private static void Place(LootItem loot)
        {
            if (placing == null) return;
            var spot = placing;
            placing = null;
            try { if (spot(loot)) placed++; }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't place " + loot.Item?.ShortName + ": " + e.Message); }
        }

        // No searching: it's your own stuff. A rig or bag needs searching while anything in it is unknown where it lies, which
        // is a different thing from whether you've ever examined that kind of item (it was only marked known if you hadn't, so
        // the rig you'd packed yourself still had to be searched).
        internal static void MakeKnown(InventoryController controller, Item root)
        {
            var search = (IPlayerSearchController)controller.SearchController;
            foreach (var item in root.GetAllItems())
            {
                if (item.Parent != null && !search.IsItemKnown(item)) search.SetItemAsKnown(item, false);
                if (item is SearchableItem container && !search.IsSearched(container)) search.SetItemAsSearched(container);
            }
        }

        // You'd sleep with your meds on you. What doesn't fit stays in the rig or backpack, and its quick slot comes back with it.
        private static IEnumerator MedsToPockets(InventoryController controller, List<Item> gear)
        {
            if (!(controller.Inventory.Equipment.GetSlot(EquipmentSlot.Pockets).ContainedItem is CompoundItem pockets)) yield break;
            var bound = controller.Inventory.FastAccess.BoundItems.Values.ToList();
            // Whatever's on a quick slot gets first go at the room.
            var meds = gear.SelectMany(item => item.GetAllItems()).OfType<Meds>().OrderByDescending(m => bound.Contains(m)).ToList();
            foreach (var med in meds)
            {
                var moved = ItemManipulator.QuickFindAppropriatePlace(med, controller, new[] { pockets }, ItemManipulator.EMoveItemOrder.PrioritizeTargetsOrder, true);
                if (moved.Failed) continue;
                yield return Run(controller, moved);
            }
        }

        // Puts your fullest spare magazine in if it beats the one in there, and a round in the chamber.
        private static IEnumerator Reload(InventoryController controller, Weapon weapon)
        {
            var slot = weapon.GetMagazineSlot();
            var magazine = weapon.GetCurrentMagazine();
            if (slot != null && (magazine == null || magazine.Count < magazine.MaxCount))
            {
                var spare = controller.Inventory.Equipment.GetAllItems().OfType<Magazine>()
                    .Where(m => !(m.Parent.Container is Slot) && slot.CanAccept(m) && m.Count > (magazine?.Count ?? 0))
                    .OrderByDescending(m => m.Count)
                    .FirstOrDefault();
                if (spare != null)
                {
                    OperationResult swapped;
                    if (magazine == null) swapped = ItemManipulator.Move(spare, slot.CreateItemAddress(), controller, true);
                    else swapped = ItemManipulator.Swap(magazine, spare.CurrentAddress, spare, magazine.CurrentAddress, controller, true);
                    if (swapped.Failed) Plugin.Log.LogInfo($"[siege] Couldn't reload {weapon.ShortName}: {swapped.Error}");
                    else
                    {
                        yield return Run(controller, swapped);
                        magazine = weapon.GetCurrentMagazine();
                    }
                }
            }
            if (!weapon.HasChambers || weapon.Chambers[0].ContainedItem != null || magazine == null || magazine.Count == 0) yield break;
            // Not the magazine's own PopTo: that one has already done it by the time it returns, and this has to go through the network.
            var top = magazine.Cartridges.Last;
            var chamber = weapon.Chambers[0].CreateItemAddress();
            OperationResult chambered;
            if (top.StackObjectsCount == 1) chambered = ItemManipulator.Move(top, chamber, controller, true);
            else chambered = ItemManipulator.SplitExact(top, 1, chamber, controller, controller, true);
            if (!chambered.Failed) yield return Run(controller, chambered);
            else Plugin.Log.LogInfo($"[siege] Couldn't chamber {weapon.ShortName}: {chambered.Error}");
        }

        private static IEnumerator Run(InventoryController controller, OperationResult operation)
        {
            bool done = false;
            controller.TryRunNetworkTransaction(operation, _ => done = true);
            float giveUp = Time.time + 1f;
            while (!done && Time.time < giveUp) yield return null;
        }

        // Quick slots come back when the item's somewhere it can be bound from, unless you've since used that key for something else.
        internal static void Update()
        {
            if (!putAway || binds.Count == 0 || Time.time < nextBindCheck) return;
            nextBindCheck = Time.time + .5f;
            var me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null) return;
            var controller = me.InventoryController;
            var bound = controller.Inventory.FastAccess.BoundItems;
            foreach (var pair in binds.ToList())
            {
                bound.TryGetValue(pair.Key, out Item now);
                if (now != null && now != pair.Value) { binds.Remove(pair.Key); continue; }
                if (pair.Value.Owner != controller || !controller.IsAtBindablePlace(pair.Value)) continue;
                binds.Remove(pair.Key);
                if (now == pair.Value) continue;
                var result = BindResult.Run(controller, pair.Value, pair.Key, true);
                if (result.Succeeded) controller.TryRunNetworkTransaction(result);
                else Plugin.Log.LogInfo($"[siege] Couldn't put {pair.Value.ShortName} back on {pair.Key}: {result.Error}");
            }
        }

        internal static void Reset()
        {
            binds.Clear();
            LaidOut.Clear();
            laidOut = putAway = Finished = false;
            placing = null;
            nextBindCheck = 0f;
        }
    }

    /// <summary>
    /// Where things go on the racks you've built. Guns go exactly where your real hideout shows them (see StashItemModel and
    /// StashWeaponItemModel in the game): each rack has a marker at its top left corner as you face it, pointing into the wall,
    /// and a grid of cells running across and down from there, with a gun's back against the marker's plane.
    /// </summary>
    internal sealed class SiegeRacks
    {
        // Cells across and down at each level of a rack, from the game's stash templates (WeaponStand_Stash_1 to 3).
        private static readonly int[] Across = { 9, 11, 13 }, Down = { 6, 6, 7 };

        private sealed class Rack
        {
            internal Transform Marker;
            internal float Step;
            internal bool[,] Taken;
            internal bool FromTheBottom;

            // The first free space this big, working along each row, as the game's own stash does.
            internal bool Fit(int wide, int tall, out int column, out int row)
            {
                int columns = Taken.GetLength(0), rows = Taken.GetLength(1);
                for (int r = 0; r + tall <= rows; r++)
                {
                    row = FromTheBottom ? rows - tall - r : r;
                    for (column = 0; column + wide <= columns; column++)
                    {
                        if (!IsFree(column, row, wide, tall)) continue;
                        for (int x = column; x < column + wide; x++)
                            for (int y = row; y < row + tall; y++)
                                Taken[x, y] = true;
                        return true;
                    }
                }
                column = row = 0;
                return false;
            }

            private bool IsFree(int column, int row, int wide, int tall)
            {
                for (int x = column; x < column + wide; x++)
                    for (int y = row; y < row + tall; y++)
                        if (Taken[x, y]) return false;
                return true;
            }
        }

        private readonly List<Rack> racks = new List<Rack>();
        private readonly Transform stand;
        private readonly Dictionary<Transform, int> gearPlaces = new Dictionary<Transform, int>();
        private readonly List<Transform> standNames = new List<Transform>();

        internal void FromTheBottom()
        {
            foreach (var rack in racks) rack.FromTheBottom = true;
        }

        internal SiegeRacks(Player me)
        {
            // Co-op players each start from a different rack and stand, as far as they go round. Everyone works this out the same.
            var everyone = SiegeDirector.Defenders().Select(p => p.ProfileId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            int seat = me == null ? 0 : Mathf.Max(0, everyone.IndexOf(me.ProfileId));

            var points = Object.FindObjectsOfType<StashItemModelSpawnPoint>();
            var levels = HideoutCopy.Levels();
            foreach (var (root, type) in new[] { ("24_weapon_stand", EAreaType.WeaponStand), ("25_weapon_stand_secondary", EAreaType.WeaponStandSecondary) })
            {
                var marker = points.FirstOrDefault(p => p.name == "SpawnPoint" && p.transform.root.name == root);
                if (marker == null) continue;
                levels.TryGetValue(type, out int level);
                if (level < 1) continue;
                int index = Mathf.Clamp(level, 1, Across.Length) - 1;
                float step = marker.GetComponentInParent<HideoutArea>()?.AreaTemplate?.stashGridStep ?? .25f;
                racks.Add(new Rack { Marker = marker.transform, Step = step, Taken = new bool[Across[index], Down[index]] });
                Plugin.Log.LogInfo($"[siege] Rack {root}: level {level}, {Across[index]}x{Down[index]} cells of {step:F3} m, top left {marker.transform.position:F2}, into the wall {marker.transform.forward:F2}, scale {marker.transform.lossyScale:F2}.");
            }
            if (racks.Count > 0)
            {
                for (int i = 0; i < seat % racks.Count; i++)
                {
                    racks.Add(racks[0]);
                    racks.RemoveAt(0);
                }
                // More players than racks: the next ones start from the bottom of them.
                if ((seat / racks.Count) % 2 == 1) foreach (var rack in racks) rack.FromTheBottom = true;
            }

            var stands = points.Where(p => p.name.StartsWith("Stand") && p.transform.root.name == "26_equipment_presets_stand").OrderBy(p => p.name).ToList();
            standNames.AddRange(stands.Select(p => p.transform));
            if (stands.Count > 0 && levels.TryGetValue(EAreaType.EquipmentPresetsStand, out int standLevel) && standLevel >= 1) stand = stands[seat % stands.Count].transform;
        }

        internal bool HaveGunRacks => racks.Count > 0;
        internal bool HaveStand => stand != null;

        // Turned the way the game turns a gun for its rack: side on, by the opposite of the turn it's shown at in a preview.
        internal bool HangGun(LootItem loot)
        {
            var pivot = loot.GetComponentInChildren<PreviewPivot>(true);
            if (pivot == null) Plugin.Log.LogWarning($"[siege] {loot.Item?.ShortName} has no preview pivot, so it may hang at an odd angle.");
            Quaternion turned = pivot != null ? Quaternion.Inverse(pivot.pivotRotation) : Quaternion.identity;
            var cells = loot.Item.CalculateCellSize();
            return Hang(loot, turned, cells.X, cells.Y);
        }

        // The rack has no way of showing these, so it's flat side to the wall and hanging upright, in whatever room the guns leave.
        internal bool HangMagazine(LootItem loot)
        {
            Transform t = loot.transform;
            Vector3 start = t.position;
            Quaternion startHow = t.rotation;
            t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Vector3 size = Box(loot).size;
            t.SetPositionAndRotation(start, startHow);
            Quaternion turned = Quaternion.identity;
            if (size.x <= size.y && size.x <= size.z) turned = Quaternion.Euler(0f, 90f, 0f);
            else if (size.y <= size.x && size.y <= size.z) turned = Quaternion.Euler(90f, 0f, 0f);
            Vector3 flat = turned * size;
            if (Mathf.Abs(flat.x) > Mathf.Abs(flat.y)) turned = Quaternion.Euler(0f, 0f, 90f) * turned;
            return Hang(loot, turned, 1, 1);
        }

        private bool Hang(LootItem loot, Quaternion turned, int atLeastWide, int atLeastTall)
        {
            Transform t = loot.transform;
            Vector3 start = t.position;
            Quaternion startHow = t.rotation;
            t.SetPositionAndRotation(Vector3.zero, turned);
            Bounds box = Box(loot);
            foreach (var rack in racks)
            {
                // Never overlapping the next thing, even where the gun's longer than its size in the stash.
                int wide = Mathf.Max(atLeastWide, Mathf.CeilToInt(box.size.x / rack.Step - .01f));
                int tall = Mathf.Max(atLeastTall, Mathf.CeilToInt(box.size.y / rack.Step - .01f));
                if (!rack.Fit(wide, tall, out int column, out int row)) continue;
                // Measured with it at the origin, this is how far its corner has to go to be on that cell.
                var local = new Vector3(rack.Step * column - box.min.x, -rack.Step * row - box.max.y, -box.max.z);
                t.SetPositionAndRotation(rack.Marker.position + rack.Marker.rotation * local, rack.Marker.rotation * turned);
                Hold(loot);
                Plugin.Log.LogInfo($"[siege] {loot.Item?.ShortName}: {box.size:F2} m, {wide}x{tall} cells, column {column} row {row} of {rack.Marker.root.name}, at {t.position:F2}.");
                return true;
            }
            t.SetPositionAndRotation(start, startHow);
            return false;
        }

        private static void Hold(LootItem loot)
        {
            var body = loot.RigidBody;
            if (body == null) return;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }

        // How big it is, by the same rules as the game's own (WeaponPreview.GetBounds), ignoring anything stray a long way off.
        private static Bounds Box(LootItem loot)
        {
            Vector3 at = loot.transform.position;
            Bounds? all = null;
            foreach (var part in loot.GetComponentsInChildren<Renderer>(false))
            {
                if (part is ParticleSystemRenderer || part.name.Contains("linza") || part.name == "MuzzleJetCombinedMesh") continue;
                Bounds b = part.bounds;
                if (b.extents == Vector3.zero || (b.center - at).sqrMagnitude > 4f) continue;
                if (all.HasValue)
                {
                    Bounds grown = all.Value;
                    grown.Encapsulate(b);
                    all = grown;
                }
                else all = b;
            }
            return all ?? new Bounds(at, Vector3.one * .1f);
        }

        internal bool StandUp(LootItem loot) => stand != null && PutAt(loot, stand);

        // That mannequin's own stand, or yours if the hideout has fewer stands than it did.
        internal bool AtMannequin(LootItem loot, int mannequin)
        {
            var at = standNames.FirstOrDefault(s => s.name == "Stand" + mannequin) ?? stand;
            return at != null && PutAt(loot, at);
        }

        // Round the foot of the stand, each in its own place, settling there on their own.
        private bool PutAt(LootItem loot, Transform stand)
        {
            gearPlaces.TryGetValue(stand, out int place);
            gearPlaces[stand] = place + 1;
            Vector3 foot = stand.position + stand.forward * .35f + stand.right * (-.45f + .3f * (place % 4)) + stand.forward * (.3f * (place / 4)) + Vector3.up * .5f;
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            if (Physics.Raycast(foot + Vector3.up * .1f, Vector3.down, out var floor, 3f, mask, QueryTriggerInteraction.Ignore)) foot = floor.point + Vector3.up * .1f;
            loot.transform.SetPositionAndRotation(foot, stand.rotation);
            var body = loot.RigidBody;
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            return true;
        }
    }
}
