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
        /// <summary>Voice lines kept quiet since the last wave started, for the log.</summary>
        internal static int Kept;

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
    // Every way a voice is started, not only the usual one: other mods' bots, and voices sent from another game, come by the rest.
    [HarmonyPatch]
    internal static class QuietVoicePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { nameof(BaseSpeaker.Play), "PlayDirect", "PlayDirectRandom", "PlayExternal" })
                foreach (var method in AccessTools.GetDeclaredMethods(typeof(BaseSpeaker)).Where(m => m.Name == name && m.GetParameters().Any(p => p.ParameterType == typeof(EPhraseTrigger))))
                    yield return method;
        }

        [HarmonyPrefix]
        private static bool Prefix(BaseSpeaker __instance, object[] __args)
        {
            if (!SiegeLauncher.InSiege) return true;
            foreach (object arg in __args)
                if (arg is EPhraseTrigger trigger && (trigger == EPhraseTrigger.OnDeath || trigger == EPhraseTrigger.OnAgony)) return true;
            var where = __instance.TrackTransform;
            if (where == null) return true;
            // A voice is at its owner's head. Where they wait is known by its floor, and those past the gate stand up a flight
            // of stairs: the heads of the ones furthest up were above what counted as "where they wait", and were heard.
            Vector3 at = where.position, feet = at - Vector3.up * 1.5f;
            if (SiegeQuiet.Hidden(at) || SiegeQuiet.Hidden(feet)) { SiegeQuiet.Kept++; return false; }
            // The host knows the ones waiting anywhere else: up the stairs, or behind the Emergency Wall.
            foreach (var bot in SiegeDirector.Waiting)
            {
                if (bot == null || bot.IsDead) continue;
                Vector3 off = at - bot.Position;
                if (off.x * off.x + off.z * off.z < 1f && off.y > -.5f && off.y < 2.5f) { SiegeQuiet.Kept++; return false; }
            }
            return true;
        }
    }
}
