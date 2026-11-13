using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Interactive;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// Where attackers wait out of sight, and the ways in besides the front door. They wait past the cultist circle's shut
    /// gate, which blows as the front door does, so nobody waits in view at the top of the stairs to be picked off. From the
    /// second wave, if Hideout Extensions has built rooms, some of each wave come through the wall of one of them (picked
    /// at random each invasion), from a tunnel they've dug to it: the wall's blown as that wave starts. Everyone's game
    /// works these out the same way.
    /// </summary>
    internal static class SiegeEntries
    {
        internal const string TunnelId = "hideoutsiege-tunnel";
        private const float TunnelWidth = 2.6f, HoleWidth = 1.7f, HoleHeight = 2.25f, Gap = .2f;
        // As long as there's room for, shortest last; and the further from the front door the better, up to a point (a tunnel
        // to the far end of a long extension would leave them a long walk to anyone).
        private static readonly float[] TunnelLengths = { 6f, 4.5f };
        // Room to spare round it, in the ground, so it isn't dug alongside another room's wall: the attackers waiting in a
        // tunnel there were a wall's thickness from that room, and one ended up in it. As much as there's room for.
        private static readonly float[] TunnelClearances = { 1.2f, .6f };
        private const float FlankFar = 28f;
        private const float FromCorners = 1.6f, FromDoorways = 2.2f, HoldingApart = .8f;

        // The cultist room's gate: blown at the first breach, with the waiting attackers on its far side.
        internal static Door Gate;
        internal static Vector3 GateCharge;
        internal static readonly List<Vector3> GateHolding = new List<Vector3>();

        // The tunnel: its hole in a room's wall (halfway through the wall), which way it goes out, and where the plug is.
        internal static readonly List<Vector3> TunnelHolding = new List<Vector3>();
        internal static Vector3 TunnelHole, TunnelOut, TunnelInside;
        internal static bool TunnelOpen;
        private static Rect tunnelFloor;
        private static float tunnelY;
        private static bool tunnelBuilt;
        // How far in from the hole the earth bank is: they wait beyond it, out of sight of the hole.
        private static float tunnelBank = 1.5f;
        private static string tunnelRoom;
        private static GameObject plug;

        internal static bool TunnelUsable => tunnelBuilt && TunnelHolding.Count > 0;

        // Where the gate is and which way is past it, and how far the tunnel goes in: for keeping players out of both.
        private static Vector3 gateCentre, gateBeyond;
        private static float tunnelLength;

        /// <summary>In the tunnel, or in the hole into it: nowhere a player's meant to go.</summary>
        internal static bool InTunnel(Vector3 p)
        {
            if (!tunnelBuilt) return false;
            Vector3 v = p - TunnelHole;
            float d = Vector3.Dot(v, TunnelOut), side = Mathf.Abs(Vector3.Dot(v, Vector3.Cross(Vector3.up, TunnelOut)));
            return d > -Gap / 2f && d < Gap + tunnelLength + .5f && side < TunnelWidth / 2f + .3f && Mathf.Abs(p.y - tunnelY) < 2.6f;
        }

        /// <summary>Past the cultist room's gate, where they wait: the floor there doesn't hold a player everywhere.</summary>
        internal static bool PastGate(Vector3 p)
        {
            if (Gate == null || GateHolding.Count == 0) return false;
            if (Vector3.Dot(p - gateCentre, gateBeyond) < .15f) return false;
            float floor = GateHolding.Min(h => h.y);
            if (p.y < floor - 1f || p.y > floor + 2.5f) return false;
            // Round the gate and round where they wait, and no wider. "Everything on the far side of the gate" was tried: what's
            // past it lies back over the hideout, and at the foot of the stairs, 12 m off and a floor down, it turned people back.
            return Flat(p - gateCentre).sqrMagnitude < 3f * 3f || GateHolding.Any(h => Flat(h - p).sqrMagnitude < 3.5f * 3.5f);
        }
        internal static Vector3 TunnelCharge => TunnelHole + TunnelOut * (Gap / 2f + .05f) + Vector3.up * 1.1f;

        /// <summary>Whether a charge going off here is the one on the tunnel's wall.</summary>
        internal static bool IsTunnelCharge(Vector3 at) => tunnelBuilt && (at - TunnelCharge).sqrMagnitude < .5f * .5f;

        internal static void Clear()
        {
            Gate = null;
            GateHolding.Clear();
            TunnelHolding.Clear();
            if (plug != null) Object.Destroy(plug);
            plug = null;
            if (plugFace != null) Object.Destroy(plugFace);
            plugFace = null;
            TunnelOpen = false;
        }

        internal static void Reset()
        {
            Clear();
            SiegeTunnelLook.Clear();
            tunnelBuilt = false;
            tunnelRoom = null;
            tunnelBank = 1.5f;
        }

        // ---- the tunnel, dug before the floor's walked ----------------------------------------------------------

        private sealed class Room
        {
            public string Id, Name, Style;
            public float X0, Z0, X1, Z1, Floor;
        }

        // Hideout Extensions' rooms, found by name so the siege works without it.
        private static object Api(string member)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HideoutExtensions")?.GetType("HideoutExtensions.HideoutExtensionsApi", false);
            return type?.GetProperty(member)?.GetValue(null);
        }

        /// <summary>
        /// A tunnel to the wall of one of the built rooms, on a side with nothing there: which room is this invasion's roll
        /// (<see cref="PickTunnel"/>). Hideout Extensions builds it with a hole in that wall; the hole's plugged once the
        /// floor's been walked.
        /// </summary>
        internal static void BuildTunnel()
        {
            if (tunnelBuilt) return;
            try
            {
                if (!(Api("Rooms") is System.Collections.IEnumerable rooms) || !(Api("Spots") is System.Collections.IEnumerable spots)) return;
                var built = new List<Room>();
                var all = new List<(string Id, float X0, float Z0, float X1, float Z1)>();
                var doorways = new List<Vector3>();
                foreach (object spot in spots)
                {
                    JObject s = JObject.Parse(JsonConvert.SerializeObject(spot));
                    var room = s["room"];
                    if (room?["min"] == null || room["max"] == null) continue;
                    all.Add(((string)s["id"], (float)room["min"][0], (float)room["min"][1], (float)room["max"][0], (float)room["max"][1]));
                    foreach (var token in s.SelectTokens("$..position"))
                        if (token is JArray p && p.Count == 3) doorways.Add(new Vector3((float)p[0], (float)p[1], (float)p[2]));
                    foreach (var token in s.SelectTokens("room.pillarWalls[*]"))
                        foreach (string end in new[] { "from", "to" })
                            if (token[end] is JArray p && p.Count == 2) doorways.Add(new Vector3((float)p[0], 0f, (float)p[1]));
                }
                foreach (object r in rooms)
                {
                    object spot = r.GetType().GetProperty("Spot")?.GetValue(r);
                    if (spot == null) continue;
                    JObject s = JObject.Parse(JsonConvert.SerializeObject(spot));
                    string style = (string)s["room"]?["style"] ?? "hideout";
                    if (style == "existing" || (string)s["id"] == TunnelId) continue;
                    built.Add(new Room
                    {
                        Id = (string)s["id"], Name = (string)s["name"], Style = style, Floor = (float?)s["room"]["floor"] ?? 0f,
                        X0 = (float)s["room"]["min"][0], Z0 = (float)s["room"]["min"][1], X1 = (float)s["room"]["max"][0], Z1 = (float)s["room"]["max"][1],
                    });
                }
                if (built.Count == 0) return;

                int mask = EFTHardSettings.Instance.MOVEMENT_MASK | (int)LayersMaskController.HighPolyCollider;
                (Room room, Vector3 hole, Vector3 outward, Rect floor, float score)? best = null;
                // Every room's places for a tunnel, at the longest a tunnel there can be.
                var byRoom = new Dictionary<string, List<(Room room, Vector3 hole, Vector3 outward, Rect floor, float score)>>();
                var turnedDown = new Dictionary<string, int>();
                var inTheWay = new Dictionary<string, int>();
                var overlaps = new Collider[8];
                void No(string why) => turnedDown[why] = turnedDown.TryGetValue(why, out int n) ? n + 1 : 1;
                foreach (var room in built.OrderBy(r => r.Id))
                {
                    // A room the attackers can't walk to from the rest (its only way in a passage they can't use): no tunnel.
                    if (!SiegeMap.Reaches(Rect.MinMaxRect(room.X0 + .3f, room.Z0 + .3f, room.X1 - .3f, room.Z1 - .3f), room.Floor)) { No("a room they can't walk to"); continue; }
                    foreach (var (length, clearance) in TunnelClearances.SelectMany(c => TunnelLengths.Select(l => (l, c))))
                    {
                        var here = new List<(Room room, Vector3 hole, Vector3 outward, Rect floor, float score)>();
                        foreach (var (start, along, wallLength, outward) in Walls(room))
                            for (float t = FromCorners; t <= wallLength - FromCorners; t += 1f)
                            {
                                Vector3 face = start + along * t;
                                Vector3 hole = face + outward * (Gap / 2f);
                                if (doorways.Any(d => Flat(d - hole).sqrMagnitude < FromDoorways * FromDoorways)) { No("by a doorway"); continue; }
                                Vector3 near = face + outward * Gap, far = near + outward * length;
                                Vector3 side = along * (TunnelWidth / 2f);
                                Vector3 a = near - side, b = far + side;
                                var rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.z, b.z), Mathf.Max(a.x, b.x), Mathf.Max(a.z, b.z));
                                // With its clearance, at the sides and the far end (the near end's the room's own wall).
                                Vector3 wide = along * (TunnelWidth / 2f + clearance), ca = near - wide, cb = far + outward * clearance + wide;
                                var room2 = Rect.MinMaxRect(Mathf.Min(ca.x, cb.x), Mathf.Min(ca.z, cb.z), Mathf.Max(ca.x, cb.x), Mathf.Max(ca.z, cb.z));
                                // Other rooms, built or not (one might be later), but not this one: the tunnel starts at its wall.
                                if (all.Any(o => o.Id != room.Id && room2.xMin < o.X1 + .3f && room2.xMax > o.X0 - .3f && room2.yMin < o.Z1 + .3f && room2.yMax > o.Z0 - .3f)) { No("another room"); continue; }
                                Vector3 centre = new Vector3(room2.center.x, room.Floor + 1.4f, room2.center.y);
                                int solid = Physics.OverlapBoxNonAlloc(centre, new Vector3(room2.width / 2f + .05f, 1.35f, room2.height / 2f + .05f), overlaps, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
                                if (solid > 0)
                                {
                                    No("the hideout's model");
                                    string name = overlaps[0].name;
                                    inTheWay[name] = inTheWay.TryGetValue(name, out int n) ? n + 1 : 1;
                                    continue;
                                }
                                float distance = Flat(hole - SiegeBreach.Doorway).magnitude;
                                float score = Mathf.Min(distance, FlankFar) - distance * .01f;
                                here.Add((room, hole, outward, rect, score));
                            }
                        if (here.Count == 0) continue;
                        byRoom[room.Id] = here;
                        break;
                    }
                }
                string tally = string.Join(", ", turnedDown.Select(r => $"{r.Value} {r.Key}"));
                if (inTheWay.Count > 0) tally += " (" + string.Join(", ", inTheWay.OrderByDescending(r => r.Value).Take(4).Select(r => $"{r.Key} x{r.Value}")) + ")";
                if (byRoom.Count == 0)
                {
                    Plugin.Log.LogInfo($"[siege] No room has a wall with space behind it for a tunnel ({tally}): the second way in is the Emergency Wall.");
                    return;
                }
                best = PickTunnel(byRoom);
                Plugin.Log.LogInfo($"[siege] Tunnel spots turned down: {tally}. {byRoom.Count} of {built.Count} rooms could take one.");
                var (target, holeAt, way, floorRect, _) = best.Value;
                // The room's floor where it is, not where its plan says: a tunnel a step above or below it isn't walked into.
                float y = target.Floor;
                if (Physics.Raycast(new Vector3(holeAt.x, target.Floor + 1.5f, holeAt.z) - way * 1f, Vector3.down, out var ground, 3f, EFTHardSettings.Instance.MOVEMENT_MASK, QueryTriggerInteraction.Ignore) && Mathf.Abs(ground.point.y - y) < .8f)
                {
                    if (Mathf.Abs(ground.point.y - y) > .02f) Plugin.Log.LogInfo($"[siege] The {target.Name}'s floor is at {ground.point.y:F2} there, not {y:F2}: the tunnel's dug level with it.");
                    y = ground.point.y;
                }
                Vector3 into = -way;
                var json = new JObject
                {
                    ["id"] = TunnelId,
                    ["name"] = "Siege tunnel",
                    ["description"] = "Dug to the hideout's wall by the attackers.",
                    ["room"] = new JObject
                    {
                        ["min"] = new JArray(floorRect.xMin, floorRect.yMin), ["max"] = new JArray(floorRect.xMax, floorRect.yMax),
                        ["floor"] = y, ["height"] = 2.6f, ["lamps"] = 0, ["style"] = "basement",
                    },
                    ["opening"] = new JObject
                    {
                        ["from"] = target.Id,
                        ["position"] = new JArray(holeAt.x, y, holeAt.z),
                        ["facing"] = Mathf.Atan2(into.x, into.z) * Mathf.Rad2Deg,
                        ["width"] = HoleWidth, ["height"] = HoleHeight, ["open"] = true,
                    },
                };
                var add = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HideoutExtensions")
                    ?.GetType("HideoutExtensions.HideoutExtensionsApi", false)?.GetMethod("AddRaidRoom");
                if (add == null) { Plugin.Log.LogInfo("[siege] This Hideout Extensions can't add a tunnel (it needs 1.3 or later)."); return; }
                string refused = add.Invoke(null, new object[] { json.ToString(Formatting.None) }) as string;
                if (refused != null) { Plugin.Log.LogWarning("[siege] The tunnel couldn't go in: " + refused); return; }
                tunnelBuilt = true;
                tunnelRoom = target.Name;
                tunnelFloor = floorRect;
                tunnelLength = Mathf.Abs(way.x) > .5f ? floorRect.width : floorRect.height;
                tunnelY = y;
                TunnelHole = new Vector3(holeAt.x, y, holeAt.z);
                TunnelOut = way;
                TunnelInside = TunnelHole - way * 2f;
                Physics.SyncTransforms();
                try { tunnelBank = SiegeTunnelLook.Dress(TunnelHole, TunnelOut, tunnelFloor, tunnelY, Gap, 2.6f); }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] The tunnel's insides: " + e.Message); }
                Plugin.Log.LogInfo($"[siege] A tunnel to the {target.Name}'s wall at {TunnelHole:F1}, going {way:F1}.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't dig the tunnel: " + e); }
        }

        // Which room they tunnel to: each invasion, one of the rooms you've built at random, from this invasion's roll (the
        // host's, so it's the same on every game). Rooms well away from the front door first, so it's a second way in and not
        // round the corner from the first; within the room, any of its better places. Without a roll (an older host), the
        // place furthest from the front door, as before.
        private const float MinFlank = 10f;

        private static (Room room, Vector3 hole, Vector3 outward, Rect floor, float score) PickTunnel(Dictionary<string, List<(Room room, Vector3 hole, Vector3 outward, Rect floor, float score)>> byRoom)
        {
            int? seed = HideoutCopy.Seed;
            if (seed == null) return byRoom.Values.SelectMany(v => v).OrderByDescending(c => c.score).First();
            var dice = new System.Random(seed.Value);
            var rooms = byRoom.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var flanking = rooms.Where(id => byRoom[id].Max(c => c.score) >= MinFlank).ToList();
            var from = flanking.Count > 0 ? flanking : rooms;
            string chosen = from[dice.Next(from.Count)];
            var spots = byRoom[chosen].OrderByDescending(c => c.score).ToList();
            float worst = spots[spots.Count - 1].score, cut = worst + (spots[0].score - worst) * .5f;
            var good = spots.Where(c => c.score >= cut).ToList();
            var pick = good[dice.Next(good.Count)];
            Plugin.Log.LogInfo($"[siege] This invasion's tunnel goes to the {pick.room.Name} (roll {seed.Value}: one of {from.Count} rooms it could go to, {good.Count} places on its walls).");
            return pick;
        }

        // A room's four walls: where the inside face starts, which way it runs, how long it is, and which way is out.
        private static IEnumerable<(Vector3 Start, Vector3 Along, float Length, Vector3 Out)> Walls(Room r)
        {
            yield return (new Vector3(r.X0, r.Floor, r.Z0), Vector3.right, r.X1 - r.X0, Vector3.back);
            yield return (new Vector3(r.X0, r.Floor, r.Z1), Vector3.right, r.X1 - r.X0, Vector3.forward);
            yield return (new Vector3(r.X0, r.Floor, r.Z0), Vector3.forward, r.Z1 - r.Z0, Vector3.left);
            yield return (new Vector3(r.X1, r.Floor, r.Z0), Vector3.forward, r.Z1 - r.Z0, Vector3.right);
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // ---- once the floor's been walked ---------------------------------------------------------------------

        internal static void Prepare()
        {
            try { FindGate(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't work out the cultist gate: " + e); }
            try { if (tunnelBuilt) Plug(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't wall the tunnel up: " + e); }
        }

        // The cultist room has two doors: the one off the stairs and the one further in. They wait past the far one, shut,
        // or if there's no floor past it, in the room behind the near one, shut for them.
        private static void FindGate()
        {
            Vector3 landing = SiegeBreach.Doorway + SiegeBreach.Outward * 5.5f + Vector3.up * 1.43f;
            var doors = Object.FindObjectsOfType<Door>()
                .Where(d => d.gameObject.activeInHierarchy && Path(d.transform).Contains("27_Cultist_Room"))
                .OrderBy(d => (SiegeBreach.CentreOf(d) - landing).sqrMagnitude).ToList();
            if (doors.Count == 0) { Plugin.Log.LogInfo("[siege] No cultist room doors: attackers wait up the stairs."); return; }
            Door near = doors[0], far = doors.Count > 1 ? doors[doors.Count - 1] : null;
            Vector3 nearCentre = SiegeBreach.CentreOf(near);
            if (far != null)
            {
                Vector3 farCentre = SiegeBreach.CentreOf(far);
                Vector3 beyond = SiegeBreach.ThroughDoor(far);
                if (Vector3.Dot(farCentre - nearCentre, beyond) < 0f) beyond = -beyond;
                var spots = Spots(SiegeMap.Walkable.Where(p => Vector3.Dot(p - farCentre, beyond) > .8f && Mathf.Abs(p.y - farCentre.y) < 2.5f && (p - farCentre).sqrMagnitude < 12f * 12f), farCentre);
                if (spots.Count >= 3)
                {
                    Use(far, farCentre, beyond, spots);
                    return;
                }
                Plugin.Log.LogInfo($"[siege] No room past the cultist room's far door ({spots.Count} spots).");
            }
            Vector3 inward = SiegeBreach.ThroughDoor(near);
            if (Vector3.Dot(nearCentre - landing, inward) < 0f) inward = -inward;
            var room = Spots(SiegeMap.Walkable.Where(p => Vector3.Dot(p - nearCentre, inward) > .8f && Mathf.Abs(p.y - nearCentre.y) < 2.5f && (p - nearCentre).sqrMagnitude < 8f * 8f), nearCentre);
            if (room.Count < 3) { Plugin.Log.LogInfo("[siege] No floor in the cultist room either: attackers wait up the stairs."); return; }
            Use(near, nearCentre, inward, room);
        }

        private static void Use(Door door, Vector3 centre, Vector3 beyond, List<Vector3> spots)
        {
            Gate = door;
            GateHolding.AddRange(spots);
            gateCentre = centre;
            gateBeyond = Flat(beyond).normalized;
            GateCharge = SiegeBreach.ChargeSpot(centre, centre + beyond);
            if (door.DoorState == EDoorState.Open)
            {
                door.DoorState = EDoorState.Shut;
                door.CurrentAngle = door.GetAngle(EDoorState.Shut);
            }
            Plugin.Log.LogInfo($"[siege] Attackers wait past the cultist room's {door.transform.parent?.name} at {centre:F1}: {spots.Count} places.");
        }

        // Places for people to stand, apart from each other, nearest the door first.
        // Not within <paramref name="clear"/> of where it's measured from (a door, which they'd be in the way of).
        private static List<Vector3> Spots(IEnumerable<Vector3> floor, Vector3 from, float clear = 1.2f)
        {
            var spots = new List<Vector3>();
            foreach (Vector3 p in floor.OrderBy(p => (p - from).sqrMagnitude))
            {
                if (spots.Count >= 20) break;
                if ((p - from).sqrMagnitude < clear * clear) continue;
                if (spots.All(s => (s - p).sqrMagnitude > HoldingApart * HoldingApart)) spots.Add(p);
            }
            return spots;
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            return string.Join("/", parts);
        }

        // The hole, filled with a block of the room's wall: solid to walk into and to shoot, and to the attackers' routes.
        private static void Plug()
        {
            TunnelHolding.AddRange(Spots(SiegeMap.Walkable.Where(p => tunnelFloor.Contains(new Vector2(p.x, p.z)) && Mathf.Abs(p.y - tunnelY) < .5f
                && Vector3.Dot(p - TunnelHole, TunnelOut) > tunnelBank + .7f), TunnelHole + TunnelOut * (tunnelBank + 1f), 0f));
            if (TunnelHolding.Count == 0)
            {
                // Why not, for the log: whether the floor was walked in the tunnel at all, and at what height.
                var inside = SiegeMap.Walkable.Where(p => tunnelFloor.Contains(new Vector2(p.x, p.z))).ToList();
                int roomSide = SiegeMap.Walkable.Count(p => (Flat(p - TunnelInside)).sqrMagnitude < 2f * 2f);
                Plugin.Log.LogWarning($"[siege] Nowhere to wait in the tunnel: {inside.Count} walked cells in it" + (inside.Count > 0 ? $" at {inside.Min(p => p.y):F2} to {inside.Max(p => p.y):F2} (tunnel floor {tunnelY:F2}, bank {tunnelBank:F1} m in)" : "") + $", {roomSide} in the room by the hole.");
            }
            Material wall = WallMaterial();
            plug = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plug.name = "Hideout Siege tunnel plug";
            plug.layer = LayerMask.NameToLayer("LowPolyCollider");
            plug.transform.SetPositionAndRotation(TunnelHole + Vector3.up * (HoleHeight / 2f), Quaternion.LookRotation(TunnelOut));
            plug.transform.localScale = new Vector3(HoleWidth + .1f, HoleHeight + .05f, Gap + .08f);
            var renderer = plug.GetComponent<MeshRenderer>();
            // What you see is the wall carrying on over the hole, not the block: the block's own faces map the wall's
            // texture their own way, so its paint band sat at the wrong height and its edges stood proud of the wall.
            if (PlugFace(out string how)) renderer.enabled = false;
            else
            {
                if (wall != null) renderer.sharedMaterial = new Material(wall) { name = "Hideout Siege tunnel plug", mainTextureScale = new Vector2(.6f, .75f) };
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            Plugin.Log.LogInfo("[siege] The tunnel's hole is covered: " + how + ".");
            var bullets = SiegeDefences.BulletBox(plug.transform, Vector3.zero, Vector3.one, EFT.Ballistics.MaterialType.Concrete);
            var block = plug.AddComponent<NavMeshObstacle>();
            block.shape = NavMeshObstacleShape.Box;
            block.size = Vector3.one;
            block.carving = true;
            Plugin.Log.LogInfo($"[siege] The tunnel's walled up behind the {tunnelRoom}'s wall: {TunnelHolding.Count} places to wait in it{(bullets ? "" : ", and bullets go through it")}.");
        }

        private static GameObject plugFace;

        /// <summary>
        /// A panel over the hole on the room's side, flush with the wall and painted as it is: the same material, with its
        /// texture laid on the way it's laid on the wall piece beside it (Hideout Extensions maps it straight from where each
        /// corner is, so a fit to that piece's corners carries on across the hole exactly).
        /// </summary>
        private static bool PlugFace(out string how)
        {
            how = "no wall beside it to match";
            var root = GameObject.Find("Hideout extensions");
            if (root == null) return false;
            Vector3 face = TunnelHole - TunnelOut * (Gap / 2f), inward = -TunnelOut, along = Vector3.Cross(Vector3.up, TunnelOut);
            MeshFilter best = null;
            float nearest = float.MaxValue;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = filter.sharedMesh;
                var r = filter.GetComponent<MeshRenderer>();
                if (mesh == null || r == null || !r.enabled || !mesh.isReadable || mesh.vertexCount != 4 || mesh.uv.Length != 4 || mesh.normals.Length != 4) continue;
                if (filter.name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (Vector3.Dot(filter.transform.TransformDirection(mesh.normals[0]).normalized, inward) < .99f) continue;
                Vector3 corner = filter.transform.TransformPoint(mesh.vertices[0]);
                if (Mathf.Abs(Vector3.Dot(corner - face, TunnelOut)) > .03f) continue;
                float d = (r.bounds.ClosestPoint(face + Vector3.up) - (face + Vector3.up)).sqrMagnitude;
                if (d < nearest) { nearest = d; best = filter; }
            }
            if (best == null || nearest > 2f * 2f) return false;

            // uv = a * (along the wall) + b * height + c, solved from three of its corners.
            var src = best.sharedMesh;
            var p = new Vector2[4];
            for (int i = 0; i < 4; i++) { Vector3 w = best.transform.TransformPoint(src.vertices[i]); p[i] = new Vector2(Vector3.Dot(w, along), w.y); }
            Vector2[] uvs = src.uv;
            int i0 = 0, i1 = 1, i2 = 2;
            float det = (p[i1].x - p[i0].x) * (p[i2].y - p[i0].y) - (p[i2].x - p[i0].x) * (p[i1].y - p[i0].y);
            if (Mathf.Abs(det) < 1e-4f) { i2 = 3; det = (p[i1].x - p[i0].x) * (p[i2].y - p[i0].y) - (p[i2].x - p[i0].x) * (p[i1].y - p[i0].y); }
            if (Mathf.Abs(det) < 1e-4f) { how = "the wall beside it is too thin to read"; return false; }
            Vector2 du1 = uvs[i1] - uvs[i0], du2 = uvs[i2] - uvs[i0];
            float ds1 = p[i1].x - p[i0].x, dy1 = p[i1].y - p[i0].y, ds2 = p[i2].x - p[i0].x, dy2 = p[i2].y - p[i0].y;
            Vector2 a = (du1 * dy2 - du2 * dy1) / det, b = (du2 * ds1 - du1 * ds2) / det;
            Vector2 c = uvs[i0] - a * p[i0].x - b * p[i0].y;
            Vector2 Uv(Vector3 w) => a * Vector3.Dot(w, along) + b * w.y + c;

            // A centimetre over the wall all round, so no crack shows where they meet (they match, so the overlap doesn't).
            float half = HoleWidth / 2f + .01f, bottom = TunnelHole.y, top = TunnelHole.y + HoleHeight + .01f;
            Vector3 at = face + inward * .001f;
            var corners = new[]
            {
                new Vector3(at.x, bottom, at.z) - along * half, new Vector3(at.x, bottom, at.z) + along * half,
                new Vector3(at.x, top, at.z) + along * half, new Vector3(at.x, top, at.z) - along * half,
            };
            var quad = new Mesh { name = "Hideout Siege tunnel plug" };
            quad.vertices = corners;
            quad.uv = corners.Select(Uv).ToArray();
            Color tint = src.colors != null && src.colors.Length == 4 ? src.colors[0] : Color.white;
            quad.colors = new[] { tint, tint, tint, tint };
            quad.normals = new[] { inward, inward, inward, inward };
            bool flip = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]), inward) < 0f;
            quad.triangles = flip ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateTangents();
            quad.RecalculateBounds();
            plugFace = new GameObject("Hideout Siege tunnel plug face");
            plugFace.AddComponent<MeshFilter>().sharedMesh = quad;
            var look = plugFace.AddComponent<MeshRenderer>();
            look.sharedMaterial = best.GetComponent<MeshRenderer>().sharedMaterial;
            look.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            how = $"the {best.name.Replace("Extension ", "")} carried on over it";
            return true;
        }

        /// <summary>What the built rooms' walls are made of, for the bits a blast throws off one.</summary>
        internal static Material WallLook => WallMaterial();

        private static Material WallMaterial()
        {
            var root = GameObject.Find("Hideout extensions");
            if (root == null) return null;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) < 0 || r.sharedMaterial == null) continue;
                if ((r.bounds.ClosestPoint(TunnelHole + Vector3.up) - (TunnelHole + Vector3.up)).sqrMagnitude < 1f) return r.sharedMaterial;
            }
            return root.GetComponentsInChildren<MeshRenderer>().FirstOrDefault(r => r.name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0)?.sharedMaterial;
        }

        /// <summary>A blast went off here: if it was the tunnel's charge, the plug's gone.</summary>
        internal static void Blasted(Vector3 at)
        {
            if (plug == null || (at - TunnelCharge).sqrMagnitude > 2f * 2f) return;
            Object.Destroy(plug);
            plug = null;
            if (plugFace != null) Object.Destroy(plugFace);
            plugFace = null;
            TunnelOpen = true;
            Rubble();
            try { SiegeTunnelLook.Blown(TunnelHole, TunnelOut, Gap, HoleWidth, HoleHeight, WallMaterial()); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] The hole's edges: " + e.Message); }
            Plugin.Log.LogInfo("[siege] The tunnel's wall is down.");
        }

        // Broken concrete across the floor where the wall was.
        private static void Rubble()
        {
            var random = new System.Random(7);
            Material wall = WallMaterial();
            // Lumps rather than blocks: big ones against the foot of the hole, smaller ones thrown further into the room, and
            // some back down the tunnel's mouth.
            for (int i = 0; i < 26; i++)
            {
                var chunk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                chunk.name = "Hideout Siege rubble";
                Object.Destroy(chunk.GetComponent<Collider>());
                chunk.GetComponent<MeshFilter>().sharedMesh = SiegeBlastLook.Rock(i);
                float far = (float)random.NextDouble();
                float s = Mathf.Lerp(.34f, .08f, far) + (float)random.NextDouble() * .1f;
                Vector3 across = Vector3.Cross(Vector3.up, TunnelOut);
                float thrown = i % 5 == 4 ? -(.2f + (float)random.NextDouble() * .7f) : .15f + far * far * 3.2f;
                Vector3 at = TunnelHole - TunnelOut * thrown + across * (((float)random.NextDouble() - .5f) * (1.5f + far * 1.6f));
                chunk.transform.SetPositionAndRotation(at + Vector3.up * s * .3f, Quaternion.Euler((float)random.NextDouble() * 40f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 40f));
                chunk.transform.localScale = new Vector3(s * 1.4f, s * .7f, s);
                if (wall != null) chunk.GetComponent<MeshRenderer>().sharedMaterial = wall;
            }
        }
    }
}
