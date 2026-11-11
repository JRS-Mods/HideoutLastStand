using System.Collections.Generic;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// What the attackers' tunnel looks like: dug earth inside, a timber frame at its mouth, and a bank of earth across most
    /// of it a couple of metres in, with a way round at one side. They wait behind the bank, so through the hole you see a
    /// dark tunnel going on past it (and a work lamp's glow), not the end where they are. When the wall's blown, its edges
    /// are broken and scorched rather than a neat cut.
    /// </summary>
    internal static class SiegeTunnelLook
    {
        private const float Gap = 1.4f, BankDepth = .6f;
        private static readonly List<GameObject> made = new List<GameObject>();
        private static Material dirt, timber, scorch, burnt;

        /// <summary>
        /// Lines the tunnel and puts the bank in (before the floor's walked, so it's walked round). Returns how far in from
        /// the hole the bank is: they wait beyond it.
        /// </summary>
        internal static float Dress(Vector3 hole, Vector3 outward, Rect floor, float y, float wallGap, float height)
        {
            Clear();
            Materials();
            Vector3 across = Vector3.Cross(Vector3.up, outward);
            float length = Mathf.Abs(outward.x) > .5f ? floor.width : floor.height;
            float width = Mathf.Abs(outward.x) > .5f ? floor.height : floor.width;
            Vector3 start = new Vector3(hole.x, y, hole.z) + outward * (wallGap / 2f);
            Vector3 At(float depth, float side, float up) => start + outward * depth + across * side + Vector3.up * up;
            Quaternion facing = Quaternion.LookRotation(outward);
            var random = new System.Random(11);
            float Jitter(float most) => ((float)random.NextDouble() - .5f) * 2f * most;

            // Earth over the walls, the roof and the floor, in a few uneven pieces each.
            for (int i = 0; i < 3; i++)
            {
                float d0 = length * i / 3f, d1 = length * (i + 1) / 3f, mid = (d0 + d1) / 2f, len = d1 - d0 + .3f;
                foreach (float side in new[] { -1f, 1f })
                    Slab(At(mid, side * (width / 2f - .04f), height / 2f), facing * Quaternion.Euler(Jitter(2f), Jitter(3f), Jitter(2f)), new Vector3(.08f, height + .1f, len), dirt);
                Slab(At(mid, 0f, height - .04f), facing * Quaternion.Euler(Jitter(2f), 0f, Jitter(2f)), new Vector3(width + .1f, .08f, len), dirt);
                Slab(At(mid, 0f, .01f), facing, new Vector3(width, .02f, len), dirt);
            }
            Slab(At(length - .04f, 0f, height / 2f), facing, new Vector3(width + .1f, height + .1f, .08f), dirt);

            // A timber frame just inside the hole.
            foreach (float side in new[] { -1f, 1f })
                Slab(At(.55f, side * (width / 2f - .14f), (height - .1f) / 2f), facing, new Vector3(.16f, height - .1f, .16f), timber);
            Slab(At(.55f, 0f, height - .22f), facing, new Vector3(width - .05f, .2f, .2f), timber);

            // The bank: across from one wall, leaving a way round at the other. Solid, and it stops bullets.
            float bank = Mathf.Clamp(length * .45f, 1.6f, 2.8f);
            float bankWidth = width - Gap;
            var heap = Slab(At(bank, -Gap / 2f, height / 2f), facing, new Vector3(bankWidth, height, BankDepth), dirt, solid: true);
            SiegeDefences.BulletBox(heap.transform, Vector3.zero, Vector3.one, EFT.Ballistics.MaterialType.Soil);
            // Earth spilling down its front, so it reads as a heap rather than a wall.
            for (int i = 0; i < 4; i++)
                Slab(At(bank - BankDepth / 2f - .2f - Jitter(.1f), -Gap / 2f + Jitter(bankWidth / 3f), .25f + Jitter(.1f)),
                     facing * Quaternion.Euler(-30f + Jitter(10f), Jitter(25f), Jitter(10f)), new Vector3(.6f + Jitter(.2f), .5f, .7f), dirt);

            // Their work lamp, round the bank: a glow past it that the hideout's power has nothing to do with.
            var lamp = new GameObject(SiegeLights.OwnLight + " tunnel lamp").AddComponent<Light>();
            lamp.transform.position = At(Mathf.Min(bank + 1.2f, length - .4f), width / 2f - Gap / 2f, 1.9f);
            lamp.type = LightType.Point;
            lamp.color = new Color(1f, .72f, .42f);
            lamp.range = 4f;
            lamp.intensity = 1.1f;
            lamp.shadows = LightShadows.None;
            made.Add(lamp.gameObject);
            Physics.SyncTransforms();
            return bank;
        }

        /// <summary>The wall's been blown: broken edges round the hole, and a scorch on the room's side.</summary>
        internal static void Blown(Vector3 hole, Vector3 outward, float wallGap, float holeWidth, float holeHeight, Material wall)
        {
            Materials();
            Vector3 across = Vector3.Cross(Vector3.up, outward);
            Vector3 face = hole - outward * (wallGap / 2f);
            Quaternion facing = Quaternion.LookRotation(outward);
            var random = new System.Random(5);
            float R(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
            // Broken wall in the wall's own thickness round the edges, blackened by the blast, so the hole's ragged.
            if (burnt == null) burnt = new Material(wall != null ? wall : dirt) { name = "Hideout Siege burnt wall", color = new Color(.32f, .3f, .28f) };
            for (int i = 0; i < 22; i++)
            {
                float side, up;
                int edge = i % 3;
                if (edge == 0) { side = -holeWidth / 2f + R(0f, .2f); up = R(.05f, holeHeight); }
                else if (edge == 1) { side = holeWidth / 2f - R(0f, .2f); up = R(.05f, holeHeight); }
                else { side = R(-holeWidth / 2f, holeWidth / 2f); up = holeHeight - R(0f, .22f); }
                float s = R(.12f, .32f);
                Slab(face + outward * (wallGap / 2f) + across * side + Vector3.up * up, facing * Quaternion.Euler(R(-6f, 6f), R(-6f, 6f), R(0f, 90f)),
                     new Vector3(s, s * R(.7f, 1.2f), wallGap + .03f), burnt);
            }
            // The scorch: dark round the hole, clear in it.
            var mark = GameObject.CreatePrimitive(PrimitiveType.Quad);
            mark.name = "Hideout Siege scorch";
            Object.Destroy(mark.GetComponent<Collider>());
            mark.transform.SetPositionAndRotation(face - outward * .015f + Vector3.up * 1.5f, Quaternion.LookRotation(outward));
            mark.transform.localScale = new Vector3(3.4f, 3f, 1f);
            mark.GetComponent<MeshRenderer>().sharedMaterial = scorch;
            made.Add(mark);
        }

        internal static void Clear()
        {
            foreach (var go in made) if (go != null) Object.Destroy(go);
            made.Clear();
        }

        private static GameObject Slab(Vector3 at, Quaternion turn, Vector3 size, Material look, bool solid = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Hideout Siege tunnel";
            if (solid) go.layer = LayerMask.NameToLayer("LowPolyCollider");
            else Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetPositionAndRotation(at, turn);
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = look;
            made.Add(go);
            return go;
        }

        // Earth and timber drawn here, rather than shipped: noise in browns, and a grain.
        private static void Materials()
        {
            if (dirt != null) return;
            var seed = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var standard = seed.GetComponent<Renderer>().sharedMaterial;
            Object.Destroy(seed);

            const int n = 256;
            var earth = new Texture2D(n, n, TextureFormat.RGB24, true) { name = "Hideout Siege earth", wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float v = Mathf.PerlinNoise(x * .03f, y * .03f) * .55f + Mathf.PerlinNoise(x * .11f + 40f, y * .11f) * .3f + Mathf.PerlinNoise(x * .45f, y * .45f + 90f) * .15f;
                    if (Mathf.PerlinNoise(x * .9f + 7f, y * .9f) > .78f) v -= .25f;   // stones
                    earth.SetPixel(x, y, Color.Lerp(new Color(.17f, .13f, .1f), new Color(.42f, .33f, .24f), Mathf.Clamp01(v)));
                }
            earth.Apply();
            dirt = new Material(standard) { name = "Hideout Siege earth", color = Color.white, mainTexture = earth, mainTextureScale = new Vector2(2f, 2f) };
            dirt.SetFloat("_Glossiness", .03f);
            dirt.SetFloat("_Metallic", 0f);

            var grain = new Texture2D(64, 256, TextureFormat.RGB24, true) { name = "Hideout Siege timber", wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 64; x++)
                {
                    float v = .5f + .3f * Mathf.Sin(x * .5f + Mathf.PerlinNoise(x * .05f, y * .02f) * 6f) + Mathf.PerlinNoise(x * .3f, y * .3f) * .2f;
                    grain.SetPixel(x, y, Color.Lerp(new Color(.22f, .15f, .09f), new Color(.43f, .31f, .19f), Mathf.Clamp01(v)));
                }
            grain.Apply();
            timber = new Material(standard) { name = "Hideout Siege timber", color = Color.white, mainTexture = grain };
            timber.SetFloat("_Glossiness", .06f);
            timber.SetFloat("_Metallic", 0f);

            // Round the hole (3.4 by 3 m, the hole 1.7 by 2.25 at the bottom middle): dark, fading out, clear inside it.
            const int w = 170, h = 150;
            var burn = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Hideout Siege scorch", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float mx = (x + .5f) / w * 3.4f - 1.7f, my = (y + .5f) / h * 3f;
                    float dx = Mathf.Max(0f, Mathf.Abs(mx) - .85f), dy = Mathf.Max(0f, my - 2.25f);
                    bool inside = dx <= 0f && dy <= 0f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = inside ? 0f : Mathf.Clamp01(.85f - d / .8f) * (.7f + .3f * Mathf.PerlinNoise(x * .15f, y * .15f));
                    burn.SetPixel(x, y, new Color(.05f, .04f, .03f, a));
                }
            burn.Apply();
            scorch = new Material(Shader.Find("Sprites/Default") ?? standard.shader) { name = "Hideout Siege scorch", mainTexture = burn, color = Color.white };
        }
    }
}
