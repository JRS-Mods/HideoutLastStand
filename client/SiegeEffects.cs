using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>Sounds from the mod's own synthesised files.</summary>
    internal static class SiegeSounds
    {
        private static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private static GameObject host;
        private static AudioSource oneShot, alarm;

        internal static void Load()
        {
            string folder = Path.Combine(Plugin.Folder, "sounds");
            if (!Directory.Exists(folder)) { Plugin.Log.LogWarning("No sounds folder: the siege will be quiet."); return; }
            foreach (string path in Directory.GetFiles(folder, "*.wav"))
            {
                try { clips[Path.GetFileNameWithoutExtension(path)] = Read(path); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"Couldn't load {path}: {e.Message}"); }
            }
        }

        private static void Ensure()
        {
            if (host != null) return;
            host = new GameObject("Hideout Siege sound");
            oneShot = Source();
            alarm = Source();
            alarm.loop = true;
        }

        private static AudioSource Source()
        {
            var source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            if (Singleton<BetterAudio>.Instantiated) source.outputAudioMixerGroup = Singleton<BetterAudio>.Instance.VeryStandartMixerGroup;
            return source;
        }

        // Silence, for while the screen's black: the game has its own sounds for reloading a gun and dropping things, and nobody needs to hear them with nothing to see.
        internal static void Hush(bool on)
        {
            if (on == hushedFrom.HasValue) return;
            if (on)
            {
                hushedFrom = AudioListener.volume;
                AudioListener.volume = 0f;
            }
            else
            {
                AudioListener.volume = hushedFrom.Value;
                hushedFrom = null;
            }
        }

        private static float? hushedFrom;

        internal static AudioClip Clip(string name) => clips.TryGetValue(name, out AudioClip clip) ? clip : null;

        internal static void Play(string name)
        {
            if (!clips.TryGetValue(name, out AudioClip clip)) return;
            Ensure();
            oneShot.PlayOneShot(clip, Plugin.Volume.Value);
        }

        internal static void StartAlarm()
        {
            if (!clips.TryGetValue("alarm", out AudioClip clip)) return;
            Ensure();
            if (alarm.isPlaying) return;
            alarm.clip = clip;
            alarm.volume = Plugin.Volume.Value * .6f;
            alarm.Play();
        }

        // The siren drops once the first shots are fired, so it doesn't drown them out.
        internal static void QuietAlarm()
        {
            if (alarm != null) alarm.volume = Plugin.Volume.Value * .12f;
        }

        internal static void StopAlarm()
        {
            if (alarm != null) alarm.Stop();
        }

        internal static void Clear()
        {
            if (host != null) Object.Destroy(host);
            host = null;
        }

        private static AudioClip Read(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
                reader.ReadInt32();
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");
                int channels = 1, rate = 44100;
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    int size = reader.ReadInt32();
                    if (id == "fmt ")
                    {
                        reader.ReadInt16();
                        channels = reader.ReadInt16();
                        rate = reader.ReadInt32();
                        reader.ReadBytes(size - 8);
                    }
                    else if (id == "data")
                    {
                        byte[] bytes = reader.ReadBytes(size);
                        var samples = new float[bytes.Length / 2];
                        for (int i = 0; i < samples.Length; i++) samples[i] = System.BitConverter.ToInt16(bytes, i * 2) / 32768f;
                        var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), samples.Length / channels, channels, rate, false);
                        clip.SetData(samples, 0);
                        return clip;
                    }
                    else reader.ReadBytes(size);
                }
                throw new InvalidDataException("no data chunk");
            }
        }
    }

    /// <summary>
    /// The hideout's lights run off its generator, and defenders can switch that off. Every light goes out until someone
    /// switches it back on. Attackers then see only by the light on their gun, if they have one, so a dark hideout can be
    /// worth having if you've got night vision or a flashlight of your own. Once Black Division cut it, it stays off.
    /// </summary>
    internal sealed class SiegeLights : MonoBehaviour
    {
        // The mod's own lights aren't on the generator: flashlights, charges and blasts.
        internal const string OwnLight = "Hideout Siege";
        private readonly List<(Light Light, bool Enabled, float Intensity)> lights = new List<(Light, bool, float)>();
        // Lamps the hideout's own power doesn't reach (the rooms Hideout Extensions builds): their glowing tubes too.
        private readonly List<EFT.Interactive.LampController> lamps = new List<EFT.Interactive.LampController>();
        private static SiegeLights instance;
        private float nextCheck;

        internal static bool PowerOut => instance != null && instance.lights.Count > 0;

        internal static void CutPower()
        {
            if (PowerOut) return;
            if (instance == null) instance = new GameObject(OwnLight + " lights").AddComponent<SiegeLights>();
            foreach (var l in FindObjectsOfType<Light>())
                if (l != null && !l.name.StartsWith(OwnLight)) instance.lights.Add((l, l.enabled, l.intensity));
            HideoutCopy.TellLampsAboutPower(false);
            foreach (var lamp in FindObjectsOfType<EFT.Interactive.LampController>())
                try
                {
                    if (lamp == null || lamp.LampState != EFT.Interactive.Turnable.EState.On) continue;
                    lamp.Switch(EFT.Interactive.Turnable.EState.Off);
                    instance.lamps.Add(lamp);
                }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[siege] A lamp ({lamp?.name}) didn't switch off: {e.Message}"); }
            instance.KeepOff();
            SiegeNvg.Refresh();
            SiegeSounds.Play("powerdown");
            Plugin.Log.LogInfo($"[siege] Generator off: {instance.lights.Count} lights out.");
        }

        internal static void PowerBack()
        {
            if (!PowerOut) return;
            instance.Restore();
            HideoutCopy.TellLampsAboutPower(true);
            SiegeNvg.Refresh();
            SiegeSounds.Play("powerup");
            Plugin.Log.LogInfo("[siege] Generator on.");
        }

        internal static void Clear()
        {
            if (instance != null) Destroy(instance.gameObject);
            instance = null;
            SiegeGenerator.Reset();
        }

        // One light at the blast, rather than touching every light in the place.
        internal static IEnumerator Flash(Vector3 at)
        {
            var go = new GameObject(OwnLight + " blast");
            go.transform.position = at;
            var flash = go.AddComponent<Light>();
            flash.type = LightType.Point;
            flash.range = 18f;
            flash.shadows = LightShadows.None;
            for (float t = 0f; t < .5f; t += Time.deltaTime)
            {
                flash.color = Color.Lerp(new Color(1f, .15f, .08f), new Color(1f, .85f, .65f), t / .12f);
                flash.intensity = Mathf.Lerp(6f, 0f, t / .5f);
                yield return null;
            }
            Destroy(go);
        }

        private void KeepOff()
        {
            foreach (var (l, _, _) in lights)
            {
                if (l == null) continue;
                l.enabled = false;
                l.intensity = 0f;
            }
        }

        private void Restore()
        {
            foreach (var lamp in lamps)
                try { if (lamp != null) lamp.Switch(EFT.Interactive.Turnable.EState.On); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[siege] A lamp ({lamp?.name}) didn't switch on: {e.Message}"); }
            lamps.Clear();
            foreach (var (l, on, intensity) in lights)
            {
                if (l == null) continue;
                l.intensity = intensity;
                l.enabled = on;
            }
            lights.Clear();
        }

        // Rarely: only flickering candles and the like turn themselves back on.
        private void Update()
        {
            if (lights.Count == 0 || Time.time < nextCheck) return;
            nextCheck = Time.time + 2f;
            KeepOff();
        }

        private void OnDestroy()
        {
            Restore();
            if (instance == this) instance = null;
        }
    }

    /// <summary>The switch on the hideout's generator. Whoever flips it, it happens for everyone in the raid.</summary>
    internal static class SiegeGenerator
    {
        internal const string Station = "04_generator";

        internal static bool Built => HideoutCopy.Levels().TryGetValue(EAreaType.Generator, out int level) && level > 0;

        internal static string ActionName => SiegeLights.PowerOut ? "Turn on the generator" : "Turn off the generator";

        /// <summary>Black Division cut the power: it's off for the rest of the raid, and the switch does nothing.</summary>
        internal static bool Cut { get; private set; }

        internal static void Flip()
        {
            if (Cut) return;
            bool off = !SiegeLights.PowerOut;
            Set(off);
            Net.SendGenerator(off, false);
        }

        /// <summary>Their cut, on the host: off for good, and everyone told.</summary>
        internal static void CutForGood()
        {
            Set(true, true);
            Net.SendGenerator(true, true);
        }

        internal static void Set(bool off, bool cut = false)
        {
            if (cut) Cut = true;
            if (Cut) off = true;
            if (off) SiegeLights.CutPower();
            else SiegeLights.PowerBack();
            // Its running noise goes with it.
            foreach (var hum in Object.FindObjectsOfType<AudioSource>())
                if (hum.transform.root.name == Station) hum.enabled = !off;
        }

        internal static void Reset() => Cut = false;
    }

    /// <summary>
    /// With the power out, an attacker sees by the light on their gun or not at all: nobody's handed a flashlight. A scav
    /// without one is all but blind until the power's back, so switching the generator off is a real choice against them.
    /// </summary>
    internal static class SiegeDarkness
    {
        // Checked about once a second for each attacker (they can pick up or swap guns).
        internal static void Follow(Player player, HashSet<Player> lit)
        {
            var light = player.IsAI ? player.AIData?.BotOwner?.BotLight : null;
            if (!SiegeLights.PowerOut)
            {
                if (lit.Remove(player) && light != null)
                    try { light.IsInDarkPlace = false; } catch (System.Exception e) { Plugin.Log.LogWarning("[siege] A light didn't go off: " + e.Message); }
                return;
            }
            if (light == null || SiegeNvg.Wearing(player.AIData.BotOwner)) return;
            lit.Add(player);
            if (light.IsEnable) return;
            try
            {
                light.FindLight();
                light.IsInDarkPlace = true;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[siege] A light didn't come on: " + e.Message); }
        }

        internal static bool HasLightOn(BotOwner bot) => bot?.BotLight != null && bot.BotLight.IsEnable;
    }

    // In the dark they can't see far by their gun's light, and hardly at all without one. Night vision sees through it.
    [HarmonyLib.HarmonyPatch(typeof(LookSensor), nameof(LookSensor.CalcVisibleDistance))]
    internal static class DarkSightPatch
    {
        private const float ByTheirLight = 20f, Blind = 4f;

        [HarmonyLib.HarmonyPostfix]
        private static void Postfix(LookSensor __instance)
        {
            if (!SiegeLights.PowerOut || SiegeNvg.Wearing(__instance._botOwner)) return;
            float most = SiegeDarkness.HasLightOn(__instance._botOwner) ? ByTheirLight : Blind;
            if (__instance.VisibleDist > most) __instance.VisibleDist = most;
        }
    }
}
