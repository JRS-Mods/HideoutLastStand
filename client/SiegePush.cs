using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// Gets attackers to the defenders. A bot's own AI (the game's or SAIN's) holds cover while it knows where you are but
    /// can't see you, so on its own a wave would wait at the stairs while you sat behind a door. Where BigBrain is installed
    /// (SAIN needs it), the siege adds a layer above the others that, for an attacker that hasn't seen any of you for a few
    /// seconds, walks it toward the nearest of you, and carries out the jobs the siege gives it (breaching a door, coming
    /// through the tunnel). As soon as one of you is in sight, its own AI fights. Without BigBrain, nothing here runs.
    /// </summary>
    internal static class SiegePush
    {
        private const string BigBrainId = "xyz.drakia.bigbrain", LinkFile = "HideoutSiege.Brains.dll", LinkType = "HideoutSiege.Brains.SiegeBrains";
        // Lost sight this long ago: go looking. Closer than Near they're as good as there (a door between is the breachers' job).
        private const float PushAfter = 3f, Near = 3f, Sight = 50f, LookEvery = .4f, SprintFrom = 10f, Arrived = .45f, Stacked = 3f;

        private sealed class Track
        {
            public float LastSeen = -100f, NextLook, StillSince = -1f, NextNote, NextRescue, CutUntil;
            // Seeing you; going for you (not a rally point); and, from its last walk toward you, cut off from you.
            public bool Seeing, ToYou, Cut;
            public Vector3 WasAt;
        }

        private static readonly Dictionary<BotOwner, Track> tracks = new Dictionary<BotOwner, Track>();
        private static readonly UnityEngine.AI.NavMeshPath route = new UnityEngine.AI.NavMeshPath();
        private static readonly Dictionary<BotOwner, Vector3> orders = new Dictionary<BotOwner, Vector3>();

        /// <summary>Where a door's being breached: the rest gather by it rather than at whatever wall's nearest you.</summary>
        internal static Vector3? Rally;

        // Your defences have them shut out: no walk gets to you, so one "toward you" ends at whatever wall's nearest, maybe a
        // dead end in another room. Until they're through, they gather in front of the defence in the way instead, where
        // they can fight you over it, and one of them can blow it.
        private static Vector3? shutOutAt;
        private static float shutOutUntil;

        internal static void ShutOut(Vector3 at)
        {
            shutOutAt = at;
            shutOutUntil = Time.time + 20f;
        }

        /// <summary>The defence in the way has gone: back to coming for you.</summary>
        internal static void NotShutOut() => shutOutAt = null;

        /// <summary>True once the layer's in: the director then leaves the walking to it.</summary>
        internal static bool Active { get; private set; }

        internal static void Load()
        {
            if (!Chainloader.PluginInfos.ContainsKey(BigBrainId)) return;
            try
            {
                string path = Path.Combine(Plugin.Folder, LinkFile);
                if (!File.Exists(path)) { Plugin.Log.LogWarning($"BigBrain is installed but {LinkFile} isn't: attackers may wait for you instead of coming in."); return; }
                Assembly.LoadFrom(path).GetType(LinkType, true).GetMethod("Register", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                Active = true;
                Plugin.Log.LogInfo("BigBrain found: attackers come looking for you whatever AI they run.");
            }
            catch (Exception e) { Plugin.Log.LogError("The siege's AI layer couldn't be added: " + e); }
        }

        internal static void Reset()
        {
            tracks.Clear();
            orders.Clear();
            Rally = null;
            shutOutAt = null;
        }

        /// <summary>The director's job for this bot: go here (and stay) until told otherwise.</summary>
        internal static void Order(BotOwner bot, Vector3 to)
        {
            if (bot != null) orders[bot] = to;
        }

        internal static void Release(BotOwner bot)
        {
            if (bot != null) orders.Remove(bot);
        }

        /// <summary>For the layer: whether the siege wants this bot moving, and where to.</summary>
        public static bool Wants(BotOwner bot, out Vector3 target, out bool sprint)
        {
            target = default;
            sprint = false;
            if (bot == null || bot.IsDead || bot.GetPlayer == null || Net.IsClient || SiegeState.Phase != SiegePhase.Fighting || SiegeCinematic.Active) return false;
            var director = SiegeDirector.Instance;
            if (director == null || !director.IsAttacker(bot) || SiegeDirector.Waiting.Contains(bot)) return false;
            if (orders.TryGetValue(bot, out target)) return true;
            // Other jobs the director steers itself.
            if (SiegeDirector.Breachers.Contains(bot)) return false;

            if (!tracks.TryGetValue(bot, out var track)) tracks[bot] = track = new Track();
            if (Time.time >= track.NextLook)
            {
                track.NextLook = Time.time + LookEvery;
                track.Seeing = Sees(bot);
                if (track.Seeing) track.LastSeen = Time.time;
            }
            if (track.Seeing || Time.time - track.LastSeen < PushAfter) return false;
            Player nearest = null;
            float best = float.MaxValue;
            foreach (var defender in SiegeDirector.Defenders())
            {
                float d = (defender.Position - bot.Position).sqrMagnitude;
                if (d < best) { best = d; nearest = defender; }
            }
            if (nearest == null || best < Near * Near) return false;
            track.ToYou = false;
            if (Rally.HasValue)
            {
                float toRally = (Rally.Value - bot.Position).sqrMagnitude;
                if (toRally < Stacked * Stacked) return false;
                // Only if that's on the way: someone already past it just keeps coming.
                if (toRally < best)
                {
                    target = Rally.Value;
                    sprint = toRally > SprintFrom * SprintFrom;
                    return true;
                }
            }
            // Only those whose own walk toward you went nowhere: one that can get to you (in through the tunnel, say) keeps coming.
            if (shutOutAt.HasValue && Time.time < shutOutUntil && track.Cut && Time.time < track.CutUntil)
            {
                float toWall = (shutOutAt.Value - bot.Position).sqrMagnitude;
                // There already: its own AI has it, fighting you over the defence if it can see you.
                if (toWall < Stacked * Stacked) return false;
                target = shutOutAt.Value;
                sprint = toWall > SprintFrom * SprintFrom;
                return true;
            }
            target = nearest.Position;
            sprint = best > SprintFrom * SprintFrom;
            track.ToYou = true;
            return true;
        }

        // In sight of any of you: what its own AI says, or a clear line from its eyes to one of you.
        internal static bool Sees(BotOwner bot)
        {
            try { if (bot.Memory?.GoalEnemy != null && bot.Memory.GoalEnemy.IsVisible) return true; }
            catch (Exception) { }
            var head = bot.GetPlayer.PlayerBones?.Head;
            Vector3 eye = head != null ? head.position : bot.Position + Vector3.up * 1.6f;
            int mask = LayersMaskController.HighPolyWithTerrainMask;
            foreach (var defender in SiegeDirector.Defenders())
            {
                Vector3 chest = defender.Position + Vector3.up * 1.3f;
                if ((chest - eye).sqrMagnitude > Sight * Sight) continue;
                if (!Physics.Linecast(eye, chest, mask, QueryTriggerInteraction.Ignore)) return true;
            }
            return false;
        }

        // Along the siege's own floor, as far toward the spot as it goes: up to a shut door, if that's what's in the way (the
        // breachers' job). The game's own route-finding goes by its cover points instead, which here would have them walk
        // into a wall, or give up and jump them somewhere else.
        internal static UnityEngine.AI.NavMeshPathStatus Route(BotOwner bot, Vector3 target)
        {
            const int all = UnityEngine.AI.NavMesh.AllAreas;
            if (!UnityEngine.AI.NavMesh.SamplePosition(bot.Position, out var on, 1.5f, all)) return UnityEngine.AI.NavMeshPathStatus.PathInvalid;
            Vector3 start = on.position;
            // Standing a little off the floor's mesh (on the stairs, say): onto it, or no route starts. Never through anything:
            // the nearest floor can be the far side of a thin wall or a door, so then it's the nearest on this side.
            Vector3 off = start - bot.Position;
            if (new Vector2(off.x, off.z).sqrMagnitude > .35f * .35f || Mathf.Abs(off.y) > .6f)
            {
                if (!SiegeMoves.Clear(bot.Position, start))
                {
                    SiegeMoves.Note(bot, bot.Position, start, "on to the floor");
                    if (!SiegeMoves.FloorThisSide(bot.Position, 1.5f, out start)) return UnityEngine.AI.NavMeshPathStatus.PathInvalid;
                }
                SiegeMoves.Put(bot, start);
            }
            Vector3 to = UnityEngine.AI.NavMesh.SamplePosition(target, out var there, 1.5f, all) ? there.position : target;
            if (!UnityEngine.AI.NavMesh.CalculatePath(start, to, all, route) || route.status == UnityEngine.AI.NavMeshPathStatus.PathInvalid || route.corners.Length < 2)
                return UnityEngine.AI.NavMeshPathStatus.PathInvalid;
            bot.GoToByWay(route.corners, .3f);
            return route.status;
        }

        // Going nowhere for a while under this layer: noted in the log, with what the route-finding said. One with no way at
        // all from where it stands to where it's going (a scrap of floor cut off by a door, a wall or someone's sandbags,
        // which it can be walked or pushed onto) is moved to the nearest floor that has one.
        private static void Stalled(BotOwner bot, Vector3 target, UnityEngine.AI.NavMeshPathStatus status)
        {
            if (!tracks.TryGetValue(bot, out var track)) tracks[bot] = track = new Track();
            if ((bot.Position - track.WasAt).sqrMagnitude > .5f * .5f) { track.WasAt = bot.Position; track.StillSince = Time.time; return; }
            if (status != UnityEngine.AI.NavMeshPathStatus.PathComplete && !orders.ContainsKey(bot) && Time.time - track.StillSince >= RescueAfter && Time.time >= track.NextRescue)
            {
                track.NextRescue = Time.time + 6f;
                if (Rescue(bot, target))
                {
                    track.WasAt = bot.Position;
                    track.StillSince = Time.time;
                    return;
                }
            }
            if (Time.time - track.StillSince < 8f || Time.time < track.NextNote) return;
            track.NextNote = Time.time + 30f;
            Plugin.Log.LogInfo($"[siege] A {bot.Profile.Info.Settings.Role} at {bot.Position:F1} isn't getting anywhere toward {target:F1}: route {status}, {(orders.ContainsKey(bot) ? "on a job" : "looking for you")}.");
        }

        private const float RescueAfter = 5f, RescueReach = 10f;

        // The nearest floor within reach that has a way through to the target, out of all of you's sight, and the bot put
        // there. What it was standing on is logged, so where floor gets cut off can be found and fixed.
        private static bool Rescue(BotOwner bot, Vector3 target)
        {
            const int all = UnityEngine.AI.NavMesh.AllAreas;
            Vector3 from = bot.Position;
            // Never in front of you: one of you watching it would see it vanish.
            if (SiegeMoves.Seen(from)) return false;
            if (!UnityEngine.AI.NavMesh.SamplePosition(target, out var there, 1.5f, all)) return false;
            bool onFloor = UnityEngine.AI.NavMesh.SamplePosition(from, out var on, 1.5f, all);
            string stoodOn = onFloor ? $"{on.position:F2}" : "no floor";
            Vector3? end = onFloor && UnityEngine.AI.NavMesh.CalculatePath(on.position, there.position, all, route) && route.corners.Length > 0 ? route.corners[route.corners.Length - 1] : (Vector3?)null;
            // It can get somewhere from here, or a shut door's what stops it (the breachers' job): not cut off.
            if (end.HasValue && (end.Value - on.position).sqrMagnitude > 2f * 2f) return false;
            if (end.HasValue && SiegeBreach.InnerDoors().Any(d => SiegeBreach.IsShut(d.Door) && (d.Centre - end.Value).sqrMagnitude < 3f * 3f)) return false;
            // Nor past your sandbags or steel: those get blown, not walked through.
            if (end.HasValue && SiegeDecor.Stopping(end.Value) != null) return false;
            string reached = end.HasValue ? $"{end.Value:F2}" : "nowhere";
            int sight = LayersMaskController.HighPolyWithTerrainMask | EFTHardSettings.Instance.MOVEMENT_MASK;
            var defenders = SiegeDirector.Defenders().ToList();
            int tried = 0;
            foreach (var cell in SiegeMap.Walkable
                         .Where(c => (c - from).sqrMagnitude > 1.2f * 1.2f && (c - from).sqrMagnitude < RescueReach * RescueReach && Mathf.Abs(c.y - from.y) < 2f)
                         .OrderBy(c => (c - from).sqrMagnitude))
            {
                if (++tried > 40) break;
                if (defenders.Any(d => !Physics.Linecast(cell + Vector3.up * 1.4f, d.Position + Vector3.up * 1.4f, sight, QueryTriggerInteraction.Ignore))) continue;
                if (!UnityEngine.AI.NavMesh.SamplePosition(cell, out var spot, .5f, all)) continue;
                if (!UnityEngine.AI.NavMesh.CalculatePath(spot.position, there.position, all, route) || route.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) continue;
                Plugin.Log.LogInfo($"[siege] A {bot.Profile.Info.Settings.Role} at {from:F2} had no way to {target:F1} (on the floor at {stoodOn}, a route got as far as {reached}): moved to {spot.position:F2}.");
                SiegeMoves.Put(bot, spot.position);
                return true;
            }
            Plugin.Log.LogInfo($"[siege] A {bot.Profile.Info.Settings.Role} at {from:F2} has no way to {target:F1} (on the floor at {stoodOn}, a route got as far as {reached}), and nowhere near does either.");
            if (tracks.TryGetValue(bot, out var track)) track.NextRescue = Time.time + 30f;
            return false;
        }

        /// <summary>For the layer's action: walks (or runs) the bot to the spot, and stands there once it's arrived.</summary>
        public static void Steer(BotOwner bot, Vector3 target, bool sprint, ref float nextOrder, ref Vector3 sent)
        {
            if ((bot.Position - target).sqrMagnitude < Arrived * Arrived)
            {
                bot.Sprint(false);
                bot.Mover.Stop();
                return;
            }
            if (Time.time >= nextOrder || (target - sent).sqrMagnitude > 1f)
            {
                nextOrder = Time.time + 1f;
                sent = target;
                var status = Route(bot, target);
                // Walking toward you and getting nowhere near: cut off, for a while (then it looks again).
                if (tracks.TryGetValue(bot, out var track) && track.ToYou)
                {
                    track.Cut = status != UnityEngine.AI.NavMeshPathStatus.PathComplete;
                    track.CutUntil = Time.time + 8f;
                }
                Stalled(bot, target, status);
            }
            bot.Mover.SetTargetMoveSpeed(1f);
            bot.Sprint(sprint);
            bot.Steering.LookToMovingDirection();
        }
    }
}
