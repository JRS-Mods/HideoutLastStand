using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// Your stash box, in the siege. Walk up to the Stash station and Open stash: it's an empty box of a set size, and what
    /// you put in it is in your real stash afterwards, whether you hold out or not, so between waves and in the minute after
    /// the last one you can put away some of what you've looted. Every player has one of their own, whoever's hideout it is:
    /// the server brings in a few with the rest of the siege's loot, kept out of sight inside the station, and tells each
    /// player's game which is theirs.
    /// </summary>
    internal static class SiegeStash
    {
        private sealed class Joined
        {
            public int Box = -1;
        }

        private const int NotAsked = -2, NoneLeft = -1;
        private static readonly Dictionary<string, LootItem> boxes = new Dictionary<string, LootItem>();
        private static LootItem box;
        // The number of this player's box, from the server (set from the request's own thread, picked up in Update).
        private static volatile int number = NotAsked;
        private static volatile bool asking;
        private static float askAt;

        /// <summary>This player's box, once the server's said which it is.</summary>
        internal static LootItem Mine => box;

        /// <summary>Once the siege's loot is in, on every game: the boxes go inside the Stash station, out of sight and reach.</summary>
        internal static void Tuck(GameWorld world, InventoryController controller)
        {
            boxes.Clear();
            foreach (var loot in world.LootList.OfType<LootItem>().Where(l => SiegeIds.IsStashBox(l.ItemId))) boxes[loot.ItemId] = loot;
            if (boxes.Count == 0)
            {
                Plugin.Log.LogInfo("[siege] No stash boxes came in with the loot (is the server half up to date?): Open stash won't be there.");
                return;
            }
            Vector3 at = Where(boxes.Values.First().transform.position);
            foreach (var loot in boxes.Values)
            {
                loot.transform.SetPositionAndRotation(at, Quaternion.identity);
                if (loot.RigidBody != null)
                {
                    loot.RigidBody.velocity = Vector3.zero;
                    loot.RigidBody.isKinematic = true;
                }
                // Out of sight, and nobody can pick one up: they're only reached through the station.
                foreach (var part in loot.GetComponentsInChildren<Renderer>(true)) part.enabled = false;
                foreach (var part in loot.GetComponentsInChildren<Collider>(true)) part.enabled = false;
                if (controller != null) SiegeGear.MakeKnown(controller, loot.Item);
            }
            Plugin.Log.LogInfo($"[siege] {boxes.Count} stash boxes are in the Stash station at {at:F1}.");
        }

        // The middle of the Stash station's "use this" box, on the floor; or wherever the station is.
        private static Vector3 Where(Vector3 otherwise)
        {
            var area = Object.FindObjectsOfType<HideoutArea>().FirstOrDefault(a => a.AreaTemplate != null && a.AreaTemplate.Type == EAreaType.Stash);
            if (area == null) return otherwise;
            Vector3 at = area.transform.position;
            var trigger = area.GetComponentsInChildren<HideoutInteractionTrigger>(true).SelectMany(t => t.GetComponents<Collider>()).FirstOrDefault();
            if (trigger != null) at = trigger.bounds.center;
            return Physics.Raycast(at + Vector3.up * .5f, Vector3.down, out var floor, 3f, EFTHardSettings.Instance.MOVEMENT_MASK, QueryTriggerInteraction.Ignore) ? floor.point + Vector3.up * .05f : at;
        }

        /// <summary>
        /// Every frame of a siege: until the server's said which box is this player's, it's asked (again every few seconds if
        /// it couldn't be reached). Saying whose siege this is also tells the server whether this player is only helping.
        /// </summary>
        internal static void Update()
        {
            if (box != null || boxes.Count == 0 || SiegeOwner.OnHeadless) return;
            int said = number;
            if (said >= 0)
            {
                if (boxes.TryGetValue(SiegeIds.StashBoxId(said), out box)) Plugin.Log.LogInfo($"[siege] Your stash box is number {said + 1}.");
                else number = NoneLeft;
                return;
            }
            if (said == NoneLeft || asking || Time.time < askAt) return;
            // On a joined player's game, only once the host has said whose hideout it is.
            string owner = SiegeOwner.ProfileId;
            if (string.IsNullOrEmpty(owner)) return;
            asking = true;
            askAt = Time.time + 5f;
            string body = JsonConvert.SerializeObject(new { Owner = owner });
            Task.Run(() =>
            {
                try
                {
                    var joined = JsonConvert.DeserializeObject<Joined>(RequestHandler.PostJson(SiegeIds.RouteJoin, body));
                    if (joined != null) number = joined.Box;
                }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't ask the server which stash box is yours: " + e.Message); }
                finally { asking = false; }
            });
        }

        /// <summary>For the Stash station: Open stash, each player for their own box.</summary>
        internal static AvailableInteractionState ActionsFor(GamePlayerOwner owner)
        {
            if (boxes.Count == 0) return null;
            if (box == null) return new AvailableInteractionState { Error = number == NoneLeft ? "No stash box left for you" : "Your stash box isn't ready yet" };
            if (!(box.Item is CompoundItem container)) return null;
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
                SiegeState.Notify("Your stash box isn't here");
                return;
            }
            owner.Player.Interact(box.ItemOwner, result =>
            {
                if (result.Succeed) owner.ShowInventoryScreenLoot(container, () => { });
                else Plugin.Log.LogWarning("[siege] Couldn't open your stash box: " + result.Error);
            });
        }

        /// <summary>
        /// What's in this player's box now, for the server. Whatever's put straight in is deposited (what's in or on it comes
        /// with it). In their own hideout, a copy of something from their racks put in the box is untouched, so the real one
        /// stays on the rack, and loot put into such a copy (a bag off the gear stand, say) is added. A friend helping keeps
        /// whatever's in theirs, copies included: those are gone from the owner's racks, as if carried out.
        /// </summary>
        internal static void Split(bool myHideout, List<string> untouched, List<Item> added, List<Item> deposited)
        {
            // Only while it's still where it was put: carried off, it's on someone, and goes with them.
            if (box == null || !(box.Item is CompoundItem container) || !ReferenceEquals(box.Item.Owner, box.ItemOwner)) return;
            foreach (var grid in container.Grids)
                foreach (var top in grid.Items)
                {
                    if (!myHideout || !IsCopy(top))
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

        /// <summary>What's in this player's box now, each thing at the top of it.</summary>
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

        internal static void Reset()
        {
            boxes.Clear();
            box = null;
            number = NotAsked;
            askAt = 0f;
        }
    }
}
