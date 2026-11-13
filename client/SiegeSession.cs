using System.Collections.Generic;
using System.Linq;
using EFT.InventoryLogic;
using System;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>Starts the siege's pieces when a siege raid begins, and clears them when it ends.</summary>
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal static class GameStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GameWorld __instance)
        {
            if (!SiegeLauncher.IsSiege(__instance.LocationId)) return;
            try
            {
                Plugin.Log.LogInfo("[siege] Raid started.");
                SiegeHud.Black = 1f;
                SiegeSounds.Hush(true);
                SiegeCharge.Load();
                SiegeDefences.Load();
                Plugin.Instance.StartCoroutine(StopWaitingInTheDark());
                SiegeCompat.Apply();
                // On another player's machine the walls wait until the host says which ones it has.
                if (Net.IsClient)
                {
                    SiegeMap.PrepareToWatch(HideoutCopy.HostLayout != null);
                    Plugin.Instance.StartCoroutine(SiegeCinematic.GetReadyWhenBuilt());
                }
                else
                {
                    // Normally in already, read on the loading screen; this is for when that didn't happen.
                    _ = HideoutCopy.InstallCustomization();
                    SiegeDirector.Begin();
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[siege] " + e); }
        }

        // The screen stays dark until the cinematic starts, unless a host without this mod means it never will.
        private static System.Collections.IEnumerator StopWaitingInTheDark()
        {
            yield return new WaitForSeconds(30f);
            if (SiegeState.Phase != SiegePhase.None) yield break;
            SiegeHud.Black = 0f;
            SiegeSounds.Hush(false);
        }
    }

    [HarmonyPatch]
    internal static class RackGearReportPatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<System.Reflection.MethodBase> Targets() => AccessTools.GetTypesFromAssembly(typeof(GameWorld).Assembly)
            .Where(t => !t.IsInterface && !t.IsAbstract && typeof(IEftSession).IsAssignableFrom(t))
            .Select(t => AccessTools.DeclaredMethod(t, "LocalRaidEnded"))
            .Where(m => m != null && m.GetMethodBody() != null).Cast<System.Reflection.MethodBase>();

        [HarmonyPrefix]
        private static void Prefix()
        {
            if (SiegeLauncher.InSiege) SiegeBackend.ReportRackGear();
        }
    }

    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.Dispose))]
    internal static class GameEndPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GameWorld __instance)
        {
            MenuAlert.Refresh();
            if (!SiegeLauncher.IsSiege(__instance.LocationId)) return;
            SiegeCinematic.Active = false;
            SiegeExit.Reset();
            HideoutCopy.HostLayout = null;
            SiegeOwner.Reset();
            SiegeState.Reset();
            SiegeHud.Black = SiegeHud.Static = 0f;
            SiegeHud.SecurityCam = false;
            SiegeBreach.Reset();
            SiegeEntries.Reset();
            SiegeNoGo.Reset();
            SiegeGear.Reset();
            SiegeDefences.Reset();
            SiegeSounds.Hush(false);
            SiegeSounds.Clear();
            SiegeLights.Clear();
            SiegeDirector.End();
            SiegeMap.Clear();
            SiegeLauncher.Restore();
            HideoutCopy.Release();
            MenuAlert.Refresh();
        }
    }

    // Backing out of the raid screens to the main menu puts your raid settings back.
    [HarmonyPatch(typeof(MainMenuShowOperation), nameof(MainMenuShowOperation.ScreenChangeHandler))]
    internal static class BackToMenuPatch
    {
        [HarmonyPostfix]
        private static void Postfix(EFT.UI.Screens.EEftScreenType eftScreenType)
        {
            MenuAlert.Screen = eftScreenType;
            if (eftScreenType == EFT.UI.Screens.EEftScreenType.MainMenu) SiegeLauncher.Restore();
        }
    }

    internal static class SiegeExit
    {
        private static bool leaving;
        // Goes up each raid, so a check left waiting from the last one knows it's not its raid any more.
        private static int raid;

        // From the moment the game's told to end the raid: the controls are held from then on (see HoldCommandPatch).
        internal static bool Closing { get; private set; }

        internal static void Reset()
        {
            leaving = Closing = false;
            raid++;
        }

        // How long the game gets to take you out before it's taken to have got stuck partway.
        private const float StuckAfter = 40f;
        private static readonly System.Reflection.FieldInfo Dragging = AccessTools.Field(typeof(EFT.UI.ItemUiContext), "_dragItemContext");

        // Ends the raid. Fika won't let a host go before the people it's hosting, so the host waits for them to go first.
        internal static void Leave(ExitStatus status)
        {
            if (leaving) return;
            leaving = true;
            Plugin.Instance.StartCoroutine(LeaveWhenClear(status));
        }

        private static System.Collections.IEnumerator LeaveWhenClear(ExitStatus status)
        {
            // A Fika headless ends the raid itself once everyone's left, and has no player of its own to leave with.
            if (SiegeOwner.OnHeadless) { Plugin.Log.LogInfo($"[siege] The siege is over ({status}): the headless ends the raid once everyone's left."); yield break; }
            float giveUp = Time.time + 30f;
            while (Net.IsHost && Net.PeerCount > 0 && Time.time < giveUp) yield return new WaitForSeconds(.5f);
            if (Net.IsHost && Net.HadPeers) yield return new WaitForSeconds(5f);
            yield return PutThingsDown();
            int mine = raid;
            var world = Singleton<GameWorld>.Instance;
            string exitName = status == ExitStatus.Survived ? "Hideout defended" : null;
            var game = Singleton<AbstractGame>.Instance as BaseLocalGame<EftGamePlayerOwner>;
            try
            {
                if (game == null) { Plugin.Log.LogWarning("[siege] No game to leave."); yield break; }
                Plugin.Log.LogInfo($"[siege] Leaving the raid: {status}.");
                Closing = true;
                game.Stop(game.PlayerOwner.Player.ProfileId, status, exitName);
            }
            catch (Exception e) { Plugin.Log.LogError("[siege] Leaving failed: " + e); }

            // Leaving's a fade to black, then the game shutting things down. If that throws partway, it's left half gone:
            // marked as leaving, so it turns down being asked again, and nothing to finish it. Then it's finished from here.
            float until = Time.time + StuckAfter;
            while (Time.time < until)
            {
                if (raid != mine) yield break;
                yield return new WaitForSeconds(1f);
            }
            if (raid != mine || world == null || Singleton<GameWorld>.Instance != world || game == null || game.Status == GameStatus.Stopped) yield break;
            Plugin.Log.LogWarning($"[siege] Still in the raid {StuckAfter:0}s after leaving ({game.Status}): finishing it off.");
            try
            {
                var screens = EFT.UI.Screens.EftScreenManager.Instance;
                if (screens != null) screens.CloseAllScreensForced();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Closing the screens: " + e.Message); }
            try
            {
                string profileId = game.PlayerOwner.Player.ProfileId;
                // It got as far as the fade: what's left is the end itself.
                if (game.Status == GameStatus.Stopping)
                    game.GameEnd(profileId, status, exitName, 0f).ContinueWith(t => Plugin.Log.LogError("[siege] Finishing the raid failed: " + t.Exception),
                        TaskContinuationOptions.OnlyOnFaulted);
                // It didn't get going at all: ask again.
                else game.Stop(profileId, status, exitName);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[siege] Finishing the raid failed: " + e);
                // The way out by hand stays open, rather than leaving you there for good, and you've your controls back.
                leaving = Closing = false;
                SiegeState.Notify($"Couldn't leave the raid: press {Plugin.LeaveKey.Value} to try again", 15f);
            }
        }

        // Something held on the cursor in the inventory when the raid ends is what's left it stuck: the game tears the
        // inventory down mid-move. So it waits (a little) for you to let go, and then shuts the inventory first.
        private static System.Collections.IEnumerator PutThingsDown()
        {
            float until = Time.time + 5f;
            while (Time.time < until && Holding()) yield return null;
            if (Holding()) Plugin.Log.LogWarning("[siege] Leaving with something still held in the inventory.");
            var screens = EFT.UI.Screens.EftScreenManager.Instance;
            if (screens == null || !screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.Inventory)) yield break;
            try { screens.CloseAllScreensForced(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Closing the inventory: " + e.Message); }
            // A frame for it to go before the raid does.
            yield return null;
            yield return null;
        }

        private static bool Holding()
        {
            try
            {
                var ui = EFT.UI.ItemUiContext.Instance;
                return ui != null && Dragging?.GetValue(ui) != null;
            }
            catch { return false; }
        }
    }

    public sealed class SiegeStatus
    {
        public bool Invaded;
        public long NextInvasion, Now;
        public int Wins, Losses;
        // A defeat the game hasn't shown yet: what they took.
        public SiegeDefeatShown Defeat;
    }

    public sealed class SiegeDefeatShown
    {
        public List<SiegeTakenItem> Taken = new List<SiegeTakenItem>();
        public List<SiegeWreckedPart> Wrecked = new List<SiegeWreckedPart>();
        public double Health, Energy, Hydration;
        public int Security, Medstation, Tier, MostTaken;
        public long When;

        // From before the server said: Security alone.
        public int Most => MostTaken > 0 ? MostTaken : Mathf.Max(1, 4 - Security);
    }

    public sealed class SiegeTakenItem
    {
        public string Name;
        public double Worth;
    }

    public sealed class SiegeWreckedPart
    {
        public string Name, Was, Now;
    }

    public sealed class SiegeResult
    {
        public bool Victory;
        public int Tier, Waves, Kills;
    }

    /// <summary>The mod's conversation with its server half.</summary>
    internal static class SiegeBackend
    {
        internal static async Task<SiegeStatus> GetStatus()
        {
            try
            {
                string json = await Task.Run(() => RequestHandler.PostJson(SiegeIds.RouteState, "{}"));
                return JsonConvert.DeserializeObject<SiegeStatus>(json);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[siege] Couldn't reach the server half: " + e.Message);
                return null;
            }
        }

        internal static void Trigger() => Fire(SiegeIds.RouteTrigger, "{}");

        internal static void DefeatSeen() => Fire(SiegeIds.RouteDefeatSeen, "{}");

        internal static void Report(bool victory, int tier, int waves, int kills)
        {
            Fire(SiegeIds.RouteResult, JsonConvert.SerializeObject(new SiegeResult { Victory = victory, Tier = tier, Waves = waves, Kills = kills }));
        }

        // Before the game's own end-of-raid message, so whatever it reloads after that is already right. Only the racks of
        // whoever's hideout it is came in; every player has a stash box.
        internal static void ReportRackGear()
        {
            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            if (world == null || SiegeOwner.OnHeadless) return;
            // Whose hideout it is: the host's, or on a Fika headless the player it's hosting for. Anyone else is helping.
            bool mine = SiegeOwner.IsMe;
            var untouched = new List<string>();
            // Loot put into something from your racks that's still lying about (a bag you filled and left): it's kept too.
            var added = new List<Item>();
            int lying = 0;
            if (mine)
                foreach (var loot in world.LootList.OfType<EFT.Interactive.LootItem>())
                    // Still the loot's, not moved on to someone (a mannequin's loadout is taken by moving it, not picking it up).
                    if (SiegeGear.IsRackGear(loot) && loot.Item != null && ReferenceEquals(loot.Item.Owner, loot.ItemOwner))
                    {
                        lying++;
                        foreach (var item in loot.Item.GetAllItems())
                        {
                            if (item.Id.ToString().StartsWith(SiegeIds.GearPrefix)) untouched.Add(item.Id);
                            else if (item.Parent?.Container?.ParentItem is Item holder && holder.Id.ToString().StartsWith(SiegeIds.GearPrefix)) added.Add(item);
                        }
                    }
            var deposited = new List<Item>();
            // The stash boxes are kept only if the hideout holds: overrun, the attackers have them. Said before it's
            // over (died and left early), the server waits for the result.
            bool held = SiegeState.Phase == SiegePhase.Won;
            bool fell = SiegeState.Phase == SiegePhase.Lost || (!held && !SiegeDirector.Defenders().Any());
            if (!fell) SiegeStash.Split(mine, untouched, added, deposited);
            // Held out: your own things you left lying about (a bag you filled and dropped) are yours to keep too.
            var left = mine && SiegeState.Phase == SiegePhase.Won ? SiegeGear.LeftLying(world.MainPlayer) : new List<Item>();
            if (!mine)
            {
                if (deposited.Count == 0) return;
                Plugin.Log.LogInfo($"[siege] Telling the server what's in your stash box: {deposited.Count} things to keep.");
            }
            else Plugin.Log.LogInfo($"[siege] Telling the server what's still on your racks: {lying} things ({untouched.Count} items, with what's in them); to keep, {added.Count} things you put in them, {deposited.Count} in your stash box and {left.Count} of your own left lying about.");
            try
            {
                var factory = Comfort.Common.Singleton<ItemFactory>.Instance;
                var body = new
                {
                    // Nothing at all from a friend helping: there's nothing of theirs on the racks to settle.
                    Untouched = mine ? untouched : null,
                    Pending = !held,
                    Deposited = deposited.Count > 0 ? factory.TreeToFlatItems(deposited) : new JsonType.FlatItem[0],
                    Added = added.Count > 0 ? factory.TreeToFlatItems(added) : new JsonType.FlatItem[0],
                    Left = left.Count > 0 ? factory.TreeToFlatItems(left) : new JsonType.FlatItem[0],
                };
                RequestHandler.PostJson(SiegeIds.RouteGear, body.ToJson());
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[siege] Couldn't report your rack gear: {e.Message}"); }
        }

        private static void Fire(string route, string body)
        {
            Task.Run(() =>
            {
                try { RequestHandler.PostJson(route, body); }
                catch (Exception e) { Plugin.Log.LogWarning($"[siege] {route} failed: {e.Message}"); }
            });
        }
    }

    /// <summary>The "your hideout is being invaded" banner on the menus, and the countdown to the next invasion on the main menu.</summary>
    internal static class MenuAlert
    {
        private static SiegeStatus status;
        private static bool hidden, wasInvaded;
        private static float nextPoll, invasionAt, shownAt = -1f;
        private static GUIStyle leftText, rightText, middleText;

        internal static EFT.UI.Screens.EEftScreenType Screen;

        internal static void Refresh()
        {
            nextPoll = 0f;
            hidden = false;
            shownAt = -1f;
        }

        private const float PollSeconds = 300f;

        internal static void Update()
        {
            // A Fika headless sits at its menu all day with nobody to see the alert, so it never asks.
            if (Application.isBatchMode || Time.realtimeSinceStartup < nextPoll || !AtMenu()) return;
            // Not often: the server's said when the next one's due, and it's asked again the moment that comes (and after
            // every raid, and when "Invade my hideout now" is switched on).
            nextPoll = Time.realtimeSinceStartup + PollSeconds;
            Poll();
        }

        private static async void Poll()
        {
            status = await SiegeBackend.GetStatus();
            // "Later" lasts until the next raid or the next invasion, not the next check.
            bool invaded = status != null && status.Invaded;
            if (invaded && !wasInvaded) { hidden = false; shownAt = -1f; }
            wasInvaded = invaded;
            long now = status != null && status.Now > 0 ? status.Now : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (status == null || invaded || status.NextInvasion <= 0) { invasionAt = 0f; return; }
            // Counted against the server's clock, and checked again the moment it runs out.
            invasionAt = Time.realtimeSinceStartup + (status.NextInvasion - now);
            nextPoll = Mathf.Min(nextPoll, Mathf.Max(invasionAt + 1f, Time.realtimeSinceStartup + 3f));
        }

        // The main menu itself, as the game's screens have it now (back from a raid, the last screen change the menu told us
        // about can be one of the results screens).
        private static bool OnMainMenu
        {
            get
            {
                var screens = EFT.UI.Screens.EftScreenManager.Instance;
                return screens != null && screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.MainMenu);
            }
        }

        private static readonly System.Reflection.FieldInfo MenuOperation = AccessTools.Field(typeof(TarkovApplication), "_menuOperation");
        private static int askedOn = -1;
        private static bool atMenu;

        // Asked several times a frame by the drawing, so worked out once a frame.
        private static bool AtMenu()
        {
            if (askedOn == Time.frameCount) return atMenu;
            askedOn = Time.frameCount;
            atMenu = false;
            if (Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld)) return false;
            var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
            if (app?.Session?.Profile == null) return false;
            return atMenu = MenuOperation?.GetValue(app) != null;
        }

        internal static void Draw()
        {
            if (status == null || !AtMenu()) return;
            if (Screen.ToString().StartsWith("MatchMaker", StringComparison.OrdinalIgnoreCase)) return;
            SiegeUi.Prepare();
            if (leftText == null)
            {
                leftText = SiegeUi.Style(TextAnchor.MiddleLeft, true);
                rightText = SiegeUi.Style(TextAnchor.MiddleRight, true);
                middleText = SiegeUi.Style(TextAnchor.MiddleCenter, true);
            }
            float unit = UnityEngine.Screen.height / 1080f;
            // What they took has the screen to itself. Shown in the end-of-raid screens, unless those were skipped: then here.
            if (SiegeDefeatScreen.Showing) return;
            if (status.Defeat != null && !SiegeDefeatScreen.Handled(status.Defeat) && OnMainMenu && SiegeLauncher.MenuReady)
            {
                var shown = status;
                SiegeDefeatScreen.Show(status.Defeat, () => shown.Defeat = null, null);
                return;
            }
            // Not while the menu's still loading after a raid: Start the Defense then would leave it blank.
            if (status.Invaded) { if (!hidden && SiegeLauncher.MenuReady) DrawAlert(unit); }
            else if (invasionAt > 0f && Screen == EFT.UI.Screens.EEftScreenType.MainMenu) DrawCountdown(unit, "NEXT INVASION", invasionAt);
            GUI.color = Color.white;
        }

        private static Color Faded(Color c, float fade) => new Color(c.r, c.g, c.b, c.a * fade);

        // Dark see-through panels with a hairline edge, like the game's own menus.
        private static void Panel(Rect r, Color fill, Color edge, float unit)
        {
            SiegeUi.Fill(r, fill);
            float t = Mathf.Max(1f, Mathf.Round(unit));
            SiegeUi.Fill(new Rect(r.x, r.y, r.width, t), edge);
            SiegeUi.Fill(new Rect(r.x, r.yMax - t, r.width, t), edge);
            SiegeUi.Fill(new Rect(r.x, r.y, t, r.height), edge);
            SiegeUi.Fill(new Rect(r.xMax - t, r.y, t, r.height), edge);
        }

        private static void DrawAlert(float unit)
        {
            if (shownAt < 0f) shownAt = Time.realtimeSinceStartup;
            float fade = Mathf.Clamp01((Time.realtimeSinceStartup - shownAt) / .3f);
            float screenWidth = UnityEngine.Screen.width;
            float w = Mathf.Min(660f * unit, screenWidth - 32f), h = 52f * unit, pad = 9f * unit;
            var box = new Rect((screenWidth - w) / 2f, 40f * unit - (1f - fade) * 8f * unit, w, h);
            var later = new Rect(box.xMax - pad - 78f * unit, box.y + pad, 78f * unit, h - pad * 2f);
            var start = new Rect(later.x - 6f * unit - 168f * unit, later.y, 168f * unit, later.height);
            if (Event.current.type == EventType.Repaint)
            {
                Vector2 mouse = Event.current.mousePosition;
                bool overStart = start.Contains(mouse), overLater = later.Contains(mouse);
                Panel(box, new Color(.03f, .03f, .03f, .86f * fade), new Color(1f, 1f, 1f, .1f * fade), unit);
                SiegeUi.Fill(new Rect(box.x, box.y, 2f * unit, h), new Color(.75f, .17f, .11f, fade));
                float dot = 7f * unit, pulse = .45f + .55f * Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 2.5f));
                SiegeUi.Fill(new Rect(box.x + 18f * unit, box.y + (h - dot) / 2f, dot, dot), new Color(.9f, .22f, .14f, pulse * fade));
                SiegeUi.Text(new Rect(box.x + 34f * unit, box.y, start.x - box.x - 34f * unit, h), "HIDEOUT IS BEING INVADED", leftText, 18f * unit, Faded(SiegeUi.Ink, fade));
                Panel(start, overStart ? new Color(.5f, .12f, .08f, .95f * fade) : new Color(.3f, .07f, .05f, .9f * fade),
                    new Color(.85f, .3f, .22f, (overStart ? .8f : .45f) * fade), unit);
                SiegeUi.Text(start, "START THE DEFENSE", middleText, 13f * unit, Faded(SiegeUi.Ink, fade));
                Panel(later, overLater ? new Color(.2f, .2f, .19f, .95f * fade) : new Color(.1f, .1f, .1f, .9f * fade),
                    new Color(1f, 1f, 1f, (overLater ? .35f : .15f) * fade), unit);
                SiegeUi.Text(later, "LATER", middleText, 13f * unit, Faded(overLater ? SiegeUi.Ink : SiegeUi.Dim, fade));
            }
            if (GUI.Button(start, GUIContent.none, GUIStyle.none) && SiegeLauncher.Launch()) hidden = true;
            if (GUI.Button(later, GUIContent.none, GUIStyle.none)) hidden = true;
        }

        private static void DrawCountdown(float unit, string label, float until)
        {
            if (Event.current.type != EventType.Repaint) return;
            float left = until - Time.realtimeSinceStartup;
            float w = 220f * unit, h = 28f * unit;
            var box = new Rect((UnityEngine.Screen.width - w) / 2f, 40f * unit, w, h);
            Panel(box, new Color(.03f, .03f, .03f, .8f), new Color(1f, 1f, 1f, .1f), unit);
            SiegeUi.Fill(new Rect(box.x, box.y, 2f * unit, h), new Color(.75f, .17f, .11f, .9f));
            SiegeUi.Text(new Rect(box.x + 14f * unit, box.y, w, h), label, leftText, 12f * unit, SiegeUi.Dim);
            SiegeUi.Text(new Rect(box.x, box.y, w - 12f * unit, h), SiegeUi.Clock(left), rightText, 15f * unit, left <= 300f ? new Color(1f, .42f, .32f, 1f) : SiegeUi.Ink);
        }
    }
}
