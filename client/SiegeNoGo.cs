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
        // Off the floor the attackers' routes were worked out on: this far from any of it, at about its height, is somewhere
        // the hideout doesn't go (out through a blown wall, round the back of a room). Looked at this often.
        private const float OffTheFloor = 3f, FloorHeightApart = 2.5f, FloorCheckEvery = .2f, OffForLong = .6f;
        private static float nextFloorCheck, offSince = -1f;
        private static bool onTheFloor = true;

        private static Vector3 lastGood;
        private static bool haveGood;
        private static float nextNote, lowestFloor = float.NaN;
        private static int turnedBack;
        private static string lastWhy;

        internal static void Reset()
        {
            haveGood = false;
            onTheFloor = true;
            offSince = -1f;
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
                if (Time.time >= nextFloorCheck)
                {
                    nextFloorCheck = Time.time + FloorCheckEvery;
                    onTheFloor = OnTheFloor(at);
                    if (onTheFloor) offSince = -1f;
                    else if (offSince < 0f) offSince = Time.time;
                }
                // Only somewhere they stood: not in the air mid-jump, or mid-fall through a gap. And only in the hideout:
                // put back to somewhere outside it, they'd only fall again.
                if (onTheFloor) { if (me.MovementContext.IsGrounded) { lastGood = at; haveGood = true; } return; }
                // A moment's grace: a jump on to a table by a wall isn't leaving.
                if (Time.time - offSince < OffForLong) return;
                why = "outside the hideout";
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

        // Near enough to floor that was walked, at about its height. Yes where there's none to go by.
        private static bool OnTheFloor(Vector3 at)
        {
            var floor = SiegeMap.Walkable;
            if (floor == null || floor.Count == 0) return true;
            for (int i = 0; i < floor.Count; i++)
            {
                Vector3 cell = floor[i];
                float dx = cell.x - at.x, dz = cell.z - at.z;
                if (dx * dx + dz * dz < OffTheFloor * OffTheFloor && Mathf.Abs(cell.y - at.y) < FloorHeightApart) return true;
            }
            return false;
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
