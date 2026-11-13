using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// The way in: the hideout's own front door, whichever one your Security level has, with the stairwell behind it.
    /// It stays locked until the charge goes off. Any other door someone shuts in their way gets a charge of its own.
    /// </summary>
    internal static class SiegeBreach
    {
        // The middle of the doorway, and the way out to the stairs. The doorway is on a diagonal.
        internal static readonly Vector3 Doorway = new Vector3(-8.85f, 0f, -8.55f);
        internal static readonly Vector3 Outward = new Vector3(-.707f, 0f, -.707f);
        internal static readonly Vector3 FrontCharge = Doorway + Outward * .14f + Vector3.up * .85f;

        // By the bed, at the far end of the hideout from the door (or wherever the Rest Space has been moved to). Co-op
        // players each get their own spot.
        private static readonly Vector3[] WakeSpots =
        {
            new Vector3(15.7f, 0f, -1.6f),
            new Vector3(14.6f, 0f, -2.2f),
            new Vector3(15.6f, 0f, -2.9f),
            new Vector3(14.4f, 0f, -3.4f),
        };
        private static readonly Vector3 PillowAt = new Vector3(15.9f, .78f, -.5f);
        internal static Vector3 Pillow => HideoutCopy.InRestSpace(PillowAt);
        // Looking up from the pillow, at the ceiling over the bed.
        internal static Vector3 PillowLook => HideoutCopy.InRestSpace(PillowAt + new Vector3(-.2f, 2f, -.5f));

        private const float BlindRange = 12f, ConcussionRange = 16f;

        private static Door front;
        private static readonly Dictionary<Door, GameObject> charges = new Dictionary<Door, GameObject>();
        private static readonly List<Door> doors = new List<Door>();
        private static readonly HashSet<Player> drawn = new HashSet<Player>(), lit = new HashSet<Player>();
        private static readonly Dictionary<Door, Vector3> centres = new Dictionary<Door, Vector3>();
        private static readonly HashSet<Door> blown = new HashSet<Door>();
        private static readonly Dictionary<Door, NavMeshObstacle> blocks = new Dictionary<Door, NavMeshObstacle>();
        private static readonly Dictionary<Door, Vector3> acrossDoors = new Dictionary<Door, Vector3>();
        // Where someone kneeling can reach it, about level with the handle.
        private const float ChargeBelowCentre = .25f;
        private static SiegeCharge carried;
        private static bool sealedUp;
        private static float nextDrawCheck, nextDoorCheck;

        // Where the first attackers end up: one at the door, two covering from the bottom of the steps.
        internal static Vector3 StagePoint(int index)
        {
            float[] distance = { .7f, 1.9f, 2.2f };
            float[] side = { 0f, .2f, -.4f };
            Vector3 across = new Vector3(Outward.z, 0f, -Outward.x);
            return Doorway + Outward * distance[index % distance.Length] + across * side[index % side.Length];
        }

        internal static Door FindFrontDoor()
        {
            return Object.FindObjectsOfType<Door>()
                .Where(d => (d.transform.position - Doorway).sqrMagnitude < 5.7f)
                .OrderBy(d => (d.transform.position - Doorway).sqrMagnitude)
                .FirstOrDefault();
        }

        // An unbuilt Security station has an open hole there, so it borrows the wood door from level 1.
        private static void EnsureFrontDoor()
        {
            if (FindFrontDoor() != null) return;
            var spare = Object.FindObjectsOfType<Door>(true)
                .Where(d => (d.transform.position - Doorway).sqrMagnitude < 5.7f)
                .FirstOrDefault(d => d.transform.parent != null && d.transform.parent.name.Contains("Wood"));
            if (spare == null) return;
            // Off its level first, or switching it on would need that whole level switched on too.
            spare.transform.parent.SetParent(null, true);
            spare.transform.parent.gameObject.SetActive(true);
        }

        // Runs the action with every door out of the way, so the floor behind them all gets walked.
        // A door someone shuts later doesn't change the walkable floor: the attackers still try to come through it.
        internal static void WithDoorsOpen(Action action)
        {
            // Another player's machine doesn't know the host's Security level yet, so that one waits for Seal.
            if (!Net.IsClient) EnsureFrontDoor();
            var solid = Object.FindObjectsOfType<Door>().SelectMany(d => d.GetComponentsInChildren<Collider>()).Where(c => c.enabled).ToList();
            foreach (var c in solid) c.enabled = false;
            try { action(); }
            finally { foreach (var c in solid) c.enabled = true; }
        }

        // Locks the front door until it's blown.
        internal static void Seal()
        {
            if (sealedUp) return;
            sealedUp = true;
            EnsureFrontDoor();
            front = FindFrontDoor();
            Bars(HideoutCopy.DoorLevel);
            doors.Clear();
            doors.AddRange(Object.FindObjectsOfType<Door>());
            Bulletproof();
            // Which way is through each door, worked out now rather than the first time one's in the way mid-fight.
            foreach (var door in doors) if (door != null && door != front) Across(door);
            // A station's "use this" box would get in the way of what's on its rack. Only the generator does anything here.
            foreach (var trigger in Object.FindObjectsOfType<EFT.Hideout.HideoutInteractionTrigger>())
            {
                var area = trigger.GetComponentInParent<EFT.Hideout.HideoutArea>();
                bool works = trigger.transform.root.name == SiegeGenerator.Station || (area != null && SiegeStations.Works(area.AreaTemplate.Type));
                if (!works)
                    foreach (var box in trigger.GetComponents<Collider>()) box.enabled = false;
            }
            // Nor do the lamps and radios work in a raid, and they'd get between you and the things you're reaching for.
            int reachable = GameWorld.InteractiveLootMaskWPlayer, switchedOff = 0;
            foreach (var appliance in Object.FindObjectsOfType<Appliance>())
                foreach (var box in appliance.GetComponentsInChildren<Collider>())
                    if ((reachable & (1 << box.gameObject.layer)) != 0)
                    {
                        box.enabled = false;
                        switchedOff++;
                    }
            Plugin.Log.LogInfo($"[siege] Took {switchedOff} lamp and radio switches out of reach.");
            if (front == null)
            {
                Plugin.Log.LogWarning("[siege] This hideout has no front door: they'll come straight in.");
                return;
            }
            Plugin.Log.LogInfo($"[siege] Front door: {front.transform.parent?.name}.");
            front.DoorState = EDoorState.Locked;
            front.Operatable = false;
            front.CanBeBreached = false;
        }

        // Some of the hideout's doors let bullets through. A door that stops people should stop those as well.
        private static void Bulletproof()
        {
            foreach (var door in doors)
                foreach (var part in (door.transform.parent ?? door.transform).GetComponentsInChildren<BallisticCollider>(true))
                {
                    if (part.PenetrationChance > 0f) Plugin.Log.LogInfo($"[siege] {door.transform.parent.name}: {part.name} was letting bullets through ({part.PenetrationChance:F2}).");
                    part.PenetrationChance = 0f;
                }
        }

        // The floor was walked with the doors out of the way, so left alone a bot's route runs straight through a shut door,
        // and its movement will put it through to stay on that route. So a shut door cuts the route for as long as it's shut.
        internal static void UpdateDoorBlocks()
        {
            foreach (var (door, centre) in InnerDoors().ToList())
            {
                if (!blocks.TryGetValue(door, out var block))
                {
                    if (!IsShut(door)) continue;
                    var parts = door.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToList();
                    if (parts.Count == 0) continue;
                    var size = parts.Aggregate(parts[0].bounds, (all, c) => { all.Encapsulate(c.bounds); return all; }).size;
                    // Just the doorway, turned to it: a box the size of the door's parts, unturned, can be much more than
                    // that and cut a corner of the room off as well.
                    Vector3 through = Across(door), along = Vector3.Cross(Vector3.up, through);
                    float width = Mathf.Clamp(Mathf.Abs(size.x * along.x) + Mathf.Abs(size.z * along.z), 1f, 1.8f);
                    var holder = new GameObject("Hideout Siege door block");
                    holder.transform.SetPositionAndRotation(centre, Quaternion.LookRotation(through));
                    block = holder.AddComponent<NavMeshObstacle>();
                    block.shape = NavMeshObstacleShape.Box;
                    block.size = new Vector3(width, Mathf.Max(size.y, 2f), .35f);
                    block.carving = true;
                    block.carveOnlyStationary = false;
                    blocks[door] = block;
                }
                block.enabled = IsShut(door);
            }
        }

        // The doors attackers might have to deal with, with the middle of each. A door's own position is its hinge.
        internal static IEnumerable<(Door Door, Vector3 Centre)> InnerDoors()
        {
            foreach (var door in doors)
                if (door != null && door != front && !blown.Contains(door)) yield return (door, Centre(door));
        }

        internal static bool IsShut(Door door) => door.DoorState == EDoorState.Shut || door.DoorState == EDoorState.Locked;

        private static Vector3 Centre(Door door)
        {
            if (centres.TryGetValue(door, out Vector3 centre)) return centre;
            var parts = door.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToList();
            centre = parts.Count == 0 ? door.transform.position : parts.Aggregate(parts[0].bounds, (all, c) => { all.Encapsulate(c.bounds); return all; }).center;
            if (IsShut(door)) centres[door] = centre;
            return centre;
        }

        private static Door DoorAt(Vector3 at)
        {
            Seal();
            RefreshDoors();
            if (front != null && (Flat(at) - Flat(Doorway)).sqrMagnitude < 2.5f) return front;
            return doors.Where(d => d != null && d != front)
                .OrderBy(d => (Centre(d) - at).sqrMagnitude).FirstOrDefault(d => (Centre(d) - at).sqrMagnitude < 9f);
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // Sticks a charge on a door, on the side the point is on.
        internal static void Plant(Vector3 at)
        {
            var door = DoorAt(at);
            if (door == null || charges.ContainsKey(door) || blown.Contains(door))
            {
                StopCarrying();
                return;
            }
            bool byHand = carried != null;
            var charge = byHand ? carried : SiegeCharge.OnItsOwn(at);
            carried = null;
            float planted = Time.time;
            charges[door] = door == front
                ? charge.PutOn(at, Outward, () => SiegeState.Phase == SiegePhase.Prep ? SiegeState.CountdownUntil - Time.time : float.PositiveInfinity, byHand)
                : charge.PutOn(at, Flat(at - Centre(door)), () => planted + SiegeDirector.InnerFuse - Time.time, byHand);
        }

        // In someone's hand until it goes on a door.
        internal static void Carry(Player who)
        {
            StopCarrying();
            if (who != null) carried = SiegeCharge.InHand(who);
        }

        internal static void StopCarrying()
        {
            if (carried != null) Object.Destroy(carried.gameObject);
            carried = null;
        }

        // The door in the Emergency Wall, if the hideout has got that far.
        internal static Door EmergencyDoor()
        {
            // Hideout Extensions' rooms have copies of this door: theirs aren't it.
            var own = doors.Where(d => d != null && d != front && d.transform.root.name != "Hideout extensions").ToList();
            foreach (var door in own)
            {
                var area = door.GetComponentInParent<EFT.Hideout.HideoutArea>();
                if (area != null && area.AreaTemplate.Type == EAreaType.EmergencyWall) return door;
            }
            return own.FirstOrDefault(d => d.transform.parent != null && d.transform.parent.name.StartsWith("Outside_Door_Metal"));
        }

        internal static Vector3 CentreOf(Door door) => Centre(door);

        /// <summary>The middle of every door, the front door's too.</summary>
        internal static IEnumerable<Vector3> AllDoorCentres()
        {
            yield return Doorway + Vector3.up;
            foreach (var door in doors) if (door != null) yield return Centre(door);
        }

        // Which way a door faces, along the floor.
        /// <summary>
        /// The way through a doorway, level: of the door's own axes, laid flat, the one with walked floor on both sides of it
        /// (the other runs along the wall). A door's parts aren't always thinnest that way, and a model turned on its side
        /// has one of them pointing up.
        /// </summary>
        internal static Vector3 Across(Door door)
        {
            if (acrossDoors.TryGetValue(door, out Vector3 known)) return known;
            Vector3 centre = Centre(door);
            var axes = new List<Vector3>();
            foreach (var axis in new[] { door.transform.forward, door.transform.right, door.transform.up })
            {
                Vector3 flat = new Vector3(axis.x, 0f, axis.z);
                if (flat.sqrMagnitude < .25f) continue;
                flat.Normalize();
                if (!axes.Any(a => Mathf.Abs(Vector3.Dot(a, flat)) > .7f)) axes.Add(flat);
            }
            Vector3 best = ThroughDoor(door);
            int bestFloor = -1;
            foreach (var axis in axes)
            {
                int floor = (FloorNear(centre + axis * 1f, centre.y) ? 1 : 0) + (FloorNear(centre - axis * 1f, centre.y) ? 1 : 0);
                if (floor > bestFloor || (floor == bestFloor && Mathf.Abs(Vector3.Dot(axis, best)) > .7f)) { bestFloor = floor; best = axis; }
            }
            if (SiegeMap.Walkable.Count > 0) acrossDoors[door] = best;
            return best;
        }

        // Walked floor within half a metre, at about the height of the door's foot.
        private static bool FloorNear(Vector3 p, float doorMiddle)
        {
            foreach (var cell in SiegeMap.Walkable)
            {
                float dx = cell.x - p.x, dz = cell.z - p.z;
                if (dx * dx + dz * dz < .5f * .5f && Mathf.Abs(cell.y - (doorMiddle - 1.1f)) < .8f) return true;
            }
            return false;
        }

        internal static Vector3 ThroughDoor(Door door)
        {
            Vector3 way = door.transform.forward;
            way.y = 0f;
            float best = 0f;
            var parts = door.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToList();
            if (parts.Count == 0) return way.sqrMagnitude > .001f ? way.normalized : Vector3.forward;
            var box = parts[0].bounds;
            foreach (var part in parts) box.Encapsulate(part.bounds);
            foreach (var candidate in new[] { door.transform.forward, door.transform.right })
            {
                // The way through is the way the door's thinnest.
                float across = Mathf.Abs(Vector3.Dot(box.size, new Vector3(Mathf.Abs(candidate.x), Mathf.Abs(candidate.y), Mathf.Abs(candidate.z))));
                if (best == 0f || across < best)
                {
                    best = across;
                    way = candidate;
                }
            }
            way.y = 0f;
            return way.sqrMagnitude > .001f ? way.normalized : Vector3.forward;
        }

        // Where a charge goes on this door, on the side of whoever's planting it.
        internal static Vector3 ChargeSpot(Vector3 centre, Vector3 planter)
        {
            Vector3 toward = Flat(planter - centre);
            return centre + (toward.sqrMagnitude > .001f ? toward.normalized * .1f : Vector3.zero) + Vector3.down * ChargeBelowCentre;
        }

        internal static void Blow()
        {
            foreach (var bar in bars) if (bar != null) Object.Destroy(bar);
            bars.Clear();
            Blast(FrontCharge);
        }

        // The front door's upgrades, on the inside of it: steel across it, a bar for each charge it'll take before it goes.
        private static readonly List<GameObject> bars = new List<GameObject>();

        private static void Bars(int level)
        {
            foreach (var bar in bars) if (bar != null) Object.Destroy(bar);
            bars.Clear();
            if (level <= 0) return;
            var seed = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var steel = new Material(seed.GetComponent<Renderer>().sharedMaterial) { name = "Hideout Siege door bars", color = new Color(.22f, .23f, .25f) };
            steel.SetFloat("_Metallic", .85f);
            steel.SetFloat("_Glossiness", .45f);
            Object.Destroy(seed);
            Vector3 across = Vector3.Cross(Vector3.up, Outward);
            Quaternion turn = Quaternion.LookRotation(-Outward);
            float[] heights = level >= 2 ? new[] { .35f, .8f, 1.25f, 1.7f } : new[] { .55f, 1.45f };
            foreach (float h in heights)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(bar.GetComponent<Collider>());
                bar.name = "Hideout Siege door bar";
                bar.transform.SetPositionAndRotation(Doorway - Outward * .16f + Vector3.up * h, turn);
                bar.transform.localScale = new Vector3(1.35f, .09f, .05f);
                bar.GetComponent<MeshRenderer>().sharedMaterial = steel;
                bars.Add(bar);
            }
            Plugin.Log.LogInfo($"[siege] The front door's reinforced: it takes {level + 1} charges.");
        }

        /// <summary>A charge went off on the front door and it held: the blast, but it stays shut, and a bar's gone.</summary>
        internal static void Held(Vector3 at)
        {
            if (front != null && charges.TryGetValue(front, out var charge))
            {
                Object.Destroy(charge);
                charges.Remove(front);
            }
            try
            {
                if (Singleton<Systems.Effects.Effects>.Instantiated) Singleton<Systems.Effects.Effects>.Instance.EmitGrenade("Grenade_new", at, Vector3.up);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
            SiegeSounds.Play("breach");
            Plugin.Instance.StartCoroutine(SiegeLights.Flash(at));
            int half = bars.Count / 2;
            for (int i = 0; i < half && bars.Count > 0; i++)
            {
                if (bars[bars.Count - 1] != null) Object.Destroy(bars[bars.Count - 1]);
                bars.RemoveAt(bars.Count - 1);
            }
            Stun(at);
        }

        internal static void Blast(Vector3 at)
        {
            // The tunnel's wall isn't a door. A door within reach of its charge would be taken for it, and if that door had
            // been blown already, the wall would never come down and those in the tunnel would be stuck there.
            // Nor are your defences: one with a charge on it goes, and no door near it.
            var defence = SiegeDecor.At(at);
            var door = defence != null || SiegeEntries.IsTunnelCharge(at) ? null : DoorAt(at);
            if (door != null && !blown.Add(door)) return;
            if (door != null && charges.TryGetValue(door, out var charge))
            {
                Object.Destroy(charge);
                charges.Remove(door);
            }
            try
            {
                if (Singleton<Systems.Effects.Effects>.Instantiated)
                {
                    Singleton<Systems.Effects.Effects>.Instance.EmitGrenade("Grenade_new", at, Vector3.up);
                    Singleton<Systems.Effects.Effects>.Instance.EmitGrenade("Grenade_new", at + Vector3.up * .3f, Vector3.up);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
            SiegeSounds.Play("breach");
            Plugin.Instance.StartCoroutine(SiegeLights.Flash(at));
            // Dust, debris and smoke, thrown the way the blast goes: through what the charge is on.
            bool wall = SiegeEntries.IsTunnelCharge(at);
            Vector3 toward = wall ? -SiegeEntries.TunnelOut : door != null ? Centre(door) - at : defence != null ? defence.position - at : Vector3.zero;
            SiegeBlastLook.Burst(at, toward, wall, wall ? SiegeEntries.WallLook : null);
            // Before the door or barricade goes: whoever's behind it is shielded by it.
            Wound(at);
            SiegeEntries.Blasted(at);
            if (defence != null)
            {
                SiegeDecor.Blow(defence);
                SiegePush.NotShutOut();
            }
            if (door != null)
            {
                if (blocks.TryGetValue(door, out var block))
                {
                    if (block != null) Object.Destroy(block.gameObject);
                    blocks.Remove(door);
                }
                TakeDown(door);
            }
            Stun(at);
        }

        private static void TakeDown(Door door)
        {
            // Out of the way first: whatever happens with the rest, they have to be able to get through.
            foreach (var c in door.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            if (door.Obstacle != null) door.Obstacle.enabled = false;
            try
            {
                if (door.transform.parent.GetComponentInChildren<BrokenDoor>(true) != null) door.KickOpen(true);
                else Hide(door);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[siege] The door didn't break properly: " + e);
                Hide(door);
            }
        }

        // A steel door has no broken version to show, so it's simply gone.
        private static void Hide(Door door)
        {
            foreach (var r in door.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        }

        // A blast hurts whoever of you is near it on its own side: badly up close, nothing past WoundRange. Each game sees to
        // its own player. Not the head: a charge going off beside you shouldn't be a coin toss. Not the attackers: they set
        // these and know to stand clear, but the ones coming up behind didn't, and waves were losing men to their own charges.
        private const float WoundRange = 4.5f, WoundFull = 1f;
        private static readonly (EBodyPart Part, float Damage)[] Wounds =
        {
            (EBodyPart.Chest, 35f), (EBodyPart.Stomach, 30f), (EBodyPart.LeftArm, 20f), (EBodyPart.RightArm, 20f), (EBodyPart.LeftLeg, 25f), (EBodyPart.RightLeg, 25f),
        };

        private static void Wound(Vector3 at)
        {
            var world = Singleton<GameWorld>.Instance;
            if (world == null || SiegeCinematic.Active) return;
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            foreach (var player in world.AllAlivePlayersList.ToList())
            {
                if (player == null || !player.IsYourPlayer) continue;
                var health = player.ActiveHealthController;
                if (health == null || !health.IsAlive) continue;
                Vector3 chest = player.Position + Vector3.up * 1.1f;
                float distance = Vector3.Distance(at, chest);
                if (distance > WoundRange) continue;
                // From just off the charge toward them: anything solid between (the door it's on, if they're the far side of it) shields them.
                Vector3 way = (chest - at).normalized;
                var shield = Shielded(at + way * .25f, chest, mask);
                if (shield != null)
                {
                    Plugin.Log.LogInfo($"[siege] {(player.IsYourPlayer ? "You were" : "A " + player.Profile.Info.Settings.Role + " was")} {distance:F1} m from the blast, shielded by {shield.name}.");
                    continue;
                }
                float share = Mathf.Clamp01(1f - (distance - WoundFull) / (WoundRange - WoundFull));
                try
                {
                    foreach (var (part, damage) in Wounds)
                        health.ApplyDamage(part, damage * share, new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Explosion, Damage = damage * share, Direction = way });
                }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] The blast couldn't hurt someone: " + e.Message); }
                Plugin.Log.LogInfo($"[siege] {(player.IsYourPlayer ? "You were" : "A " + player.Profile.Info.Settings.Role + " was")} caught by the blast, {distance:F1} m away ({share:P0}).");
            }
        }

        // Something solid between: a wall, a door, a barricade. Not anyone's body, theirs or another's.
        private static Collider Shielded(Vector3 from, Vector3 to, int mask)
        {
            Vector3 line = to - from;
            foreach (var hit in Physics.RaycastAll(from, line.normalized, line.magnitude, mask, QueryTriggerInteraction.Ignore))
                if (hit.collider != null && hit.collider.GetComponentInParent<Player>() == null) return hit.collider;
            return null;
        }

        // Too close and you're concussed for a while. If you could see it as well, you're blinded.
        private static void Stun(Vector3 at)
        {
            var me = Singleton<GameWorld>.Instance?.MainPlayer;
            var health = me?.ActiveHealthController;
            if (health == null || !health.IsAlive) return;
            Vector3 eyes = me.Position + Vector3.up * 1.5f;
            float distance = Vector3.Distance(eyes, at);
            if (distance > ConcussionRange) return;
            bool sees = distance <= BlindRange && !Physics.Linecast(at + Vector3.up * .1f, eyes, EFTHardSettings.Instance.MOVEMENT_MASK, QueryTriggerInteraction.Ignore);
            float near = 1f - distance / ConcussionRange;
            health.DoContusion(4f + 14f * near, Mathf.Clamp01(near + .25f));
            if (sees)
            {
                float blind = 1f - distance / BlindRange;
                health.DoBurnEyes(at, blind, blind, 3f);
            }
            Plugin.Log.LogInfo($"[siege] Caught in the blast at {distance:F1} m{(sees ? ", and looking at it" : "")}.");
        }

        // On every machine, this player's own character goes to bed.
        internal static void MoveToBed()
        {
            var me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null) return;
            Vector3 spot = WakeSpot(me);
            me.Teleport(spot, true);
            Vector3 toDoor = Doorway - spot;
            me.Rotation = new Vector2(Mathf.Atan2(toDoor.x, toDoor.z) * Mathf.Rad2Deg, 0f);
        }

        internal static Vector3 WakeSpot(Player player)
        {
            int seat = 0;
            foreach (char c in player.ProfileId) seat = (seat * 31 + c) & 0xFFFF;
            Vector3 wanted = HideoutCopy.InRestSpace(WakeSpots[seat % WakeSpots.Length]);
            var floor = SiegeMap.Walkable;
            return floor.Count == 0 ? wanted : floor.OrderBy(p => (p - wanted).sqrMagnitude).First();
        }

        // The game hides other characters' bodies unless its culling system says they're in view, and that system has
        // nothing to go on in this scene. So they'd be helmets and vests floating in the air.
        private static void DrawEveryone()
        {
            if (Time.time < nextDrawCheck) return;
            nextDrawCheck = Time.time + 1f;
            var world = Singleton<GameWorld>.Instance;
            if (world == null || !SiegeLauncher.IsSiege(world.LocationId)) return;
            foreach (var player in world.AllAlivePlayersList)
            {
                if (player == null || player.IsYourPlayer) continue;
                // Attackers react to the lights: in the dark, the light on their gun if they've got one (and nothing if they
                // haven't); back off when the power is. Only where the bot's brain is (the host), and Fika shows the light.
                SiegeDarkness.Follow(player, lit);
                if (drawn.Contains(player)) continue;
                try
                {
                    // Not there yet the moment someone spawns, so this keeps trying until it is.
                    var culling = Traverse.Create(player).Field("botPlayerCulling").GetValue<BasePlayerCulling>();
                    if (culling == null) continue;
                    culling.SetMode(BasePlayerCulling.EMode.Visible);
                    // Other people in a co-op raid have a second culling object of Fika's, with the same problem.
                    var follower = player.GetComponent<FollowerCullingObject>();
                    if (follower != null)
                    {
                        follower.enabled = false;
                        follower.SetVisibility(true);
                    }
                    drawn.Add(player);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
            }
        }

        internal static void Update()
        {
            DrawEveryone();
            SiegeGear.Update();
            SiegeStash.Update();
            if (sealedUp && Time.time >= nextDoorCheck) { nextDoorCheck = Time.time + 1f; RefreshDoors(); }
        }

        // Hideout Extensions makes its rooms' doors again when the hideout's look changes, and the siege puts your walls in
        // just after the raid starts: a door that's gone means looking again, and the new ones stop bullets too.
        private static void RefreshDoors()
        {
            if (!doors.Any(d => d == null)) return;
            doors.Clear();
            doors.AddRange(Object.FindObjectsOfType<Door>());
            Bulletproof();
            Plugin.Log.LogInfo($"[siege] The doors changed: {doors.Count} now.");
        }

        internal static void Reset()
        {
            foreach (var charge in charges.Values) if (charge != null) Object.Destroy(charge);
            charges.Clear();
            StopCarrying();
            doors.Clear();
            drawn.Clear();
            lit.Clear();
            foreach (var block in blocks.Values) if (block != null) Object.Destroy(block.gameObject);
            blocks.Clear();
            centres.Clear();
            acrossDoors.Clear();
            blown.Clear();
            front = null;
            sealedUp = false;
            nextDrawCheck = 0f;
        }
    }

    // A bot's decisions tick separately from the rest of it. Left running, its patrol orders fight the walk on the camera.
    [HarmonyPatch(typeof(AICoreAgent<BotLogicDecision>), nameof(AICoreAgent<BotLogicDecision>.Update))]
    internal static class WaitingBrainsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(AICoreAgent<BotLogicDecision> __instance)
        {
            // Every bot's thinking in every raid comes through here: nothing to look through outside a siege, or once they're all in.
            if (SiegeDirector.Waiting.Count == 0 && SiegeDirector.Breachers.Count == 0) return true;
            foreach (var bot in SiegeDirector.Waiting) if (bot != null && bot.Brain?.Agent == __instance) return false;
            foreach (var bot in SiegeDirector.Breachers) if (bot != null && bot.Brain?.Agent == __instance) return false;
            return true;
        }
    }

    // Attackers staged at the door don't think until it goes up: they aren't told about you, and the director walks them.
    [HarmonyPatch(typeof(BotOwner), nameof(BotOwner.UpdateManual))]
    internal static class WaitingBotsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(BotOwner __instance)
        {
            if (__instance.BotState != EBotState.Active) return true;
            if (!SiegeDirector.Breachers.Contains(__instance)) return !SiegeDirector.Waiting.Contains(__instance);
            // Someone on a scripted walk just walks: legs only, until they're there.
            __instance.Mover.ManualUpdate();
            if (__instance.GetPlayer.UpdateQueue == EUpdateQueue.Update)
            {
                __instance.Mover.ManualFixedUpdate();
                __instance.Steering.ManualFixedUpdate();
            }
            return false;
        }
    }

    // The same for its legs on the physics step: left running, they'd cancel the walk on the camera.
    [HarmonyPatch(typeof(BotOwner), nameof(BotOwner.FixedUpdate))]
    internal static class WaitingBotsPhysicsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(BotOwner __instance) => !SiegeDirector.Waiting.Contains(__instance) || SiegeDirector.Breachers.Contains(__instance);
    }

    // A station counts as its own wrapper, so looking at one makes the game's interaction code call itself until it crashes.
    // The same goes for a lamp or radio (an Appliance), whose real handler only exists for the hideout, and a shooting range
    // control has no handler at all and throws. The real hideout never gets here, but a siege does.
    // There's nothing to do with any of them in a siege, apart from the generator's switch.
    [HarmonyPatch(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions), new[] { typeof(GamePlayerOwner), typeof(IInteractive) })]
    internal static class StationInteractionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GamePlayerOwner owner, IInteractive interactive, ref EFT.UI.AvailableInteractionState __result)
        {
            if (!SiegeLauncher.InSiege) return true;
            __result = null;
            if (interactive is Appliance || interactive is EFT.Hideout.ShootingRange.InteractiveShootingRange) return false;
            if (interactive is SiegeMannequinPart mannequin)
            {
                __result = SiegeMannequins.ActionsFor(mannequin, owner);
                return false;
            }
            if (!(interactive is EFT.Hideout.IHideoutAreaWrapper)) return true;
            if (interactive is Component station && station.transform.root.name != SiegeGenerator.Station)
            {
                __result = SiegeStations.ActionsFor(station, owner);
                return false;
            }
            if (interactive is Component part && part.transform.root.name == SiegeGenerator.Station && SiegeGenerator.Built)
            {
                if (SiegeGenerator.Cut)
                {
                    __result = new EFT.UI.AvailableInteractionState { Error = "They've cut the power" };
                    return false;
                }
                __result = new EFT.UI.AvailableInteractionState
                {
                    Actions = new List<EFT.UI.InteractionAction>
                    {
                        new EFT.UI.InteractionAction
                        {
                            Name = SiegeGenerator.ActionName,
                            Action = () =>
                            {
                                SiegeGenerator.Flip();
                                owner.Player.UpdateInteractionCast();
                            },
                        },
                    },
                };
            }
            return false;
        }
    }

    // A raid keeps its scene list loaded through one asset system and the real hideout loads it through another, which
    // refuses a file that's already open: so the hideout would load forever after a siege. Let go of it first.
    [HarmonyPatch(typeof(ModernLoadScenesFromPreset), nameof(ModernLoadScenesFromPreset.Load))]
    internal static class HideoutAfterSiegePatch
    {
        [HarmonyPrefix]
        private static void Prefix(ResourceKey preset)
        {
            try
            {
                if (!string.Equals(preset.path, SiegeIds.HideoutPreset, StringComparison.OrdinalIgnoreCase)) return;
                if (!(EFT.Assets.Manager is EFT.AssetsManager.AssetsManager manager)) return;
                if (manager.BundlesManager.FindBundle(preset.path) == null) return;
                manager.UnloadBundle(preset.path, false);
                Plugin.Log.LogInfo("[siege] Released the siege's scene list so the hideout can load.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't release the siege's scene list: " + e); }
        }
    }
}
