using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// The Defences tab in Hideout Interiors' decor panel: what you can put in your hideout to hold it with, each with a
    /// limit to how many a hideout can have, so a hideout can't be walled up solid. Hideout Interiors keeps them and puts
    /// them in the siege's copy of your hideout; here they're made to work there. Everything's found by name, so the siege
    /// runs without Hideout Interiors (and with an older one, which has no tabs of mods' own: they go under its own).
    /// </summary>
    internal static class SiegeDecor
    {
        internal const string Tab = "Defences";

        private static Type Api() => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HideoutInteriors")?.GetType("HideoutInteriors.HideoutInteriorsApi", false);

        internal static bool Installed => Api() != null;

        /// <summary>Into the Defences tab, at most <paramref name="limit"/> in a hideout. False if Hideout Interiors isn't there.</summary>
        internal static bool Offer(string key, string name, int limit, Func<GameObject> make, string olderKind)
        {
            var api = Api();
            if (api == null) return false;
            try
            {
                var withLimit = api.GetMethod("AddDecor", new[] { typeof(string), typeof(string), typeof(string), typeof(Func<GameObject>), typeof(int) });
                if (withLimit != null) withLimit.Invoke(null, new object[] { key, name, Tab, make, limit });
                else
                {
                    var plain = api.GetMethod("AddDecor", new[] { typeof(string), typeof(string), typeof(string), typeof(Func<GameObject>) });
                    if (plain == null) return false;
                    plain.Invoke(null, new object[] { key, name, olderKind, make });
                    Plugin.Log.LogInfo($"[siege] This Hideout Interiors has no Defences tab (it needs 0.2 or later): {name} is under {olderKind}, with no limit.");
                }
                Plugin.Log.LogInfo($"[siege] {name} is in Hideout Interiors' decor panel.");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[siege] Couldn't offer {name} to Hideout Interiors: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        /// <summary>
        /// The ones placed in this raid's copy of the hideout, at most <paramref name="limit"/> of them: any over that (from a
        /// layout made before there was one) are put away for the siege.
        /// </summary>
        internal static List<Transform> Placed(string key, int limit)
        {
            List<Transform> all;
            try { all = (Api()?.GetMethod("DecorOf")?.Invoke(null, new object[] { key }) as List<Transform>)?.Where(t => t != null).ToList() ?? new List<Transform>(); }
            catch (Exception) { return new List<Transform>(); }
            if (all.Count <= limit) return all;
            for (int i = limit; i < all.Count; i++) all[i].gameObject.SetActive(false);
            Plugin.Log.LogInfo($"[siege] {all.Count} {key} placed, but a hideout can only have {limit}: the rest sit this one out.");
            return all.Take(limit).ToList();
        }

        private const float FromDoors = 1.3f;

        /// <summary>
        /// Something solid standing in a doorway, or out on the stairs the attackers come down, would shut them out
        /// altogether (the floor they walk is found round what's solid). It sits the siege out, and says so: true if it does.
        /// </summary>
        internal static bool InTheWay(Transform thing, string what)
        {
            Vector3 at = thing.position;
            string why = null;
            if (Vector3.Dot(at - SiegeBreach.Doorway, SiegeBreach.Outward) > -.3f && (Flat(at - SiegeBreach.Doorway)).magnitude < 12f && Mathf.Abs(at.y - SiegeBreach.Doorway.y) < 6f)
                why = "at the front door or on the stairs";
            else
                foreach (var door in Object.FindObjectsOfType<EFT.Interactive.Door>())
                {
                    var parts = door.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToList();
                    if (parts.Count == 0) continue;
                    var box = parts.Aggregate(parts[0].bounds, (all, c) => { all.Encapsulate(c.bounds); return all; });
                    if (Flat(box.center - at).magnitude < FromDoors && Mathf.Abs(box.center.y - at.y) < 2f) { why = "in a doorway"; break; }
                }
            if (why == null) return false;
            thing.gameObject.SetActive(false);
            Plugin.Log.LogInfo($"[siege] Your {what} at {at:F1} is {why}, where it'd shut the attackers out: it sits this siege out.");
            return true;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // ---- breached when they're in the way ----------------------------------------------------------------------

        // Your solid defences in this raid (sandbags and steel), in the order they were put in.
        private static readonly List<Transform> blockers = new List<Transform>();
        // A route that stops this close to one is stopped by it.
        private const float Stops = 3.5f;

        internal static void Blocks(Transform thing)
        {
            if (thing != null && !blockers.Contains(thing)) blockers.Add(thing);
        }

        internal static void Reset() => blockers.Clear();

        /// <summary>The ones still standing.</summary>
        internal static List<Transform> Standing() => blockers.Where(b => b != null && b.gameObject.activeInHierarchy).ToList();

        /// <summary>
        /// Runs the action with your defences out of the way, so the floor behind them gets walked, as it's walked with the
        /// doors open. While they stand, they're cut out of the attackers' routes (each carves the floor it stands on), so
        /// they're walked round; one that shuts the way altogether gets blown (the director's CheckDefences).
        /// </summary>
        internal static void WithDefencesOut(Action action)
        {
            var off = Standing().SelectMany(b => b.GetComponentsInChildren<Collider>()).Where(c => c.enabled).ToList();
            foreach (var c in off) c.enabled = false;
            if (off.Count > 0) Physics.SyncTransforms();
            try { action(); }
            finally
            {
                foreach (var c in off) if (c != null) c.enabled = true;
                if (off.Count > 0) Physics.SyncTransforms();
            }
        }

        /// <summary>The defence a route that stops here was stopped by, if one was.</summary>
        internal static Transform Stopping(Vector3 end) =>
            Standing().Where(b => Flat(b.position - end).magnitude < Stops && Mathf.Abs(b.position.y - end.y) < 2f)
                .OrderBy(b => Flat(b.position - end).sqrMagnitude).FirstOrDefault();

        /// <summary>
        /// Where someone coming from <paramref name="from"/> stands to set a charge on it, and where the charge goes: on the
        /// face toward them, about knee height.
        /// </summary>
        internal static (Vector3 Stand, Vector3 Charge, Vector3 Out) ChargeOn(Transform b, Vector3 from)
        {
            Vector3 front = Flat(b.forward).normalized;
            float side = Vector3.Dot(from - b.position, front) >= 0f ? 1f : -1f;
            bool steel = IsSteel(b);
            // Sandbags are 0.4 deep about their middle; the steel's plate is at its middle, its props 0.6 behind it.
            float face = steel ? .05f : .22f, stand = steel ? (side > 0f ? .75f : 1.05f) : .85f;
            Vector3 way = front * side;
            return (b.position + way * stand, b.position + way * face + Vector3.up * (steel ? .7f : .5f), way);
        }

        /// <summary>The defence a charge going off here is on, if any: only right on its face (a door's charge is never that near one).</summary>
        internal static Transform At(Vector3 charge) =>
            Standing().Where(b => Flat(b.position - charge).magnitude < .45f && Mathf.Abs(b.position.y + .6f - charge.y) < .5f)
                .OrderBy(b => Flat(b.position - charge).sqrMagnitude).FirstOrDefault();

        /// <summary>Blown: gone, on everyone's game, with the way through it open.</summary>
        internal static void Blow(Transform b)
        {
            if (b == null) return;
            Plugin.Log.LogInfo($"[siege] Your {Kind(b)} at {b.position:F1} has been blown.");
            // Whatever was stacked on it comes down with it, not left hanging in the air.
            var above = Standing().Where(o => o != b && Flat(o.position - b.position).magnitude < .9f && o.position.y > b.position.y + .3f && o.position.y < b.position.y + 2.5f).ToList();
            b.gameObject.SetActive(false);
            foreach (var o in above) Blow(o);
            Physics.SyncTransforms();
        }

        internal static string Kind(Transform b) => IsSteel(b) ? "steel barricade" : "sandbags";

        private static bool IsSteel(Transform b) => b.GetComponentInChildren<SteelMark>(true) != null;

        /// <summary>Marks a steel barricade among the defences.</summary>
        internal sealed class SteelMark : MonoBehaviour { }

        /// <summary>The model and its look, read from the mod's models folder (see tools\defences).</summary>
        internal static (Mesh Mesh, Material Look) Model(string name, bool normalMap, float metallic, float gloss)
        {
            string folder = Path.Combine(Plugin.Folder, "models");
            var mesh = SiegeCharge.ReadMesh(Path.Combine(folder, name + ".mesh"), out _);
            mesh.name = "Hideout Siege " + name;
            var seed = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var look = new Material(seed.GetComponent<Renderer>().sharedMaterial) { name = "Hideout Siege " + name, color = Color.white };
            Object.Destroy(seed);
            look.SetTexture("_MainTex", SiegeCharge.ReadTexture(Path.Combine(folder, name + "_base.jpg"), false));
            if (normalMap)
            {
                look.SetTexture("_BumpMap", SiegeCharge.ReadTexture(Path.Combine(folder, name + "_normal.png"), true));
                look.EnableKeyword("_NORMALMAP");
            }
            string metal = Path.Combine(folder, name + "_metal.png");
            if (File.Exists(metal))
            {
                look.SetTexture("_MetallicGlossMap", SiegeCharge.ReadTexture(metal, true));
                look.EnableKeyword("_METALLICGLOSSMAP");
                look.SetFloat("_GlossMapScale", 1f);
            }
            // If the game has no copy of the shader with the maps, it uses one without, and these apply.
            look.SetFloat("_Metallic", metallic);
            look.SetFloat("_Glossiness", gloss);
            return (mesh, look);
        }

        /// <summary>A thing to look at with the model, or a plain block of this size (origin in the middle of its foot) without it.</summary>
        internal static GameObject Shape(string name, Mesh mesh, Material look, Vector3 size, Color plain)
        {
            var go = new GameObject(name) { layer = LayerMask.NameToLayer("LowPolyCollider") };
            if (mesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = look;
                return go;
            }
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(box.GetComponent<Collider>());
            box.transform.SetParent(go.transform, false);
            box.transform.localPosition = Vector3.up * size.y / 2f;
            box.transform.localScale = size;
            box.GetComponent<MeshRenderer>().material.color = plain;
            return go;
        }

        /// <summary>Solid to walk into, on the layer the hideout's walls are.</summary>
        internal static void Solid(Transform parent, Vector3 centre, Vector3 size)
        {
            var go = new GameObject("Hideout Siege solid") { layer = LayerMask.NameToLayer("LowPolyCollider") };
            go.transform.SetParent(parent, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = centre;
            box.size = size;
        }

        /// <summary>Walked round by the attackers.</summary>
        internal static void Block(GameObject on, Vector3 centre, Vector3 size)
        {
            var block = on.AddComponent<NavMeshObstacle>();
            block.shape = NavMeshObstacleShape.Box;
            block.center = centre;
            block.size = size;
            block.carving = true;
        }
    }

    /// <summary>
    /// A steel barricade: a plate taller than you are, with a firing slit at standing eye height, on skids, propped from
    /// behind. Nothing gets through the plate; through the slit you can see and shoot, and so can they. You stand in between
    /// its props, right up against it. Attackers go round it.
    /// </summary>
    internal static class SiegeSteel
    {
        internal const string DecorKey = "hideoutsiege.steel";
        internal const int Limit = 3;
        // As tools\defences\make_defences.py builds it: the plate's front is +z at z = 0, its props reach back to -z.
        private const float Width = 1.2f, Height = 1.5f, Foot = .05f, SlitLow = 1.2f, SlitHigh = 1.32f, SlitHalf = .2f, Props = .6f, PropAt = .5f, Thick = .06f;

        private static Mesh mesh;
        private static Material look;
        private static bool loaded;

        internal static void Offer() => SiegeDecor.Offer(DecorKey, "Steel barricade", Limit, () => Look("Hideout Siege steel barricade (decor)"), "Other");

        private static GameObject Look(string name)
        {
            if (!loaded)
            {
                loaded = true;
                try { (mesh, look) = SiegeDecor.Model("steel", false, .15f, .3f); }
                catch (Exception e) { mesh = null; Plugin.Log.LogWarning("[siege] Couldn't load the steel barricade's model, so it's a plain block: " + e.Message); }
            }
            return SiegeDecor.Shape(name, mesh, look, new Vector3(Width, Height, .1f), new Color(.29f, .31f, .22f));
        }

        /// <summary>In the siege's copy of the hideout, before the floor's walked.</summary>
        internal static void Arm()
        {
            int armed = 0;
            foreach (var steel in SiegeDecor.Placed(DecorKey, Limit))
            {
                if (steel.GetComponent<NavMeshObstacle>() != null || SiegeDecor.InTheWay(steel, "steel barricade")) continue;
                // Hideout Interiors gives it one box round everything, props and all; you'd be kept 0.8 m off the slit.
                foreach (var c in steel.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                SiegeDecor.Solid(steel, new Vector3(0f, Height / 2f, 0f), new Vector3(Width, Height, .1f));
                foreach (float side in new[] { -1f, 1f })
                    SiegeDecor.Solid(steel, new Vector3(side * PropAt, .3f, -Props / 2f), new Vector3(.1f, .6f, Props));
                // Bullets stop on the plate, all but the slit.
                var steelHit = MaterialType.MetalThick;
                SiegeDefences.BulletBox(steel, new Vector3(0f, SlitLow / 2f, 0f), new Vector3(Width, SlitLow, Thick), steelHit);
                SiegeDefences.BulletBox(steel, new Vector3(0f, (SlitHigh + Height) / 2f, 0f), new Vector3(Width, Height - SlitHigh, Thick), steelHit);
                foreach (float side in new[] { -1f, 1f })
                    SiegeDefences.BulletBox(steel, new Vector3(side * (Width / 2f + SlitHalf) / 2f, (SlitLow + SlitHigh) / 2f, 0f), new Vector3(Width / 2f - SlitHalf, SlitHigh - SlitLow, Thick), steelHit);
                SiegeDecor.Block(steel.gameObject, new Vector3(0f, Height / 2f, -Props / 2f + .05f), new Vector3(Width + .2f, Height, Props + .3f));
                steel.gameObject.AddComponent<SiegeDecor.SteelMark>();
                SiegeDecor.Blocks(steel);
                armed++;
            }
            if (armed == 0) return;
            Physics.SyncTransforms();
            Plugin.Log.LogInfo($"[siege] {armed} steel barricades of yours are in the hideout.");
        }
    }

    /// <summary>
    /// Barbed wire: a coil on pickets that doesn't stop a bullet but holds up anyone going through it. In it you can't run,
    /// you're slowed to a crawl and it cuts your legs as you push through. Attackers don't go round it: it's there to keep
    /// them in your sights. It catches you too.
    /// </summary>
    internal static class SiegeBarbedWire
    {
        internal const string DecorKey = "hideoutsiege.wire";
        internal const int Limit = 4;
        internal const float Length = 2f, Depth = .74f, Height = .8f;

        private static Mesh mesh;
        private static Material look;
        private static bool loaded;

        internal static void Offer() => SiegeDecor.Offer(DecorKey, "Barbed wire", Limit, () => Look("Hideout Siege barbed wire (decor)"), "Other");

        private static GameObject Look(string name)
        {
            if (!loaded)
            {
                loaded = true;
                try { (mesh, look) = SiegeDecor.Model("wire", false, .7f, .4f); }
                catch (Exception e) { mesh = null; Plugin.Log.LogWarning("[siege] Couldn't load the barbed wire's model, so it's a plain block: " + e.Message); }
            }
            return SiegeDecor.Shape(name, mesh, look, new Vector3(Length, Height, Depth), new Color(.45f, .44f, .42f));
        }

        /// <summary>In the siege's copy of the hideout, before the floor's walked: walked into, not round.</summary>
        internal static void Arm()
        {
            int armed = 0;
            foreach (var wire in SiegeDecor.Placed(DecorKey, Limit))
            {
                if (wire.GetComponent<SiegeWireTrap>() != null) continue;
                foreach (var c in wire.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                wire.gameObject.AddComponent<SiegeWireTrap>();
                armed++;
            }
            if (armed > 0) Plugin.Log.LogInfo($"[siege] {armed} lengths of barbed wire of yours are in the hideout.");
        }
    }

    /// <summary>
    /// One length of wire at work. Each game looks after its own player, and the host after the attackers, so it's the
    /// same for everyone in co-op.
    /// </summary>
    internal sealed class SiegeWireTrap : MonoBehaviour
    {
        // How slow, as the game's own barbed wire has it; how far you push through for each cut, and what it takes off each leg.
        private const float Slowed = .2f, CutEvery = .45f, CutDamage = 3f, LookEvery = .15f;
        private static readonly Vector3 Half = new Vector3(SiegeBarbedWire.Length / 2f + .1f, .9f, SiegeBarbedWire.Depth / 2f + .08f);

        // Who's in a length of wire (they can be in two at once where they overlap), and where each was when last looked at.
        private static readonly Dictionary<Player, int> caught = new Dictionary<Player, int>();
        private readonly Dictionary<Player, (Vector3 At, float Pushed)> mine = new Dictionary<Player, (Vector3, float)>();
        private float nextLook;

        private void Update()
        {
            if (Time.time < nextLook) return;
            nextLook = Time.time + LookEvery;
            var world = Singleton<GameWorld>.Instance;
            if (world == null) return;
            foreach (var player in world.AllAlivePlayersList)
            {
                if (player == null || !(player.IsYourPlayer || (player.IsAI && !Net.IsClient))) continue;
                Vector3 local = transform.InverseTransformPoint(player.Position + Vector3.up * .3f);
                bool inside = Mathf.Abs(local.x) < Half.x && local.y > -.2f && local.y < Half.y && Mathf.Abs(local.z) < Half.z;
                bool had = mine.TryGetValue(player, out var was);
                if (inside && !had)
                {
                    mine[player] = (player.Position, 0f);
                    Catch(player);
                }
                else if (inside)
                {
                    float pushed = was.Pushed + Flat(player.Position - was.At).magnitude;
                    if (player.IsSprintEnabled) player.EnableSprint(false);
                    while (pushed >= CutEvery)
                    {
                        pushed -= CutEvery;
                        Cut(player);
                    }
                    mine[player] = (player.Position, pushed);
                }
                else if (had)
                {
                    mine.Remove(player);
                    Free(player);
                }
            }
            // Dead or gone while in it.
            foreach (var gone in mine.Keys.Where(p => p == null || p.HealthController == null || !p.HealthController.IsAlive).ToList())
            {
                mine.Remove(gone);
                Free(gone);
            }
        }

        private void OnDestroy()
        {
            foreach (var player in mine.Keys.ToList()) Free(player);
            mine.Clear();
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private static void Catch(Player player)
        {
            caught.TryGetValue(player, out int n);
            caught[player] = n + 1;
            if (n > 0) return;
            try
            {
                player.AddStateSpeedLimit(Slowed, Player.ESpeedLimit.BarbedWire);
                player.EnableSprint(false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Barbed wire: " + e.Message); }
            if (player.IsYourPlayer) SiegeState.Notify("Caught in the wire");
            Plugin.Log.LogInfo($"[siege] {(player.IsYourPlayer ? "You're" : "A " + player.Profile.Info.Settings.Role + " is")} caught in the barbed wire at {player.Position:F1}.");
        }

        private static void Free(Player player)
        {
            if (player == null || !caught.TryGetValue(player, out int n)) return;
            if (n > 1) { caught[player] = n - 1; return; }
            caught.Remove(player);
            try { if (player.MovementContext != null) player.RemoveStateSpeedLimit(Player.ESpeedLimit.BarbedWire); }
            catch (Exception) { }
        }

        private static void Cut(Player player)
        {
            var health = player.ActiveHealthController;
            if (health == null || !health.IsAlive) return;
            try
            {
                foreach (var leg in new[] { EBodyPart.LeftLeg, EBodyPart.RightLeg })
                    health.ApplyDamage(leg, CutDamage, new DamageInfo { DamageType = EDamageType.Barbed, Damage = CutDamage });
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Barbed wire couldn't cut: " + e.Message); }
        }

        internal static void Reset() => caught.Clear();
    }
}
