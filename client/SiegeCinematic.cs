using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// The opening: a security camera on the front door. Attackers come down the stairs and set a charge on it. The feed
    /// cuts out, and you wake up in bed with the alarm going. It's the same on every machine and can be skipped with Space.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class SiegeCinematic : MonoBehaviour
    {
        // Long enough on the door for him to come down, set the charge, turn round and go back up out of the picture.
        private const float CameraSeconds = 10.5f, CutSeconds = .6f, WakeSeconds = 3.2f;

        internal static bool Active;
        private const float GearGivesUp = 8f, LayoutGivesUp = 20f;
        private static SiegeCinematic current;
        private Camera cam;
        private Vector3 fromPos, toPos, fromLook, toLook;
        private float t, seconds, nextHold;
        private bool skip;

        internal static IEnumerator Play()
        {
            yield return GetReady();
            // On another player's game it could still be going, from before the camera was called for.
            float giveUp = Time.time + GearGivesUp;
            while (!SiegeGear.Finished && Time.time < giveUp) yield return null;
            if (!Plugin.Cinematic.Value)
            {
                SiegeSounds.Hush(false);
                SiegeHud.Black = 0f;
                SiegeSounds.StartAlarm();
                yield break;
            }
            var go = new GameObject("Hideout Siege Cinematic");
            current = go.AddComponent<SiegeCinematic>();
            Active = true;
            GameUiVisible(false);
            HoldPlayers(true);
            // The game stays silent for as long as the camera's not yours: a security camera has no microphone, and what
            // you'd hear is your own body in bed (guns being put on racks) and the attackers' feet on the stairs. The siege's
            // own sounds (the feed cutting out, the alarm) aren't the game's, and play through it.
            yield return current.Run();
            SiegeSounds.Hush(false);
            SiegeSounds.StartAlarm();
            HoldPlayers(false);
            GameUiVisible(true);
            Active = false;
            SiegeHud.SecurityCam = false;
            SiegeHud.Static = 0f;
            Destroy(go);
            current = null;
        }

        // The slow, noisy part of the start, for the black screen: sealing the place up, and clearing your gear on to the racks.
        internal static IEnumerator GetReady()
        {
            SiegeBreach.Seal();
            SiegeBreach.MoveToBed();
            yield return SiegeGear.LayOut();
        }

        // Another player's game can't set anything up until the host says what the hideout looks like.
        internal static IEnumerator GetReadyWhenBuilt()
        {
            float giveUp = Time.time + LayoutGivesUp;
            while (HideoutCopy.HostLayout == null && Time.time < giveUp) yield return null;
            if (HideoutCopy.HostLayout != null) yield return GetReady();
        }

        private IEnumerator Run()
        {
            cam = EFT.CameraControl.CameraManager.Instance?.Camera ?? Camera.main;
            if (cam == null) yield break;
            yield return SecurityCamera();
            yield return WakeUp();
            SiegeHud.Black = 0f;
        }

        // Up at the top of the stairwell, looking down at the door. It's only lit like this for the camera.
        private IEnumerator SecurityCamera()
        {
            Vector3 door = SiegeBreach.Doorway + Vector3.up * .95f;
            // Pushing in slowly, so you can see what he's doing at the door.
            fromPos = SiegeBreach.Doorway + SiegeBreach.Outward * 3.5f + Vector3.up * 3.6f;
            toPos = SiegeBreach.Doorway + SiegeBreach.Outward * 2.5f + Vector3.up * 2.9f;
            fromLook = door + new Vector3(.25f, 0f, -.25f);
            toLook = door + new Vector3(-.25f, 0f, .25f);
            seconds = CameraSeconds;
            var lamp = new GameObject("Hideout Siege camera lamp").AddComponent<Light>();
            lamp.transform.position = SiegeBreach.Doorway + SiegeBreach.Outward * 2f + Vector3.up * 2.2f;
            lamp.type = LightType.Point;
            lamp.range = 8f;
            lamp.intensity = .5f;
            lamp.color = new Color(.9f, 1f, .9f);
            lamp.shadows = LightShadows.None;
            SiegeHud.SecurityCam = true;
            SiegeHud.Black = 0f;
            for (t = 0f; t < CameraSeconds && !skip; t += Time.deltaTime) yield return Frame();
            if (!skip) SiegeSounds.Play("static");
            for (float cut = 0f; cut < CutSeconds && !skip; cut += Time.deltaTime)
            {
                SiegeHud.Static = cut / CutSeconds;
                yield return Frame();
            }
            Destroy(lamp.gameObject);
            SiegeHud.SecurityCam = false;
            SiegeHud.Static = 0f;
        }

        // Flat on your back, then eyes opening, then up on your feet looking down the hideout.
        private IEnumerator WakeUp()
        {
            SiegeHud.Black = 1f;
            SiegeSounds.StartAlarm();
            if (skip) yield break;
            var me = Singleton<GameWorld>.Instance?.MainPlayer;
            Vector3 stand = (me != null ? SiegeBreach.WakeSpot(me) : SiegeBreach.Pillow) + Vector3.up * 1.55f;
            Vector3 toDoor = SiegeBreach.Doorway - stand;
            toDoor.y = 0f;
            fromPos = SiegeBreach.Pillow;
            fromLook = SiegeBreach.PillowLook;
            toPos = stand;
            toLook = stand + toDoor.normalized * 5f - Vector3.up * .2f;
            seconds = WakeSeconds;
            for (t = 0f; t < WakeSeconds && !skip; t += Time.deltaTime)
            {
                // Two heavy blinks.
                float k = t / WakeSeconds;
                SiegeHud.Black = k < .12f ? 1f : k < .3f ? Mathf.Lerp(1f, .25f, (k - .12f) / .18f) : k < .42f ? Mathf.Lerp(.25f, .9f, (k - .3f) / .12f) : k < .7f ? Mathf.Lerp(.9f, 0f, (k - .42f) / .28f) : 0f;
                yield return Frame();
            }
        }

        private object Frame()
        {
            if (Input.GetKeyDown(KeyCode.Space)) skip = true;
            return null;
        }

        private void LateUpdate()
        {
            // Repeated because Fika resets this itself as a raid starts. Not every frame: each call goes out over the network.
            if (Time.time >= nextHold) { nextHold = Time.time + .5f; HoldPlayers(true); HideMyself(); }
            // The battle UI fades itself back in as the raid starts, so keep it down.
            GameUiVisible(false);
            if (cam == null || seconds <= 0f) return;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / seconds));
            Vector3 pos = Vector3.Lerp(fromPos, toPos, k);
            cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.Lerp(fromLook, toLook, k) - pos));
        }

        private readonly List<Renderer> hidden = new List<Renderer>();

        // The camera's outside your head, so your own body would be in shot.
        private void HideMyself()
        {
            var me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null) return;
            foreach (var part in me.GetComponentsInChildren<Renderer>())
            {
                if (part.forceRenderingOff) continue;
                part.forceRenderingOff = true;
                hidden.Add(part);
            }
        }

        private void OnDestroy()
        {
            SiegeHud.Black = 0f;
            SiegeSounds.Hush(false);
            foreach (var part in hidden) if (part != null) part.forceRenderingOff = false;
        }

        private static CanvasGroup[] faded;

        // Faded rather than switched off: the battle UI keeps updating, and it errors if its text isn't being drawn.
        private static void GameUiVisible(bool visible)
        {
            try
            {
                if (faded == null)
                {
                    var groups = new List<CanvasGroup>();
                    if (MonoBehaviourSingleton<CommonUI>.Instantiated) groups.Add(Group(MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen.gameObject));
                    if (MonoBehaviourSingleton<GameUI>.Instantiated) groups.Add(Group(MonoBehaviourSingleton<GameUI>.Instance.gameObject));
                    faded = groups.ToArray();
                }
                foreach (var g in faded) if (g != null) g.alpha = visible ? 1f : 0f;
                if (visible) faded = null;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
        }

        private static CanvasGroup Group(GameObject go) => go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();

        // Nobody can be hurt while they can't see or move.
        private static void HoldPlayers(bool hold)
        {
            foreach (var p in SiegeDirector.Defenders())
                try { p.ActiveHealthController?.SetDamageCoeff(hold ? 0f : 1f); }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
        }
    }

    // While the camera isn't yours, neither are the controls.
    [HarmonyPatch(typeof(GamePlayerOwner), nameof(GamePlayerOwner.TranslateAxes))]
    internal static class HoldAxesPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref float[] axes)
        {
            if (!SiegeCinematic.Active) return true;
            if (axes != null) for (int i = 0; i < axes.Length; i++) axes[i] = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(GamePlayerOwner), nameof(GamePlayerOwner.TranslateCommand))]
    internal static class HoldCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref EFT.InputSystem.InputNode.ETranslateResult __result)
        {
            // And once you're on your way out: Fika's already let go of your player's networking, so opening the
            // inventory and moving things then throws partway through.
            if (!SiegeCinematic.Active && !SiegeExit.Closing) return true;
            __result = EFT.InputSystem.InputNode.ETranslateResult.Block;
            return false;
        }
    }
}
