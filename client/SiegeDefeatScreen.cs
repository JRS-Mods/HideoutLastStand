using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// After a lost siege, a screen in the run of the game's own end-of-raid screens, laid out like its raid statistics: what
    /// they took from your stash and what it was worth, the state you came round in, and what your Security and Medstation
    /// did for you. It comes after the experience screen and before the one to heal up; NEXT goes on to that, BACK goes back.
    /// Skip those screens and it's on the main menu instead. Each defeat is shown once.
    /// </summary>
    internal static class SiegeDefeatScreen
    {
        private static SiegeDefeatShown showing;
        private static Action next, back;
        private static readonly HashSet<long> handled = new HashSet<long>();
        private static UnityEngine.EventSystems.EventSystem muted;
        private static float shownAt;
        private static GUIStyle left, right, middle, title;

        internal static bool Showing => showing != null;

        /// <summary>Whether this defeat's been shown already (or there isn't one).</summary>
        internal static bool Handled(SiegeDefeatShown defeat) => defeat == null || handled.Contains(defeat.When);

        internal static void Show(SiegeDefeatShown defeat, Action onNext, Action onBack)
        {
            showing = defeat;
            next = onNext;
            back = onBack;
            shownAt = Time.realtimeSinceStartup;
            var screens = EFT.UI.Screens.EftScreenManager.Instance;
            under = screens?.CurrentScreenController?.ScreenType ?? EFT.UI.Screens.EEftScreenType.None;
            bool copied = false;
            try { copied = BuildCopy(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't make the defeat screen from the game's statistics screen: " + e); DropCopy(); }
            if (!copied) Mute(true);
            Plugin.Log.LogInfo($"[siege] Showing what they took: {defeat.Taken?.Count ?? 0} things, {(copied ? "on the game's statistics screen" : "drawn")}.");
        }

        // ---- on a copy of the game's own statistics screen --------------------------------------------------------

        private static GameObject copy;
        private static CanvasGroup hidden;
        private static float hiddenAlpha;
        private static EFT.UI.Screens.EEftScreenType under;

        // The sections, as the game's statistics screen has them: a caption, then a label and a value a row.
        private static List<(string Caption, List<(string Label, string Value)> Rows)> Sections(SiegeDefeatShown defeat)
        {
            string rub = SiegeUi.Font == null || SiegeUi.Font.HasCharacter('\u20bd') ? "\u20bd" : "RUB";
            var taken = defeat.Taken ?? new List<SiegeTakenItem>();
            var stash = taken.Select(t => (t.Name ?? "?", $"{t.Worth:N0} {rub}")).ToList();
            if (stash.Count == 0) stash.Add(("Nothing worth taking", "-"));
            if (taken.Count > 1) stash.Add(("All told", $"{taken.Sum(t => t.Worth):N0} {rub}"));
            return new List<(string, List<(string, string)>)>
            {
                ("Taken from your stash", stash),
                ("Wrecked in your hideout", Wrecked(defeat)),
                ("Health and physical condition", new List<(string, string)>
                {
                    ("Health left", Percent(defeat.Health)), ("Energy left", Percent(defeat.Energy)),
                    ("Hydration left", Percent(defeat.Hydration)), ("Before your next raid", "Heal up"),
                }),
                ("Your defences", new List<(string, string)>
                {
                    ("Security level", $"{defeat.Security} of 3"), ("Most they could take", $"{defeat.Most}"),
                    ("Medstation level", $"{defeat.Medstation} of 3"), ("Health it kept you at", Percent(.12 + .08 * defeat.Medstation)),
                }),
            };
        }

        // "Security: level 3 to level 2", one a row, or that nothing was.
        private static List<(string Label, string Value)> Wrecked(SiegeDefeatShown defeat)
        {
            var rows = (defeat.Wrecked ?? new List<SiegeWreckedPart>()).Select(w => (w.Name ?? "?", $"{w.Was} → {w.Now}")).ToList();
            if (rows.Count == 0) rows.Add(("Nothing they could get at", "-"));
            else rows.Add(("To put it right", "Rebuild it in the hideout"));
            return rows;
        }

        private static readonly System.Reflection.FieldInfo NextField = AccessTools.Field(typeof(EFT.UI.SessionEnd.SessionResultStatistics), "_nextButton"),
            BackField = AccessTools.Field(typeof(EFT.UI.SessionEnd.SessionResultStatistics), "_backButton"),
            SpawnField = AccessTools.Field(typeof(EFT.UI.SessionEnd.SessionResultStatistics), "_statsSpawn"),
            LocationField = AccessTools.Field(typeof(EFT.UI.SessionEnd.SessionResultStatistics), "_locationName"),
            GroupTemplate = AccessTools.Field(typeof(EFT.UI.StatisticsSpawn), "_statGroupViewTemplate"),
            CaptionField = AccessTools.Field(typeof(EFT.UI.StatGroupView), "_caption"),
            IconField = AccessTools.Field(typeof(EFT.UI.StatGroupView), "_icon"),
            Container = AccessTools.Field(typeof(EFT.UI.StatisticsSpawn), "_statsContainer"),
            Columns = AccessTools.Field(typeof(EFT.UI.StatisticsSpawn), "_horizontalGroup");

        /// <summary>
        /// The game's raid statistics screen, copied with its workings taken out: its title, its sections and rows (made from
        /// its own templates), and its NEXT and BACK. False if there's none to copy.
        /// </summary>
        private static bool BuildCopy()
        {
            var original = Resources.FindObjectsOfTypeAll<EFT.UI.SessionEnd.SessionResultStatistics>().FirstOrDefault(o => o.gameObject.scene.IsValid());
            if (original == null) return false;
            // The game's own section icons, from the raid statistics it showed: health, looting and combat.
            var icons = new List<(string Caption, Sprite Icon)>();
            foreach (var shown in original.GetComponentsInChildren<EFT.UI.StatGroupView>(true))
            {
                var caption = CaptionField.GetValue(shown) as TMPro.TMP_Text;
                var image = IconField.GetValue(shown) as UnityEngine.UI.Image;
                if (caption != null && image != null && image.sprite != null) icons.Add((caption.text ?? "", image.sprite));
            }
            Sprite IconFor(params string[] words) => icons.FirstOrDefault(i => words.Any(w => i.Caption.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).Icon;
            var parent = original.transform.parent;
            // Copied asleep, so the screen's own code never runs on it.
            var holder = new GameObject("Hideout Siege defeat screen holder", typeof(RectTransform));
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            copy = UnityEngine.Object.Instantiate(original.gameObject, holder.transform);
            copy.name = "Hideout Siege defeat screen";
            var screen = copy.GetComponent<EFT.UI.SessionEnd.SessionResultStatistics>();
            var nextButton = NextField.GetValue(screen) as EFT.UI.DefaultUIButton;
            var backButton = BackField.GetValue(screen) as EFT.UI.DefaultUIButton;
            var spawn = SpawnField.GetValue(screen) as EFT.UI.StatisticsSpawn;
            var location = LocationField.GetValue(screen) as TMPro.TMP_Text;
            var template = spawn == null ? null : GroupTemplate.GetValue(spawn) as EFT.UI.StatGroupView;
            var container = spawn == null ? null : Container.GetValue(spawn) as Transform;
            var columns = spawn == null ? null : Columns.GetValue(spawn) as UnityEngine.UI.HorizontalLayoutGroup;
            UnityEngine.Object.DestroyImmediate(screen);
            if (spawn != null) UnityEngine.Object.DestroyImmediate(spawn);
            if (nextButton == null || template == null || container == null) throw new Exception("the statistics screen isn't laid out as expected");

            // The raid's own statistics, copied along with it, out; the templates stay.
            foreach (Transform child in container.Cast<Transform>().ToList())
                if (child != template.transform && (columns == null || child != columns.transform)) UnityEngine.Object.DestroyImmediate(child.gameObject);

            // The heading: the largest words outside the rows and buttons are the title, the next the line under it.
            bool InRowsOrButtons(Transform t) => t.IsChildOf(container) || t.IsChildOf(nextButton.transform) || (backButton != null && t.IsChildOf(backButton.transform));
            foreach (var label in copy.GetComponentsInChildren<EFT.UI.LocalizedText>(true).Where(l => !InRowsOrButtons(l.transform)).ToList())
                UnityEngine.Object.DestroyImmediate(label);
            var words = copy.GetComponentsInChildren<TMPro.TMP_Text>(true).Where(t => t != location && !InRowsOrButtons(t.transform) && !string.IsNullOrWhiteSpace(t.text))
                .OrderByDescending(t => t.fontSize).ToList();
            Plugin.Log.LogInfo("[siege] The statistics screen's heading: " + string.Join(", ", words.Select(t => $"'{t.text}' ({t.name}, {t.fontSize:F0})")));
            if (words.Count > 0) words[0].text = "HIDEOUT OVERRUN";
            if (words.Count > 1) words[1].text = "WHAT THEY TOOK";
            if (location != null) location.text = "Your hideout";

            // The sections, two to a column as the game lays them out.
            GameObject column = null;
            int placed = 0;
            var sectionIcons = new[] { IconFor("Loot", "Item"), IconFor("Combat", "Kill", "Battle"), IconFor("Health"), IconFor("Combat", "Kill", "Battle") };
            int index = 0;
            foreach (var (caption, rows) in Sections(showing))
            {
                var icon = sectionIcons[Math.Min(index++, sectionIcons.Length - 1)] ?? icons.Select(i => i.Icon).FirstOrDefault();
                if (columns != null && placed % 2 == 0)
                {
                    column = UnityEngine.Object.Instantiate(columns.gameObject, container);
                    column.SetActive(true);
                }
                var view = UnityEngine.Object.Instantiate(template, column != null ? column.transform : container);
                // Its heading would put the template's own words back over ours as it wakes.
                foreach (var label in view.GetComponentsInChildren<EFT.UI.LocalizedText>(true).ToList()) UnityEngine.Object.DestroyImmediate(label);
                var group = new EFT.UI.SessionEnd.SessionResultStatistics.StatGroup { Caption = caption, Icon = icon };
                foreach (var (label, value) in rows)
                    group.StatItems.Add(new ProfileStats.StatInfo { Caption = label, Value = value, Type = ProfileStats.EStatType.Text });
                view.Show(group);
                if (CaptionField.GetValue(view) is TMPro.TMP_Text heading) heading.text = caption;
                // No icon to give it: none, rather than a white square.
                if (icon == null && IconField.GetValue(view) is UnityEngine.UI.Image blank) blank.enabled = false;
                placed++;
            }

            nextButton.OnClick.AddListener(() => Close(true));
            nextButton.Interactable = true;
            if (backButton != null)
            {
                backButton.OnClick.AddListener(() => Close(false));
                backButton.Interactable = true;
            }

            copy.transform.SetParent(parent, false);
            UnityEngine.Object.Destroy(holder);
            copy.transform.SetAsLastSibling();
            var own = copy.GetComponent<CanvasGroup>();
            if (own != null) { own.alpha = 1f; own.interactable = true; own.blocksRaycasts = true; }
            copy.SetActive(true);

            // The screen it comes after doesn't show through.
            var below = FindCurrentScreen();
            if (below != null && below != copy)
            {
                hidden = below.GetComponent<CanvasGroup>() ?? below.AddComponent<CanvasGroup>();
                hiddenAlpha = hidden.alpha;
                hidden.alpha = 0f;
                hidden.blocksRaycasts = false;
            }
            return true;
        }

        // The game object of the screen showing now (the experience screen, in the run of end-of-raid screens).
        private static GameObject FindCurrentScreen()
        {
            if (under == EFT.UI.Screens.EEftScreenType.SessionExperience)
                return Resources.FindObjectsOfTypeAll<EFT.UI.SessionEnd.SessionResultExperienceCount>().FirstOrDefault(o => o.gameObject.scene.IsValid() && o.gameObject.activeInHierarchy)?.gameObject;
            return null;
        }

        private static void DropCopy()
        {
            if (copy != null) UnityEngine.Object.Destroy(copy);
            copy = null;
            if (hidden != null)
            {
                hidden.alpha = hiddenAlpha;
                hidden.blocksRaycasts = true;
            }
            hidden = null;
        }

        /// <summary>If the game moves on from the screen it was shown over (Escape, say), it goes too, as BACK.</summary>
        internal static void Update()
        {
            if (showing == null || copy == null) return;
            var screens = EFT.UI.Screens.EftScreenManager.Instance;
            if (screens == null || under == EFT.UI.Screens.EEftScreenType.None) return;
            if (!screens.CheckCurrentScreen(under)) Close(false);
        }

        /// <summary>NEXT, as the button (for the test driver).</summary>
        internal static void Next()
        {
            if (showing != null) Close(true);
        }

        private static void Close(bool done)
        {
            var defeat = showing;
            showing = null;
            Mute(false);
            DropCopy();
            if (done)
            {
                handled.Add(defeat.When);
                SiegeBackend.DefeatSeen();
                next?.Invoke();
            }
            else back?.Invoke();
        }

        // The game's own screen underneath doesn't take clicks while this is up.
        private static void Mute(bool on)
        {
            if (on)
            {
                var events = UnityEngine.EventSystems.EventSystem.current;
                if (events != null && events.enabled) { events.enabled = false; muted = events; }
            }
            else if (muted != null)
            {
                muted.enabled = true;
                muted = null;
            }
        }

        private static Color Faded(Color c, float fade) => new Color(c.r, c.g, c.b, c.a * fade);

        internal static void Draw()
        {
            if (showing == null || copy != null) return;
            SiegeUi.Prepare();
            if (left == null)
            {
                left = SiegeUi.Style(TextAnchor.MiddleLeft, false);
                right = SiegeUi.Style(TextAnchor.MiddleRight, false);
                middle = SiegeUi.Style(TextAnchor.MiddleCenter, false);
                title = SiegeUi.Style(TextAnchor.MiddleCenter, true);
            }
            float unit = Screen.height / 1080f, fade = Mathf.Clamp01((Time.realtimeSinceStartup - shownAt) / .3f);
            float sw = Screen.width, sh = Screen.height;
            float w = Mathf.Min(1100f * unit, sw - 64f), x = (sw - w) / 2f, row = 25f * unit;
            var nextButton = new Rect(sw / 2f - 90f * unit, sh - 118f * unit, 180f * unit, 34f * unit);
            var backButton = new Rect(sw / 2f - 90f * unit, sh - 80f * unit, 180f * unit, 30f * unit);
            Vector2 mouse = Event.current.mousePosition;

            if (Event.current.type == EventType.Repaint)
            {
                string rub = SiegeUi.Font != null && SiegeUi.Font.HasCharacter('₽') ? "₽" : "RUB";
                SiegeUi.Fill(new Rect(0f, 0f, sw, sh), new Color(.015f, .015f, .015f, .93f * fade));
                SiegeUi.Text(new Rect(0f, 26f * unit, sw, 44f * unit), "HIDEOUT OVERRUN", title, 34f * unit, Faded(SiegeUi.Ink, fade));
                SiegeUi.Text(new Rect(0f, 66f * unit, sw, 18f * unit), "WHAT THEY TOOK", middle, 11f * unit, Faded(SiegeUi.Dim, fade));
                float dot = 6f * unit;
                SiegeUi.Fill(new Rect(sw / 2f - 62f * unit, 96f * unit - dot / 2f, dot, dot), Faded(SiegeUi.Ink, fade));
                SiegeUi.Text(new Rect(sw / 2f - 50f * unit, 86f * unit, 200f * unit, 20f * unit), "Your hideout", left, 15f * unit, Faded(SiegeUi.Ink, fade));

                float y = 140f * unit;
                void Header(string text)
                {
                    float s = 9f * unit;
                    SiegeUi.Fill(new Rect(x + 4f * unit, y + (row - s) / 2f, s, s), Faded(SiegeUi.Ink, fade));
                    SiegeUi.Text(new Rect(x + 22f * unit, y, w, row), text, left, 14f * unit, Faded(SiegeUi.Ink, fade));
                    y += row;
                    SiegeUi.Fill(new Rect(x, y - 1f, w, 1f), new Color(1f, 1f, 1f, .08f * fade));
                    y += 4f * unit;
                }
                void Row(string label, string value, Color? colour = null)
                {
                    SiegeUi.Text(new Rect(x + 22f * unit, y, w * .7f, row), label, left, 13.5f * unit, Faded(SiegeUi.Dim, fade));
                    SiegeUi.Text(new Rect(x, y, w - 8f * unit, row), value, right, 13.5f * unit, Faded(colour ?? SiegeUi.Ink, fade));
                    y += row;
                }

                var taken = showing.Taken ?? new List<SiegeTakenItem>();
                var lost = new Color(1f, .5f, .4f, 1f);
                Header("Taken from your stash");
                if (taken.Count == 0) Row("Nothing worth taking", "-");
                foreach (var item in taken) Row(item.Name ?? "?", $"{item.Worth:N0} {rub}", lost);
                if (taken.Count > 1) Row("All told", $"{taken.Sum(t => t.Worth):N0} {rub}", lost);
                y += 16f * unit;

                Header("Wrecked in your hideout");
                foreach (var (label, value) in Wrecked(showing)) Row(label, value, value == "-" || label == "To put it right" ? (Color?)null : lost);
                y += 16f * unit;

                Header("Health and physical condition");
                Row("Health left", Percent(showing.Health));
                Row("Energy left", Percent(showing.Energy));
                Row("Hydration left", Percent(showing.Hydration));
                Row("Before your next raid", "Heal up", new Color(1f, .78f, .35f, 1f));
                y += 16f * unit;

                Header("Your defences");
                Row("Security level", $"{showing.Security} of 3");
                Row("Most they could take", $"{showing.Most}");
                Row("Medstation level", $"{showing.Medstation} of 3");
                Row("Health it kept you at", Percent(.12 + .08 * showing.Medstation));
                y += 18f * unit;
                string advice = showing.Security < 3 || showing.Medstation < 3
                    ? "A better Security station and they take less. A better Medstation and you come round less hurt."
                    : "With Security and the Medstation as good as they get, that's the least it could have cost.";
                SiegeUi.Text(new Rect(x, y, w, row), advice, middle, 13f * unit, Faded(SiegeUi.Dim, fade));

                bool overNext = nextButton.Contains(mouse), overBack = back != null && backButton.Contains(mouse);
                SiegeUi.Text(nextButton, "NEXT", middle, 20f * unit, Faded(overNext ? Color.white : SiegeUi.Ink, fade));
                if (back != null) SiegeUi.Text(backButton, "BACK", middle, 16f * unit, Faded(overBack ? Color.white : SiegeUi.Dim, fade));
            }
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (enter) Event.current.Use();
            if (GUI.Button(nextButton, GUIContent.none, GUIStyle.none) || enter) Close(true);
            else if (back != null && GUI.Button(backButton, GUIContent.none, GUIStyle.none)) Close(false);
        }

        // "36%", not the "36 %" some languages' formatting gives.
        private static string Percent(double share) => $"{Mathf.RoundToInt((float)share * 100f)}%";
    }

    // The experience screen's NEXT goes to the one to heal up: after a lost siege, what they took comes in between.
    [HarmonyPatch(typeof(SessionResultShowOperation), nameof(SessionResultShowOperation.ShowHealthTreatmentScreen))]
    internal static class DefeatBeforeTreatmentPatch
    {
        private static bool passing;

        [HarmonyPrefix]
        private static bool Prefix(SessionResultShowOperation __instance)
        {
            if (passing) return true;
            if (SiegeDefeatScreen.Showing) return false;
            SiegeDefeatShown defeat = null;
            try
            {
                string json = RequestHandler.PostJson(SiegeIds.RouteState, "{}");
                defeat = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<SiegeStatus>(json)?.Defeat;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't ask whether a siege was lost: " + e.Message); }
            if (SiegeDefeatScreen.Handled(defeat)) return true;
            SiegeDefeatScreen.Show(defeat, () =>
            {
                passing = true;
                try { __instance.ShowHealthTreatmentScreen(); }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't go on to the next screen: " + e.Message); }
                finally { passing = false; }
            }, () => { });
            return false;
        }
    }
}
