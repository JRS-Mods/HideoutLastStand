using System;
using System.Linq;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>What the siege's on-screen pieces share: a plain texture to fill with, a font that suits the game, and shadowed text.</summary>
    internal static class SiegeUi
    {
        internal static Texture2D White, Dot, Lines;
        internal static UnityEngine.Font Font;
        internal static readonly Color Ink = new Color(.93f, .91f, .86f, 1f), Dim = new Color(.72f, .7f, .66f, .85f), Red = new Color(.95f, .2f, .14f, 1f);
        private static bool ready, hasDot = true;

        internal static void Prepare()
        {
            if (ready) return;
            ready = true;
            White = new Texture2D(1, 1) { name = "Hideout Siege white" };
            White.SetPixel(0, 0, Color.white);
            White.Apply();
            // A round dot, drawn rather than typed: a character the font hasn't got sends Unity looking through every font
            // Windows has for it, which froze the game for over a second the first time.
            const int size = 64;
            Dot = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Hideout Siege dot", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(size / 2f, size / 2f));
                    Dot.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(size / 2f - 1f - d)));
                }
            Dot.Apply();
            // A camera picture's scan lines, as one picture repeated down the screen rather than a strip drawn for each.
            Lines = new Texture2D(1, 10, TextureFormat.RGBA32, false) { name = "Hideout Siege scan lines", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
            for (int y = 0; y < 10; y++) Lines.SetPixel(0, y, new Color(0f, 0f, 0f, y < 3 ? .2f : 0f));
            Lines.Apply();
            // The game's own typeface if it has a plain font of it loaded, otherwise Windows' DIN-style one.
            try { Font = Resources.FindObjectsOfTypeAll<UnityEngine.Font>().FirstOrDefault(f => f.dynamic && f.name.IndexOf("Bender", StringComparison.OrdinalIgnoreCase) >= 0); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Font search: " + e.Message); }
            if (Font == null)
                try { Font = UnityEngine.Font.CreateDynamicFontFromOSFont(new[] { "Bahnschrift", "Segoe UI", "Arial" }, 32); }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] No system font: " + e.Message); }
            try { hasDot = Font == null || Font.HasCharacter('·'); }
            catch (Exception) { hasDot = false; }
        }

        internal static GUIStyle Style(TextAnchor anchor, bool bold) => new GUIStyle(GUI.skin.label)
        {
            font = Font,
            alignment = anchor,
            fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
            wordWrap = false,
            clipping = TextClipping.Overflow,
            richText = false,
        };

        internal static void Text(Rect rect, string text, GUIStyle style, float size, Color colour)
        {
            if (!hasDot && text.IndexOf('·') >= 0) text = text.Replace('·', '/');
            style.fontSize = Mathf.Max(8, Mathf.RoundToInt(size));
            float drop = Mathf.Max(1f, size / 20f);
            GUI.color = new Color(0, 0, 0, colour.a * .75f);
            GUI.Label(new Rect(rect.x + drop, rect.y + drop, rect.width, rect.height), text, style);
            GUI.color = colour;
            GUI.Label(rect, text, style);
        }

        internal static void Fill(Rect rect, Color colour)
        {
            GUI.color = colour;
            GUI.DrawTexture(rect, White);
        }

        internal static string Clock(float seconds)
        {
            int s = Mathf.CeilToInt(Mathf.Max(0f, seconds));
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }
    }

    /// <summary>The small readout at the top of the screen, the big announcements, personal notices, and the security camera's picture.</summary>
    internal static class SiegeHud
    {
        private static GUIStyle label, value, title, notice, corner;
        private static string shownBanner = "";
        private static float bannerSince;
        internal static float Black; // full-screen fade, 0 to 1
        internal static bool SecurityCam;
        internal static float Static; // the camera's picture breaking up, 0 to 1

        private static void Prepare()
        {
            if (label != null) return;
            SiegeUi.Prepare();
            label = SiegeUi.Style(TextAnchor.MiddleCenter, true);
            value = SiegeUi.Style(TextAnchor.MiddleCenter, true);
            title = SiegeUi.Style(TextAnchor.MiddleCenter, true);
            notice = SiegeUi.Style(TextAnchor.MiddleCenter, false);
            corner = SiegeUi.Style(TextAnchor.UpperLeft, true);
        }

        private static void DrawCamera(float w, float h, float unit)
        {
            // Washes the colour out of the picture, then adds what a cheap camera's picture has.
            SiegeUi.Fill(new Rect(0, 0, w, h), new Color(.1f, .15f, .12f, .4f));
            float line = Mathf.Max(2f, 3f * unit);
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(new Rect(0, 0, w, h), SiegeUi.Lines, new Rect(0f, 0f, 1f, h / (line * 2f)));
            SiegeUi.Fill(new Rect(0, Time.time * 90f * unit % h, w, 50f * unit), new Color(1f, 1f, 1f, .035f));
            Color ink = new Color(.85f, .95f, .85f, .9f);
            corner.alignment = TextAnchor.UpperLeft;
            SiegeUi.Text(new Rect(52 * unit, 40 * unit, w, 40 * unit), "CAM 03   FRONT ENTRANCE", corner, 24 * unit, ink);
            corner.alignment = TextAnchor.UpperRight;
            SiegeUi.Text(new Rect(0, h - 76 * unit, w - 52 * unit, 40 * unit), DateTime.Now.ToString("yyyy-MM-dd   HH:mm:ss"), corner, 22 * unit, ink);
            if (Time.time % 1f < .6f)
            {
                var rec = new Color(1f, .25f, .2f, .95f);
                SiegeUi.Text(new Rect(0, 40 * unit, w - 52 * unit, 40 * unit), "REC", corner, 24 * unit, rec);
                float recWidth = corner.CalcSize(new GUIContent("REC")).x, dot = 16f * unit;
                GUI.color = rec;
                GUI.DrawTexture(new Rect(w - 52 * unit - recWidth - dot - 10f * unit, 40 * unit + 7f * unit, dot, dot), SiegeUi.Dot);
            }
            for (int i = 0; Static > 0f && i < 90; i++)
            {
                float grey = UnityEngine.Random.value;
                SiegeUi.Fill(new Rect(0, UnityEngine.Random.value * h, w, UnityEngine.Random.Range(2f, 26f) * unit), new Color(grey, grey, grey, Static * UnityEngine.Random.Range(.2f, .9f)));
            }
        }

        // A label, a number under it, and an optional line under that, centred at the top of the screen.
        private static void Readout(float w, float unit, string top, string number, string under, Color numberColour)
        {
            float y = 110f * unit;
            SiegeUi.Fill(new Rect((w - 26f * unit) / 2f, y, 26f * unit, 2f * unit), new Color(SiegeUi.Red.r, SiegeUi.Red.g, SiegeUi.Red.b, .9f));
            SiegeUi.Text(new Rect(0, y + 5f * unit, w, 18f * unit), top, label, 14f * unit, SiegeUi.Dim);
            SiegeUi.Text(new Rect(0, y + 22f * unit, w, 34f * unit), number, value, 30f * unit, numberColour);
            if (!string.IsNullOrEmpty(under)) SiegeUi.Text(new Rect(0, y + 56f * unit, w, 16f * unit), under, label, 12f * unit, SiegeUi.Dim);
        }

        internal static void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (SiegeState.Phase == SiegePhase.None && Black <= 0f && !SecurityCam) return;
            Prepare();
            float w = Screen.width, h = Screen.height, unit = h / 1080f;
            float left = SiegeState.CountdownUntil - Time.time;

            if (SecurityCam) DrawCamera(w, h, unit);
            if (Black > 0f) SiegeUi.Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, Black));

            switch (SiegeState.Phase)
            {
                case SiegePhase.Prep:
                    bool close = left <= 10f;
                    Color urgent = close && Time.time % .5f < .25f ? SiegeUi.Red : close ? new Color(1f, .45f, .35f, 1f) : SiegeUi.Ink;
                    Readout(w, unit, "BREACH", SiegeUi.Clock(left), null, urgent);
                    break;
                case SiegePhase.Intermission:
                    Readout(w, unit, $"WAVE {SiegeState.Wave}/{SiegeState.TotalWaves} IN", SiegeUi.Clock(left), null, SiegeUi.Ink);
                    break;
                case SiegePhase.Fighting:
                    Readout(w, unit, $"WAVE {SiegeState.Wave}/{SiegeState.TotalWaves}", SiegeState.Remaining.ToString(), "HOSTILES", SiegeUi.Ink);
                    break;
                case SiegePhase.Won:
                    Readout(w, unit, "LEAVING IN", SiegeUi.Clock(left), $"{Plugin.LeaveKey.Value}  LEAVE NOW", new Color(.65f, .92f, .6f, 1f));
                    break;
            }

            if (SiegeState.Banner != shownBanner)
            {
                shownBanner = SiegeState.Banner;
                bannerSince = Time.time;
            }
            if (Plugin.Announcements.Value && Time.time < SiegeState.BannerUntil && !string.IsNullOrEmpty(SiegeState.Banner))
            {
                float a = Mathf.Clamp01((SiegeState.BannerUntil - Time.time) / .6f) * Mathf.Clamp01((Time.time - bannerSince) / .15f);
                float size = 42f * unit, y = h * .24f;
                title.fontSize = Mathf.RoundToInt(size);
                float textWidth = title.CalcSize(new GUIContent(SiegeState.Banner)).x;
                SiegeUi.Text(new Rect(0, y, w, size * 1.4f), SiegeState.Banner, title, size, new Color(SiegeUi.Ink.r, SiegeUi.Ink.g, SiegeUi.Ink.b, a));
                float bar = textWidth + 48f * unit;
                SiegeUi.Fill(new Rect((w - bar) / 2f, y + size * 1.4f + 2f * unit, bar, 2f * unit), new Color(SiegeUi.Red.r, SiegeUi.Red.g, SiegeUi.Red.b, a * .85f));
            }

            // Another player asking for something of yours: who, what, and the two keys.
            var asking = SiegeAsk.Current;
            if (asking != null)
            {
                float y = h * .56f, wide = 520f * unit, tall = 74f * unit;
                SiegeUi.Fill(new Rect((w - wide) / 2f, y, wide, tall), new Color(0f, 0f, 0f, .55f));
                SiegeUi.Fill(new Rect((w - wide) / 2f, y, wide, 2f * unit), new Color(SiegeUi.Red.r, SiegeUi.Red.g, SiegeUi.Red.b, .9f));
                SiegeUi.Text(new Rect(0, y + 10f * unit, w, 24f * unit), $"{asking.Name} wants your {asking.What}", notice, 18f * unit, SiegeUi.Ink);
                SiegeUi.Text(new Rect(0, y + 40f * unit, w, 22f * unit), $"{Plugin.AllowKey.Value}  ALLOW      {Plugin.DeclineKey.Value}  DECLINE      {SiegeUi.Clock(asking.Until - Time.time)}", label, 14f * unit, SiegeUi.Dim);
            }

            if (Time.time < SiegeState.NoticeUntil && !string.IsNullOrEmpty(SiegeState.Notice))
            {
                float a = Mathf.Clamp01((SiegeState.NoticeUntil - Time.time) / .4f);
                SiegeUi.Text(new Rect(0, h * .68f, w, 24f * unit), SiegeState.Notice, notice, 17f * unit, new Color(SiegeUi.Ink.r, SiegeUi.Ink.g, SiegeUi.Ink.b, a * .9f));
            }
            GUI.color = Color.white;
        }
    }
}
