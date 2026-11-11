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
    /// station. It starts empty: what's already in your stash stays where it is. In co-op it's the host's.
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

        /// <summary>For the Stash station: Open stash, for the host (whose stash it is) while the box is still there.</summary>
        internal static AvailableInteractionState ActionsFor(GamePlayerOwner owner)
        {
            if (Net.IsClient) return new AvailableInteractionState { Error = "It's the host's stash" };
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

        /// <summary>What's in it now, each thing at the top of it (what's in or on those comes with them).</summary>
        internal static List<Item> Contents()
        {
            var found = new List<Item>();
            // Only while it's still where it was put: carried off, it's on someone, and goes with them.
            if (box == null || !(box.Item is CompoundItem container) || !ReferenceEquals(box.Item.Owner, box.ItemOwner)) return found;
            foreach (var grid in container.Grids) found.AddRange(grid.Items);
            return found;
        }

        internal static void Reset() => box = null;
    }
}
