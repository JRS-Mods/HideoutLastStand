using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Bots;
using HarmonyLib;
using JsonType;

namespace HideoutSiege
{
    /// <summary>Takes you from the menu to the normal raid-start screen with the siege selected, so Fika hosting still works.</summary>
    internal static class SiegeLauncher
    {
        internal static bool IsSiege(string locationId) => locationId == SiegeIds.LocationId;

        internal static bool InSiege => Singleton<GameWorld>.Instantiated && IsSiege(Singleton<GameWorld>.Instance.LocationId);

        // True from the moment a siege raid is selected and loading, before a GameWorld exists.
        internal static bool Selected
        {
            get
            {
                var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
                return app != null && IsSiege(app.CurrentRaidSettings?.LocationId);
            }
        }

        /// <summary>
        /// Whether the menu's raid screens are there to go to. Back from a raid (longer after a death) the menu is rebuilt for a
        /// while; going to the raid screen then finds it missing and leaves a menu with no buttons on it.
        /// </summary>
        internal static bool MenuReady
        {
            get
            {
                var screens = EFT.UI.Screens.EftScreenManager.Instance;
                if (screens == null || !screens.TryGetScreen(EFT.UI.Screens.EEftScreenType.MatchMakerAccept, out var accept) || accept == null) return false;
                // On the main menu or in the hideout: not on a results screen, nor with the menu still loading behind its spinner.
                return screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.MainMenu) || screens.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.Hideout);
            }
        }

        internal static bool Launch()
        {
            var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
            var menu = app == null ? null : Traverse.Create(app).Field<MainMenuShowOperation>("_menuOperation").Value;
            if (menu == null) { Plugin.Log.LogWarning("Not at the main menu yet."); return false; }
            if (!MenuReady) { Plugin.Log.LogInfo("The menu's still loading: the siege waits until it's ready."); return false; }

            var location = app.Session.LocationSettings.locations.Values.FirstOrDefault(l => IsSiege(l.Id));
            if (location == null) { Plugin.Log.LogError("The server has no siege location: is HideoutSiege.Server installed?"); return false; }

            var settings = app.CurrentRaidSettings;
            var offline = Traverse.Create(menu).Field<RaidSettings>("raidSettings_1").Value;
            if (saved == null) saved = new Saved(settings, offline);
            Prepare(settings, location);
            if (offline != null) Prepare(offline, location);
            // In case a Fika headless hosts it: it needs to know what your hideout looks like.
            SiegeOwner.Tell();
            AccessTools.Method(typeof(MainMenuShowOperation), "method_52").Invoke(menu, null);
            return true;
        }

        // A siege borrows the raid settings. Put them back afterwards so your next normal raid isn't affected.
        private sealed class Saved
        {
            private readonly RaidSettings main, offline;
            private readonly LocationSettings.Location location, offlineLocation;
            private readonly bool bosses, offlineBosses;
            private readonly ESideType side;

            internal Saved(RaidSettings main, RaidSettings offline)
            {
                this.main = main;
                this.offline = offline;
                location = main.SelectedLocation;
                bosses = main.WavesSettings.IsBosses;
                side = main.Side;
                if (offline != null)
                {
                    offlineLocation = offline.SelectedLocation;
                    offlineBosses = offline.WavesSettings.IsBosses;
                }
            }

            internal void Apply()
            {
                main.SelectedLocation = location;
                main.WavesSettings.IsBosses = bosses;
                main.Side = side;
                if (offline != null)
                {
                    offline.SelectedLocation = offlineLocation;
                    offline.WavesSettings.IsBosses = offlineBosses;
                }
            }
        }

        private static Saved saved;

        internal static void Restore()
        {
            if (saved == null) return;
            saved.Apply();
            saved = null;
        }

        private static void Prepare(RaidSettings settings, LocationSettings.Location location)
        {
            settings.Side = ESideType.Pmc;
            settings.SelectedLocation = location;
            settings.SelectedDateTime = EDateTime.CURR;
            settings.RaidMode = ERaidMode.Local;
            settings.IsPveOffline = true;
            settings.WavesSettings.IsBosses = false;
            settings.WavesSettings.BotAmount = EBotAmount.AsOnline;
            settings.BotSettings.BotAmount = EBotAmount.AsOnline;
        }
    }
}
