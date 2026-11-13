using System;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// Gives the siege's camera the bloom effect a raid's camera has, for HollywoodFX and HollywoodGraphics.
    ///
    /// A raid map's camera comes with one (UltimateBloom), sitting among its effects just before the one that turns the
    /// picture from HDR into what's shown. The hideout's camera is a different one and has none. HollywoodGraphics adds one
    /// when it's missing, but a new one has none of its lists made yet, so setting it up failed; and an effect added to a
    /// camera goes last, after the picture has been handed to the screen, where this one draws black.
    ///
    /// So the siege's camera is given one before either mod looks for it, set up as the game's own are. It's kept switched
    /// off, which only stops Unity drawing it last, and is drawn from here at the place a raid's camera has it.
    /// </summary>
    internal static class SiegeBloom
    {
        // The game's own camera with this effect on it that can be loaded by name: the raid maps' one is only there with a map.
        private const string CameraWithBloom = "Cam2_ARENA_FIX";
        private const int GivesUpAfter = 3;

        private static UltimateBloom bloom;
        private static PrismEffects before;
        private static SSAAPropagator frames;
        private static bool draws;
        private static int failed;

        internal static void Hook(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted)), prefix: new HarmonyMethod(typeof(SiegeBloom), nameof(Add)));
                harmony.Patch(AccessTools.Method(typeof(PrismEffects), nameof(PrismEffects.OnRenderImage)), prefix: new HarmonyMethod(typeof(SiegeBloom), nameof(Draw)));
                Plugin.Log.LogInfo("[compat] Handling HollywoodFX's bloom on the siege's camera.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[compat] HollywoodFX's bloom: " + e.Message); }
        }

        /// <summary>HollywoodGraphics is about to set the bloom up, so it's one to be seen. With HollywoodFX alone it's only
        /// there to be found: that mod turns the lens dust on it up after a blast, and would fail every frame without one.</summary>
        internal static void Wanted()
        {
            draws = bloom != null && before != null && frames != null;
            if (draws) Plugin.Log.LogInfo("[compat] HollywoodGraphics' bloom is drawn on the siege's camera.");
        }

        // Before the raid's start is told to anyone: both mods look for the effect as it is.
        private static void Add(GameWorld __instance)
        {
            bloom = null;
            before = null;
            frames = null;
            draws = false;
            failed = 0;
            try
            {
                if (!SiegeLauncher.IsSiege(__instance.LocationId)) return;
                Camera cam = CameraManager.Instance?.Camera;
                if (cam == null || cam.GetComponent<UltimateBloom>() != null) return;
                var added = cam.gameObject.AddComponent<UltimateBloom>();
                added.enabled = false;
                var model = Resources.Load<GameObject>(CameraWithBloom);
                var settings = model != null ? model.GetComponent<UltimateBloom>() : null;
                if (settings != null) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(settings), added);
                // Makes the lists a new one lacks. A camera's own has them from the start.
                added.CreateMaterials();
                // Not run by Unity on one that's switched off: where it finds the night vision it dims itself for.
                added.Start();
                bloom = added;
                before = cam.GetComponent<PrismEffects>();
                frames = cam.GetComponent<SSAAPropagator>();
                Plugin.Log.LogInfo($"[compat] The siege's camera has a bloom effect for HollywoodFX ({(settings != null ? "set up as the game's" : "with default settings")}).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[compat] HollywoodFX's bloom: " + e.Message); }
        }

        // Just before the picture is turned from HDR into what's shown, which is where a raid's camera has it.
        private static void Draw(PrismEffects __instance)
        {
            if (!draws || !ReferenceEquals(__instance, before)) return;
            if (bloom == null || frames == null) { draws = false; return; }
            try
            {
                // Nothing to draw on until the frame's been taken in, as on the first one.
                if (!frames.GetSourceDestination(out RenderTexture from, out RenderTexture to) || from == null || to == null) return;
                bloom.OnRenderImage(from, to);
                failed = 0;
            }
            catch (Exception e)
            {
                if (++failed < GivesUpAfter) return;
                draws = false;
                Plugin.Log.LogWarning("[compat] HollywoodGraphics' bloom isn't drawn in this siege: " + e.Message);
            }
        }
    }
}
