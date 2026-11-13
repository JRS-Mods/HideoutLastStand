using System;
using System.IO;
using EFT;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>A breaching charge: carried in a hand, set on a door, then blinking and beeping faster until it goes off.</summary>
    internal sealed class SiegeCharge : MonoBehaviour
    {
        private const float PlaceSeconds = .7f;
        private const float BeepsFrom = 10f, HurriesFrom = 3f, SolidFrom = .6f;
        private const float BackOfOldCharge = .035f;
        // Hanging from the hand by its top, centred on it, a little clear of the leg.
        private const float BelowHand = .24f, HalfDepth = .08f, ClearOfLeg = .04f;
        private static readonly Vector3 PlainSize = new Vector3(.2f, .3f, .1f);
        private static readonly Color Red = new Color(1f, .07f, .03f);
        private static Mesh mesh;
        private static Material body, lampLook, glowLook;
        private static Vector3 lampAt = new Vector3(0f, 0f, .1f);
        private static bool loaded;

        private enum State { Carried, Placing, Armed }

        private State state;
        private Player carrier;
        private Func<float> secondsLeft;
        private Vector3 fromWhere, restAt;
        private Quaternion fromHow, restHow;
        private float placedAt, nextBlink, litUntil;
        private bool lit;
        private Light glowLight;
        private Renderer lampShape, glowShape;
        // Its timer, heard from close by and not far beyond that: through the door it's on, if you put your ear near it.
        private const float TickHeardTo = 6f, TickFullWithin = .8f, TickVolume = .3f;
        private static AudioClip tick;
        private AudioSource ticking;

        // Reading it all takes a moment, so this happens while the screen's black. If anything's wrong the charge is a plain box and the siege goes on.
        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string folder = Path.Combine(Plugin.Folder, "models");
                mesh = ReadMesh(Path.Combine(folder, "charge.mesh"), out lampAt);
                var seed = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body = new Material(seed.GetComponent<Renderer>().sharedMaterial) { name = "Hideout Siege charge", color = Color.white };
                Destroy(seed);
                body.SetTexture("_MainTex", ReadTexture(Path.Combine(folder, "charge_base.jpg"), false));
                body.SetTexture("_BumpMap", ReadTexture(Path.Combine(folder, "charge_normal.png"), true));
                body.EnableKeyword("_NORMALMAP");
                body.SetTexture("_MetallicGlossMap", ReadTexture(Path.Combine(folder, "charge_metal.png"), true));
                body.EnableKeyword("_METALLICGLOSSMAP");
                body.SetFloat("_GlossMapScale", 1f);
                // If the game has no copy of the shader with those maps, it uses one without them, and these apply instead.
                body.SetFloat("_Metallic", .4f);
                body.SetFloat("_Glossiness", .35f);
                lampLook = Look(Red, null, "Unlit/Color", "Hidden/Internal-Colored", "Sprites/Default");
                glowLook = Look(new Color(Red.r, Red.g, Red.b, .6f), GlowImage(), "Legacy Shaders/Particles/Additive", "Sprites/Default");
                Plugin.Log.LogInfo($"[siege] Charge model ready: {mesh.vertexCount} vertices, shader {body.shader.name}, lamp {lampLook?.shader.name}, glow {glowLook?.shader.name}.");
            }
            catch (Exception e)
            {
                mesh = null;
                Plugin.Log.LogWarning("[siege] Couldn't load the charge model, so it'll be a plain box: " + e.Message);
            }
        }

        // The packed models the tools make: counts, a point of the model's own (the charge's lamp), then the vertices and triangles.
        internal static Mesh ReadMesh(string path, out Vector3 point)
        {
            using (var file = new BinaryReader(File.OpenRead(path)))
            {
                int vertices = file.ReadInt32(), indices = file.ReadInt32();
                point = ReadVector(file);
                var positions = new Vector3[vertices];
                var normals = new Vector3[vertices];
                var tangents = new Vector4[vertices];
                var uvs = new Vector2[vertices];
                var triangles = new int[indices];
                for (int i = 0; i < vertices; i++) positions[i] = ReadVector(file);
                for (int i = 0; i < vertices; i++) normals[i] = ReadVector(file);
                for (int i = 0; i < vertices; i++) tangents[i] = new Vector4(file.ReadSingle(), file.ReadSingle(), file.ReadSingle(), file.ReadSingle());
                for (int i = 0; i < vertices; i++) uvs[i] = new Vector2(file.ReadSingle(), file.ReadSingle());
                for (int i = 0; i < indices; i++) triangles[i] = file.ReadInt32();
                var result = new Mesh { name = "Hideout Siege charge", vertices = positions, normals = normals, tangents = tangents, uv = uvs, triangles = triangles };
                result.RecalculateBounds();
                result.UploadMeshData(true);
                return result;
            }
        }

        private static Vector3 ReadVector(BinaryReader file) => new Vector3(file.ReadSingle(), file.ReadSingle(), file.ReadSingle());

        internal static Texture2D ReadTexture(string path, bool data)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, data) { name = Path.GetFileNameWithoutExtension(path), anisoLevel = 4 };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) throw new IOException("Couldn't read " + path);
            texture.Apply(true, false);
            texture.Compress(false);
            texture.Apply(false, true);
            return texture;
        }

        private static Material Look(Color colour, Texture image, params string[] shaders)
        {
            foreach (string name in shaders)
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;
                var look = new Material(shader) { name = "Hideout Siege charge lamp" };
                if (look.HasProperty("_Color")) look.SetColor("_Color", colour);
                if (look.HasProperty("_TintColor")) look.SetColor("_TintColor", colour);
                if (image != null) look.SetTexture("_MainTex", image);
                return look;
            }
            return null;
        }

        private static Texture2D GlowImage()
        {
            const int size = 64;
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Hideout Siege charge glow", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float fromMiddle = Mathf.Clamp01(new Vector2(x + .5f - size / 2f, y + .5f - size / 2f).magnitude / (size / 2f));
                    image.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Pow(1f - fromMiddle, 2.5f)));
                }
            image.Apply(false, true);
            return image;
        }

        // Someone's carrying it. It follows their hand until it's put on a door.
        internal static SiegeCharge InHand(Player who)
        {
            var charge = New();
            charge.carrier = who;
            charge.state = State.Carried;
            charge.Follow();
            return charge;
        }

        // Nobody brought it, so it's just there. A carrier who died on the way, or a message that came too late.
        internal static SiegeCharge OnItsOwn(Vector3 at)
        {
            var charge = New();
            charge.transform.position = at;
            return charge;
        }

        private static SiegeCharge New()
        {
            var charge = new GameObject("Hideout Siege charge").AddComponent<SiegeCharge>();
            charge.Build();
            return charge;
        }

        /// <summary>Sets it on a door. <paramref name="at"/> is where its middle would be, <paramref name="face"/> points away from the door.</summary>
        internal GameObject PutOn(Vector3 at, Vector3 face, Func<float> left, bool byHand)
        {
            face = face.sqrMagnitude > .001f ? face.normalized : Vector3.forward;
            carrier = null;
            secondsLeft = left;
            restAt = at - face * BackOfOldCharge;
            restHow = Quaternion.LookRotation(face);
            fromWhere = transform.position;
            fromHow = transform.rotation;
            placedAt = Time.time;
            state = byHand ? State.Placing : State.Armed;
            if (!byHand) transform.SetPositionAndRotation(restAt, restHow);
            return gameObject;
        }

        private void Build()
        {
            if (mesh != null)
            {
                var shape = new GameObject("Hideout Siege charge body");
                shape.transform.SetParent(transform, false);
                shape.AddComponent<MeshFilter>().sharedMesh = mesh;
                shape.AddComponent<MeshRenderer>().sharedMaterial = body;
                lampShape = Part(PrimitiveType.Sphere, lampLook, lampAt, Vector3.one * .013f);
                glowShape = Part(PrimitiveType.Quad, glowLook, lampAt + Vector3.forward * .012f, Vector3.one * .16f);
            }
            else
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(box.GetComponent<Collider>());
                box.name = "Hideout Siege charge body";
                box.transform.SetParent(transform, false);
                box.transform.localPosition = new Vector3(0f, 0f, PlainSize.z / 2f);
                box.transform.localScale = PlainSize;
                box.GetComponent<Renderer>().material.color = new Color(.16f, .16f, .13f);
                lampAt = new Vector3(0f, 0f, PlainSize.z);
            }

            var lamp = new GameObject("Hideout Siege charge light");
            lamp.transform.SetParent(transform, false);
            lamp.transform.localPosition = lampAt + Vector3.forward * .07f;
            glowLight = lamp.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.color = new Color(1f, .1f, .05f);
            glowLight.shadows = LightShadows.None;
            ShowLit(false);
        }

        private Renderer Part(PrimitiveType type, Material look, Vector3 position, Vector3 scale)
        {
            if (look == null) return null;
            var part = GameObject.CreatePrimitive(type);
            part.name = "Hideout Siege charge lamp";
            Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = look;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        // After the animations, or it'd trail a frame behind the hand.
        private void LateUpdate()
        {
            if (state == State.Carried) Follow();
        }

        private void Follow()
        {
            if (carrier == null || carrier.HealthController == null || !carrier.HealthController.IsAlive)
            {
                Destroy(gameObject);
                return;
            }
            Transform hand = carrier.PlayerBones != null ? carrier.PlayerBones.RightPalm : null;
            if (hand == null) return;
            Quaternion facing = Quaternion.Euler(0f, carrier.Rotation.x, 0f);
            transform.SetPositionAndRotation(hand.position + Vector3.down * BelowHand + facing * new Vector3(ClearOfLeg, 0f, -HalfDepth), facing);
        }

        private void Update()
        {
            if (state == State.Carried) return;
            if (state == State.Placing)
            {
                float t = Mathf.Clamp01((Time.time - placedAt) / PlaceSeconds);
                float eased = t * t * (3f - 2f * t);
                transform.SetPositionAndRotation(Vector3.Lerp(fromWhere, restAt, eased), Quaternion.Slerp(fromHow, restHow, eased));
                if (t < 1f) return;
                state = State.Armed;
            }
            if (ticking == null) Tick();

            float left = secondsLeft();
            if (left > SolidFrom && Time.time >= nextBlink)
            {
                float every = left > BeepsFrom ? 1.2f : left > HurriesFrom ? 1f : .4f;
                nextBlink = Time.time + every;
                litUntil = Time.time + Mathf.Min(every * .4f, .25f);
                if (left <= BeepsFrom) SiegeSounds.Play("beep");
            }
            bool on = left <= SolidFrom || Time.time < litUntil;
            if (on != lit) ShowLit(on);
            if (on)
            {
                float closer = Mathf.Clamp01(1f - left / BeepsFrom);
                glowLight.intensity = Mathf.Lerp(2.5f, 6f, closer);
                glowLight.range = Mathf.Lerp(2.2f, 3.5f, closer);
            }
        }

        private void Tick()
        {
            ticking = gameObject.AddComponent<AudioSource>();
            ticking.clip = TickClip();
            ticking.loop = true;
            ticking.playOnAwake = false;
            ticking.spatialBlend = 1f;
            ticking.dopplerLevel = 0f;
            ticking.rolloffMode = AudioRolloffMode.Linear;
            ticking.minDistance = TickFullWithin;
            ticking.maxDistance = TickHeardTo;
            ticking.volume = TickVolume * Plugin.Volume.Value;
            if (Comfort.Common.Singleton<BetterAudio>.Instantiated) ticking.outputAudioMixerGroup = Comfort.Common.Singleton<BetterAudio>.Instance.VeryStandartMixerGroup;
            ticking.Play();
        }

        // A second of clockwork, made here: a sharp tick, and a duller tock half a second after it.
        private static AudioClip TickClip()
        {
            if (tick != null) return tick;
            const int rate = 44100;
            var samples = new float[rate];
            void Click(int from, float pitch, float loud)
            {
                for (int i = 0; i < rate / 40 && from + i < samples.Length; i++)
                {
                    float t = i / (float)rate, fade = Mathf.Exp(-t * 260f);
                    samples[from + i] += loud * fade * (Mathf.Sin(2f * Mathf.PI * pitch * t) * .7f + Mathf.Sin(2f * Mathf.PI * pitch * 2.7f * t) * .3f);
                }
            }
            Click(0, 2400f, .9f);
            Click(rate / 2, 1700f, .6f);
            tick = AudioClip.Create("Hideout Siege tick", samples.Length, 1, rate, false);
            tick.SetData(samples, 0);
            return tick;
        }

        private void ShowLit(bool on)
        {
            lit = on;
            glowLight.enabled = on;
            if (lampShape != null) lampShape.enabled = on;
            if (glowShape != null) glowShape.enabled = on;
        }
    }
}
