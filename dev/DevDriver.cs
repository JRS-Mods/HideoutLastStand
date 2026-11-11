using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using EFT.Interactive;
using UnityEngine.UI;

namespace HideoutSiege.Dev
{
    /// <summary>Drives the game with nobody at the keyboard: silent, starts the siege from the menu, hosts through Fika, screenshots.</summary>
    [BepInPlugin("jrs.hideoutsiege.dev", "Hideout Siege test driver", "0.0.1")]
    [BepInDependency("jrs.hideoutsiege")]
    [DefaultExecutionOrder(32001)]
    public sealed class DevDriver : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private string shots;
        private int stage;
        private float nextTry, nextShot, menuSeenAt = -1, startedAt;
        private int shotIndex;

        private void Awake()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("SIEGE_ROLL"), out int roll)) HideoutCopy.NextRoll = roll;
            Log = Logger;
            shots = Path.Combine(Path.GetDirectoryName(Info.Location), IsCoopClient ? "shots_client" : "shots");
            Directory.CreateDirectory(shots);
            // Testing only: the installed Fika server is older than the client wants, which would close the game.
            var check = AccessTools.Method("Fika.Core.FikaPlugin:VerifyServerVersion");
            if (check != null) new Harmony("jrs.hideoutsiege.dev").Patch(check, new HarmonyMethod(typeof(DevDriver), nameof(Skip)));
            if (Environment.GetEnvironmentVariable("SIEGE_HITCHES") != null) TimeSuspects();
            if (WalkLog || Environment.GetEnvironmentVariable("SIEGE_SNAPS") != null)
            {
                var h = new Harmony("jrs.hideoutsiege.dev.walk");
                h.Patch(AccessTools.Method(typeof(BotMover), nameof(BotMover.SetPlayerToNavMesh)), postfix: new HarmonyMethod(typeof(DevDriver), nameof(LinkLogged)));
                h.Patch(AccessTools.Method(typeof(BotMover), "method_12"), new HarmonyMethod(typeof(DevDriver), nameof(SnapBefore)), new HarmonyMethod(typeof(DevDriver), nameof(SnapAfter)));
            }
            // SIEGE_TUNNELTRACE: who moves an attacker to the tunnel's mouth, with the call that did it.
            if (Environment.GetEnvironmentVariable("SIEGE_TUNNELTRACE") != null)
            {
                var t = new Harmony("jrs.hideoutsiege.dev.tunnel");
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(Player)))
                    if (m.Name == "Teleport" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Vector3))
                        t.Patch(m, new HarmonyMethod(typeof(DevDriver), nameof(TeleportTraced)));
            }
            // Test settings stay out of the real config file.
            Plugin.Instance.Config.SaveOnConfigSet = false;
            if (Environment.GetEnvironmentVariable("SIEGE_FAST") != null)
            {
                Plugin.WaveDelay.Value = float.TryParse(Environment.GetEnvironmentVariable("SIEGE_BREAK"), out float brk) ? brk : 3f;
                Plugin.LootTime.Value = 12f;
                Plugin.PrepTime.Value = Environment.GetEnvironmentVariable("SIEGE_TAKE") != null ? 30f : 14f;
            }
            Logger.LogInfo("Test driver running.");
        }

        private static bool Skip() => false;

        private static int traced;

        private static void TeleportTraced(Player __instance, Vector3 __0)
        {
            if (traced >= 6 || __instance == null || !__instance.IsAI || !SiegeEntries.TunnelUsable) return;
            Vector3 mouth = SiegeEntries.TunnelHole;
            if ((new Vector3(__0.x - mouth.x, 0f, __0.z - mouth.z)).sqrMagnitude > 1.2f * 1.2f) return;
            traced++;
            Log.LogInfo($"[trace] {__instance.Profile?.Info?.Settings?.Role} moved from {__instance.Position:F2} to {__0:F2}: {Environment.StackTrace}");
        }

        // ---- who a slow frame's time went on ----------------------------------------------------------------------

        private static System.Collections.Generic.Dictionary<string, double> spent = new System.Collections.Generic.Dictionary<string, double>();
        private static System.Collections.Generic.Dictionary<string, double> before = new System.Collections.Generic.Dictionary<string, double>();
        private static readonly System.Collections.Generic.Dictionary<MethodBase, string> labels = new System.Collections.Generic.Dictionary<MethodBase, string>();

        private void TimeSuspects()
        {
            var h = new Harmony("jrs.hideoutsiege.dev.timing");
            void Add(string label, MethodBase m)
            {
                if (m == null) { Logger.LogInfo($"[timing] not found: {label}"); return; }
                try
                {
                    labels[m] = label;
                    h.Patch(m, new HarmonyMethod(typeof(DevDriver), nameof(Started)), new HarmonyMethod(typeof(DevDriver), nameof(Stopped)));
                }
                catch (Exception e) { Logger.LogWarning($"[timing] {label}: {e.Message}"); }
            }
            MethodBase ByName(string type, string method)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = a.GetType(type, false); } catch (Exception) { }
                    if (t != null) return AccessTools.Method(t, method);
                }
                return null;
            }
            MethodBase ByShortName(string assembly, string typeName, string method)
            {
                var a = System.Linq.Enumerable.FirstOrDefault(AppDomain.CurrentDomain.GetAssemblies(), x => x.GetName().Name == assembly);
                if (a == null) return null;
                foreach (var t in AccessTools.GetTypesFromAssembly(a))
                    if (t.Name == typeName) { var m = AccessTools.DeclaredMethod(t, method); if (m != null) return m; }
                return null;
            }
            Add("siege Update", AccessTools.Method(typeof(Plugin), "Update"));
            Add("siege OnGUI", AccessTools.Method(typeof(Plugin), "OnGUI"));
            Add("siege CheckDoors", AccessTools.Method(typeof(SiegeDirector), "CheckDoors"));
            Add("siege CheckStuck", AccessTools.Method(typeof(SiegeDirector), "CheckStuck"));
            Add("siege Retarget", AccessTools.Method(typeof(SiegeDirector), "Retarget"));
            Add("siege turret", AccessTools.Method(typeof(SiegeTurret), "Update"));
            Add("Hideout+ Update", ByName("HideoutExtensions.Plugin", "Update"));
            Add("Interiors Update", ByName("HideoutInteriors.Plugin", "Update"));
            Add("K9 Update", ByName("K9Companion.Plugin", "Update"));
            Add("AI core (and ORBIT)", AccessTools.Method(typeof(AICoreController), "Update"));
            Add("bots controller", AccessTools.Method(typeof(BotsController), "method_0") ?? AccessTools.Method(typeof(BotsController), "Update"));
            Add("game world", AccessTools.Method(typeof(GameWorld), "Update"));
            Add("SAIN controller", ByShortName("SAIN", "SAINBotController", "Update"));
            Add("ORBIT manager", ByShortName("ORBIT", "OrbitManager", "Tick"));

            // Every plugin's Unity callbacks, coroutines and async methods, so whatever runs once a second shows up by name.
            // With SIEGE_HITCHES=all, the game's own Update and LateUpdate methods too.
            string plugins = Path.GetFullPath(BepInEx.Paths.PluginPath);
            bool game = Environment.GetEnvironmentVariable("SIEGE_HITCHES") == "all";
            var callbacks = new[] { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string where;
                try { where = a.IsDynamic ? "" : a.Location; } catch (Exception) { continue; }
                bool plugin = !string.IsNullOrEmpty(where) && Path.GetFullPath(where).StartsWith(plugins, StringComparison.OrdinalIgnoreCase)
                    && !where.Contains("HideoutSiege.Dev") && !a.GetName().Name.StartsWith("0Harmony");
                bool ours = game && a.GetName().Name == "Assembly-CSharp";
                if (!plugin && !ours) continue;
                string short_ = a.GetName().Name;
                foreach (var t in AccessTools.GetTypesFromAssembly(a))
                {
                    if (t == null || t.IsGenericTypeDefinition || t.ContainsGenericParameters || t.IsInterface) continue;
                    try
                    {
                        if (typeof(MonoBehaviour).IsAssignableFrom(t))
                            foreach (string name in plugin ? callbacks : new[] { "Update", "LateUpdate" })
                            {
                                var m = AccessTools.DeclaredMethod(t, name, Type.EmptyTypes);
                                if (m != null && !m.IsAbstract && !labels.ContainsKey(m)) Add($"{short_}:{t.Name}.{name}", m);
                            }
                        else if (plugin && (typeof(System.Collections.IEnumerator).IsAssignableFrom(t) || typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(t))
                                 && t.Name.StartsWith("<"))
                        {
                            var m = AccessTools.DeclaredMethod(t, "MoveNext", Type.EmptyTypes);
                            if (m != null && !labels.ContainsKey(m)) Add($"{short_}:{t.DeclaringType?.Name}.{t.Name.Substring(1, Math.Max(0, t.Name.IndexOf('>') - 1))} (coroutine)", m);
                        }
                    }
                    catch (Exception) { }
                }
            }
            Logger.LogInfo($"[timing] timing {labels.Count} methods.");
        }

        private static void Started(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();

        private static void Stopped(MethodBase __originalMethod, long __state)
        {
            if (!labels.TryGetValue(__originalMethod, out string label)) return;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __state) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            spent.TryGetValue(label, out double sofar);
            spent[label] = sofar + ms;
        }

        // This runs last in each frame's Update, so a slow frame's work is split between what was counted before the last
        // report and since: both are shown.
        private void ReportSpent()
        {
            if (labels.Count == 0) return;
            if (Time.unscaledDeltaTime * 1000f > 50f && SiegeLauncher.InSiege)
            {
                var both = new System.Collections.Generic.Dictionary<string, double>(spent);
                foreach (var p in before) both[p.Key] = (both.TryGetValue(p.Key, out double v) ? v : 0) + p.Value;
                double total = System.Linq.Enumerable.Sum(both.Values);
                var parts = System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(System.Linq.Enumerable.Where(both, p => p.Value >= .5), p => p.Value), 8);
                Logger.LogInfo($"[timing] {Time.unscaledDeltaTime * 1000f:F0} ms frame at {Time.time:F1} ({total:F0} ms timed): {string.Join(", ", System.Linq.Enumerable.Select(parts, p => $"{p.Key} {p.Value:F1}"))} | GC {GC.CollectionCount(0)}");
            }
            var swap = before;
            before = spent;
            spent = swap;
            spent.Clear();
        }

        // Every frame on the camera: the one nearest the front door, where he is, which way he faces, how low, and what
        // the game has him doing.
        private void LogCinematic()
        {
            if (!SiegeCinematic.Active || Environment.GetEnvironmentVariable("SIEGE_CINELOG") == null || !Singleton<IBotGame>.Instantiated) return;
            var bots = Singleton<IBotGame>.Instance.BotsController?.Bots?.BotOwners;
            if (bots == null) return;
            BotOwner planter = null;
            float best = 36f;
            foreach (var b in bots)
            {
                if (b == null || b.IsDead) continue;
                float d = (b.Position - SiegeBreach.Doorway).sqrMagnitude;
                if (d < best) { best = d; planter = b; }
            }
            if (planter == null) return;
            var p = planter.GetPlayer;
            Logger.LogInfo($"[cine] {Time.time:F3} dt={Time.deltaTime * 1000f:F0} {D(p.Position)} x={p.Position.x:F3} z={p.Position.z:F3} yaw={p.Rotation.x:F1} body={p.MovementContext.TransformRotation.eulerAngles.y:F1} pose={p.MovementContext.PoseLevel:F2} state={p.MovementContext.CurrentState?.GetType().Name}");
        }

        private static bool Watched(BotOwner bot) => bot != null && SiegeCinematic.Active && SiegeDirector.Breachers.Contains(bot);
        private static string D(Vector3 p) => $"d={Vector3.Dot(p - SiegeBreach.Doorway, SiegeBreach.Outward):F3},y={p.y:F3}";

        private static void LinkLogged(BotMover __instance, Vector3 castPoint, EBotLinkResult __result)
        {
            var bot = __instance._owner;
            if (!Watched(bot)) return;
            Log.LogInfo($"[link] {Time.time:F3} {__result} cast {D(castPoint)} body {D(bot.Position)} way {D(__instance.PositionOnWayInner)} good {D(__instance._lastGoodCastPoint)} linked {D(__instance._prevSuccessLinkedFrom)} inert {__instance.InertionOffset().magnitude:F2}");
        }

        private static void SnapBefore(BotMover __instance, out Vector3 __state) => __state = __instance._owner != null ? __instance._owner.Position : Vector3.zero;

        private static void SnapAfter(BotMover __instance, Vector3 __state)
        {
            var bot = __instance._owner;
            if (!Watched(bot) || (bot.Position - __state).sqrMagnitude < .0025f) return;
            Log.LogInfo($"[snap] {Time.time:F3} from {D(__state)} to {D(bot.Position)} path={__instance.HasPathAndNoComplete} teleport={__instance._allowTeleport}");
        }

        private float hitchWorst, hitchSum, hitchNext;
        private int hitchCount, hitchFrames;
        private SiegePhase hitchPhase;

        // Every slow frame in a siege, and how each phase went overall. No screenshots while this runs: those are slow frames too.
        // Going down by exactly who has come in means every bot came from what was loaded ahead, not from the server.
        private void LogLoadedBots()
        {
            try
            {
                var creator = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner?._botCreator;
                if (creator == null) return;
                foreach (var field in HarmonyLib.AccessTools.GetDeclaredFields(creator.GetType()))
                    if (field.GetValue(creator) is ABotProfileCreator profiles) Logger.LogInfo($"[loaded] {profiles._simpleProfiles.Count} bots left");
            }
            catch (Exception e) { Logger.LogWarning("[loaded] " + e.Message); }
        }

        private void Hitches()
        {
            if (!SiegeLauncher.InSiege) return;
            float ms = Time.unscaledDeltaTime * 1000f;
            if (ms > 50f) Logger.LogInfo($"[hitch] {ms:F0} ms at {Time.time:F1} in {SiegeState.Phase}");
            if (SiegeState.Phase != hitchPhase)
            {
                LogLoadedBots();
                if (hitchFrames > 0) Logger.LogInfo($"[frames] {hitchPhase}: {hitchFrames} frames, average {hitchSum / hitchFrames:F1} ms ({hitchFrames * 1000f / hitchSum:F0} fps), worst {hitchWorst:F0} ms, over 50 ms {hitchCount} times");
                hitchPhase = SiegeState.Phase;
                hitchWorst = hitchSum = 0f;
                hitchCount = hitchFrames = 0;
            }
            hitchFrames++;
            hitchSum += ms;
            if (ms > hitchWorst) hitchWorst = ms;
            if (ms > 50f) hitchCount++;
        }

        private static readonly bool WalkLog = Environment.GetEnvironmentVariable("SIEGE_WALKLOG") != null;
        private bool stairsScanned;

        // The stairwell from the front door outward: what the ground is made of, and where the navmesh lies on it.
        private void ScanStairs()
        {
            if (stairsScanned || !SiegeLauncher.InSiege || SiegeMap.Walkable.Count == 0) return;
            stairsScanned = true;
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK;
            var sb = new System.Text.StringBuilder("[walk] stairs from the door outward (d, ground y, collider/layer, navmesh y):");
            for (float d = 0f; d <= 7f; d += .1f)
            {
                Vector3 p = SiegeBreach.Doorway + SiegeBreach.Outward * d;
                string ground = "none";
                if (Physics.Raycast(p + Vector3.up * 4f, Vector3.down, out RaycastHit hit, 8f, mask, QueryTriggerInteraction.Ignore))
                    ground = $"{hit.point.y:F3} {hit.collider.name}/{LayerMask.LayerToName(hit.collider.gameObject.layer)} n={hit.normal.y:F2}";
                string nav = UnityEngine.AI.NavMesh.SamplePosition(p + Vector3.up * 1f, out var nh, 1.5f, UnityEngine.AI.NavMesh.AllAreas) ? nh.position.y.ToString("F3") : "-";
                sb.Append($"\n   {d:F1}  {ground}  nav {nav}");
            }
            Logger.LogInfo(sb.ToString());
            var scenes = new System.Text.StringBuilder("[walk] scenes:");
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                scenes.Append($" {sc.name}({sc.rootCount})");
            }
            var style = GameObject.Find("STYLE");
            scenes.Append(style == null ? " STYLE=none" : $" STYLE in {style.scene.name}: {string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, style.transform.childCount), i => style.transform.GetChild(i).name))}");
            var areas = FindObjectsOfType<EFT.Hideout.HideoutArea>(true);
            scenes.Append($" areas={areas.Length} withData={System.Linq.Enumerable.Count(areas, a => a.Data != null)}");
            scenes.Append($" representation={Singleton<EFT.Hideout.HideoutRepresentation>.Instantiated} extensionsRoot={(GameObject.Find("Hideout extensions") != null)}");
            Logger.LogInfo(scenes.ToString());
        }

        // Every frame of the camera shot: where the walker's feet and head are, so a bob shows as numbers.
        private void LogWalk()
        {
            if (!WalkLog || !SiegeCinematic.Active) return;
            ScanStairs();
            foreach (var bot in SiegeDirector.Breachers)
            {
                if (bot == null || bot.IsDead) continue;
                var p = bot.GetPlayer;
                float head = p.PlayerBones?.Head?.Original != null ? p.PlayerBones.Head.Original.position.y : float.NaN;
                float d = Vector3.Dot(p.Position - SiegeBreach.Doorway, SiegeBreach.Outward);
                Logger.LogInfo($"[walk] {Time.time:F3} d={d:F3} y={p.Position.y:F3} head={head:F3} v={p.Velocity.magnitude:F2} vy={p.Velocity.y:F2} grounded={p.MovementContext?.IsGrounded} pose={p.MovementContext?.PoseLevel:F2} state={p.CurrentManagedState?.Name}");
            }
        }

        // Pictures of what this batch added, taken by a camera of its own so nobody's view moves.
        private bool tunnelShot, bagsShot, nvgShot, plugShot, gateShot;
        private int noGoStep;
        private float noGoAt;
        private Vector3 noGoHome;

        // SIEGE_NOGO: during prep, puts you in the tunnel, past the cultist gate and under the floor in turn. Each time
        // the siege should put you straight back ("Kept you out of" in the log), and you end up where you started.
        private void NoGoChecks()
        {
            if (Environment.GetEnvironmentVariable("SIEGE_NOGO") == null || SiegeState.Phase != SiegePhase.Prep || SiegeCinematic.Active) return;
            var me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null) return;
            if (noGoStep == 0) { noGoStep = 1; noGoAt = Time.time + 4f; return; }
            if (Time.time < noGoAt || noGoStep > 4) return;
            noGoAt = Time.time + 2f;
            if (noGoStep > 1) Logger.LogInfo($"[nogo] After step {noGoStep - 1}: at {me.Position:F2} ({(Vector3.Distance(me.Position, noGoHome) < 1.5f ? "back where you were" : "NOT back")}).");
            if (noGoStep == 4) { noGoStep++; return; }
            noGoHome = me.Position;
            Vector3 to;
            string what;
            if (noGoStep == 1) { what = "the tunnel"; to = SiegeEntries.TunnelUsable ? SiegeEntries.TunnelHole + SiegeEntries.TunnelOut * 1f : Vector3.zero; }
            else if (noGoStep == 2) { what = "past the cultist gate"; to = SiegeEntries.GateHolding.Count > 0 ? SiegeEntries.GateHolding[0] : Vector3.zero; }
            else { what = "under the floor"; to = noGoHome - Vector3.up * 8f; }
            noGoStep++;
            if (to == Vector3.zero) { Logger.LogInfo($"[nogo] No {what} in this hideout."); return; }
            Logger.LogInfo($"[nogo] Putting you {what}: {to:F2}.");
            me.Teleport(to, true);
        }
        private float tunnelOpenedAt = -1f;

        private void ShowNewThings()
        {
            if (SiegeState.Phase == SiegePhase.Prep && !bagsShot)
            {
                bagsShot = true;
                Snap(new Vector3(-2.6f, 1.6f, -.9f), new Vector3(.3f, .8f, -2.2f), "defences_steel_front.png");
                Snap(new Vector3(2.6f, 1.7f, -3.4f), new Vector3(.3f, .8f, -2.2f), "defences_steel_back.png");
                Snap(new Vector3(-3.6f, 1.5f, -3.4f), new Vector3(-6.4f, .35f, -6.1f), "defences_wire.png");
            }
            // The tunnel's wall before it's blown, from the room: it should look like the rest of the wall.
            if (SiegeState.Phase == SiegePhase.Prep && !plugShot && SiegeEntries.TunnelUsable)
            {
                plugShot = true;
                Vector3 across = Vector3.Cross(Vector3.up, SiegeEntries.TunnelOut);
                Snap(SiegeEntries.TunnelHole - SiegeEntries.TunnelOut * 2.6f + Vector3.up * 1.5f, SiegeEntries.TunnelHole + Vector3.up * 1.1f, "tunnel_plug.png");
                Snap(SiegeEntries.TunnelHole - SiegeEntries.TunnelOut * 4.5f + across * 1.5f + Vector3.up * 1.6f, SiegeEntries.TunnelHole + Vector3.up * 1.2f, "tunnel_plug_wide.png");
            }
            if (SiegeState.Phase == SiegePhase.Prep && !gateShot && SiegeEntries.Gate != null && SiegeEntries.GateHolding.Count > 0)
            {
                gateShot = true;
                Vector3 gate = SiegeBreach.CentreOf(SiegeEntries.Gate), past = SiegeEntries.GateHolding[0];
                Vector3 back = gate + (gate - past).normalized * 1.5f;
                Snap(new Vector3(back.x, gate.y + .5f, back.z), past + Vector3.up * .8f, "cultist_gate.png");
            }
            NoGoChecks();
            if (SiegeEntries.TunnelOpen && !tunnelShot)
            {
                if (tunnelOpenedAt < 0f) tunnelOpenedAt = Time.time;
                if (Time.time - tunnelOpenedAt > 1.5f)
                {
                    tunnelShot = true;
                    Snap(SiegeEntries.TunnelInside - SiegeEntries.TunnelOut * 2.5f + Vector3.up * 1.6f, SiegeEntries.TunnelHole + Vector3.up * 1.1f, "tunnel_open.png");
                }
            }
            if (!nvgShot && SiegeLights.PowerOut && Singleton<IBotGame>.Instantiated)
            {
                var bots = Singleton<IBotGame>.Instance.BotsController?.Bots?.BotOwners;
                var wearing = bots == null ? null : System.Linq.Enumerable.FirstOrDefault(bots, b => b != null && !b.IsDead && SiegeNvg.Wearing(b));
                if (wearing != null)
                {
                    nvgShot = true;
                    var head = wearing.GetPlayer.PlayerBones.Head.Original;
                    Vector3 front = head.position + wearing.LookDirection.normalized * 1.2f + Vector3.up * .1f;
                    Snap(front, head.position, "attacker_nvg.png");
                    int count = System.Linq.Enumerable.Count(bots, b => b != null && !b.IsDead && SiegeNvg.Wearing(b));
                    Logger.LogInfo($"[nvg] {count} attackers have night vision on; pictured a {wearing.Profile.Info.Settings.Role}.");
                }
            }
        }

        private void Snap(Vector3 from, Vector3 at, string file)
        {
            try
            {
                var go = new GameObject("dev snapshot camera");
                var cam = go.AddComponent<Camera>();
                var main = EFT.CameraControl.CameraManager.Instance?.Camera ?? Camera.main;
                if (main != null) cam.CopyFrom(main);
                cam.rect = new Rect(0f, 0f, 1f, 1f);
                cam.fieldOfView = 60f;
                cam.nearClipPlane = .05f;
                go.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                File.WriteAllBytes(Path.Combine(shots, file), tex.EncodeToPNG());
                Destroy(tex);
                rt.Release();
                Destroy(go);
                Logger.LogInfo($"[snap] {file} from {from} at {at}");
            }
            catch (Exception e) { Logger.LogWarning("[snap] " + e.Message); }
        }

        private void Update()
        {
            ReportSpent();
            ShowNewThings();
            LogWalk();
            LogSpawns();
            if (Environment.GetEnvironmentVariable("SIEGE_HITCHES") != null) Hitches();
            AudioListener.volume = 0f;
            if (Environment.GetEnvironmentVariable("SIEGE_GOD") != null && !SiegeCinematic.Active)
            {
                if (Time.time >= nextGod && SiegeState.Phase != SiegePhase.None)
                {
                    nextGod = Time.time + .5f;
                    var localHealth = Singleton<GameWorld>.Instance?.MainPlayer?.ActiveHealthController;
                    try { if (localHealth != null) localHealth.SetDamageCoeff(0f); }
                    catch (Exception) { }
                }
            }
            try { Step(); }
            catch (Exception e) { Logger.LogError(e); nextTry = Time.realtimeSinceStartup + 10f; }
            LogCinematic();
            // Pictures every 0.3 s on the camera stall the game each time, which makes the walk look rough: only when asked.
            if (Environment.GetEnvironmentVariable("SIEGE_CINESHOTS") != null && SiegeCinematic.Active && nextShot - Time.realtimeSinceStartup > .3f) nextShot = Time.realtimeSinceStartup + .3f;
            if (Environment.GetEnvironmentVariable("SIEGE_HITCHES") != null || WalkLog) nextShot = float.MaxValue;
            if (Time.realtimeSinceStartup >= nextShot)
            {
                nextShot = Time.realtimeSinceStartup + (SiegeCinematic.Active ? .3f : SiegeState.Phase == SiegePhase.Prep ? 4f : 12f);
                ScreenCapture.CaptureScreenshot(Path.Combine(shots, $"{DateTime.Now:HHmmss}_{shotIndex++:000}.png"));
            }
        }

        private float scenarioNext;
        private bool stoodAtDoor, hid, died, hideoutAsked;
        private int taken;
        private float diedAt, backAt = -1;

        // Plays the part of someone at the keyboard, for what can't be checked any other way.
        private void Scenario()
        {
            var world = Singleton<GameWorld>.Instance;
            var me = world?.MainPlayer;
            if (me != null && SiegeLauncher.InSiege)
            {
                if (SiegeState.Phase == SiegePhase.Prep && !racksScanned) ScanRacks();
                if (!IsCoopClient && Environment.GetEnvironmentVariable("SIEGE_GEN") != null && (rackView < 0 || rackView >= RackViews.Length) && SiegeState.Phase == SiegePhase.Prep) TryGenerator(me);
                if (SiegeState.Phase == SiegePhase.Prep && Environment.GetEnvironmentVariable("SIEGE_TAKE") != null && Time.time >= scenarioNext) TakeNext(me);
                if (SiegeState.Phase == SiegePhase.Fighting && !reported) { reported = true; Report(me); }
                if (!bindsLogged) { bindsLogged = true; Logger.LogInfo($"[take] In the raid at first sight: {me.Inventory.FastAccess.BoundItems.Count} binds, controller {me.InventoryController.Inventory.FastAccess.BoundItems.Count}, profile {me.Profile.Inventory.FastAccess.BoundItems.Count}."); }
                float left = SiegeState.CountdownUntil - Time.time;
                if (SiegeState.Phase == SiegePhase.Prep && left < 2f && !stoodAtDoor && Environment.GetEnvironmentVariable("SIEGE_DOORSTUN") != null)
                {
                    stoodAtDoor = true;
                    me.Teleport(new Vector3(-6.2f, 0f, -5.9f), true);
                    me.Rotation = new Vector2(225f, 0f);
                    Logger.LogInfo("Standing in sight of the door.");
                }
                if (stoodAtDoor && SiegeState.Phase == SiegePhase.Fighting)
                {
                    if (stunShotsUntil == 0f) stunShotsUntil = Time.realtimeSinceStartup + 14f;
                    me.ActiveHealthController.SetDamageCoeff(0f);
                    if (Time.realtimeSinceStartup < stunShotsUntil && nextShot - Time.realtimeSinceStartup > 1.2f) nextShot = Time.realtimeSinceStartup + 1.2f;
                    if (Time.realtimeSinceStartup >= stunShotsUntil && !stunDone) { stunDone = true; Logger.LogInfo("[siege] Stun pictures done."); }
                }
                if (SiegeState.Phase == SiegePhase.Prep && !hid && Environment.GetEnvironmentVariable("SIEGE_HIDE") != null && (roomViews == null || roomView >= roomViews.Count || Environment.GetEnvironmentVariable("SIEGE_ROOMCAM") == null))
                {
                    hid = true;
                    if (Environment.GetEnvironmentVariable("SIEGE_HIDE") == "room") HideInRoom(me);
                    else Hide(me);
                }
                if (SiegeState.Phase == SiegePhase.Prep && !bagged && Environment.GetEnvironmentVariable("SIEGE_BAGS") != null)
                {
                    bagged = true;
                    Vector3 inside = SiegeBreach.Doorway - SiegeBreach.Outward * 6f;
                    Vector3 across = Vector3.Cross(Vector3.up, SiegeBreach.Outward);
                    foreach (float side in new[] { -1.4f, 1.4f })
                        if (Physics.Raycast(inside + across * side + Vector3.up, Vector3.down, out var floor, 3f, EFTHardSettings.Instance.MOVEMENT_MASK))
                            SiegeSandbags.Place(floor.point, Mathf.Atan2(-SiegeBreach.Outward.x, -SiegeBreach.Outward.z) * Mathf.Rad2Deg + 180f);
                }
                // The first time they go to breach your defences, the one going is shot down before his charge is on.
                if (!breachShot && Environment.GetEnvironmentVariable("SIEGE_STOPBREACH") != null && SiegeState.Banner == "THEY'RE BREACHING YOUR DEFENCES" && Time.time < SiegeState.BannerUntil)
                {
                    breachShot = true;
                    foreach (var b in System.Linq.Enumerable.ToList(SiegeDirector.Breachers))
                        if (b != null && !b.IsDead) { Logger.LogInfo($"[test] Shooting the {b.Profile.Info.Settings.Role} going to breach, at {b.Position}."); b.GetPlayer.ActiveHealthController.Kill(EDamageType.Bullet); }
                }
                // Takes Mannequin 1's whole loadout early in prep, then says whether what it put on still needs searching.
                if (SiegeState.Phase == SiegePhase.Prep && !died && Environment.GetEnvironmentVariable("SIEGE_MANNEQUIN") != null)
                {
                    if (mannequinAt == 0f) mannequinAt = Time.time + 3f;
                    if (!mannequinTaken && Time.time >= mannequinAt)
                    {
                        mannequinTaken = true;
                        int lying = System.Linq.Enumerable.Count(System.Linq.Enumerable.OfType<EFT.Interactive.LootItem>(Singleton<GameWorld>.Instance.LootList), l => SiegeGear.MannequinOf(l) == 1);
                        Logger.LogInfo($"[test] Taking Mannequin 1's loadout: {lying} things on it.");
                        StartCoroutine(SiegeGear.TakeLoadout(me, 1));
                    }
                    if (mannequinTaken && !searchChecked && Time.time >= mannequinAt + 5f)
                    {
                        searchChecked = true;
                        var search = me.SearchController;
                        foreach (var slot in new[] { EFT.InventoryLogic.EquipmentSlot.TacticalVest, EFT.InventoryLogic.EquipmentSlot.Backpack, EFT.InventoryLogic.EquipmentSlot.ArmorVest })
                        {
                            var worn = me.Inventory.Equipment.GetSlot(slot).ContainedItem;
                            if (worn == null) { Logger.LogInfo($"[test] {slot}: nothing on."); continue; }
                            string state = worn is EFT.InventoryLogic.SearchableItem bag ? $"searched={search.IsSearched(bag)} unknown inside={search.ContainsUnknownItems(bag)}" : "not searchable";
                            Logger.LogInfo($"[test] {slot}: {worn.ShortName.Localized()} {state}, from the mannequin={worn.Id.StartsWith(SiegeIds.GearPrefix)}.");
                        }
                    }
                }
                LogBanners();
                if (Environment.GetEnvironmentVariable("SIEGE_STASH") != null && !IsCoopClient) StashSteps(me);
                // "second": hold out in the first siege, go down in the last.
                bool dieThisOne = Environment.GetEnvironmentVariable("SIEGE_DIE") != "second" || loops <= 1;
                if (SiegeState.Phase == SiegePhase.Fighting && !died && !godAfterSecond && dieThisOne && Environment.GetEnvironmentVariable("SIEGE_DIE") != null)
                {
                    if (diedAt == 0f) diedAt = Time.time + 3f;
                    if (Time.time >= diedAt)
                    {
                        died = true;
                        Logger.LogInfo("Dying.");
                        me.ActiveHealthController.SetDamageCoeff(1f);
                        me.ActiveHealthController.Kill(EDamageType.Bullet);
                    }
                }
            }
            if (Environment.GetEnvironmentVariable("SIEGE_OPENHIDEOUT") != null && stage == 0 && backAt > 0f && !hideoutSeen && Time.realtimeSinceStartup - backAt > 8f)
            {
                hideoutAsked = true;
                // Nothing happens if the results screens are still closing, so keep asking.
                backAt = Time.realtimeSinceStartup + 25f;
                var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
                Logger.LogInfo("Opening the hideout.");
                AccessTools.Method(typeof(MainMenuShowOperation), "method_24").Invoke(Traverse.Create(app).Field<MainMenuShowOperation>("_menuOperation").Value, new object[] { null });
            }
            if (hideoutAsked && world is HideoutGameWorld && Environment.GetEnvironmentVariable("SIEGE_OPENHIDEOUT") != null && !hideoutSeen)
            {
                hideoutSeen = true;
                Logger.LogInfo("[siege] THE HIDEOUT OPENED.");
            }
            if (hideoutSeen && !hideoutChecked)
            {
                if (hideoutCheckAt == 0f) hideoutCheckAt = Time.realtimeSinceStartup + 40f;
                if (Time.realtimeSinceStartup >= hideoutCheckAt)
                {
                    hideoutChecked = true;
                    ScreenCapture.CaptureScreenshot(Path.Combine(shots, "hideout.png"));
                    Logger.LogInfo($"[siege] HIDEOUT CHECK: root screen {EFT.UI.Screens.EftScreenManager.Instance.RootScreenType}, player {world?.MainPlayer?.Position}.");
                }
            }
            if (hideoutChecked && Environment.GetEnvironmentVariable("SIEGE_SECOND") != null && Time.realtimeSinceStartup >= hideoutCheckAt + 3f && !secondAsked)
            {
                secondAsked = true;
                var app2 = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
                Logger.LogInfo("Back to the main menu from the hideout.");
                _ = Traverse.Create(app2).Field<MainMenuShowOperation>("_menuOperation").Value.TryOpenMainMenu();
            }
            if (secondAsked && !secondLaunched && Time.realtimeSinceStartup >= hideoutCheckAt + 14f)
            {
                secondLaunched = true;
                Logger.LogInfo("Launching a second siege.");
                SiegeLauncher.Launch();
                backAt = -1f;
                godAfterSecond = true;
                stage = 1;
                startedAt = Time.realtimeSinceStartup;
            }
        }

        // ---- tonight's checks: announcements, the stash, loot in rack bags --------------------------------------------

        private string lastBanner = "";

        // Each announcement, with where whoever's breaching is, and how far that is from the nearest of your defences and doors.
        private void LogBanners()
        {
            string banner = Time.time < SiegeState.BannerUntil ? SiegeState.Banner : "";
            if (banner == lastBanner) return;
            lastBanner = banner;
            if (banner.Length == 0) return;
            var sb = new System.Text.StringBuilder($"[test] Banner '{banner}' in {SiegeState.Phase}.");
            foreach (var b in System.Linq.Enumerable.ToList(SiegeDirector.Breachers))
            {
                if (b == null || b.IsDead) continue;
                float defence = float.MaxValue, door = float.MaxValue;
                foreach (var d in SiegeDecor.Standing()) defence = Mathf.Min(defence, Vector3.Distance(d.position, b.Position));
                foreach (var (_, centre) in SiegeBreach.InnerDoors()) door = Mathf.Min(door, Vector3.Distance(centre, b.Position));
                sb.Append($" Breacher {b.Profile.Info.Settings.Role} at {b.Position:F1}: {defence:F1} m from a defence, {door:F1} m from a door.");
            }
            Logger.LogInfo(sb.ToString());
        }

        private int stashStep;
        private float stashAt;
        private SiegePhase stashPhase;

        // In prep: Open stash as you would, put something in, picture it, close it; put something in a bag from the racks.
        // After holding out: something more in the stash.
        private void StashSteps(Player me)
        {
            if (SiegeState.Phase != stashPhase)
            {
                stashPhase = SiegeState.Phase;
                if (stashPhase == SiegePhase.Prep || stashPhase == SiegePhase.Won) { stashStep = 0; stashAt = Time.time + (stashPhase == SiegePhase.Prep ? 4f : 1.5f); }
                else stashStep = 99;
            }
            if (stashStep >= 99 || Time.time < stashAt) return;
            var owner = me.GetComponent<GamePlayerOwner>();
            switch (stashStep)
            {
                case 0:
                {
                    var area = System.Linq.Enumerable.FirstOrDefault(FindObjectsOfType<EFT.Hideout.HideoutArea>(), a => a.AreaTemplate != null && a.AreaTemplate.Type == EAreaType.Stash);
                    var state = area == null ? null : SiegeStations.ActionsFor(area, owner);
                    Logger.LogInfo($"[test] Stash station: {(area == null ? "none" : "found")}, actions: {(state?.Actions == null ? state?.Error ?? "none" : string.Join(", ", System.Linq.Enumerable.Select(state.Actions, a => a.Name)))}.");
                    var open = state?.Actions == null ? null : System.Linq.Enumerable.FirstOrDefault(state.Actions, a => a.Name == "Open stash");
                    if (open == null) { stashStep = 99; return; }
                    open.Action();
                    stashStep = 1;
                    stashAt = Time.time + 1.5f;
                    return;
                }
                case 1:
                    StartCoroutine(PutAway(me, StashBox(), "the stash"));
                    stashStep = 2;
                    stashAt = Time.time + 2f;
                    return;
                case 2:
                    ScreenCapture.CaptureScreenshot(Path.Combine(shots, $"stash_{stashPhase}_{loops}.png"));
                    stashStep = 3;
                    stashAt = Time.time + 1f;
                    return;
                case 3:
                    owner.CloseInventoryIfOpen();
                    Logger.LogInfo($"[test] Closed the stash. In it now: {string.Join(", ", System.Linq.Enumerable.Select(SiegeStash.Contents(), i => i.ShortName.Localized()))}.");
                    stashStep = stashPhase == SiegePhase.Prep ? 4 : 99;
                    stashAt = Time.time + 1f;
                    return;
                case 4:
                {
                    // A bag or rig from the racks, still lying where it was put, with room for what's to go in it.
                    var bags = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(System.Linq.Enumerable.OfType<EFT.Interactive.LootItem>(Singleton<GameWorld>.Instance.LootList),
                        l => SiegeGear.IsRackGear(l) && l.Item is EFT.InventoryLogic.CompoundItem c && c.Grids != null && c.Grids.Length > 0 && ReferenceEquals(l.Item.Owner, l.ItemOwner)));
                    var item = NextToPutAway(me);
                    var bag = item == null ? null : System.Linq.Enumerable.FirstOrDefault(bags, l => EFT.InventoryLogic.ItemManipulator.QuickFindAppropriatePlace(item, me.InventoryController,
                        new[] { (EFT.InventoryLogic.CompoundItem)l.Item }, EFT.InventoryLogic.ItemManipulator.EMoveItemOrder.PrioritizeTargetsOrder, true).Failed == false);
                    Logger.LogInfo($"[test] Bags from the racks: {bags.Count}; one with room for {item?.ShortName.Localized() ?? "nothing"}: {(bag == null ? "none" : bag.Item.ShortName.Localized())}.");
                    if (bag != null) StartCoroutine(PutAway(me, bag, "a bag from the racks"));
                    stashStep = 99;
                    return;
                }
            }
        }

        private static EFT.Interactive.LootItem StashBox() =>
            System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<EFT.Interactive.LootItem>(Singleton<GameWorld>.Instance.LootList), l => l.ItemId == SiegeIds.StashBox);

        // Something from your pockets (or else a magazine off the racks) into it, as a quick move would.
        private System.Collections.IEnumerator PutAway(Player me, EFT.Interactive.LootItem into, string what)
        {
            if (into == null || !(into.Item is EFT.InventoryLogic.CompoundItem container)) { Logger.LogInfo($"[test] Nothing to put things in for {what}."); yield break; }
            var controller = me.InventoryController;
            var item = NextToPutAway(me);
            if (item == null) { Logger.LogInfo($"[test] Nothing to put in {what}."); yield break; }
            string name = item.ShortName.Localized();
            if (what != "the stash")
            {
                bool answered = false;
                me.Interact(into.ItemOwner, _ => answered = true);
                float wait = Time.time + 1f;
                while (!answered && Time.time < wait) yield return null;
            }
            var move = EFT.InventoryLogic.ItemManipulator.QuickFindAppropriatePlace(item, controller, new[] { container }, EFT.InventoryLogic.ItemManipulator.EMoveItemOrder.PrioritizeTargetsOrder, true);
            if (move.Failed) { Logger.LogWarning($"[test] Couldn't find room for {name} in {what}: {move.Error}"); yield break; }
            if (!controller.CanExecute(move.Value)) { Logger.LogWarning($"[test] Not allowed to put {name} in {what}."); yield break; }
            controller.RunNetworkTransaction(move.Value, null);
            float giveUp = Time.time + 2f;
            while (Time.time < giveUp && !Inside(item, container)) yield return null;
            Logger.LogInfo($"[test] Put {name} ({item.Id}) in {what}: {(Inside(item, container) ? "it's in" : "it isn't")}.");
        }

        // Something from your pockets, or else a magazine off the racks.
        private static EFT.InventoryLogic.Item NextToPutAway(Player me)
        {
            var pockets = me.Inventory.Equipment.GetSlot(EFT.InventoryLogic.EquipmentSlot.Pockets).ContainedItem as EFT.InventoryLogic.CompoundItem;
            var item = pockets == null ? null : System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.SelectMany(pockets.Grids, g => g.Items));
            return item ?? System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Select(System.Linq.Enumerable.OfType<EFT.Interactive.LootItem>(Singleton<GameWorld>.Instance.LootList),
                l => SiegeGear.IsRackGear(l) && l.Item is EFT.InventoryLogic.Magazine && ReferenceEquals(l.Item.Owner, l.ItemOwner) ? l.Item : null), i => i != null);
        }

        private static bool Inside(EFT.InventoryLogic.Item item, EFT.InventoryLogic.Item container)
        {
            var up = item.Parent?.Container?.ParentItem;
            for (int depth = 0; up != null && depth < 12; depth++, up = up.Parent?.Container?.ParentItem)
                if (up == container) return true;
            return false;
        }

        private int genStep;
        private float genNext;
        private EFT.Hideout.HideoutInteractionTrigger genTrigger;

        // Stands in front of the generator, looks at it, and uses what the interaction ray finds.
        private void TryGenerator(Player me)
        {
            if (genStep > 4 || Time.time < genNext) return;
            var owner = (Singleton<AbstractGame>.Instance as BaseLocalGame<EftGamePlayerOwner>)?.PlayerOwner;
            if (owner == null) return;
            switch (genStep)
            {
                case 0:
                    foreach (var t in FindObjectsOfType<EFT.Hideout.HideoutInteractionTrigger>())
                    {
                        if (t.transform.root.name != SiegeGenerator.Station) continue;
                        var c = t.GetComponent<Collider>();
                        Logger.LogInfo($"[gen] Trigger {t.name} at {t.transform.position} collider {(c != null ? c.bounds.center + " size " + c.bounds.size + " on=" + c.enabled + " trig=" + c.isTrigger + " L" + c.gameObject.layer : "none")} active={t.gameObject.activeInHierarchy}");
                        if (genTrigger == null && t.gameObject.activeInHierarchy && c != null) genTrigger = t;
                    }
                    if (genTrigger == null) { Logger.LogError("[gen] No trigger on the generator."); genStep = 9; return; }
                    Vector3 at = genTrigger.GetComponent<Collider>().bounds.center;
                    Vector3 from = new Vector3(at.x, 0f, at.z + 1.6f);
                    me.Teleport(from, true);
                    Vector3 look = at - (from + Vector3.up * 1.45f);
                    me.Rotation = new Vector2(Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, -Mathf.Asin(look.normalized.y) * Mathf.Rad2Deg);
                    Logger.LogInfo($"[gen] Standing at {from}, looking at {at}.");
                    genNext = Time.time + .8f;
                    genStep = 1;
                    return;
                case 1:
                case 3:
                    var state = InteractionContextHelper.GetAvailableActions(owner, genTrigger);
                    var action = state?.Actions == null ? null : System.Linq.Enumerable.FirstOrDefault(state.Actions);
                    Logger.LogInfo($"[gen] Ray finds {me.InteractableObject?.GetType().Name} {(me.InteractableObject as Component)?.name}; action '{action?.Name}'; power out {SiegeLights.PowerOut}.");
                    action?.Action();
                    Logger.LogInfo($"[gen] After using it: power out {SiegeLights.PowerOut}.");
                    genNext = Time.time + 2f;
                    genStep++;
                    if (Environment.GetEnvironmentVariable("SIEGE_GEN_STAY") != null) genStep = 9;
                    return;
                case 2:
                    ScreenCapture.CaptureScreenshot(Path.Combine(shots, "gen_off.png"));
                    genNext = Time.time + 1f;
                    genStep = 3;
                    return;
                case 4:
                    ScreenCapture.CaptureScreenshot(Path.Combine(shots, "gen_on.png"));
                    genStep = 5;
                    return;
            }
        }

        private bool racksScanned;

        private void ScanRacks()
        {
            racksScanned = true;
            var lines = new System.Collections.Generic.List<string>();
            foreach (var point in Resources.FindObjectsOfTypeAll<EFT.Hideout.StashItemModelSpawnPoint>())
            {
                if (!point.gameObject.scene.IsValid()) continue;
                string path = point.name;
                for (var u = point.transform.parent; u != null; u = u.parent) path = u.name + "/" + path;
                lines.Add($"{point.GetType().Name} active={point.gameObject.activeInHierarchy} pos={point.transform.position} rot={point.transform.eulerAngles} {path}");
            }
            lines.Sort();
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Info.Location), "racks.txt"), lines);
            Logger.LogInfo($"[siege] Scanned {lines.Count} rack spots.");
        }

        private bool hideoutSeen, hideoutChecked, reported, bindsLogged, stunDone;
        private float stunShotsUntil;
        private float hideoutCheckAt;
        private bool secondAsked, secondLaunched, godAfterSecond, mannequinTaken, searchChecked;
        private float mannequinAt;
        private int launchTries;
        private float nextNotReady, readyShotAt = -1f;
        private bool readyShot, breachShot;

        private void Report(Player me)
        {
            var eq = me.InventoryController.Inventory;
            Logger.LogInfo("[take] Binds now: " + string.Join(", ", System.Linq.Enumerable.Select(eq.FastAccess.BoundItems, b => b.Key + "=" + b.Value?.Name.Localized())));
            foreach (var slot in new[] { EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon, EFT.InventoryLogic.EquipmentSlot.SecondPrimaryWeapon, EFT.InventoryLogic.EquipmentSlot.Holster, EFT.InventoryLogic.EquipmentSlot.ArmorVest, EFT.InventoryLogic.EquipmentSlot.TacticalVest, EFT.InventoryLogic.EquipmentSlot.Backpack, EFT.InventoryLogic.EquipmentSlot.Headwear })
            {
                var item = eq.Equipment.GetSlot(slot).ContainedItem;
                string state = item is EFT.InventoryLogic.Weapon w ? $" chamber={(w.HasChambers && w.Chambers[0].ContainedItem != null)} mag={w.GetCurrentMagazine()?.Count}/{w.GetCurrentMagazine()?.MaxCount}" : "";
                Logger.LogInfo($"[take] {slot}: {item?.Name.Localized() ?? "EMPTY"}{state}");
            }
        }

        private EFT.Interactive.LootItem aimingAt;
        private float aimedAt;
        private readonly System.Collections.Generic.HashSet<EFT.Interactive.LootItem> tried = new System.Collections.Generic.HashSet<EFT.Interactive.LootItem>();

        // Walks up to each thing it laid out, looks at it, and uses what the interaction ray finds: what a player would get.
        private void TakeNext(Player me)
        {
            var owner = (Singleton<AbstractGame>.Instance as BaseLocalGame<EftGamePlayerOwner>)?.PlayerOwner;
            if (owner == null) return;
            if (aimingAt != null)
            {
                if (Time.time < aimedAt) return;
                var loot = aimingAt;
                aimingAt = null;
                scenarioNext = Time.time + .9f;
                bool hit = me.InteractableObject == loot;
                var actions = InteractionContextHelper.GetAvailableActions(owner, loot);
                var take = actions?.Actions == null ? null : System.Linq.Enumerable.FirstOrDefault(actions.Actions, a => a.Name == "Take");
                Logger.LogInfo($"[take] {loot.Item.Name.Localized()} at {loot.transform.position}: ray {(hit ? "HIT" : "MISSED, found " + me.InteractableObject)}, Take {(take != null ? "offered" : "not offered")}.");
                take?.Action();
                taken++;
                return;
            }
            foreach (var loot in FindObjectsOfType<EFT.Interactive.LootItem>())
            {
                if (loot.Item == null || tried.Contains(loot) || loot.LastOwner?.ProfileId != me.ProfileId) continue;
                tried.Add(loot);
                var solid = loot.GetComponentInChildren<Collider>();
                Vector3 at = solid != null ? solid.bounds.center : loot.transform.position;
                Vector3 from = new Vector3(at.x, 0f, at.z);
                if (Mathf.Abs(at.x + 2.7f) < .3f) from.x += 1f;        // the first rack, from its open side
                else if (Mathf.Abs(at.x + 3.4f) < .3f) from.x -= 1f;   // the second rack
                else from += new Vector3(.7f, 0f, .7f);                // the gear stand, from the front
                me.Teleport(from, true);
                Vector3 look = at - (from + Vector3.up * 1.45f);
                me.Rotation = new Vector2(Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, -Mathf.Asin(look.normalized.y) * Mathf.Rad2Deg);
                aimingAt = loot;
                aimedAt = Time.time + .5f;
                return;
            }
        }

        // Past the Emergency Wall's door, with it shut behind you.
        private bool bagged;

        // In the deepest of Hideout Extensions' rooms (the furthest walk from the front door), with every one of its doors shut.
        private void HideInRoom(Player me)
        {
            var path = new UnityEngine.AI.NavMeshPath();
            Vector3 from = SiegeBreach.Doorway - SiegeBreach.Outward * 1.5f;
            var root = GameObject.Find("Hideout extensions");
            string wanted = Environment.GetEnvironmentVariable("SIEGE_HIDE_ROOM") ?? "Annex far room";
            Vector3 best = default;
            float longest = 0f;
            if (root != null)
                foreach (Transform room in root.transform)
                {
                    if (!room.name.StartsWith("Extension room ") || room.name.Contains("tunnel")) continue;
                    Vector3 middle = room.position + new Vector3(2f, 0f, 2f);
                    if (!UnityEngine.AI.NavMesh.SamplePosition(middle, out var hit, 2f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    // The one named is taken if it has floor: shut doors on the way cut the path, which is the point.
                    if (room.name == "Extension room " + wanted) { longest = 10000f; best = hit.position; break; }
                    if (!UnityEngine.AI.NavMesh.CalculatePath(from, hit.position, UnityEngine.AI.NavMesh.AllAreas, path) || path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) continue;
                    float length = 0f;
                    for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                    if (length > longest) { longest = length; best = hit.position; }
                }
            if (longest <= 0f) { Logger.LogError("[door] No extension room to hide in."); return; }
            int shut = 0;
            foreach (var d in FindObjectsOfType<EFT.Interactive.Door>())
                if (d.transform.root.name == "Hideout extensions") { d.SetDoorState(EDoorState.Shut, true); d.CurrentAngle = d.GetAngle(EDoorState.Shut); shut++; }
            me.Teleport(best, true);
            Logger.LogInfo($"[door] Hiding at {best} in an extension room {longest % 10000f:F0} m from the front door{(longest >= 10000f ? " (" + wanted + ")" : "")}, {shut} doors shut.");
        }

        private void Hide(Player me)
        {
            var doors = FindObjectsOfType<EFT.Interactive.Door>();
            foreach (var d in doors) Logger.LogInfo($"[door] {d.transform.parent?.name} {d.transform.position} {d.DoorState}");
            var door = System.Linq.Enumerable.FirstOrDefault(doors, d => (d.transform.position - new Vector3(6.32f, 1.06f, .38f)).sqrMagnitude < 2f);
            if (door == null) { Logger.LogError("[door] No Emergency Wall door."); return; }
            door.SetDoorState(EDoorState.Shut, true);
            door.CurrentAngle = door.GetAngle(EDoorState.Shut);
            me.Teleport(new Vector3(9f, 0f, 3.5f), true);
            Logger.LogInfo($"[door] Hiding at {me.Position} behind {door.name}, {door.DoorState}.");
        }

        private void Step()
        {
            Scenario();
            if (Time.realtimeSinceStartup < nextTry) return;
            nextTry = Time.realtimeSinceStartup + 1f;
            switch (stage)
            {
                case 0:
                    var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
                    if (app == null || Traverse.Create(app).Field<MainMenuShowOperation>("_menuOperation").Value == null) return;
                    if (backAt > 0f) return;
                    if (menuSeenAt < 0)
                    {
                        menuSeenAt = Time.realtimeSinceStartup;
                        Logger.LogInfo("Menu is up. Invading.");
                        SiegeBackend.Trigger();
                        MenuAlert.Refresh();
                        return;
                    }
                    if (Time.realtimeSinceStartup - menuSeenAt < 22f) return;
                    if (shotIndex >= 0 && !bannerShot)
                    {
                        bannerShot = true;
                        ScreenCapture.CaptureScreenshot(Path.Combine(shots, "banner.png"));
                        return;
                    }
                    if (Time.realtimeSinceStartup - menuSeenAt < 25f) return;
                    if (!SiegeLauncher.MenuReady)
                    {
                        if (Time.realtimeSinceStartup >= nextNotReady) { nextNotReady = Time.realtimeSinceStartup + 10f; Logger.LogInfo("[test] The menu isn't ready for a launch yet."); }
                        return;
                    }
                    // A picture of the menu as it's ready (after a defeat, with what they took on it), a moment before launching.
                    if (readyShotAt < 0f)
                    {
                        readyShotAt = Time.realtimeSinceStartup;
                        return;
                    }
                    if (Time.realtimeSinceStartup - readyShotAt < 2f) return;
                    if (!readyShot)
                    {
                        readyShot = true;
                        ScreenCapture.CaptureScreenshot(Path.Combine(shots, $"menu_ready_{loops}.png"));
                        Logger.LogInfo("[test] The menu's ready: pictured it.");
                        return;
                    }
                    string normal = Environment.GetEnvironmentVariable("SIEGE_NORMAL");
                    if (normal != null)
                    {
                        Logger.LogInfo("Launching a normal raid on " + normal);
                        var op = Traverse.Create(app).Field<MainMenuShowOperation>("_menuOperation").Value;
                        var loc = System.Linq.Enumerable.First(app.Session.LocationSettings.locations.Values, l => l.Id == normal);
                        foreach (var rs in new[] { app.CurrentRaidSettings, Traverse.Create(op).Field<RaidSettings>("raidSettings_1").Value })
                        {
                            if (rs == null) continue;
                            rs.Side = ESideType.Pmc;
                            rs.SelectedLocation = loc;
                            rs.RaidMode = ERaidMode.Local;
                            rs.IsPveOffline = true;
                        }
                        AccessTools.Method(typeof(MainMenuShowOperation), "method_52").Invoke(op, null);
                    }
                    else
                    {
                        Logger.LogInfo("Launching the siege.");
                        Logger.LogInfo($"[take] At the menu: {app.Session.Profile.Inventory.FastAccess.BoundItems.Count} binds.");
                        if (int.TryParse(Environment.GetEnvironmentVariable("SIEGE_SECURITY"), out int security))
                        {
                            var layout = new System.Collections.Generic.Dictionary<EAreaType, int>(HideoutCopy.Levels());
                            layout[EAreaType.Security] = security;
                            HideoutCopy.HostLayout = layout;
                        }
                        SiegeLauncher.Launch();
                    }
                    stage = IsCoopClient ? 5 : 1;
                    startedAt = Time.realtimeSinceStartup;
                    return;
                case 5:
                    if (JoinThroughFika()) stage = 3;
                    return;
                case 1:
                    if (HostThroughFika()) { stage = 2; launchTries = 0; Logger.LogInfo("Hosted."); }
                    // The menu can still be reloading the profile after a raid (longer after a death) and drop the launch: try again.
                    else if (Time.realtimeSinceStartup - startedAt > 30f && launchTries < 3)
                    {
                        launchTries++;
                        Logger.LogInfo($"The raid screen didn't open: launching again ({launchTries}).");
                        SiegeLauncher.Launch();
                        startedAt = Time.realtimeSinceStartup;
                    }
                    else if (Time.realtimeSinceStartup - startedAt > 60f) { Logger.LogError("The Fika host screen never appeared."); stage = 9; }
                    return;
                case 2:
                    var go = GameObject.Find("FikaStartButton");
                    if (go == null) return;
                    if (Environment.GetEnvironmentVariable("SIEGE_WAIT_PEERS") != null && Peers() < 1 && Time.realtimeSinceStartup - startedAt < 420f) return;
                    go.GetComponent<EFT.UI.DefaultUIButton>().OnClick.Invoke();
                    Logger.LogInfo("Pressed Start Raid.");
                    stage = 3;
                    return;
                case 3:
                    Telemetry();
                    // Searching the scene for the results screen costs ~50 ms in the hideout: only once the raid's over.
                    if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null && Singleton<GameWorld>.Instance.MainPlayer != null) return;
                    if (loops > 1 && (Environment.GetEnvironmentVariable("SIEGE_RESULTS") == "next" ? StepResults() : ClickThroughResults())) { pictured.Clear(); loops--; stage = 0; menuSeenAt = -1; bannerShot = false; readyShot = false; readyShotAt = -1f; Logger.LogInfo("Back to the menu for another siege."); }
                    else if (loops <= 1 && backAt < 0f && Environment.GetEnvironmentVariable("SIEGE_OPENHIDEOUT") != null
                        && (Environment.GetEnvironmentVariable("SIEGE_RESULTS") == "next" ? StepResults() : ClickThroughResults())) { stage = 0; backAt = Time.realtimeSinceStartup; menuSeenAt = 1e9f; Logger.LogInfo("Back to the menu."); }
                    return;
            }
        }

        private float nextTelemetry, nextGod;
        private int loops = int.TryParse(Environment.GetEnvironmentVariable("SIEGE_LOOPS"), out int n) ? n : 1;

        // Through the end-of-raid screens as a player goes, with NEXT on each, picturing each one (the siege's defeat screen too).
        private float nextStep;
        private readonly System.Collections.Generic.HashSet<string> pictured = new System.Collections.Generic.HashSet<string>();
        private static readonly (EFT.UI.Screens.EEftScreenType Screen, Type Type)[] ResultScreens =
        {
            (EFT.UI.Screens.EEftScreenType.ExitStatus, typeof(EFT.UI.SessionEnd.SessionResultExitStatus)),
            (EFT.UI.Screens.EEftScreenType.KillList, typeof(EFT.UI.SessionEnd.SessionResultKillList)),
            (EFT.UI.Screens.EEftScreenType.SessionStatistics, typeof(EFT.UI.SessionEnd.SessionResultStatistics)),
            (EFT.UI.Screens.EEftScreenType.SessionExperience, typeof(EFT.UI.SessionEnd.SessionResultExperienceCount)),
            (EFT.UI.Screens.EEftScreenType.HealthTreatment, typeof(EFT.UI.SessionEnd.HealthTreatmentScreen)),
        };

        private bool StepResults()
        {
            if (Time.realtimeSinceStartup < nextStep) return false;
            nextStep = Time.realtimeSinceStartup + 3f;
            if (SiegeDefeatScreen.Showing)
            {
                if (pictured.Add("defeat")) { ScreenCapture.CaptureScreenshot(Path.Combine(shots, "after_defeat_screen.png")); Logger.LogInfo("[test] The defeat screen's up: pictured it."); return false; }
                Logger.LogInfo("[test] NEXT on the defeat screen.");
                SiegeDefeatScreen.Next();
                return false;
            }
            var screens = EFT.UI.Screens.EftScreenManager.Instance;
            foreach (var (screen, type) in ResultScreens)
            {
                if (screens == null || !screens.CheckCurrentScreen(screen)) continue;
                if (pictured.Add(type.Name)) { ScreenCapture.CaptureScreenshot(Path.Combine(shots, $"after_{type.Name}.png")); Logger.LogInfo($"[test] On {type.Name}: pictured it."); return false; }
                var shown = FindObjectOfType(type);
                var button = shown == null ? null : AccessTools.Field(type, "_nextButton")?.GetValue(shown) as EFT.UI.DefaultUIButton;
                if (button == null) { Logger.LogWarning($"[test] No NEXT on {type.Name}."); return false; }
                Logger.LogInfo($"[test] NEXT on {type.Name}.");
                button.OnClick.Invoke();
                return false;
            }
            return screens != null && screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.MainMenu);
        }

        private bool ClickThroughResults()
        {
            var results = FindObjectOfType<EFT.UI.SessionEnd.SessionResultExitStatus>();
            if (results == null) return false;
            var button = AccessTools.Field(typeof(EFT.UI.SessionEnd.SessionResultExitStatus), "_mainMenuButton")?.GetValue(results) as EFT.UI.DefaultUIButton;
            if (button == null) { Logger.LogWarning("No main menu button found."); return false; }
            button.OnClick.Invoke();
            return true;
        }
        private bool godded, bannerShot, canvasesLogged;
        private static readonly string AutoKill = Environment.GetEnvironmentVariable("SIEGE_AUTOKILL");
        private readonly System.Collections.Generic.Dictionary<BotOwner, float> seen = new System.Collections.Generic.Dictionary<BotOwner, float>();

        private void Telemetry()
        {
            var world = Singleton<GameWorld>.Instance;
            if (world == null || world.MainPlayer == null) return;
            var me = world.MainPlayer;
            if (Environment.GetEnvironmentVariable("SIEGE_GOD") != null && me.ActiveHealthController != null && !SiegeCinematic.Active)
            {
                me.ActiveHealthController.SetDamageCoeff(0f);
                if (!godded) Logger.LogInfo("God mode for testing.");
                godded = true;
            }
            if (SiegeCinematic.Active && !canvasesLogged)
            {
                canvasesLogged = true;
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                    if (c.isActiveAndEnabled && c.gameObject.scene.IsValid() && c.isRootCanvas) Logger.LogInfo($"[siege] canvas {c.name} parent={c.transform.parent?.name} order={c.sortingOrder} mode={c.renderMode} root={c.transform.root.name}");
            }
            if (Time.realtimeSinceStartup < nextTelemetry || !Singleton<IBotGame>.Instantiated) return;
            nextTelemetry = Time.realtimeSinceStartup + 8f;
            var bots = Singleton<IBotGame>.Instance.BotsController?.Bots?.BotOwners;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[siege] t phase={SiegeState.Phase} wave={SiegeState.Wave}/{SiegeState.TotalWaves} left={SiegeState.Remaining} fps={1f / Time.smoothDeltaTime:F0} me={me.Position} hp={me.ActiveHealthController?.GetBodyPartHealth(EBodyPart.Common).Current:F0}");
            if (bots != null)
            {
                foreach (var b in System.Linq.Enumerable.ToList(bots))
                {
                    if (b == null || b.IsDead) continue;
                    string goal = "";
                    try { goal = b.Memory?.GoalEnemy != null ? $"enemy(vis={b.Memory.GoalEnemy.IsVisible})" : "noenemy"; } catch (Exception) { goal = "?"; }
                    string layer = "", act = "";
                    try { layer = b.Brain?.ActiveLayerName(); act = b.Brain?.GetActiveNodeReason(); } catch (Exception) { layer = "?"; }
                    sb.Append($"\n   {b.Profile.Info.Settings.Role} d={Vector3.Distance(b.Position, me.Position):F1} pos={b.Position} {goal} layer={layer} act={act}");
                    if (!seen.ContainsKey(b)) seen[b] = Time.realtimeSinceStartup;
                    if (AutoKill != null && !SiegeDirector.Waiting.Contains(b) && Time.realtimeSinceStartup - seen[b] > float.Parse(AutoKill))
                        b.GetPlayer.ActiveHealthController.Kill(EDamageType.Bullet);
                }
            }
            Logger.LogInfo(sb.ToString());
        }

        private static bool IsCoopClient => Environment.GetEnvironmentVariable("SIEGE_ROLE") == "client";

        private static int Peers()
        {
            Type type = AccessTools.TypeByName("Fika.Core.Networking.FikaServer");
            var singleton = AccessTools.Property(typeof(Singleton<>).MakeGenericType(type), "Instance")?.GetValue(null);
            var net = singleton == null ? null : AccessTools.Property(type, "NetServer")?.GetValue(singleton);
            return net == null ? 0 : (int)AccessTools.Property(net.GetType(), "ConnectedPeersCount").GetValue(net);
        }

        private bool joining;

        // Joins the first raid listed, the same way the Join button in Fika's server browser does.
        private bool JoinThroughFika()
        {
            if (joining) return false;
            Type script = AccessTools.TypeByName("Fika.Core.UI.Custom.MatchMakerUIScript");
            var instance = script == null ? null : FindObjectOfType(script);
            if (instance == null) return false;
            Type requests = AccessTools.TypeByName("Fika.Core.Networking.Http.FikaRequestHandler");
            var settings = AccessTools.Field(script, "RaidSettings")?.GetValue(instance) ?? AccessTools.Property(script, "RaidSettings").GetValue(instance);
            var matches = (Array)AccessTools.Method(requests, "LocationRaids").Invoke(null, new[] { settings });
            if (matches == null || matches.Length == 0) { nextTry = Time.realtimeSinceStartup + 5f; Logger.LogInfo("No raid to join yet."); return false; }
            object entry = matches.GetValue(0);
            string serverId = AccessTools.Field(entry.GetType(), "ServerId")?.GetValue(entry)?.ToString() ?? AccessTools.Property(entry.GetType(), "ServerId").GetValue(entry).ToString();
            Type utils = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
            string location = (AccessTools.Field(entry.GetType(), "Location")?.GetValue(entry) ?? AccessTools.Property(entry.GetType(), "Location").GetValue(entry)).ToString();
            AccessTools.Property(utils, "HostLocationId")?.SetValue(null, location);
            AccessTools.Field(utils, "HostLocationId")?.SetValue(null, location);
            var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
            string profileId = app.Session.Profile.ProfileId;
            joining = true;
            Logger.LogInfo($"Joining raid hosted by {serverId}.");
            var task = (System.Threading.Tasks.Task<bool>)AccessTools.Method(script, "JoinMatch").Invoke(null, new object[] { profileId, serverId, null, false });
            task.ContinueWith(t =>
            {
                joined = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result;
                Log.LogInfo($"Join result: {joined} {t.Exception?.Message}");
                joinAccept = instance;
            });
            return false;
        }

        private bool joined;
        private object joinAccept;

        // A look at each rack during Prep.
        private int rackView = -1;
        private float rackViewUntil;
        private bool rackShot;
        private static readonly (Vector3 From, Vector3 To)[] RackViews =
        {
            (new Vector3(-1.6f, 1.5f, 5.6f), new Vector3(-2.7f, 1.5f, 5.5f)),
            (new Vector3(-4.6f, 1.5f, 4.0f), new Vector3(-3.4f, 1.5f, 4.0f)),
            (new Vector3(4.6f, 1.5f, 3.3f), new Vector3(3.43f, .4f, 2.29f)),
        };

        private void RackCam()
        {
            if (Environment.GetEnvironmentVariable("SIEGE_RACKCAM") == null) return;
            if (rackView < 0)
            {
                if (SiegeState.Phase == SiegePhase.Prep && Environment.GetEnvironmentVariable("SIEGE_TAKE") == null) rackView = 0;
                else return;
            }
            if (rackView >= RackViews.Length) return;
            var cam = EFT.CameraControl.CameraManager.Instance?.Camera ?? Camera.main;
            if (cam == null) return;
            var view = RackViews[rackView];
            cam.transform.SetPositionAndRotation(view.From, Quaternion.LookRotation(view.To - view.From));
            if (rackViewUntil == 0f) rackViewUntil = Time.time + 1.4f;
            if (!rackShot && Time.time > rackViewUntil - .5f)
            {
                rackShot = true;
                ScreenCapture.CaptureScreenshot(Path.Combine(shots, $"rack_{rackView}.png"));
            }
            if (Time.time >= rackViewUntil)
            {
                rackView++; rackViewUntil = 0f; rackShot = false;
                if (rackView == 1)
                    foreach (var loot in FindObjectsOfType<EFT.Interactive.LootItem>())
                        if (loot.Item != null && loot.transform.position.y > -100f && loot.transform.position.z > -8f && loot.transform.position.z < 900f)
                            Logger.LogInfo($"[rack] Loose: {loot.Item.Name.Localized()} at {loot.transform.position} kinematic={(loot.RigidBody != null ? loot.RigidBody.isKinematic.ToString() : "none")}");
            }
        }

        private float friendCamUntil;
        private bool friendDone;

        // In co-op: a good look at the other player, and how their body's set up.
        private void FriendCam()
        {
            if (Environment.GetEnvironmentVariable("SIEGE_FRIENDCAM") == null || SiegeState.Phase != SiegePhase.Prep) return;
            var world = Singleton<GameWorld>.Instance;
            var other = world == null ? null : System.Linq.Enumerable.FirstOrDefault(world.AllAlivePlayersList, p => p != null && !p.IsYourPlayer && p.Profile.Info.Side != EPlayerSide.Savage);
            if (other == null) return;
            var cam = EFT.CameraControl.CameraManager.Instance?.Camera ?? Camera.main;
            if (cam == null) return;
            if (friendCamUntil == 0f)
            {
                friendCamUntil = Time.time + 2f;
                var parts = other.GetComponentsInChildren<Renderer>(true);
                var body = new System.Collections.Generic.List<Renderer>();
                other.PlayerBody.GetRenderersNonAlloc(body);
                var follower = other.GetComponent<FollowerCullingObject>();
                Logger.LogInfo($"[friend] {other.GetType().Name} at {other.Position}: renderers {parts.Length}, body {body.Count}, body forced off {System.Linq.Enumerable.Count(body, r => r.forceRenderingOff)}, follower {(follower == null ? "none" : "on=" + follower.enabled + " visible=" + follower.IsVisible)}, IsVisible {other.IsVisible}");
                Invoke(nameof(FriendShot), 1f);
            }
            if (Time.time >= friendCamUntil) return;
            Vector3 at = other.Position + Vector3.up;
            Vector3 from = at + other.Transform.forward * 2f + Vector3.up * .2f;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
        }

        private void FriendShot() => ScreenCapture.CaptureScreenshot(Path.Combine(shots, "friend.png"));

        // ---- a look round the rooms other mods built into the hideout ---------------------------------------------

        private System.Collections.Generic.List<(string Name, Vector3 From, Vector3 To)> roomViews;
        private int roomView = -1;
        private float roomViewUntil;
        private bool roomShot;

        // Every room Hideout Extensions put in: how much of its floor the attackers can walk, and a picture across it.
        private void RoomCam()
        {
            if (Environment.GetEnvironmentVariable("SIEGE_ROOMCAM") == null || SiegeState.Phase != SiegePhase.Prep || SiegeCinematic.Active) return;
            if (roomViews == null)
            {
                roomViews = new System.Collections.Generic.List<(string, Vector3, Vector3)>();
                var api = System.Linq.Enumerable.FirstOrDefault(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "HideoutExtensions")?.GetType("HideoutExtensions.HideoutExtensionsApi");
                var rooms = api?.GetProperty("Rooms")?.GetValue(null) as System.Collections.IEnumerable;
                var sb = new System.Text.StringBuilder("[rooms] walkable floor in each built room:");
                foreach (object room in rooms ?? new object[0])
                {
                    var t = room.GetType();
                    var root = t.GetProperty("Root").GetValue(room) as Transform;
                    var size = (Vector3)t.GetProperty("Size").GetValue(room);
                    object spot = t.GetProperty("Spot").GetValue(room);
                    string name = spot.GetType().GetProperty("Name")?.GetValue(spot) as string ?? spot.GetType().GetField("Name")?.GetValue(spot) as string ?? "?";
                    if (root == null) continue;
                    Vector3 c = root.position;
                    int walk = System.Linq.Enumerable.Count(SiegeMap.Walkable, w => w.x >= c.x && w.x <= c.x + size.x && w.z >= c.z && w.z <= c.z + size.z && Mathf.Abs(w.y - c.y) < 1f);
                    sb.Append($"\n   {name}: {walk * .04f:F0} of {size.x * size.z:F0} m2");
                    if (!name.Contains("tunnel")) roomViews.Add((name, c + new Vector3(.5f, 1.7f, .5f), c + new Vector3(size.x, .9f, size.z)));
                }
                Logger.LogInfo(sb.ToString());
                if (SiegeEntries.TunnelUsable)
                    roomViews.Add(("tunnel plug", SiegeEntries.TunnelInside + Vector3.up * 1.6f - SiegeEntries.TunnelOut * 1.5f, SiegeEntries.TunnelHole + Vector3.up * 1.1f));
                roomViews.Add(("front door", SiegeBreach.Doorway - SiegeBreach.Outward * 3f + Vector3.up * 1.6f, SiegeBreach.Doorway + Vector3.up * 1f));
                roomViews.Add(("sandbags", SiegeBreach.Doorway - SiegeBreach.Outward * 9.5f + Vector3.up * 1.8f, SiegeBreach.Doorway - SiegeBreach.Outward * 6f + Vector3.up * .4f));
                roomView = 0;
            }
            if (roomView < 0 || roomView >= roomViews.Count) return;
            var cam = EFT.CameraControl.CameraManager.Instance?.Camera ?? Camera.main;
            if (cam == null) return;
            var view = roomViews[roomView];
            cam.transform.SetPositionAndRotation(view.From, Quaternion.LookRotation(view.To - view.From));
            if (roomViewUntil == 0f) roomViewUntil = Time.time + 1.2f;
            if (!roomShot && Time.time > roomViewUntil - .4f)
            {
                roomShot = true;
                ScreenCapture.CaptureScreenshot(Path.Combine(shots, $"room_{roomView}_{view.Name.Replace(' ', '_')}.png"));
            }
            if (Time.time >= roomViewUntil) { roomView++; roomViewUntil = 0f; roomShot = false; }
        }

        // ---- what's where at the top of the stairs, and when attackers arrive ----------------------------------------

        private int lastBots = -1;

        private void LogSpawns()
        {
            if (!SiegeLauncher.InSiege || !Singleton<IBotGame>.Instantiated) return;
            var owners = Singleton<IBotGame>.Instance.BotsController?.Bots?.BotOwners; int count = owners == null ? 0 : System.Linq.Enumerable.Count(owners);
            if (count != lastBots) Logger.LogInfo($"[spawned] {Time.time:F2} bots={count} phase={SiegeState.Phase}");
            lastBots = count;
        }

        private System.Collections.Generic.List<(string Name, Vector3 From, Vector3 To)> scanViews;
        private int scanView = -1;
        private float scanViewUntil;
        private bool scanShot;

        // Everything gate-like near the top of the front stairs and in the cultist circle's area, and a look round from the landing.
        private void StairsScan()
        {
            if (Environment.GetEnvironmentVariable("SIEGE_SCAN") == null || SiegeState.Phase != SiegePhase.Prep || SiegeCinematic.Active) return;
            if (scanViews == null)
            {
                Vector3 top = SiegeBreach.Doorway + SiegeBreach.Outward * 5.5f + Vector3.up * 1.43f;
                var words = new[] { "gate", "door", "grid", "bar", "fence", "lattice", "lock", "cage", "grate", "net", "cultist", "circle", "wall" };
                var sb = new System.Text.StringBuilder($"[scan] near the top of the stairs ({top:F2}):");
                foreach (var t in FindObjectsOfType<Transform>(true))
                {
                    if (t == null || (t.position - top).sqrMagnitude > 18f * 18f) continue;
                    string n = t.name.ToLowerInvariant();
                    if (!System.Linq.Enumerable.Any(words, w => n.Contains(w))) continue;
                    var col = t.GetComponent<Collider>();
                    var area = t.GetComponentInParent<EFT.Hideout.HideoutArea>(true);
                    sb.Append($"\n   {t.name} at {t.position:F2} active={t.gameObject.activeInHierarchy} layer={LayerMask.LayerToName(t.gameObject.layer)} col={(col != null ? col.GetType().Name + (col.enabled ? "" : "(off)") + " " + col.bounds.size.ToString("F2") : "-")} door={(t.GetComponent<EFT.Interactive.WorldInteractiveObject>() != null)} area={(area != null ? area.AreaTemplate?.Type.ToString() : "-")} path={PathOf(t)}");
                }
                Logger.LogInfo(sb.ToString());
                var doors = new System.Text.StringBuilder("[scan] every door:");
                foreach (var d in FindObjectsOfType<EFT.Interactive.WorldInteractiveObject>(true))
                    doors.Append($"\n   {d.GetType().Name} {d.name} id={d.Id} at {d.transform.position:F2} state={d.DoorState} active={d.gameObject.activeInHierarchy} path={PathOf(d.transform)}");
                Logger.LogInfo(doors.ToString());
                var cult = System.Linq.Enumerable.FirstOrDefault(FindObjectsOfType<EFT.Hideout.HideoutArea>(true), a => a.AreaTemplate != null && a.AreaTemplate.Type == EAreaType.CircleOfCultists);
                if (cult != null)
                {
                    var c = new System.Text.StringBuilder($"[scan] cultist circle {cult.name} at {cult.transform.position:F2}:");
                    foreach (Transform child in cult.GetComponentsInChildren<Transform>(true))
                        if (child.parent == cult.transform || (child.parent != null && child.parent.parent == cult.transform) || (child.parent?.parent?.parent == cult.transform))
                            c.Append($"\n   {PathOf(child)} at {child.position:F2} active={child.gameObject.activeSelf}");
                    Logger.LogInfo(c.ToString());
                }
                Vector3 eye = top + Vector3.up * 1.6f, side = Vector3.Cross(Vector3.up, SiegeBreach.Outward);
                scanViews = new System.Collections.Generic.List<(string, Vector3, Vector3)>
                {
                    ("up", eye, eye + SiegeBreach.Outward * 5f),
                    ("left", eye, eye + side * 5f),
                    ("right", eye, eye - side * 5f),
                    ("down", eye, eye - SiegeBreach.Outward * 5f - Vector3.up * 1f),
                    ("above", eye + Vector3.up * .8f - SiegeBreach.Outward * 1.5f, eye + SiegeBreach.Outward * 4f - Vector3.up * 1.2f),
                };
                scanView = 0;
            }
            if (scanView < 0 || scanView >= scanViews.Count) return;
            var cam = EFT.CameraControl.CameraManager.Instance?.Camera ?? Camera.main;
            if (cam == null) return;
            var view = scanViews[scanView];
            cam.transform.SetPositionAndRotation(view.From, Quaternion.LookRotation(view.To - view.From));
            if (scanViewUntil == 0f) scanViewUntil = Time.time + 1f;
            if (!scanShot && Time.time > scanViewUntil - .4f) { scanShot = true; ScreenCapture.CaptureScreenshot(Path.Combine(shots, $"stairs_{scanView}_{view.Name}.png")); }
            if (Time.time >= scanViewUntil) { scanView++; scanViewUntil = 0f; scanShot = false; }
        }

        private static string PathOf(Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; t != null && i < 6; t = t.parent, i++) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private void LateUpdate()
        {
            StairsScan();
            RoomCam();
            RackCam();
            FriendCam();
            if (joinAccept == null) return;
            object instance = joinAccept;
            joinAccept = null;
            if (!joined) { joining = false; return; }
            var accept = (AccessTools.Field(instance.GetType(), "AcceptButton")?.GetValue(instance)) as EFT.UI.DefaultUIButton;
            accept?.OnClick.Invoke();
            stage = 3;
            Logger.LogInfo("Joined.");
        }

        private bool HostThroughFika()
        {
            Type script = AccessTools.TypeByName("Fika.Core.UI.Custom.MatchMakerUIScript");
            if (script == null)
            {
                var screen = FindObjectOfType<EFT.UI.Matchmaker.MatchMakerAcceptScreen>();
                if (screen == null || Time.realtimeSinceStartup - startedAt < 4f) return false;
                Logger.LogInfo("No Fika: pressing Ready.");
                ((EFT.UI.DefaultUIButton)AccessTools.Field(typeof(EFT.UI.Matchmaker.MatchMakerAcceptScreen), "_acceptButton").GetValue(screen)).OnClick.Invoke();
                stage = 3;
                return false;
            }
            var instance = FindObjectOfType(script);
            if (instance == null || Time.realtimeSinceStartup - startedAt < 4f) return false;
            object ui = AccessTools.Field(script, "_fikaMatchMakerUi").GetValue(instance);
            if (ui == null) return false;
            var field = AccessTools.Field(ui.GetType(), "StartButton");
            var button = (field != null ? field.GetValue(ui) : AccessTools.Property(ui.GetType(), "StartButton").GetValue(ui)) as Button;
            if (button == null) return false;
            button.onClick.Invoke();
            return true;
        }
    }
}
