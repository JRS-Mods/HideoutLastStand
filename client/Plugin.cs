using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace HideoutSiege
{
    [BepInPlugin(Id, "Tushonka: Hideout Last Stand", Version)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("xyz.drakia.bigbrain", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("jrs.hideoutinteriors", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("jrs.hideoutextensions", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "jrs.hideoutsiege";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static string Folder;
        internal static Plugin Instance;
        internal static ConfigEntry<bool> Cinematic;
        internal static ConfigEntry<float> Volume, PrepTime, WaveDelay, LootTime;
        internal static ConfigEntry<int> MaxAlive;
        internal static ConfigEntry<KeyCode> LeaveKey;
        internal static ConfigEntry<bool> TriggerNow;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Folder = Path.GetDirectoryName(Info.Location);
            Cinematic = Config.Bind("Siege", "Opening cinematic", true, "Play the opening cinematic. Space skips it.");
            Volume = Config.Bind("Siege", "Volume", .8f, new ConfigDescription("How loud the siren and stingers are.", new AcceptableValueRange<float>(0f, 1f)));
            PrepTime = Config.Bind("Siege", "Seconds to prepare", 60f, new ConfigDescription("From waking up to the door blowing.", new AcceptableValueRange<float>(10f, 300f)));
            WaveDelay = Config.Bind("Siege", "Seconds between waves", 25f, new ConfigDescription("Time to heal, reload and loot.", new AcceptableValueRange<float>(0f, 180f)));
            MaxAlive = Config.Bind("Siege", "Attackers at once", 7, new ConfigDescription("The hideout is small, and more than this gets crowded. The rest wait their turn.", new AcceptableValueRange<int>(1, 20)));
            LootTime = Config.Bind("Siege", "Seconds to loot after winning", 60f, new ConfigDescription("You leave as survived when this runs out. What you've put in your stash, or in bags from your racks, is kept.", new AcceptableValueRange<float>(10f, 900f)));
            LeaveKey = Config.Bind("Siege", "Leave key", KeyCode.F10, "Leave right away after winning.");
            TriggerNow = Config.Bind("Testing", "Invade my hideout now", false, "Turn on at the main menu to get the invasion alert straight away.");
            TriggerNow.SettingChanged += (_, __) =>
            {
                if (!TriggerNow.Value) return;
                TriggerNow.Value = false;
                SiegeBackend.Trigger();
                MenuAlert.Refresh();
            };
            SiegeSounds.Load();
            Net.Load();
            SiegePush.Load();
            SiegeSandbags.OfferAsDecor();
            SiegeSteel.Offer();
            SiegeBarbedWire.Offer();
            new Harmony(Id).PatchAll(typeof(Plugin).Assembly);
            SiegeDoorUpgrades.Patch(new Harmony(Id + ".door"));
            Logger.LogInfo("Hideout Last Stand loaded.");
        }

        private float nextSlow;

        private void Update()
        {
            MenuAlert.Update();
            SiegeBreach.Update();
            SiegeNoGo.Update();
            SiegeDoorUpgrades.Update();
            SiegeDefeatScreen.Update();
            // Neither needs to be the frame something changes: once a second does.
            if (Time.unscaledTime >= nextSlow)
            {
                nextSlow = Time.unscaledTime + 1f;
                SiegeGun.OfferAsDecor();
                SiegeMannequins.Remember();
            }
            if (SiegeState.Phase == SiegePhase.Won && Input.GetKeyDown(LeaveKey.Value)) SiegeExit.Leave(ExitStatus.Survived);
        }

        private void OnGUI()
        {
            MenuAlert.Draw();
            SiegeHud.Draw();
            SiegeDefeatScreen.Draw();
        }
    }
}
