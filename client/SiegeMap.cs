using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Game.Spawning;
using EFT.Hideout;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>Gives the hideout scene what a raid map has and it lacks: a walkable mesh, AI data, and a zone bots belong to.</summary>
    internal static class SiegeMap
    {
        // The bunker, and the rooms Hideout Extensions can build onto it (they reach 99 m north, down the range).
        private static readonly Bounds Area = new Bounds(new Vector3(5, 3, 35), new Vector3(130, 22, 170));
        internal static BotZone Zone;
        internal static readonly List<SpawnPointMarker> Entrances = new List<SpawnPointMarker>();
        internal static readonly List<Vector3> EntrancePoints = new List<Vector3>();
        internal static AICorePoint Core;
        private static readonly List<GameObject> Built = new List<GameObject>();
        private static NavMeshDataInstance navInstance;

        internal static void Prepare(ref BotZone[] botZones)
        {
            Clear();
            HideoutCopy.InstallLoaded();
            HideoutCopy.Apply();
            SiegeSandbags.ArmDecor();
            SiegeSteel.Arm();
            SiegeBarbedWire.Arm();
            OpenTheRange();
            SiegeEntries.BuildTunnel();
            BuildNavMesh();
            Zone = BuildZone();
            botZones = new[] { Zone };
            BuildAiData();
            SiegeEntries.Prepare();
            Plugin.Log.LogInfo($"[map] Ready: {Entrances.Count} ways in.");
        }

        // On other players' machines: no bots here, but the cinematic uses the same floor and the same way in.
        internal static void PrepareToWatch(bool hostKnown)
        {
            Clear();
            HideoutCopy.Apply(hostKnown);
            SiegeSandbags.ArmDecor();
            SiegeSteel.Arm();
            SiegeBarbedWire.Arm();
            OpenTheRange();
            if (HideoutCopy.CopiesBuilt) SiegeEntries.BuildTunnel();
            BuildNavMesh();
            EntrancePoints.AddRange(FindEntrances());
            SiegeEntries.Prepare();
        }

        // A built shooting range has a turnstile across its way in: a bar at chest height, which nobody's floor goes under.
        // With it there the range is cut off from the hideout, and so is Hideout Extensions' range extension beyond it, so
        // no tunnel could come out there. In a siege the bar doesn't stop anyone, attackers or you.
        private static void OpenTheRange()
        {
            int opened = 0;
            foreach (var c in Object.FindObjectsOfType<Collider>())
            {
                if (!c.enabled || c.isTrigger || c.name.IndexOf("turnstile", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (PathOf(c.transform).IndexOf("shooting_range", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // What you walk into, not what stops a bullet.
                if (((1 << c.gameObject.layer) & EFTHardSettings.Instance.MOVEMENT_MASK) == 0) continue;
                c.enabled = false;
                opened++;
            }
            if (opened == 0) return;
            Physics.SyncTransforms();
            Plugin.Log.LogInfo($"[map] The shooting range's turnstile doesn't stop anyone in a siege ({opened} collider(s)).");
        }

        internal static void Clear()
        {
            if (navInstance.valid) navInstance.Remove();
            foreach (var go in Built) if (go != null) Object.Destroy(go);
            Built.Clear();
            Entrances.Clear();
            EntrancePoints.Clear();
            Walkable.Clear();
            preWalk = null;
            SiegeEntries.Clear();
            Zone = null;
            Core = null;
        }

        private static T Make<T>(string name, Transform parent = null) where T : Component
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            else Built.Add(go);
            return go.AddComponent<T>();
        }

        private const float Cell = .2f, Radius = .22f, Step = .34f;
        internal static readonly List<Vector3> Walkable = new List<Vector3>();

        // The hideout's walls have no colliders Unity can bake from, so the walkable floor is found by physically
        // walking outward from the player's spawn: every step checks the floor, the headroom, and that no wall is in the way.
        // Null if there's no floor to start from.
        private static Dictionary<(int, int, int), Vector3> WalkFloor()
        {
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            // The player's spawn, unless something's been moved on to it: then just inside the front door, then by the beds.
            Vector3 start = default;
            var starts = new[] { new Vector3(-5.8f, .1f, -5.4f), SiegeBreach.Doorway - SiegeBreach.Outward * 1.5f + Vector3.up * .1f, HideoutCopy.InRestSpace(new Vector3(14.4f, .1f, -3.4f)) };
            if (!starts.Any(s => Floor(s, 1.5f, mask, out start) && Mathf.Abs(start.y) < .3f)) { Plugin.Log.LogError("[map] No floor at the player's spawn, the front door or the beds."); return null; }
            return WalkFrom(start);
        }

        private static Dictionary<(int, int, int), Vector3> WalkFrom(Vector3 start)
        {
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            var cells = new Dictionary<(int, int, int), Vector3>();
            var queue = new Queue<Vector3>();
            cells[Key(start)] = start;
            queue.Enqueue(start);
            var directions = new[] { new Vector3(Cell, 0, 0), new Vector3(-Cell, 0, 0), new Vector3(0, 0, Cell), new Vector3(0, 0, -Cell) };
            // With the doors and your defences out of the way: attackers need the floor on both sides of them.
            SiegeBreach.WithDoorsOpen(() => SiegeDecor.WithDefencesOut(() =>
            {
                while (queue.Count > 0 && cells.Count < 200000)
                {
                    Vector3 from = queue.Dequeue();
                    foreach (var dir in directions)
                    {
                        Vector3 guess = from + dir;
                        if (!Area.Contains(guess)) continue;
                        if (!Floor(guess, Step, mask, out Vector3 to)) continue;
                        var key = Key(to);
                        if (cells.ContainsKey(key) || !CanStep(from, to, mask)) continue;
                        cells[key] = to;
                        queue.Enqueue(to);
                    }
                }
            }));
            return cells;
        }

        private static List<Vector3> preWalk;

        /// <summary>
        /// For the tunnel, dug before the floor's walked for good: whether the floor as it stands reaches into this
        /// rectangle (a room attackers couldn't walk to is no use tunnelling to). Walked once, the first time it's asked.
        /// </summary>
        internal static bool Reaches(Rect area, float y)
        {
            if (preWalk == null) preWalk = WalkFloor()?.Values.ToList() ?? new List<Vector3>();
            return preWalk.Any(p => area.Contains(new Vector2(p.x, p.z)) && Mathf.Abs(p.y - y) < 1f);
        }

        /// <summary>
        /// For the log, when the floor doesn't reach a room: the floor walked from inside that room instead, where it comes
        /// nearest the rest, and what stands between the two at each height.
        /// </summary>
        internal static string WhyNot(Rect area, float y, string name)
        {
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            Vector3 start = default;
            bool found = false;
            for (int i = 0; i < 25 && !found; i++)
            {
                var p = new Vector3(Mathf.Lerp(area.xMin, area.xMax, (i % 5 + .5f) / 5f), y + .1f, Mathf.Lerp(area.yMin, area.yMax, (i / 5 + .5f) / 5f));
                found = Floor(p, 1.5f, mask, out start);
            }
            if (!found) return $"[map] The {name}: no floor to stand on anywhere in it.";
            var island = WalkFrom(start).Values.ToList();
            var main = preWalk ?? new List<Vector3>();
            if (main.Count == 0) return $"[map] The {name}: the hideout's own floor wasn't walked.";
            var box = new Bounds(main[0], Vector3.zero);
            foreach (var p in main) box.Encapsulate(p);
            box.Expand(8f);
            var near = island.Where(box.Contains).ToList();
            var text = new System.Text.StringBuilder($"[map] The {name} can't be walked to. Walked from inside it: {island.Count * Cell * Cell:F0} m2, x {island.Min(p => p.x):F1} to {island.Max(p => p.x):F1}, z {island.Min(p => p.z):F1} to {island.Max(p => p.z):F1}, y {island.Min(p => p.y):F2} to {island.Max(p => p.y):F2}.");
            if (near.Count == 0) return text.Append(" None of it comes within 4 m of the hideout's floor.").ToString();
            Vector3 a = default, b = default;
            float best = float.MaxValue;
            foreach (var q in near)
                foreach (var p in main)
                {
                    float d = (p - q).sqrMagnitude;
                    if (d < best) { best = d; a = p; b = q; }
                }
            text.Append($" Nearest: the hideout's floor at {a:F2}, the room's at {b:F2}, {Mathf.Sqrt(best):F2} m apart.");
            // The widest stretch where the two run alongside each other: how broad whatever's between them is.
            var facing = near.Where(q => main.Any(p => (p - q).sqrMagnitude < best + .5f)).ToList();
            text.Append($" {facing.Count} of the room's cells are that close, x {facing.Min(p => p.x):F1} to {facing.Max(p => p.x):F1}, z {facing.Min(p => p.z):F1} to {facing.Max(p => p.z):F1}.");
            SiegeBreach.WithDoorsOpen(() => SiegeDecor.WithDefencesOut(() =>
            {
                Vector3 across = b - a;
                float length = across.magnitude;
                foreach (float h in new[] { .15f, .35f, .55f, .8f, 1f, 1.2f, 1.4f, 1.7f, 2f })
                {
                    Vector3 from = a + Vector3.up * h;
                    if (!Physics.Raycast(from, across / length, out var hit, length, mask, QueryTriggerInteraction.Ignore)) { text.Append($"\n   at {h:F2} m: clear"); continue; }
                    var c = hit.collider;
                    text.Append($"\n   at {h:F2} m: {PathOf(c.transform)} ({LayerMask.LayerToName(c.gameObject.layer)}, {c.GetType().Name}), {hit.distance:F2} m along, from {c.bounds.min:F2} to {c.bounds.max:F2}");
                }
                // Straight down between them: a drop, or the top of what's in the way.
                Vector3 middle = (a + b) / 2f;
                text.Append(Physics.Raycast(middle + Vector3.up * 2.2f, Vector3.down, out var top, 5f, mask, QueryTriggerInteraction.Ignore)
                    ? $"\n   halfway, looking down from 2.2 m: {PathOf(top.collider.transform)} at y {top.point.y:F2} (the hideout's floor there is {a.y:F2}, the room's {b.y:F2})"
                    : "\n   halfway, looking down from 2.2 m: nothing for 5 m");
            }));
            return text.ToString();
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; t = t.parent, i++) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        // The walked floor, made into the attackers' NavMesh.
        private static void BuildNavMesh()
        {
            var cells = WalkFloor();
            if (cells == null) return;
            var verts = new List<Vector3>(cells.Count * 4);
            var tris = new List<int>(cells.Count * 6);
            float h = Cell * .5f;
            foreach (var c in cells.Values)
            {
                int n = verts.Count;
                verts.Add(c + new Vector3(-h, 0, -h));
                verts.Add(c + new Vector3(-h, 0, h));
                verts.Add(c + new Vector3(h, 0, h));
                verts.Add(c + new Vector3(h, 0, -h));
                tris.AddRange(new[] { n, n + 1, n + 2, n, n + 2, n + 3 });
            }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            var settings = NavMesh.GetSettingsByIndex(0);
            settings.agentRadius = .18f;
            settings.agentHeight = 1.7f;
            settings.agentClimb = Step + .05f;
            settings.agentSlope = 50f;
            settings.overrideVoxelSize = true;
            settings.voxelSize = .06f;
            settings.overrideTileSize = true;
            settings.tileSize = 128;
            settings.minRegionArea = .5f;
            var sources = new List<NavMeshBuildSource> { new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Mesh, sourceObject = mesh, transform = Matrix4x4.identity, area = 0 } };
            var data = NavMeshBuilder.BuildNavMeshData(settings, sources, Area, Vector3.zero, Quaternion.identity);
            if (data != null) navInstance = NavMesh.AddNavMeshData(data);

            Walkable.Clear();
            Walkable.AddRange(cells.Values);
            var tri = NavMesh.CalculateTriangulation();
            Plugin.Log.LogInfo($"[map] Walked {cells.Count} floor cells ({cells.Count * Cell * Cell:F0} m2). NavMesh verts={tri.vertices.Length} tris={tri.indices.Length / 3}");
        }

        private static (int, int, int) Key(Vector3 p) => (Mathf.RoundToInt(p.x / Cell), Mathf.RoundToInt(p.y / .5f), Mathf.RoundToInt(p.z / Cell));

        private static bool Floor(Vector3 near, float reach, int mask, out Vector3 floor)
        {
            floor = near;
            if (!Physics.Raycast(near + Vector3.up * (reach + .05f), Vector3.down, out var hit, reach * 2f + .1f, mask, QueryTriggerInteraction.Ignore)) return false;
            if (hit.normal.y < .6f) return false;
            floor = hit.point;
            // Room to stand: a body-sized capsule above the floor touches nothing.
            Vector3 low = floor + Vector3.up * (Radius + Step), high = floor + Vector3.up * 1.6f;
            return !Physics.CheckCapsule(low, high, Radius, mask, QueryTriggerInteraction.Ignore);
        }

        private static bool CanStep(Vector3 from, Vector3 to, int mask)
        {
            Vector3 delta = to - from;
            float length = delta.magnitude;
            Vector3 dir = delta / length;
            return !Physics.Raycast(from + Vector3.up * .55f, dir, length, mask, QueryTriggerInteraction.Ignore)
                && !Physics.Raycast(from + Vector3.up * 1.4f, dir, length, mask, QueryTriggerInteraction.Ignore);
        }

        internal static Vector3 PlayerSpawn;

        private static BotZone BuildZone()
        {
            var zone = Make<BotZone>("BotZoneSiege");
            zone.SpawnPointMarkers = new List<SpawnPointMarker>();
            zone.UnSpawnPoints = Array.Empty<UnspawnPoint>();
            zone.PatrolWays = Array.Empty<PatrolWay>();
            zone.Modifier = new BotLocationModifier();

            var holder = Object.FindObjectOfType<AICorePointHolder>() ?? Make<AICorePointHolder>("AICorePointHolder");
            if (holder.CorePoints == null) holder.CorePoints = new List<AICorePoint>();
            Core = Make<AICorePoint>("SiegeCore");
            Core.transform.position = Vector3.zero;
            Core.SetIds(1, 1);
            holder.AddCorePoint(Core);

            int i = 0;
            EntrancePoints.AddRange(FindEntrances());
            foreach (Vector3 p in EntrancePoints)
            {
                Entrances.Add(Marker(zone, p, "siege_entrance_" + i));
                i++;
            }
            zone.CenterOfSpawnPoints = Entrances.Count > 0 ? Entrances[0].transform.position : Vector3.zero;

            var way = Make<PatrolWay>("SiegePatrol", zone.transform);
            way.PatrolType = PatrolType.patrolling;
            way.Points = new List<PatrolPoint>();
            int id = 900000;
            foreach (Vector3 p in PatrolPositions())
            {
                var point = Make<PatrolPoint>("P" + id, way.transform);
                point.transform.position = p;
                point.Id = id++;
                point.SetCorePoint(Core);
                way.Points.Add(point);
            }
            way.InitPoints();
            zone.PatrolWays = new[] { way };
            return zone;
        }

        /// <summary>A place in the siege's zone that the game can spawn a bot at.</summary>
        internal static SpawnPointMarker Marker(BotZone zone, Vector3 p, string id)
        {
            var marker = SpawnPointMarker.Create(new SpawnPointParams
            {
                Id = id,
                Position = p,
                Rotation = 0,
                Sides = EPlayerSideMask.All,
                Categories = ESpawnCategoryMask.Bot,
                Infiltration = "siege",
                BotZoneName = zone.name,
                DelayToCanSpawnSec = 0f,
                CorePointId = 1,
                ColliderParams = new SpawnSphereParams { Center = Vector3.zero, Radius = 1f },
            }, zone.transform);
            var collider = marker.gameObject.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 1f;
            AccessTools.Field(typeof(SpawnPointMarker), "_collider")?.SetValue(marker, collider);
            marker.BotZone = zone;
            zone.SpawnPointMarkers.Add(marker);
            return marker;
        }

        // Everyone comes down the stairs behind the front door, out of sight of it.
        private static IEnumerable<Vector3> FindEntrances()
        {
            Vector3 stairs = SiegeBreach.Doorway + SiegeBreach.Outward * 4.9f + Vector3.up * 1.43f;
            var path = new NavMeshPath();
            if (NavMesh.SamplePosition(stairs, out var hit, 1f, NavMesh.AllAreas)
                && NavMesh.CalculatePath(PlayerSpawnPoint(), hit.position, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete)
            {
                Plugin.Log.LogInfo($"[map] Attackers come from the stairs at {hit.position}, {PathLength(PlayerSpawnPoint(), hit.position, path):F1} m of walking from the main room.");
                return new[] { hit.position };
            }
            // Only if a hideout's floor doesn't reach the stairs: the farthest point from the beds is the next best thing.
            Plugin.Log.LogWarning("[map] Can't reach the stairs behind the front door: attackers will come from the far end instead.");
            Vector3 farthest = Walkable.Count > 0 ? Walkable.OrderByDescending(p => (p - HideoutCopy.InRestSpace(new Vector3(15.7f, 0f, -1.6f))).sqrMagnitude).First() : PlayerSpawnPoint();
            return new[] { NavMesh.SamplePosition(farthest, out hit, 1f, NavMesh.AllAreas) ? hit.position : farthest };
        }

        private static float PathLength(Vector3 a, Vector3 b, NavMeshPath path)
        {
            if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path)) return 0;
            float d = 0;
            for (int i = 1; i < path.corners.Length; i++) d += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            return d;
        }

        private static Vector3 PlayerSpawnPoint()
        {
            if (NavMesh.SamplePosition(new Vector3(-5.8f, .1f, -5.4f), out var hit, 6f, NavMesh.AllAreas)) return hit.position;
            return new Vector3(-5.8f, .1f, -5.4f);
        }

        private static IEnumerable<Vector3> PatrolPositions()
        {
            yield return PlayerSpawnPoint();
            foreach (var e in Entrances) yield return e.transform.position;
        }

        // Fills in the voxel grid and cover points that stock bot AI looks up.
        private static void BuildAiData()
        {
            var covers = AICoversData.CreateOrFind(undestandAtGame: true);
            Built.Add(covers.gameObject);
            foreach (var c in new Component[] { covers.Voxels, covers.Patrols, covers.AIManualPointsHolder, covers.AICorePointsHolder, covers.AIMinesPositions, covers.AIDangerPlacesHolder, covers.AIPlaceInfoHolder })
                if (c != null && !Built.Contains(c.gameObject)) Built.Add(c.gameObject);
            if (covers.AIManualPointsHolder.ManualPoints == null) covers.AIManualPointsHolder.ManualPoints = new List<GroupPoint>();
            if (covers.AICorePointsHolder.CorePoints == null) covers.AICorePointsHolder.CorePoints = new List<AICorePoint>();
            var patrols = covers.Patrols;
            if (patrols.ContainerLootPoints == null) patrols.ContainerLootPoints = new List<AILootPoint>();
            if (patrols.SimpleLootPoints == null) patrols.SimpleLootPoints = new List<AILootPoint>();
            if (patrols.ExfiltrationPoints == null) patrols.ExfiltrationPoints = new List<AIExfiltrationPoint>();
            if (patrols.LootPointClusters == null) patrols.LootPointClusters = new List<AILootPointsCluster>();
            if (covers.AIPlaceInfoHolder.Places == null) covers.AIPlaceInfoHolder.Places = new List<AIPlaceInfo>();
            if (covers.Points == null) covers.Points = new List<GroupPoint>();
            covers.Points.Clear();
            if (covers.Ways == null) covers.Ways = new List<GroupPointWay>();
            if (covers.Pathes == null) covers.Pathes = new List<GroupPointPath>();

            // A cell is 10 x 5 x 10 metres, set by AICoversData.GetIndexes.
            var voxels = covers.Voxels;
            voxels.MinVoxelesValues = Area.min;
            int nx = Mathf.CeilToInt(Area.size.x / 10f), ny = Mathf.CeilToInt(Area.size.y / 5f), nz = Mathf.CeilToInt(Area.size.z / 10f);
            voxels.SetMaxValues(new Vector3(nx, ny, nz));
            voxels.VoxelsList = new List<NavGraphVoxelSimple>();
            var grid = new NavGraphVoxelSimple[nx, ny, nz];
            int voxelId = 1;
            for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz; z++)
            {
                var v = new NavGraphVoxelSimple(Area.min + new Vector3(x * 10f, y * 5f, z * 10f), x, y, z, voxelId++);
                grid[x, y, z] = v;
                voxels.VoxelsList.Add(v);
            }

            int pointId = 1;
            for (int i = 0; i < Walkable.Count; i += 11)
            {
                if (!NavMesh.SamplePosition(Walkable[i], out var hit, .5f, NavMesh.AllAreas)) continue;
                if (!NavMesh.FindClosestEdge(hit.position, out var edge, NavMesh.AllAreas) || edge.distance > .9f) continue;
                Vector3 wall = -edge.normal;
                var point = new GroupPoint(pointId++, null, hit.position, null, Core, CoverLevel.Sit, false, wall, hit.position, PointWithNeighborType.cover) { CoverType = CoverType.Wall };
                covers.Points.Add(point);
                var index = covers.GetIndexes(hit.position);
                var cell = grid[Mathf.Clamp(index.x, 0, nx - 1), Mathf.Clamp(index.y, 0, ny - 1), Mathf.Clamp(index.z, 0, nz - 1)];
                cell.HaveNavMesh = true;
                if (!cell.HaveStartPointId) cell.SetClosestsPoint(point);
                cell.PointsIds.Add(point.Id);
            }
            Plugin.Log.LogInfo($"[map] AI data: {nx}x{ny}x{nz} cells, {covers.Points.Count} cover points.");

            if (Object.FindObjectOfType<AIStationaryController>() == null) Make<AIStationaryController>("AIStationaryController");
        }
    }

    // A normal raid loads its bots on the loading screen, from the waves it has planned. A siege plans none, so every bot
    // would be fetched from the server and have its gear read from disk at the moment it's needed: mid-fight, that's a freeze.
    [HarmonyPatch(typeof(BotProfileClient), nameof(BotProfileClient.TryLoadBotsProfilesOnStart))]
    internal static class PreloadBotsPatch
    {
        // A few over, in case some come without weapons and have to be replaced.
        private const int Spares = 2;
        private static List<CountTypeBotWave> expected;

        [HarmonyPrefix]
        private static void Prefix(ref IEnumerable<CountTypeBotWave> external)
        {
            if (!SiegeLauncher.Selected) return;
            try
            {
                expected = SiegeDirector.Expected();
                var wanted = external?.ToList() ?? new List<CountTypeBotWave>();
                wanted.AddRange(expected.Select(w => new CountTypeBotWave(w.Limit, w.Role, w.Difficulty)));
                external = wanted;
                Plugin.Log.LogInfo("[map] Loading ahead: " + string.Join(", ", expected.Select(w => w.Limit + " " + w.Role + " " + w.Difficulty)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Couldn't plan the bots ahead: " + e.Message); }
        }

        [HarmonyPostfix]
        private static void Postfix(BotProfileClient __instance, ref System.Threading.Tasks.Task __result)
        {
            if (SiegeLauncher.Selected) __result = ThenTheirGear(__instance, __result);
        }

        private static async System.Threading.Tasks.Task ThenTheirGear(BotProfileClient client, System.Threading.Tasks.Task profiles)
        {
            // Your walls, floor and ceiling are read while the bots are, so they're in before the hideout's rooms are copied.
            var styles = HideoutCopy.LoadCustomizationAhead();
            await profiles;
            await styles;
            try
            {
                // The game holds some of a big order back for later. There's no later here.
                for (int i = 0; i < 5 && client._delayed != null && client._delayed.Count > 0; i++)
                    await client.LoadBots(new List<CountTypeBotWave>(client._delayed));
                int sent = client._simpleProfiles.Count;
                KeepWhatsNeeded(client);
                if (await StandInsAhead(client)) KeepWhatsNeeded(client);
                var gear = client._simpleProfiles.SelectMany(p => p.GetAllPrefabPaths(false)).Concat(SiegeNvg.Prefabs()).Distinct().ToArray();
                await Comfort.Common.Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local, gear, Diz.Jobs.JobYieldPriority.General, null, ObjectsFactory.DefaultCancellationToken);
                Plugin.Log.LogInfo($"[map] Loaded ahead: {client._simpleProfiles.Count} of the {sent} bots the server sent, {gear.Length} pieces of gear.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Couldn't load the bots' gear ahead, so it'll load as they arrive: " + e.Message); }
        }

        // A role whose bots all came without weapons (a bot mod's gear from another mod that isn't there) gets its stand-ins
        // now, rather than from the server mid-siege when the first of them turns up empty-handed.
        private static async System.Threading.Tasks.Task<bool> StandInsAhead(BotProfileClient client)
        {
            if (expected == null) return false;
            bool loaded = false;
            for (int round = 0; round < 3; round++)
            {
                var wanted = new Dictionary<(WildSpawnType, BotDifficulty), int>();
                foreach (var wave in expected)
                {
                    if (SiegeDirector.UnarmedAhead.Contains(wave.Role)) continue;
                    var theirs = client._simpleProfiles.Where(p => p.Info.Settings.Role == wave.Role && p.Info.Settings.BotDifficulty == wave.Difficulty).ToList();
                    if (theirs.Count == 0 || theirs.Any(SiegeDirector.Armed)) continue;
                    SiegeDirector.UnarmedAhead.Add(wave.Role);
                    var standIn = SiegeDirector.StandIn(wave.Role, SiegeDirector.UnarmedAhead);
                    Plugin.Log.LogWarning($"[map] {wave.Role} bots come without weapons (is a gear mod they rely on missing?): {standIn} will stand in for them.");
                    var key = (standIn, wave.Difficulty);
                    wanted.TryGetValue(key, out int count);
                    wanted[key] = count + wave.Limit + Spares;
                    client._simpleProfiles.RemoveAll(p => p.Info.Settings.Role == wave.Role);
                }
                if (wanted.Count == 0) return loaded;
                var more = wanted.Select(w => new CountTypeBotWave(w.Value, w.Key.Item1, w.Key.Item2)).ToList();
                expected.AddRange(more);
                int before = client._simpleProfiles.Count;
                await client.LoadBots(more);
                loaded = true;
                Plugin.Log.LogInfo($"[map] Loaded {client._simpleProfiles.Count - before} stand-ins ahead.");
            }
            return loaded;
        }

        // The server sends whole batches, many more than a siege can use, and all their gear would take a long time to load.
        private static void KeepWhatsNeeded(BotProfileClient client)
        {
            if (expected == null) return;
            var kept = new List<Profile>();
            foreach (var group in expected.GroupBy(w => (w.Role, w.Difficulty)))
                kept.AddRange(client._simpleProfiles.Where(p => p.Info.Settings.Role == group.Key.Role && p.Info.Settings.BotDifficulty == group.Key.Difficulty)
                    .Take(group.Sum(w => w.Limit) + Spares));
            client._simpleProfiles.Clear();
            client._simpleProfiles.AddRange(kept);
        }
    }

    // The game keeps a stock of spare bot profiles topped up from the server as a raid goes on: scavs and marksmen of every
    // difficulty, every 50 seconds, and the server answers with hundreds, all built on the main thread at once (a freeze of up
    // to most of a second, mid-fight). A siege loads all it needs on the loading screen, so it does without.
    [HarmonyPatch]
    internal static class NoBackupBotsPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(BotProfileBackuploader), nameof(BotProfileBackuploader.Update)),
            AccessTools.Method(typeof(BotProfileBackuploader), nameof(BotProfileBackuploader.CacheSimpleProfilesChecker)),
            AccessTools.Method(typeof(BotProfileBackuploader), nameof(BotProfileBackuploader.AddProfileForBackup)),
        };

        [HarmonyPrefix]
        private static bool Prefix() => !SiegeLauncher.Selected;
    }

    // The hideout has no baked audio data, and its audio object isn't set up for raids. The hideout itself never starts this.
    [HarmonyPatch(typeof(Audio.SpatialSystem.SpatialAudioSystem), nameof(Audio.SpatialSystem.SpatialAudioSystem.Initialize))]
    internal static class SpatialAudioPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref System.Threading.Tasks.Task __result)
        {
            if (!SiegeLauncher.Selected) return true;
            __result = System.Threading.Tasks.Task.CompletedTask;
            return false;
        }
    }

    // Without baked data there's nothing to occlude against, and these would throw for every sound played.
    [HarmonyPatch]
    internal static class SpatialOcclusionPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(Audio.SpatialSystem.SpatialAudioSystem)).Where(m => m.Name == "ProcessSourceOcclusion");

        [HarmonyPrefix]
        private static bool Prefix(ref int __result)
        {
            if (!SiegeLauncher.Selected) return true;
            __result = -1;
            return false;
        }
    }

    // The same for sounds made by doors.
    [HarmonyPatch(typeof(EFT.Interactive.WorldInteractiveObject), nameof(EFT.Interactive.WorldInteractiveObject.ProcessOcclusion))]
    internal static class DoorOcclusionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix() => !SiegeLauncher.Selected;
    }

    [HarmonyPatch(typeof(BotsController), nameof(BotsController.Init))]
    internal static class BotsInitPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref BotZone[] botZones)
        {
            if (!SiegeLauncher.Selected) return;
            SiegeCompat.Apply();
            try { SiegeMap.Prepare(ref botZones); }
            catch (Exception e) { Plugin.Log.LogError("[map] Building the siege map failed: " + e); }
        }
    }
}
