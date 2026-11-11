using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>What you look at to use a mannequin. Its gear is real loose loot kept out of sight, so what you take works like any pickup.</summary>
    internal sealed class SiegeMannequinPart : InteractableObject
    {
        internal int Mannequin;
    }

    /// <summary>
    /// Your real mannequins, dressed the way they are in your hideout, built by the same code the hideout uses. Until they're up, or if they
    /// can't be built, or on another player's game, their gear stays lying at the foot of each stand.
    /// </summary>
    internal static class SiegeMannequins
    {
        private const float GivesUpAfter = 30f, BelowTheWorld = 200f;
        private static readonly Vector3 Size = new Vector3(.8f, 1.9f, .8f);
        private static HideoutAreaStashController controller;
        private static readonly List<GameObject> parts = new List<GameObject>();
        private static readonly Dictionary<int, InventoryEquipment> dressed = new Dictionary<int, InventoryEquipment>();

        private static CompoundItem remembered;

        /// <summary>
        /// The gear stands' stash, kept while it's to hand: starting a raid, the game takes the hideout's stashes off your
        /// profile, so in the siege there'd be nothing to dress the mannequins from.
        /// </summary>
        internal static void Remember()
        {
            var stashes = (Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication)?.Session?.Profile?.Inventory?.HideoutAreaStashes;
            if (stashes != null && stashes.TryGetValue(EAreaType.EquipmentPresetsStand, out var stands) && stands != null) remembered = stands;
        }

        private static bool Start(Player me, out CompoundItem stash, out Dictionary<string, StashItemModelSpawnPoint> points, out int count)
        {
            stash = null;
            points = null;
            count = 0;
            try
            {
                var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
                var profile = app?.Session?.Profile;
                HideoutCopy.Levels().TryGetValue(EAreaType.EquipmentPresetsStand, out int level);
                if (profile == null || level < 1) return false;
                // There can be more than one of the stand's area about (copies without its levels): the one with places to dress.
                var stands = Object.FindObjectsOfType<HideoutArea>(true).Where(a => a != null && a.AreaTemplate != null && a.AreaTemplate.Type == EAreaType.EquipmentPresetsStand).ToList();
                var area = stands.FirstOrDefault(a => a.AreaLevels != null && level < a.AreaLevels.Length && a.AreaLevels[level]?.StashObjectsSpawnPoints?.Count > 0);
                if (area == null)
                {
                    Plugin.Log.LogWarning($"[siege] The mannequins stay on the floor: no gear stand at level {level} with places for them ({stands.Count} found: "
                        + string.Join(", ", stands.Select(a => $"{a.name} with {a.AreaLevels?.Length ?? 0} levels")) + ").");
                    return false;
                }
                points = area.AreaLevels[level].StashObjectsSpawnPoints;
                if (profile.Inventory?.HideoutAreaStashes == null || !profile.Inventory.HideoutAreaStashes.TryGetValue(EAreaType.EquipmentPresetsStand, out stash) || stash == null)
                    stash = remembered;
                if (stash == null) { Plugin.Log.LogWarning("[siege] The mannequins stay on the floor: the gear stands' stash wasn't seen before the raid."); return false; }
                if (points == null || points.Count == 0) return false;
                count = stash.Containers.Sum(c => c.Items.Count());
                if (count == 0) return false;
                controller = new HideoutAreaStashController();
                controller.Init(null, me.InventoryController, stash, area.AreaTemplate.stashGridStep, points, profile.Hideout.MannequinPoses);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[siege] Couldn't dress the mannequins, so their gear stays on the floor: " + e);
                return false;
            }
        }

        internal static IEnumerator Build(Player me)
        {
            if (!Start(me, out var stash, out var points, out int count)) yield break;

            float giveUp = Time.time + GivesUpAfter;
            while (Time.time < giveUp && controller != null && controller.LoadedInventoryEquipment.Count < count) yield return null;
            if (controller == null) yield break;
            int ready = controller.LoadedInventoryEquipment.Count;
            Plugin.Log.LogInfo($"[siege] {ready} of {count} mannequins dressed.");
            if (ready == 0) yield break;

            try
            {
                foreach (var container in stash.Containers)
                    foreach (var item in container.Items.OfType<InventoryEquipment>())
                    {
                        if (!controller.LoadedInventoryEquipment.ContainsKey(item) || !points.TryGetValue(container.ID, out var point)) continue;
                        int number = container.ID.LastOrDefault() - '0';
                        dressed[number] = item;
                        MakePart(number, point.transform);
                    }
                // Out of the way, since what's on the mannequin is what you see now.
                foreach (var loot in Singleton<GameWorld>.Instance.LootList.OfType<LootItem>().Where(l => dressed.ContainsKey(SiegeGear.MannequinOf(l))).ToList())
                {
                    if (loot.RigidBody != null) loot.RigidBody.isKinematic = true;
                    loot.transform.position += Vector3.down * BelowTheWorld;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't set up the mannequins: " + e); }
        }

        private static void MakePart(int number, Transform stand)
        {
            var go = new GameObject("Hideout Siege mannequin " + number) { layer = LayerMask.NameToLayer("Interactive") };
            go.transform.SetPositionAndRotation(stand.position + Vector3.up * Size.y / 2f, stand.rotation);
            var box = go.AddComponent<BoxCollider>();
            box.size = Size;
            box.isTrigger = false;
            go.AddComponent<SiegeMannequinPart>().Mannequin = number;
            parts.Add(go);
        }

        internal static AvailableInteractionState ActionsFor(SiegeMannequinPart part, GamePlayerOwner owner)
        {
            var left = Loose(part.Mannequin);
            if (left.Count == 0) return new AvailableInteractionState { Error = "Nothing left on this mannequin" };
            var actions = new List<InteractionAction>
            {
                new InteractionAction
                {
                    Name = $"Take Mannequin {part.Mannequin}'s loadout",
                    Action = () => Plugin.Instance.StartCoroutine(SiegeGear.TakeLoadout(owner.Player, part.Mannequin)),
                },
            };
            foreach (var loot in left)
                actions.Add(new InteractionAction
                {
                    Name = "Take",
                    TargetName = loot.Name.Localized(),
                    Action = () => Plugin.Instance.StartCoroutine(SiegeGear.TakeOne(owner.Player, loot, part.Mannequin)),
                });
            return new AvailableInteractionState { Actions = actions };
        }

        private static List<LootItem> Loose(int mannequin) => Singleton<GameWorld>.Instance.LootList.OfType<LootItem>().Where(l => SiegeGear.MannequinOf(l) == mannequin).ToList();

        // Off the mannequin when it's taken. Worn things change how the body looks, so whatever slot held it stops being drawn.
        internal static void Undress(int mannequin, Item taken)
        {
            if (controller == null || !dressed.TryGetValue(mannequin, out var equipment)) return;
            try
            {
                if (!controller.LoadedInventoryEquipment.TryGetValue(equipment, out var model)) return;
                foreach (var slot in equipment.AllSlots.Where(s => s.ContainedItem != null && s.ContainedItem.TemplateId == taken.TemplateId))
                {
                    if (!Enum.TryParse(slot.ID, out EquipmentSlot which) || !model.PlayerBody.SlotViews.TryGetByKey(which, out var view)) continue;
                    if (view.Dresses != null) foreach (var dress in view.Dresses) dress.gameObject.SetActive(false);
                    view.ParentedModel?.Value?.SetActive(false);
                    break;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't take that off the mannequin: " + e.Message); }
        }

        internal static void Reset()
        {
            parts.Clear();
            dressed.Clear();
            try { controller?.Dispose(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
            controller = null;
        }
    }

    // A magazine can't be taken with nowhere to put it, and the game just doesn't offer it. A rig full of empties is the usual reason,
    // so this offers to drop your emptiest one for it, and the log says what's really stopping it.
    [HarmonyPatch(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions), new[] { typeof(GamePlayerOwner), typeof(LootItem) })]
    internal static class MagazineSwapPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GamePlayerOwner owner, LootItem lootItem, ref AvailableInteractionState __result)
        {
            if (!SiegeLauncher.InSiege || !(lootItem?.Item is Magazine magazine) || __result?.Actions == null) return;
            if (__result.Actions.Any(a => a.Name == "Take")) return;
            var player = owner.Player;
            var inventory = player.InventoryController;
            var move = ItemManipulator.QuickFindAppropriatePlace(magazine, inventory, new[] { inventory.Inventory.Equipment }, ItemManipulator.EMoveItemOrder.PickUp, true);
            Plugin.Log.LogInfo($"[siege] No Take on a magazine: pickup supported {player.HandsController.SupportPickup()}, room {!move.Failed}, {move.Error}.");
            var spent = inventory.Inventory.Equipment.GetAllItems().OfType<Magazine>()
                .Where(m => !(m.Parent.Container is Slot) && m.Count < magazine.Count).OrderBy(m => m.Count).FirstOrDefault();
            if (spent == null) return;
            __result.Actions.Add(new InteractionAction
            {
                Name = $"Swap for your {spent.Count}/{spent.MaxCount} magazine",
                Action = () => Plugin.Instance.StartCoroutine(SiegeGear.SwapMagazine(player, spent, lootItem)),
            });
        }
    }
}
