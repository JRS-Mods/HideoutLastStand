using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// Sandbag walls you put down during prep and the breaks: look at the floor where you want one and press the key. It's
    /// chest high, square to where you're looking, stops bullets, and the attackers go round it. Each player has four, and
    /// a wall can't close a corridor or stand in a doorway, so there's always a way through.
    /// </summary>
    internal static class SiegeSandbags
    {
        private const int PerPlayer = 4;
        private const float Reach = 4f, Length = 1.5f, Depth = .4f, Height = .88f, FromDoors = 1.4f, WayPast = .8f;

        internal static int Left { get; private set; } = PerPlayer;
        internal static readonly List<(Vector3 At, float Yaw)> Placed = new List<(Vector3, float)>();
        private static readonly List<GameObject> walls = new List<GameObject>();
        private static Mesh mesh;
        private static Material material;
        private static bool loaded;

        // On the floor you're looking at, long side across your view. Null if it can go there, otherwise why not.
        private static string FindSpot(Ray look, out Vector3 at, out float yaw)
        {
            at = default;
            yaw = 0f;
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            Vector3 aimed = Physics.Raycast(look, out var hit, Reach, mask, QueryTriggerInteraction.Ignore) ? hit.point + hit.normal * .1f : look.GetPoint(Reach * .6f);
            if (!Physics.Raycast(aimed + Vector3.up * .3f, Vector3.down, out var floor, 3f, mask, QueryTriggerInteraction.Ignore) || floor.normal.y < .9f) return "Not on the floor";
            Vector3 flat = new Vector3(look.direction.x, 0f, look.direction.z);
            if (flat.sqrMagnitude < .01f) return "Not on the floor";
            yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            at = floor.point;
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            Vector3 middle = at + Vector3.up * (Height / 2f + .04f);
            if (Physics.CheckBox(middle, new Vector3(Length / 2f, Height / 2f - .02f, Depth / 2f), turn, mask, QueryTriggerInteraction.Ignore)) return "No room for sandbags there";
            // Not in a doorway, nor across a corridor: the attackers need a way through.
            Vector3 spot = at;
            if (SiegeBreach.AllDoorCentres().Any(d => (new Vector3(d.x - spot.x, 0f, d.z - spot.z)).sqrMagnitude < FromDoors * FromDoors)) return "Too close to a door";
            Vector3 across = turn * Vector3.right;
            float Clear(Vector3 way) => Physics.Raycast(middle + way * (Length / 2f), way, out var wall, WayPast + .05f, mask, QueryTriggerInteraction.Ignore) ? wall.distance : WayPast + 1f;
            if (Clear(across) < WayPast && Clear(-across) < WayPast) return "That would block the way through";
            return null;
        }

        /// <summary>A wall of sandbags, on everyone's game.</summary>
        internal static void Place(Vector3 at, float yaw)
        {
            Placed.Add((at, yaw));
            var go = Look("Hideout Siege sandbags");
            go.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            var solid = go.AddComponent<BoxCollider>();
            solid.center = Vector3.up * Height / 2f;
            solid.size = new Vector3(Length, Height, Depth);
            SiegeDefences.BulletBox(go.transform, Vector3.up * Height / 2f, new Vector3(Length, Height, Depth), EFT.Ballistics.MaterialType.Soil);
            var block = go.AddComponent<NavMeshObstacle>();
            block.shape = NavMeshObstacleShape.Box;
            block.center = Vector3.up * Height / 2f;
            block.size = new Vector3(Length + .2f, Height, Depth + .2f);
            block.carving = true;
            walls.Add(go);
            Plugin.Log.LogInfo($"[siege] Sandbags at {at:F2}, facing {yaw:F0}.");
        }

        // ---- as Hideout Interiors decor: put in your hideout yourself, and there in the siege ----------------------

        internal const string DecorKey = "hideoutsiege.sandbags";
        internal const int DecorLimit = 6;
        private const string DecorName = "Hideout Siege sandbags (decor)";

        /// <summary>Adds sandbags to Hideout Interiors' decor panel (its Defences tab), where it's installed.</summary>
        internal static void OfferAsDecor() => SiegeDecor.Offer(DecorKey, "Sandbags", DecorLimit, () => Look(DecorName), "Other");

        /// <summary>
        /// In the siege's copy of the hideout, before the floor's walked: the sandbags you put in with Hideout Interiors stop
        /// bullets, and stand on the attackers' floor as solid (so they're walked round, as the siege's own are).
        /// </summary>
        internal static void ArmDecor()
        {
            try
            {
                var placed = SiegeDecor.Placed(DecorKey, DecorLimit);
                if (placed.Count == 0) return;
                int layer = LayerMask.NameToLayer("LowPolyCollider"), armed = 0;
                foreach (var bags in placed)
                {
                    if (bags == null || bags.GetComponent<NavMeshObstacle>() != null || SiegeDecor.InTheWay(bags, "sandbags")) continue;
                    foreach (var c in bags.GetComponentsInChildren<Collider>(true)) { c.gameObject.layer = layer; c.enabled = true; }
                    SiegeDefences.BulletBox(bags, Vector3.up * Height / 2f, new Vector3(Length, Height, Depth), EFT.Ballistics.MaterialType.Soil);
                    var block = bags.gameObject.AddComponent<NavMeshObstacle>();
                    block.shape = NavMeshObstacleShape.Box;
                    block.center = Vector3.up * Height / 2f;
                    block.size = new Vector3(Length + .2f, Height, Depth + .2f);
                    block.carving = true;
                    SiegeDecor.Blocks(bags);
                    armed++;
                }
                Plugin.Log.LogInfo($"[siege] {armed} sandbag walls of yours are in the hideout.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't set up your sandbags: " + e.Message); }
        }

        // The wall's look: the model, or a plain block without it. Its origin's in the middle of its foot.
        private static GameObject Look(string name)
        {
            Load();
            var go = new GameObject(name) { layer = LayerMask.NameToLayer("LowPolyCollider") };
            if (mesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
            }
            else
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(box.GetComponent<Collider>());
                box.transform.SetParent(go.transform, false);
                box.transform.localPosition = Vector3.up * Height / 2f;
                box.transform.localScale = new Vector3(Length, Height, Depth);
                box.GetComponent<MeshRenderer>().sharedMaterial.color = new Color(.6f, .52f, .38f);
            }
            return go;
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string folder = Path.Combine(Plugin.Folder, "models");
                mesh = SiegeCharge.ReadMesh(Path.Combine(folder, "sandbags.mesh"), out _);
                mesh.name = "Hideout Siege sandbags";
                var seed = GameObject.CreatePrimitive(PrimitiveType.Cube);
                material = new Material(seed.GetComponent<Renderer>().sharedMaterial) { name = "Hideout Siege sandbags", color = Color.white };
                Object.Destroy(seed);
                material.SetTexture("_MainTex", SiegeCharge.ReadTexture(Path.Combine(folder, "sandbags_base.jpg"), false));
                material.SetTexture("_BumpMap", SiegeCharge.ReadTexture(Path.Combine(folder, "sandbags_normal.png"), true));
                material.EnableKeyword("_NORMALMAP");
                material.SetFloat("_Metallic", 0f);
                material.SetFloat("_Glossiness", .08f);
            }
            catch (Exception e)
            {
                mesh = null;
                Plugin.Log.LogWarning("[siege] Couldn't load the sandbags' model, so they're plain blocks: " + e.Message);
            }
        }

        internal static void Reset()
        {
            Left = PerPlayer;
            Placed.Clear();
            foreach (var w in walls) if (w != null) Object.Destroy(w);
            walls.Clear();
        }
    }
}
