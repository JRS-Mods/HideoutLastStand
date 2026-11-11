using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Hideout;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;
using JsonType;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>What the game's interaction check finds just inside the hideout's front door.</summary>
    internal sealed class SiegeDoorInteractive : InteractableObject { }

    /// <summary>
    /// The front door's upgrades, built in the hideout: walk up to the front door from inside and your interact key shows the
    /// next one, what it costs (and what you have of each), how long it takes, and why it's locked if it is. Building takes
    /// the items as a hideout upgrade does. In a siege each one built makes the front door take one more charge.
    /// </summary>
    internal static class SiegeDoorUpgrades
    {
        internal sealed class Cost { [JsonProperty("template")] public string Template; [JsonProperty("count")] public int Count; }

        internal sealed class Upgrade
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("description")] public string Description;
            [JsonProperty("seconds")] public long Seconds;
            [JsonProperty("cost")] public List<Cost> Cost = new List<Cost>();
            [JsonProperty("state")] public string State;
            [JsonProperty("completeAt")] public long CompleteAt;
            [JsonProperty("locked")] public string Locked;
        }

        private sealed class Reply { [JsonProperty("level")] public int Level; [JsonProperty("upgrades")] public List<Upgrade> Upgrades = new List<Upgrade>(); }

        private static List<Upgrade> upgrades = new List<Upgrade>();
        private static GameObject zone;
        private static float nextLook, refetchAt;

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>How many are built for the playing profile, asked of the server (0 if it can't say).</summary>
        internal static int Level()
        {
            try { return JsonConvert.DeserializeObject<Reply>(RequestHandler.PostJson(SiegeIds.RouteDoor, "{}"))?.Level ?? 0; }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't ask how the front door's been upgraded: " + e.Message); return 0; }
        }

        private static void Fetch()
        {
            try { upgrades = JsonConvert.DeserializeObject<Reply>(RequestHandler.PostJson(SiegeIds.RouteDoor, "{}"))?.Upgrades ?? new List<Upgrade>(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't ask about the front door's upgrades: " + e.Message); }
        }

        // ---- the prompt at the front door, in the hideout -----------------------------------------------------

        internal static void Patch(Harmony harmony)
        {
            var actions = AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions), new[] { typeof(GamePlayerOwner), typeof(IInteractive) });
            if (actions == null) { Plugin.Log.LogWarning("[siege] The interaction prompt has moved: the front door can't be upgraded from the hideout."); return; }
            harmony.Patch(actions, prefix: new HarmonyMethod(typeof(SiegeDoorUpgrades), nameof(Actions)));
        }

        // Every couple of seconds while you're in the hideout: the prompt's put in once a visit.
        internal static void Update()
        {
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 2f;
            if (refetchAt > 0f && Time.unscaledTime >= refetchAt) { refetchAt = 0f; Fetch(); }
            if (zone != null || !Singleton<GameWorld>.Instantiated || !(Singleton<GameWorld>.Instance is HideoutGameWorld) || !Singleton<HideoutRepresentation>.Instantiated) return;
            var style = GameObject.Find("STYLE");
            if (style == null) return;
            Fetch();
            zone = new GameObject("Hideout Siege front door upgrades") { layer = LayerMask.NameToLayer("Interactive") };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(zone, style.scene);
            Vector3 inward = -SiegeBreach.Outward;
            zone.transform.SetPositionAndRotation(SiegeBreach.Doorway + inward * .45f + Vector3.up * 1.1f, Quaternion.LookRotation(SiegeBreach.Outward));
            var box = zone.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(1.3f, 2.2f, .6f);
            zone.AddComponent<SiegeDoorInteractive>();
            Plugin.Log.LogInfo($"[siege] The front door's upgrades prompt is in the hideout: {upgrades.Count(u => Current(u, out _) == "built")} of {upgrades.Count} built.");
        }

        private static bool Actions(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
        {
            if (!(interactive is SiegeDoorInteractive)) return true;
            try { __result = For(); }
            catch (Exception e)
            {
                Plugin.Log.LogError("[siege] The front door's prompt failed: " + e);
                __result = new AvailableInteractionState();
            }
            return false;
        }

        private static AvailableInteractionState For()
        {
            var state = new AvailableInteractionState();
            Upgrade next = upgrades.FirstOrDefault(u => Current(u, out _) != "built");
            if (next == null)
            {
                Info(state, upgrades.Count == 0 ? "Front door: can't reach the siege's server mod" : "Front door: fully reinforced");
            }
            else if (Current(next, out long completeAt) == "building")
            {
                Info(state, completeAt > 0 ? $"{next.Name}: being built, {Duration(completeAt - Now)} left" : $"{next.Name}: being built");
            }
            else
            {
                ItemRequirement[] cost = Requirements(next);
                bool can = next.Locked == null && cost.All(r => r.Fulfilled);
                state.Actions.Add(new InteractionAction { Name = "Build " + next.Name.ToLowerInvariant(), Disabled = !can, Action = () => Build(next) });
                if (next.Locked != null) Info(state, "Locked: " + next.Locked);
                foreach (ItemRequirement r in cost) Info(state, $"{r.ItemName}: {Count(r.UserItemsCount)} / {Count(r.IntCount)}");
                Info(state, "Build time: " + Duration(next.Seconds));
                Info(state, next.Description);
            }
            state.InitSelected();
            return state;
        }

        // The hideout's own records first (a build that finished since the server last said), then the server's.
        private static string Current(Upgrade upgrade, out long completeAt)
        {
            completeAt = upgrade.CompleteAt;
            var records = Singleton<HideoutRepresentation>.Instantiated ? Singleton<HideoutRepresentation>.Instance.ImprovementController?._improvementDataBy : null;
            if (records != null && records.TryGetValue(upgrade.Id, out ImprovementData data))
            {
                completeAt = data.CompleteTimestamp;
                return data.Completed || (data.CompleteTimestamp > 0 && data.CompleteTimestamp <= Now) ? "built" : "building";
            }
            if (upgrade.State == "building" && upgrade.CompleteAt > 0 && upgrade.CompleteAt <= Now) return "built";
            return upgrade.State ?? "none";
        }

        private static ItemRequirement[] Requirements(Upgrade upgrade)
        {
            var list = upgrade.Cost.Select(c => new ItemRequirement { TemplateId = c.Template, IntCount = c.Count }).ToArray();
            if (Singleton<HideoutRepresentation>.Instantiated)
                foreach (ItemRequirement r in list) r.Test(Singleton<HideoutRepresentation>.Instance.AllStashItems);
            return list;
        }

        private static void Build(Upgrade upgrade)
        {
            try
            {
                HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
                ItemRequirement[] cost = Requirements(upgrade);
                if (upgrade.Locked != null) { NotificationManager.DisplayWarningNotification(upgrade.Locked); return; }
                if (!cost.All(r => r.Fulfilled)) { NotificationManager.DisplayWarningNotification("You don't have everything it needs in your stash."); return; }
                var handover = hideout.GetItemReferences(cost).Select(r => new ItemToHandover(r.Item, r.Count)).ToList();
                hideout._session.StartImprovement(upgrade.Id, EAreaType.Stash, handover);
                upgrade.State = "building";
                upgrade.CompleteAt = Now + upgrade.Seconds;
                refetchAt = Time.unscaledTime + 3f;
                NotificationManager.DisplayMessageNotification($"Building the {upgrade.Name.ToLowerInvariant()}: ready in {Duration(upgrade.Seconds)}.");
                Plugin.Log.LogInfo($"[siege] Started the {upgrade.Name.ToLowerInvariant()} with {handover.Count} item stack(s) from the stash.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[siege] Couldn't start the front door's upgrade: " + e);
                NotificationManager.DisplayWarningNotification("Couldn't start the upgrade (see the log).");
            }
        }

        private static void Info(AvailableInteractionState state, string text)
            => state.Actions.Add(new InteractionAction { Name = text, Disabled = true, Action = () => { } });

        private static string Count(int n) => n >= 10000 ? n.ToString("N0") : n.ToString();

        private static string Duration(long seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : $"{t.Seconds} s";
        }
    }
}
