using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// The attackers waiting to come in make no noise. They're a wall away, past the cultist room's gate or in their tunnel,
    /// and you could hear them walking to their places and talking to each other before the wave had started. Each game
    /// does this for itself, as each plays the sounds: the host knows who's waiting, and every game knows where they wait.
    /// </summary>
    internal static class SiegeQuiet
    {
        internal static bool Is(Player player)
        {
            if (player == null || player.IsYourPlayer || !SiegeLauncher.InSiege) return false;
            var bot = player.AIData?.BotOwner;
            if (bot != null && SiegeDirector.Waiting.Contains(bot)) return true;
            return Hidden(player.Position);
        }

        /// <summary>Where they wait, while they're waiting: once a wave's on, whoever's still there is on his way in.</summary>
        internal static bool Hidden(Vector3 at)
        {
            var phase = SiegeState.Phase;
            if (phase == SiegePhase.Fighting || phase == SiegePhase.Won) return false;
            return SiegeEntries.PastGate(at) || SiegeEntries.InTunnel(at);
        }
    }

    // Footsteps, landing, turning on the spot and the rattle of their gear.
    [HarmonyPatch]
    internal static class QuietFeetPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Player), nameof(Player.PlayStepSound));
            yield return AccessTools.Method(typeof(Player), nameof(Player.PlayTurnSound));
            yield return AccessTools.Method(typeof(Player), nameof(Player.PlayGroundedSound));
            // The one that takes the sounds to play: the other calls it.
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Player)).Where(m => m.Name == nameof(Player.PlayGearSound) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType != typeof(float)))
                yield return method;
        }

        [HarmonyPrefix]
        private static bool Prefix(Player __instance) => !SiegeQuiet.Is(__instance);
    }

    // What they say to each other. A voice only knows where it is, not whose it is; nobody else is ever where they wait.
    [HarmonyPatch(typeof(BaseSpeaker), nameof(BaseSpeaker.Play))]
    internal static class QuietVoicePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(BaseSpeaker __instance, EPhraseTrigger trigger)
        {
            if (trigger == EPhraseTrigger.OnDeath || trigger == EPhraseTrigger.OnAgony || !SiegeLauncher.InSiege) return true;
            var where = __instance.TrackTransform;
            if (where == null) return true;
            Vector3 at = where.position;
            if (SiegeQuiet.Hidden(at)) return false;
            // The host knows the ones waiting anywhere else: up the stairs, or behind the Emergency Wall.
            foreach (var bot in SiegeDirector.Waiting)
                if (bot != null && !bot.IsDead && (bot.Position - at).sqrMagnitude < 1f) return false;
            return true;
        }
    }
}
