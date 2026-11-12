using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// Your stash, in the siege. Walk up to the Stash station and Open stash: what you put in it is in your real stash
    /// afterwards, whether you hold out or not, so between waves and in the minute after the last one you can put away
    /// what you've looted. It's a box the server brings in with the rest of the siege's loot, kept out of sight inside the
    /// station. It holds a copy of your stash as you keep it: what's taken out of it is gone from the real one at the end, unless
    /// it's put back or left lying in the hideout (the racks work the same way). In co-op it's the host's.
    /// </summary>
    internal static class SiegeStash
    {
        private static LootItem box;

        /// <summary>Once the siege's loot is in, on every game: the box goes inside the Stash station, out of sight and reach.</summary>
        internal static void Tuck(GameWorld world, InventoryController controller)
        {
            box = world.LootList.OfType<LootItem>().FirstOrDefault(l => l.ItemId == SiegeIds.StashBox);
            if (box == null)
            {
                Plugin.Log.LogInfo("[siege] No stash box came in with the loot (is the server half up to date?): Open stash won't be there.");
                return;
            }
            Vector3 at = Where();
            box.transform.SetPositionAndRotation(at, Quaternion.identity);
            if (box.RigidBody != null)
            {
                box.RigidBody.velocity = Vector3.zero;
                box.RigidBody.isKinematic = true;
            }
            // Out of sight, and nobody can pick it up: it's only reached through the station.
            foreach (var part in box.GetComponentsInChildren<Renderer>(true)) part.enabled = false;
            foreach (var part in box.GetComponentsInChildren<Collider>(true)) part.enabled = false;
            if (controller != null) SiegeGear.MakeKnown(controller, box.Item);
            Plugin.Log.LogInfo($"[siege] Your stash is in the Stash station at {at:F1}.");
        }

        // The middle of the Stash station's "use this" box, on the floor; or wherever the station is.
        private static Vector3 Where()
        {
            var area = Object.FindObjectsOfType<HideoutArea>().FirstOrDefault(a => a.AreaTemplate != null && a.AreaTemplate.Type == EAreaType.Stash);
            if (area == null) return box.transform.position;
            Vector3 at = area.transform.position;
            var trigger = area.GetComponentsInChildren<HideoutInteractionTrigger>(true).SelectMany(t => t.GetComponents<Collider>()).FirstOrDefault();
            if (trigger != null) at = trigger.bounds.center;
            return Physics.Raycast(at + Vector3.up * .5f, Vector3.down, out var floor, 3f, EFTHardSettings.Instance.MOVEMENT_MASK, QueryTriggerInteraction.Ignore) ? floor.point + Vector3.up * .05f : at;
        }

        /// <summary>
        /// For the Stash station: Open stash, for the player whose stash it is (the host's, or on a Fika headless the player it's
        /// hosting for) while the box is still there.
        /// </summary>
        internal static AvailableInteractionState ActionsFor(GamePlayerOwner owner)
        {
            if (!SiegeOwner.IsMe) return new AvailableInteractionState { Error = Net.IsClient && SiegeOwner.Said == null ? "It's the host's stash" : "It's not your stash" };
            if (box == null || !(box.Item is CompoundItem container)) return null;
            return new AvailableInteractionState
            {
                Actions = new List<InteractionAction>
                {
                    new InteractionAction { Name = "Open stash", Action = () => Open(owner, container) },
                },
            };
        }

        // As the game opens a bag lying on the floor.
        private static void Open(GamePlayerOwner owner, CompoundItem container)
        {
            if (box == null || !ReferenceEquals(box.Item.Owner, box.ItemOwner))
            {
                SiegeState.Notify("Your stash isn't here");
                return;
            }
            owner.Player.Interact(box.ItemOwner, result =>
            {
                if (result.Succeed) owner.ShowInventoryScreenLoot(container, () => { });
                else Plugin.Log.LogWarning("[siege] Couldn't open your stash: " + result.Error);
            });
        }

        /// <summary>
        /// What's in it now, for the server. Copies of what was in your stash, still in it as they were (moved about in it,
        /// or taken out and put back, is the same): untouched, so the real ones stay. New things put into one of those (in a
        /// bag of yours, say): added. New things put straight in: deposited (what's in or on those comes with them).
        /// </summary>
        internal static void Split(List<string> untouched, List<Item> added, List<Item> deposited)
        {
            // Only while it's still where it was put: carried off, it's on someone, and goes with them.
            if (box == null || !(box.Item is CompoundItem container) || !ReferenceEquals(box.Item.Owner, box.ItemOwner)) return;
            foreach (var grid in container.Grids)
                foreach (var top in grid.Items)
                {
                    if (!IsCopy(top))
                    {
                        deposited.Add(top);
                        continue;
                    }
                    foreach (var item in top.GetAllItems())
                    {
                        if (ReferenceEquals(item, top)) untouched.Add(item.Id);
                        else if (IsCopy(item) && CopiesUpTo(item, top)) untouched.Add(item.Id);
                        else if (!IsCopy(item) && item.Parent?.Container?.ParentItem is Item holder && IsCopy(holder) && CopiesUpTo(holder, top)) added.Add(item);
                    }
                }
        }

        /// <summary>What's in it now, each thing at the top of it.</summary>
        internal static List<Item> Contents()
        {
            var found = new List<Item>();
            if (box == null || !(box.Item is CompoundItem container) || !ReferenceEquals(box.Item.Owner, box.ItemOwner)) return found;
            foreach (var grid in container.Grids) found.AddRange(grid.Items);
            return found;
        }

        private static bool IsCopy(Item item) => item?.Id != null && item.Id.ToString().StartsWith(SiegeIds.GearPrefix);

        // Every container between this and the top is a copy too: something of yours inside a new bag isn't where it was.
        private static bool CopiesUpTo(Item item, Item top)
        {
            for (Item at = item; at != null && !ReferenceEquals(at, top); at = at.Parent?.Container?.ParentItem as Item)
                if (!IsCopy(at)) return false;
            return true;
        }

        internal static void Reset() => box = null;
    }
}
