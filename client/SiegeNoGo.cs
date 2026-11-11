using System.Linq;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// Places players can't go in a siege: the attackers' tunnel (and the hole into it), and past the cultist room's gate,
    /// where they wait and the floor doesn't hold a player everywhere. Walking into either stops you where you were, as a
    /// wall would. Anyone who falls through the floor anyway is put back where they last stood. Each game minds its own player.
    /// </summary>
    internal static class SiegeNoGo
    {
        // This far below the lowest floor anyone walks on is under the hideout.
        private const float UnderTheFloor = 2.5f;
        private const float NoteEvery = 3f;

        private static Vector3 lastGood;
        private static bool haveGood;
        private static float nextNote, lowestFloor = float.NaN;
        private static int turnedBack;
        private static string lastWhy;

        internal static void Reset()
        {
            haveGood = false;
            lowestFloor = float.NaN;
            turnedBack = 0;
        }

        internal static void Update()
        {
            if (!SiegeLauncher.InSiege || SiegeCinematic.Active || SiegeState.Phase == SiegePhase.None) { haveGood = false; return; }
            var me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null || me.HealthController == null || !me.HealthController.IsAlive || me.MovementContext == null) return;
            Vector3 at = me.Position;
            string why = SiegeEntries.InTunnel(at) ? "the tunnel" : SiegeEntries.PastGate(at) ? "past the cultist gate" : Under(at) ? "under the floor" : null;
            if (why == null)
            {
                // Only somewhere they stood: not in the air mid-jump, or mid-fall through a gap.
                if (me.MovementContext.IsGrounded) { lastGood = at; haveGood = true; }
                return;
            }
            if (!haveGood) return;
            me.Teleport(lastGood, true);
            turnedBack++;
            if (Time.time >= nextNote || why != lastWhy)
            {
                nextNote = Time.time + NoteEvery;
                lastWhy = why;
                SiegeState.Notify("You can't go that way");
                Plugin.Log.LogInfo($"[siege] Kept you out of {why}: at {at:F2}, back to {lastGood:F2} ({turnedBack} so far).");
            }
        }

        private static bool Under(Vector3 at)
        {
            if (float.IsNaN(lowestFloor))
            {
                if (SiegeMap.Walkable == null || SiegeMap.Walkable.Count == 0) return false;
                lowestFloor = SiegeMap.Walkable.Min(p => p.y);
            }
            return at.y < lowestFloor - UnderTheFloor;
        }
    }
}
