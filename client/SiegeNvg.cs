using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace HideoutSiege
{
    /// <summary>
    /// Raiders, Rogues and Black Division come with night vision, for when they've cut the power: on a mount on their
    /// helmet if it takes one, or a head-strap set if they've no helmet. Scavs make do with flashlights. Theirs go on while
    /// the power's out and flip off when it's back, whatever the game's own idea of how dark it is.
    /// </summary>
    internal static class SiegeNvg
    {
        // A PVS-14 on a dual dovetail on a TATM mount; or Armasight's head-strap mask with its N-15.
        private const string Mount = "5a16b8a9fcdbcb00165aa6ca", Dovetail = "5a16b93dfcdbcbcae6687261", Pvs14 = "57235b6f24597759bf5a30f1";
        private const string Mask = "5c066ef40db834001966a595", N15 = "5c066e3a0db834001b7353f0";

        /// <summary>
        /// Into the profile before the bot's made from it: so it's dressed in them from the start, other players in a co-op
        /// raid see them, and the game knows they've got night vision.
        /// </summary>
        internal static void Equip(Profile profile)
        {
            if (profile?.Info?.Settings == null || profile.Info.Settings.Role == WildSpawnType.assault) return;
            // Bosses and their guards come as they are: nobody straps goggles on Reshala.
            string role = profile.Info.Settings.Role.ToString();
            if (role.StartsWith("boss", StringComparison.OrdinalIgnoreCase) || role.StartsWith("follower", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var head = profile.Inventory.Equipment.GetSlot(EquipmentSlot.Headwear);
                var factory = Singleton<ItemFactory>.Instance;
                if (head.ContainedItem is CompoundItem helmet)
                {
                    if (helmet.GetItemComponentsInChildren<NightVisionComponent>().Any()) return;
                    var mount = Make(factory, Mount, Dovetail, Pvs14);
                    var slot = helmet.Slots?.FirstOrDefault(s => s.ID == "mod_nvg" && s.ContainedItem == null && mount != null && s.CheckCompatibility(mount));
                    if (slot == null || slot.Add(mount, false).Failed) return;
                }
                else if (head.ContainedItem == null)
                {
                    var mask = Make(factory, Mask, N15);
                    if (mask == null || head.Add(mask, false).Failed) return;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Night vision: " + e.Message); }
        }

        /// <summary>The goggles' and mounts' models, to load with the bots' gear on the loading screen.</summary>
        internal static IEnumerable<ResourceKey> Prefabs()
        {
            var factory = Singleton<ItemFactory>.Instance;
            foreach (var template in new[] { Mount, Dovetail, Pvs14, Mask, N15 })
            {
                ResourceKey key = null;
                try { key = factory.CreateItem(MongoID.Generate(), template, null)?.Prefab; }
                catch (Exception) { }
                if (key != null) yield return key;
            }
        }

        // An item with each of the next ones in its night vision slot: the mount, what's on it, and the goggles.
        private static Item Make(ItemFactory factory, params string[] chain)
        {
            Item first = null, last = null;
            foreach (string template in chain)
            {
                var item = factory.CreateItem(MongoID.Generate(), template, null);
                if (item == null) return null;
                if (last is CompoundItem holder)
                {
                    var slot = holder.Slots?.FirstOrDefault(s => s.ID == "mod_nvg");
                    if (slot == null || slot.Add(item, false).Failed) return null;
                }
                first = first ?? item;
                last = item;
            }
            return first;
        }

        internal static bool Wearing(BotOwner bot)
        {
            var nv = bot?.NightVision;
            return nv != null && nv.HaveNightVision && nv.NightVisionItem != null && nv.NightVisionItem.Togglable.On;
        }

        /// <summary>The power's gone off or come back: everyone's goggles follow straight away.</summary>
        internal static void Refresh()
        {
            if (!Singleton<IBotGame>.Instantiated || Net.IsClient) return;
            var bots = Singleton<IBotGame>.Instance.BotsController?.Bots?.BotOwners;
            if (bots == null) return;
            foreach (var bot in bots.ToList())
                try { if (bot != null && !bot.IsDead && bot.NightVision != null && bot.NightVision.HaveNightVision) bot.NightVision.CheckWhatIWant(); }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] Night vision: " + e.Message); }
        }

        // An attacker's goggles: down while the power's out, up when it's on. Nobody else's are touched.
        [HarmonyPatch(typeof(BotNightVisionData), nameof(BotNightVisionData.CheckWhatIWant))]
        internal static class GogglesPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(BotNightVisionData __instance)
            {
                var bot = __instance._owner;
                if (!SiegeLauncher.Selected || SiegeDirector.Instance == null || bot == null || !SiegeDirector.Instance.IsAttacker(bot)) return true;
                if (__instance.NightVisionItem == null || __instance._nightVisionAtPocket) return true;
                var togglable = __instance.NightVisionItem.Togglable;
                if (SiegeLights.PowerOut) __instance.ToggleOnIfNeed();
                else if (togglable.On)
                {
                    bot.GetPlayer.InventoryController.TryRunNetworkTransaction(togglable.Set(false, true));
                    __instance.UsingNow = false;
                }
                return false;
            }
        }
    }
}
