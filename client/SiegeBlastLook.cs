using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>
    /// What a breaching charge throws out besides the game's own grenade burst: a jet of dust the way the blast goes, bits
    /// of the door or wall that bounce across the floor, sparks, and smoke that hangs in the doorway for a while after.
    /// All made here, so it's the same on every machine and needs nothing loaded. Nothing in it can be walked into.
    /// </summary>
    internal static class SiegeBlastLook
    {
        private const float GoneAfter = 16f;

        private static Texture2D puff;
        private static Material dustLook, sparkLook, chunkLook;
        private static readonly Mesh[] rocks = new Mesh[4];

        /// <param name="toward">The way the blast goes: through the door or wall, away from the charge.</param>
        /// <param name="wall">A wall coming down rather than a door: more of everything, across the width of the hole.</param>
        /// <param name="chunks">What the bits are made of, where that's known.</param>
        internal static void Burst(Vector3 at, Vector3 toward, bool wall, Material chunks = null)
        {
            try
            {
                toward.y = Mathf.Clamp(toward.y, -.2f, .2f);
                toward = toward.sqrMagnitude < .01f ? Vector3.up : toward.normalized;
                var root = new GameObject("Hideout Siege blast");
                root.transform.SetPositionAndRotation(at, Quaternion.LookRotation(toward));
                Dust(root, wall);
                Smoke(root, wall);
                Sparks(root, wall);
                Chunks(root, wall, chunks);
                foreach (var system in root.GetComponentsInChildren<ParticleSystem>()) system.Play();
                Object.Destroy(root, GoneAfter);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] The blast's dust and debris: " + e.Message); }
        }

        // The jet: out fast the way the blast goes, slowing to a cloud.
        private static void Dust(GameObject root, bool wall)
        {
            var system = New(root, "dust", DustLook(), out _);
            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, wall ? 10f : 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(wall ? .7f : .5f, wall ? 2.2f : 1.6f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(.3f, .28f, .25f), new Color(.2f, .19f, .18f));
            main.gravityModifier = -.01f;
            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 30f;
            shape.radius = wall ? .7f : .3f;
            Slowing(system, .5f, .1f);
            Growing(system, .5f, 1.7f);
            Fading(system, .6f, .08f);
            Turning(system, .4f);
            Burst(system, wall ? 80 : 45);
        }

        // What's left hanging where it went off.
        private static void Smoke(GameObject root, bool wall)
        {
            var system = New(root, "smoke", DustLook(), out _);
            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 11f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.2f, 1.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.4f, wall ? 3.2f : 2.6f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(.16f, .16f, .16f), new Color(.26f, .24f, .22f));
            main.gravityModifier = -.015f;
            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = wall ? .9f : .6f;
            Slowing(system, .15f, .05f);
            Growing(system, .7f, 1.5f);
            Fading(system, .3f, .2f);
            Turning(system, .2f);
            Burst(system, wall ? 24 : 14);
        }

        private static void Sparks(GameObject root, bool wall)
        {
            var system = New(root, "sparks", SparkLook(), out var renderer);
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = .04f;
            renderer.lengthScale = 1f;
            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.3f, .9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 16f);
            main.startSize = new ParticleSystem.MinMaxCurve(.03f, .07f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, .75f, .35f), new Color(1f, .45f, .15f));
            main.gravityModifier = 1f;
            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 55f;
            shape.radius = .15f;
            Fading(system, 1f, .02f);
            Burst(system, wall ? 60 : 40);
        }

        // Bits of what was blown, which land and bounce. They touch the floor and walls and nothing else: nobody trips on them.
        private static void Chunks(GameObject root, bool wall, Material made)
        {
            var system = New(root, "debris", made != null ? made : ChunkLook(), out var renderer);
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.SetMeshes(new[] { Rock(0), Rock(1), Rock(2), Rock(3) });
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 12f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3f, wall ? 11f : 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(.04f, wall ? .26f : .15f);
            main.startRotation3D = true;
            main.startRotationX = main.startRotationY = main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = Color.white;
            main.gravityModifier = 1f;
            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 38f;
            shape.radius = wall ? .7f : .3f;
            var collision = system.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Medium;
            collision.collidesWith = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain");
            collision.bounce = .25f;
            collision.dampen = .45f;
            collision.lifetimeLoss = 0f;
            collision.radiusScale = .6f;
            var spin = system.rotationOverLifetime;
            spin.enabled = true;
            spin.separateAxes = true;
            spin.x = spin.y = spin.z = new ParticleSystem.MinMaxCurve(-4f, 4f);
            // Gone by shrinking at the very end, as there's no fading something solid.
            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(.92f, 1f), new Keyframe(1f, 0f)));
            Burst(system, wall ? 34 : 16);
        }

        private static ParticleSystem New(GameObject root, string name, Material look, out ParticleSystemRenderer renderer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var system = go.AddComponent<ParticleSystem>();
            // A new one's already running, and most of it can only be set while it's stopped.
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = look;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var main = system.main;
            main.duration = 1f;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            var emission = system.emission;
            emission.rateOverTime = 0f;
            return system;
        }

        private static void Burst(ParticleSystem system, int count)
        {
            var emission = system.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
        }

        private static void Slowing(ParticleSystem system, float to, float by)
        {
            var limit = system.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = to;
            limit.dampen = by;
        }

        private static void Growing(ParticleSystem system, float from, float to)
        {
            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, from), new Keyframe(.3f, Mathf.Lerp(from, to, .7f)), new Keyframe(1f, to)));
        }

        // In quickly, then away slowly.
        private static void Fading(ParticleSystem system, float most, float inBy)
        {
            var colour = system.colorOverLifetime;
            colour.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(most, inBy), new GradientAlphaKey(most * .6f, .5f), new GradientAlphaKey(0f, 1f) });
            colour.color = fade;
        }

        private static void Turning(ParticleSystem system, float by)
        {
            var turn = system.rotationOverLifetime;
            turn.enabled = true;
            turn.z = new ParticleSystem.MinMaxCurve(-by, by);
        }

        private static Material DustLook()
        {
            if (dustLook == null) dustLook = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent")) { name = "Hideout Siege dust", mainTexture = Puff() };
            return dustLook;
        }

        private static Material SparkLook()
        {
            if (sparkLook == null) sparkLook = new Material(Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default")) { name = "Hideout Siege sparks", mainTexture = Puff() };
            return sparkLook;
        }

        private static Material ChunkLook()
        {
            if (chunkLook == null) chunkLook = new Material(Shader.Find("Standard") ?? Shader.Find("Sprites/Default")) { name = "Hideout Siege debris", color = new Color(.3f, .29f, .27f) };
            return chunkLook;
        }

        // A soft round blob with a ragged edge: a hard-edged square would show as one.
        private static Texture2D Puff()
        {
            if (puff != null) return puff;
            const int size = 64;
            puff = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "Hideout Siege puff", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + .5f) / size * 2f - 1f, dy = (y + .5f) / size * 2f - 1f;
                    float ragged = .82f + .18f * Mathf.PerlinNoise(x * .13f + 3.1f, y * .13f + 7.7f);
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy) / ragged);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
                }
            puff.SetPixels32(pixels);
            puff.Apply(true, true);
            return puff;
        }

        // Each face of a box, by its corners (corner n is at x from bit 1, y from bit 2, z from bit 4), wound to face outward.
        private static readonly int[][] Faces =
        {
            new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },
        };

        /// <summary>A broken lump: a box with every corner knocked out of true, flat-faced. A few different ones.</summary>
        internal static Mesh Rock(int which)
        {
            which = (which % rocks.Length + rocks.Length) % rocks.Length;
            if (rocks[which] != null) return rocks[which];
            var random = new System.Random(31 + which * 7);
            float Knock() => ((float)random.NextDouble() - .5f) * .5f;
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = new Vector3((i & 1) != 0 ? .5f : -.5f, (i & 2) != 0 ? .5f : -.5f, (i & 4) != 0 ? .5f : -.5f) + new Vector3(Knock(), Knock(), Knock());
            var points = new Vector3[24];
            var map = new Vector2[24];
            var triangles = new int[36];
            for (int f = 0; f < 6; f++)
            {
                for (int k = 0; k < 4; k++)
                {
                    points[f * 4 + k] = corners[Faces[f][k]];
                    map[f * 4 + k] = new Vector2(k == 1 || k == 2 ? 1f : 0f, k >= 2 ? 1f : 0f);
                }
                int a = f * 4, t = f * 6;
                triangles[t] = a; triangles[t + 1] = a + 1; triangles[t + 2] = a + 2;
                triangles[t + 3] = a; triangles[t + 4] = a + 2; triangles[t + 5] = a + 3;
            }
            var mesh = new Mesh { name = "Hideout Siege lump " + which, vertices = points, uv = map, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return rocks[which] = mesh;
        }
    }
}
