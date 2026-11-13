using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// In co-op, what's on the racks and mannequins belongs to someone: the copies of the real racks' gear to whoever's
    /// hideout it is, and the gear each player came in with (laid out on the racks as they woke) to that player. To take
    /// something of another player's you ask, and they say yes or no from a prompt on their screen. Nobody's asked once the
    /// owner's dead or gone: it's no use to them then.
    /// </summary>
    internal static class SiegeAsk
    {
        internal const int Owned = 0, Asked = 1, Answered = 2;
        private const float AnswerWithin = 20f, AskAgainAfter = 15f;

        internal sealed class Request
        {
            internal string Player, Name, What;
            internal string[] Items;
            internal float Until;
        }

        // The gear each player came in with, by item: whose it is.
        private static readonly Dictionary<string, string> owners = new Dictionary<string, string>();
        // Who's been let take what: "player|item".
        private static readonly HashSet<string> allowed = new HashSet<string>();
        // This player's own asking: what's waiting for an answer, and what was refused, each until when.
        private static readonly Dictionary<string, float> waiting = new Dictionary<string, float>(), refused = new Dictionary<string, float>();
        // What this player's being asked for, oldest first.
        private static readonly List<Request> requests = new List<Request>();

        private static string Me => Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;

        private static Player Alive(string profileId) => SiegeDirector.Defenders().FirstOrDefault(p => p.ProfileId == profileId);

        private static string OwnerOf(string itemId)
        {
            if (itemId == null) return null;
            if (owners.TryGetValue(itemId, out string owner)) return owner;
            return itemId.StartsWith(SiegeIds.GearPrefix) ? SiegeOwner.ProfileId : null;
        }

        /// <summary>Whose this is, lying where it is: null if it's nobody's in particular (loot, or something dropped since).</summary>
        internal static string OwnerOf(LootItem loot) => OwnerOf(loot?.ItemId);

        /// <summary>The one this player's being asked about now, if any.</summary>
        internal static Request Current => requests.Count > 0 ? requests[0] : null;

        /// <summary>As a player's gear is laid out on the racks: everyone's told it's theirs.</summary>
        internal static void Announce(string profileId, string[] items)
        {
            if (string.IsNullOrEmpty(profileId) || items == null || items.Length == 0) return;
            Received(Owned, profileId, "", items);
            Net.SendAsk(Owned, profileId, "", items);
        }

        /// <summary>For a player who joins later: whose gear is whose so far.</summary>
        internal static IEnumerable<(string Player, string[] Items)> OwnedSoFar() =>
            owners.GroupBy(pair => pair.Value).Select(group => (group.Key, group.Select(pair => pair.Key).ToArray())).ToList();

        /// <summary>
        /// What looking at something on the ground offers. Another player's gear, while they're alive and haven't said yes:
        /// only asking for it.
        /// </summary>
        internal static void Gate(GamePlayerOwner owner, LootItem loot, ref AvailableInteractionState result)
        {
            string id = loot.ItemId, me = owner.Player.ProfileId, whose = OwnerOf(id);
            if (whose == null || whose == me) return;
            var them = Alive(whose);
            if (them == null) return;
            int mannequin = SiegeGear.MannequinOf(loot);
            var loadout = mannequin == 0 ? new List<LootItem>()
                : Singleton<GameWorld>.Instance.LootList.OfType<LootItem>().Where(l => SiegeGear.MannequinOf(l) == mannequin).ToList();
            if (allowed.Contains(me + "|" + id))
            {
                // The whole loadout in one press only if every piece of it has been let go.
                if (result?.Actions != null && loadout.Any(l => !allowed.Contains(me + "|" + l.ItemId)))
                    result.Actions.RemoveAll(a => a.Name != null && a.Name.StartsWith("Take Mannequin"));
                return;
            }
            string name = them.Profile?.Nickname ?? "its owner";
            if (refused.TryGetValue(id, out float until) && Time.time < until)
            {
                result = new AvailableInteractionState { Error = $"{name} said no" };
                return;
            }
            if (waiting.TryGetValue(id, out until) && Time.time < until)
            {
                result = new AvailableInteractionState { Error = $"Asked {name}: waiting for an answer" };
                return;
            }
            string what = loot.Name.Localized();
            var actions = new List<InteractionAction>
            {
                new InteractionAction { Name = $"Ask {name} for this", Action = () => Ask(owner.Player, name, what, new[] { id }) },
            };
            if (loadout.Count > 1)
                actions.Add(new InteractionAction
                {
                    Name = $"Ask {name} for Mannequin {mannequin}'s loadout",
                    Action = () => Ask(owner.Player, name, $"Mannequin {mannequin} loadout", loadout.Select(l => l.ItemId).ToArray()),
                });
            result = new AvailableInteractionState { Actions = actions };
        }

        private static void Ask(Player me, string name, string what, string[] items)
        {
            foreach (string item in items)
            {
                waiting[item] = Time.time + AnswerWithin;
                refused.Remove(item);
            }
            Net.SendAsk(Asked, me.ProfileId, (me.Profile?.Nickname ?? "Someone") + "\n" + what, items);
            SiegeState.Notify($"Asked {name}");
            me.UpdateInteractionCast();
            Plugin.Log.LogInfo($"[siege] Asked {name} for {what}.");
        }

        /// <summary>From another player's game (by way of the host): whose gear is whose, someone asking, or an answer.</summary>
        internal static void Received(int kind, string player, string other, string[] items)
        {
            if (string.IsNullOrEmpty(player) || items == null) return;
            string me = Me;
            switch (kind)
            {
                case Owned:
                    foreach (string item in items) owners[item] = player;
                    break;
                case Asked:
                    // Only what's this player's to give, and only while they're alive to answer.
                    var mine = items.Where(item => OwnerOf(item) == me).ToArray();
                    if (me == null || player == me || mine.Length == 0 || Alive(me) == null) break;
                    if (requests.Any(r => r.Player == player && r.Items.SequenceEqual(mine))) break;
                    string[] said = (other ?? "").Split('\n');
                    var request = new Request { Player = player, Name = said[0], What = said.Length > 1 ? said[1] : "gear", Items = mine, Until = Time.time + AnswerWithin };
                    requests.Add(request);
                    Plugin.Log.LogInfo($"[siege] {request.Name} is asking for your {request.What}.");
                    break;
                case Answered:
                    bool yes = other == "1";
                    foreach (string item in items)
                    {
                        if (yes) allowed.Add(player + "|" + item);
                        if (player != me) continue;
                        waiting.Remove(item);
                        if (!yes) refused[item] = Time.time + AskAgainAfter;
                    }
                    if (player != me) break;
                    SiegeState.Notify(yes ? "They said yes: it's yours to take" : "They said no");
                    Plugin.Log.LogInfo($"[siege] The answer for {items.Length} things asked for: {(yes ? "yes" : "no")}.");
                    Singleton<GameWorld>.Instance?.MainPlayer?.UpdateInteractionCast();
                    break;
            }
        }

        /// <summary>This player's answer to the one on their screen.</summary>
        internal static void Respond(bool yes)
        {
            var request = Current;
            if (request == null) return;
            requests.RemoveAt(0);
            Received(Answered, request.Player, yes ? "1" : "0", request.Items);
            Net.SendAsk(Answered, request.Player, yes ? "1" : "0", request.Items);
            SiegeState.Notify(yes ? $"{request.Name} can take your {request.What}" : $"You said no to {request.Name}");
            Plugin.Log.LogInfo($"[siege] Told {request.Name} {(yes ? "yes" : "no")} for your {request.What}.");
        }

        /// <summary>Every frame: what's gone unanswered too long is dropped (they can ask again), and the keys answer the one showing.</summary>
        internal static void Update()
        {
            if (requests.Count == 0) return;
            requests.RemoveAll(r => Time.time > r.Until || Alive(r.Player) == null);
            if (requests.Count == 0) return;
            if (Input.GetKeyDown(Plugin.AllowKey.Value)) Respond(true);
            else if (Input.GetKeyDown(Plugin.DeclineKey.Value)) Respond(false);
        }

        internal static void Reset()
        {
            owners.Clear();
            allowed.Clear();
            waiting.Clear();
            refused.Clear();
            requests.Clear();
        }
    }

    // After everything else has had its say about what's on offer for something lying about.
    [HarmonyPatch(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions), new[] { typeof(GamePlayerOwner), typeof(LootItem) })]
    internal static class AskFirstPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(GamePlayerOwner owner, LootItem lootItem, ref AvailableInteractionState __result)
        {
            if (!SiegeLauncher.InSiege || lootItem == null || owner?.Player == null) return;
            SiegeAsk.Gate(owner, lootItem, ref __result);
        }
    }
}
