using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace HideoutSiege
{
    /// <summary>
    /// Whose hideout the siege is in. Normally the host's. A Fika headless hosts a raid for someone else, so there it's the
    /// player who asked for it: their game says what their hideout looks like as they start the defence, and the headless
    /// gets that from the server. Their racks and stash, what's put away and the result are theirs too.
    /// </summary>
    internal static class SiegeOwner
    {
        private sealed class Layout
        {
            public string ProfileId;
            public Dictionary<string, int> Levels;
            public Dictionary<string, string> Surfaces;
            public Dictionary<string, string> Copies;
        }

        private static bool asked;

        /// <summary>On a headless: the profile of the player it's running the siege for.</summary>
        internal static string HostingFor { get; private set; }

        internal static bool OnHeadless => HostingFor != null;

        /// <summary>On a joined player's game: whose hideout it is, as the host said (null from a host on an older siege).</summary>
        internal static string Said;

        private static string Mine => (Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication)?.Session?.Profile?.Id;

        /// <summary>The profile whose hideout this is, for telling the other players.</summary>
        internal static string ProfileId => HostingFor ?? (Net.IsClient ? Said : Mine);

        /// <summary>Whether it's this game's player's hideout: their stash to open and their racks to settle with the server.</summary>
        internal static bool IsMe => Net.IsClient ? Said != null && Said == Mine : !OnHeadless;

        /// <summary>
        /// Once a siege, as soon as anything wants the hideout: whether the server says this game is a headless hosting for
        /// someone, and if so, their hideout in place of this profile's.
        /// </summary>
        internal static void Ask()
        {
            if (asked || !(SiegeLauncher.Selected || SiegeLauncher.InSiege)) return;
            asked = true;
            try
            {
                string json = RequestHandler.PostJson(SiegeIds.RouteOwner, "{}");
                var layout = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<Layout>(json);
                if (string.IsNullOrEmpty(layout?.ProfileId) || layout.Levels == null) return;
                HostingFor = layout.ProfileId;
                var levels = new Dictionary<EAreaType, int>();
                foreach (var pair in layout.Levels)
                    if (int.TryParse(pair.Key, out int type)) levels[(EAreaType)type] = pair.Value;
                var pack = new Dictionary<EHideoutCustomizationType, string>();
                foreach (var pair in layout.Surfaces ?? new Dictionary<string, string>())
                    if (int.TryParse(pair.Key, out int type) && !string.IsNullOrEmpty(pair.Value)) pack[(EHideoutCustomizationType)type] = pair.Value;
                HideoutCopy.HostLayout = levels;
                HideoutCopy.HostPack = pack;
                HideoutCopy.HostCopies = layout.Copies ?? new Dictionary<string, string>();
                Plugin.Log.LogInfo($"[siege] Hosting the siege for {HostingFor}: their hideout, {levels.Count} stations, {pack.Count} surfaces, {HideoutCopy.HostCopies.Count} other parts.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't ask the server whose siege this is: " + e.Message); }
        }

        /// <summary>
        /// As the defence starts: tells the server what this hideout looks like, in case a Fika headless hosts it. Read here,
        /// sent in the background.
        /// </summary>
        internal static void Tell()
        {
            object body;
            try
            {
                body = new
                {
                    Levels = HideoutCopy.OwnLevels().ToDictionary(p => ((int)p.Key).ToString(), p => p.Value),
                    Surfaces = HideoutCopy.OwnPack().ToDictionary(p => ((int)p.Key).ToString(), p => p.Value),
                    Copies = HideoutCopy.Snapshot(false),
                };
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't read your hideout for the server: " + e.Message); return; }
            string json = JsonConvert.SerializeObject(body);
            Task.Run(() =>
            {
                try { RequestHandler.PostJson(SiegeIds.RouteLayout, json); }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't tell the server about your hideout: " + e.Message); }
            });
        }

        internal static void Reset()
        {
            asked = false;
            HostingFor = null;
            Said = null;
        }
    }
}
