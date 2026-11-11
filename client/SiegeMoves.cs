using System.Linq;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace HideoutSiege
{
    /// <summary>
    /// Nobody is moved through a wall, a door or your defences. The game keeps a walking bot on the floor's mesh by putting
    /// it back on the nearest floor it can find, up to 100 m away when there's none close by. In the hideout that was often
    /// the far side of a wall or a shut door, and it's how attackers came through them, on the security camera too. The
    /// siege's own moves (getting someone unstuck) go through the same check, and the ones that jump a bot further are only
    /// made where none of you can see it go or arrive.
    /// </summary>
    internal static class SiegeMoves
    {
        private static readonly RaycastHit[] hits = new RaycastHit[16];
        private static float nextNote;
        private static int stopped;

        /// <summary>Nothing solid between the two places: no wall, door, barricade or sandbags. Bodies don't count.</summary>
        internal static bool Clear(Vector3 from, Vector3 to)
        {
            if (Blocked(from + Vector3.up * .95f, to + Vector3.up * .95f)) return false;
            // More than a step: anything low in the way counts as well.
            return Flat(to - from).sqrMagnitude <= 1f || !Blocked(from + Vector3.up * .45f, to + Vector3.up * .45f);
        }

        private static bool Blocked(Vector3 a, Vector3 b)
        {
            Vector3 line = b - a;
            float length = line.magnitude;
            if (length < .01f) return false;
            int n = Physics.RaycastNonAlloc(a, line / length, hits, length, EFTHardSettings.Instance.MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (hits[i].collider != null && hits[i].collider.GetComponentInParent<Player>() == null) return true;
            return false;
        }

        /// <summary>The nearest walked floor within reach with nothing solid between it and here, ring by ring outward.</summary>
        internal static bool FloorThisSide(Vector3 from, float reach, out Vector3 floor)
        {
            floor = from;
            for (float ring = .3f; ring <= reach + .01f; ring += .3f)
            {
                float best = float.MaxValue;
                for (int i = 0; i < 12; i++)
                {
                    Vector3 p = from + Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward * ring;
                    if (!NavMesh.SamplePosition(p, out var hit, .25f, NavMesh.AllAreas)) continue;
                    float d = (hit.position - from).sqrMagnitude;
                    if (d >= best || Mathf.Abs(hit.position.y - from.y) > .8f || !Clear(from, hit.position)) continue;
                    best = d;
                    floor = hit.position;
                }
                if (best < float.MaxValue) return true;
            }
            return false;
        }

        /// <summary>Whether any of you could see someone standing here.</summary>
        internal static bool Seen(Vector3 at)
        {
            int mask = LayersMaskController.HighPolyWithTerrainMask | EFTHardSettings.Instance.MOVEMENT_MASK;
            return SiegeDirector.Defenders().Any(d => !Physics.Linecast(at + Vector3.up * 1.4f, d.Position + Vector3.up * 1.4f, mask, QueryTriggerInteraction.Ignore));
        }

        /// <summary>
        /// Puts a bot somewhere, as the game's own walking does it: told first, so it takes the new place as its own. Moved
        /// behind its back, it keeps trying to put the bot back where it was, through whatever's in between.
        /// </summary>
        internal static void Put(BotOwner bot, Vector3 to)
        {
            if (bot == null || bot.GetPlayer == null) return;
            try { bot.Mover?.AllowTeleport(); }
            catch (System.Exception) { }
            bot.GetPlayer.Teleport(to, true);
        }

        /// <summary>A short move, only with nothing solid between. False (and nothing done) otherwise.</summary>
        internal static bool Step(BotOwner bot, Vector3 to, string why)
        {
            if (bot == null || bot.IsDead || bot.GetPlayer == null) return false;
            Vector3 from = bot.Position;
            if (!Clear(from, to)) { Note(bot, from, to, why); return false; }
            Put(bot, to);
            return true;
        }

        /// <summary>
        /// A longer move, for one that's cut off: only where none of you can see it leave or arrive. False (and nothing done)
        /// otherwise.
        /// </summary>
        internal static bool Unseen(BotOwner bot, Vector3 to, string why)
        {
            if (bot == null || bot.IsDead || bot.GetPlayer == null) return false;
            Vector3 from = bot.Position;
            if (Seen(from) || Seen(to)) return false;
            Put(bot, to);
            Plugin.Log.LogInfo($"[siege] A {bot.Profile.Info.Settings.Role} at {from:F1}, out of your sight, {why}: moved to {to:F1}.");
            return true;
        }

        internal static void Note(BotOwner bot, Vector3 from, Vector3 to, string by)
        {
            stopped++;
            if (Time.time < nextNote) return;
            nextNote = Time.time + 10f;
            Plugin.Log.LogInfo($"[siege] Kept a {bot?.Profile?.Info?.Settings?.Role} from going through a wall: {from:F1} to {to:F1} ({by}; {stopped} so far).");
        }

        internal static void Reset() => stopped = 0;

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        internal static bool Watching(BotMover mover) => mover?._owner != null && SiegeLauncher.InSiege;
    }

    // The game's walking puts a bot back on the floor's mesh after each step. Not through anything solid.
    [HarmonyPatch(typeof(BotMover), nameof(BotMover.CastFromPos))]
    internal static class MoverStaysThisSidePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(BotMover __instance, Vector3 posiblePos, ref EBotLinkResult __result)
        {
            if (!SiegeMoves.Watching(__instance)) return true;
            Vector3 from = __instance._owner.Position;
            Vector3 way = posiblePos - from;
            // Most calls are a few centimetres: nothing to check.
            if (way.x * way.x + way.z * way.z < .3f * .3f) return true;
            if (SiegeMoves.Clear(from, posiblePos)) return true;
            SiegeMoves.Note(__instance._owner, from, posiblePos, "the game's walking");
            __result = EBotLinkResult.fail;
            return false;
        }
    }

    // With no floor close by, the game jumps a bot to the nearest floor it can find, as far as 100 m away. Only to floor
    // close by with nothing solid between, or it stays where it is.
    [HarmonyPatch(typeof(BotMover), nameof(BotMover.FindBetterPosition))]
    internal static class MoverNoLongJumpPatch
    {
        private const float Furthest = 2f;

        [HarmonyPostfix]
        private static void Postfix(BotMover __instance, ref Vector3 __result)
        {
            if (!SiegeMoves.Watching(__instance)) return;
            Vector3 from = __instance._owner.Position;
            if ((__result - from).sqrMagnitude <= Furthest * Furthest && SiegeMoves.Clear(from, __result)) return;
            Vector3 wanted = __result;
            __result = SiegeMoves.FloorThisSide(from, 1.5f, out var near) ? near : from;
            SiegeMoves.Note(__instance._owner, from, wanted, "the game's jump back to the floor");
        }
    }

    // Someone waiting to come in isn't going anywhere. As one wakes, the game starts him on a patrol, and its route-finding
    // starts by putting him at the beginning of the route: for one waiting in the tunnel, the nearest patrol point was the
    // tunnel's mouth, or a room beside it (where one turned up in the Rest Space). With BigBrain, his patrol even runs while
    // he waits. So nobody waiting is sent anywhere: the siege moves him itself when it's his turn.
    [HarmonyPatch]
    internal static class WaitingGoNowherePatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(BotMover)))
                if (m.Name == nameof(BotMover.GoToPoint) && m.ReturnType == typeof(NavMeshPathStatus)) yield return m;
        }

        [HarmonyPrefix]
        private static bool Prefix(BotMover __instance, ref NavMeshPathStatus __result)
        {
            if (!Holding(__instance)) return true;
            __result = NavMeshPathStatus.PathInvalid;
            return false;
        }

        internal static bool Holding(BotMover mover)
        {
            var bot = mover?._owner;
            return bot != null && SiegeDirector.Waiting.Count > 0 && SiegeDirector.Waiting.Contains(bot) && !SiegeDirector.Breachers.Contains(bot);
        }
    }

    [HarmonyPatch(typeof(BotMover), nameof(BotMover.GoToByWay))]
    internal static class WaitingNoWayPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(BotMover __instance) => !WaitingGoNowherePatch.Holding(__instance);
    }
}
