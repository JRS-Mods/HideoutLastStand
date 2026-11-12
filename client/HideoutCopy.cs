using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>Makes the loaded hideout scene match a profile: each station shows the level it has in your real hideout.</summary>
    internal static class HideoutCopy
    {
        // Set when another player's game told us the layout to show: everyone in a raid sees the host's hideout.
        internal static Dictionary<EAreaType, int> HostLayout;

        internal static Dictionary<EAreaType, int> Levels()
        {
            SiegeOwner.Ask();
            return HostLayout ?? OwnLevels();
        }

        /// <summary>This profile's own stations, whoever's hideout the siege is in.</summary>
        internal static Dictionary<EAreaType, int> OwnLevels()
        {
            var levels = new Dictionary<EAreaType, int>();
            var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
            var areas = app?.Session?.Profile?.Hideout?.Areas;
            if (areas != null) foreach (var a in areas) levels[a.AreaType] = a.Level;
            return levels;
        }

        /// <param name="hostKnown">False on another player's game before the host has said what its hideout has: what other
        /// mods add to it waits until it has.</param>
        internal static void Apply(bool hostKnown = true)
        {
            var levels = Levels();
            var areas = Object.FindObjectsOfType<HideoutArea>(true);
            int shown = 0;
            foreach (var area in areas)
            {
                try
                {
                    levels.TryGetValue(area.AreaTemplate.Type, out int level);
                    ShowLevel(area, level);
                    area.enabled = false;
                    shown++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[map] Couldn't set up {area.name}: {e.Message}"); }
            }
            // Once a raid: the areas they move would move again.
            if (hostKnown && !copiesBuilt)
            {
                copiesBuilt = true;
                var rest = areas.FirstOrDefault(a => a.AreaTemplate != null && a.AreaTemplate.Type == EAreaType.RestSpace);
                Matrix4x4 restBefore = rest != null ? rest.transform.localToWorldMatrix : Matrix4x4.identity;
                BuildCopies();
                restMoved = rest != null ? rest.transform.localToWorldMatrix * restBefore.inverse : Matrix4x4.identity;
                if (rest != null && (InRestSpace(Vector3.zero) - Vector3.zero).sqrMagnitude > .01f) Plugin.Log.LogInfo($"[map] The Rest Space has been moved: the beds are by {InRestSpace(new Vector3(15.7f, 0f, -1.6f)):F1}.");
            }
            var lighting = TurnLightsOn(areas, levels);
            foreach (var area in areas)
            {
                var parts = area.GetComponentsInChildren<Renderer>().Where(r => r.enabled).Select(r => r.bounds).ToList();
                if (parts.Count == 0) continue;
                var box = parts[0];
                foreach (var part in parts) box.Encapsulate(part);
                Plugin.Log.LogInfo($"[map] Station {area.AreaTemplate.Type} level {(levels.TryGetValue(area.AreaTemplate.Type, out int shownLevel) ? shownLevel : 0)}: from {box.min:F1} to {box.max:F1}.");
            }
            Plugin.Log.LogInfo($"[map] Hideout copy: {shown}/{areas.Length} stations set, lighting {lighting}, defences {DefenceRating.Percent(levels):F0}%.");
        }

        // What the real hideout does when it opens: the Illumination station decides how well lit the place is. Nothing does
        // this in a raid, and the hideout would sit in near darkness with the generator running.
        private static ELightingLevel TurnLightsOn(HideoutArea[] areas, Dictionary<EAreaType, int> levels)
        {
            levels.TryGetValue(EAreaType.Illumination, out int illumination);
            var best = illumination >= 3 ? ELightingLevel.FluorescentLamps : illumination == 2 ? ELightingLevel.LightBulbs : illumination == 1 ? ELightingLevel.Candles : ELightingLevel.Off;
            var available = new[] { ELightingLevel.Off, best };
            foreach (var area in areas)
            {
                try
                {
                    levels.TryGetValue(area.AreaTemplate.Type, out int level);
                    var list = area.AreaLevels;
                    if (level >= list.Length || list[level] == null) continue;
                    list[level].SetAvailableLightingLevels(available);
                    list[level].SetLightLevel(best);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[map] Couldn't light {area.name}: {e.Message}"); }
            }
            TellLampsAboutPower(true);
            var hideout = Object.FindObjectOfType<HideoutController>(true);
            if (hideout != null && hideout._globalLighting != null)
            {
                foreach (var pair in hideout._globalLighting)
                    if (pair.Value != null) pair.Value.SetActive(pair.Key == best);
                Plugin.Log.LogInfo($"[map] The hideout's lighting: {string.Join(", ", hideout._globalLighting.Where(p => p.Value != null).Select(p => p.Key))}; using {best}.");
            }
            else Plugin.Log.LogWarning("[map] The hideout has no lighting of its own to switch on.");
            return best;
        }

        // The hideout's lamps are told by this whether there's power. Nothing tells them in a raid, so they'd sit there off.
        // It ignores everything until the real hideout has shown it the stations, which here never happens.
        internal static void TellLampsAboutPower(bool on)
        {
            try
            {
                var ambiance = Object.FindObjectOfType<AmbianceController>(true);
                if (ambiance == null) { Plugin.Log.LogWarning("[map] No ambiance controller, so the hideout's lamps are staying as they are."); return; }
                Traverse.Create(ambiance).Field("_datas").SetValue(new List<AreaData>());
                ambiance.EnergySupplyChanged(on);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Couldn't switch the hideout's lamps: " + e); }
        }

        // ---- what other mods have done to the hideout ----------------------------------------------------------

        // Mods that change a player's hideout and can put the same into this copy, in the order they go in: the rooms and
        // where the areas stand first, then the things in them. Each has RaidCopySnapshot() and BuildRaidCopy(string).
        private static readonly (string Name, string Assembly, string Type)[] Copiers =
        {
            ("Hideout Extensions", "HideoutExtensions", "HideoutExtensions.HideoutExtensionsApi"),
            ("Hideout Interiors", "HideoutInteriors", "HideoutInteriors.HideoutInteriorsApi"),
        };

        // Set when another player's game told us what the host's hideout has; otherwise this player's, taken once a raid.
        internal static Dictionary<string, string> HostCopies;
        private static Dictionary<string, string> copies;
        private static Matrix4x4 restMoved = Matrix4x4.identity;
        internal static bool CopiesBuilt => copiesBuilt;
        private const string DoorKey = "Hideout Siege front door", SeedKey = "Hideout Siege seed";

        /// <summary>How many of the front door's upgrades the hideout has (the host's, in co-op).</summary>
        internal static int DoorLevel => Copies().TryGetValue(DoorKey, out string level) && int.TryParse(level, out int n) ? n : 0;

        /// <summary>
        /// This invasion's roll of the dice, the host's in co-op, so every game makes the same choices from it (which room
        /// they tunnel to). Null from a host whose siege is older than this.
        /// </summary>
        /// <summary>For the test driver: the roll the next siege uses, to replay one.</summary>
        internal static int? NextRoll;

        internal static int? Seed => Copies().TryGetValue(SeedKey, out string seed) && int.TryParse(seed, out int n) ? n : (int?)null;
        private static bool copiesBuilt;

        /// <summary>A point by the beds, where it is now that other mods may have moved the Rest Space.</summary>
        internal static Vector3 InRestSpace(Vector3 point) => restMoved.MultiplyPoint3x4(point);

        internal static Dictionary<string, string> Copies()
        {
            SiegeOwner.Ask();
            if (HostCopies != null) return HostCopies;
            return copies ?? (copies = Snapshot(true));
        }

        /// <summary>
        /// What this player's hideout has, read now: the front door, the invasion's roll and what other mods add.
        /// <paramref name="useRoll"/>: whether this uses up the test driver's roll.
        /// </summary>
        internal static Dictionary<string, string> Snapshot(bool useRoll)
        {
            var copies = new Dictionary<string, string>();
            copies[DoorKey] = SiegeDoorUpgrades.Level().ToString();
            copies[SeedKey] = (NextRoll ?? new System.Random().Next(1, int.MaxValue)).ToString();
            if (useRoll) NextRoll = null;
            foreach (var copier in Copiers)
                try
                {
                    var take = Api(copier.Assembly, copier.Type, "RaidCopySnapshot");
                    if (take != null) copies[copier.Name] = take.Invoke(null, null) as string ?? "";
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[map] Couldn't ask {copier.Name} what this hideout has: {e.InnerException?.Message ?? e.Message}"); }
            return copies;
        }

        private static void BuildCopies()
        {
            var have = Copies();
            foreach (var copier in Copiers)
            {
                if (!have.TryGetValue(copier.Name, out string snapshot)) continue;
                var build = Api(copier.Assembly, copier.Type, "BuildRaidCopy");
                if (build == null) { Plugin.Log.LogWarning($"[map] The host's hideout uses {copier.Name}, which isn't installed here: that part of it is missing."); continue; }
                try
                {
                    build.Invoke(null, new object[] { snapshot });
                    Plugin.Log.LogInfo($"[map] Hideout copy: {copier.Name} put in.");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[map] {copier.Name} couldn't put its part of the hideout in: {e.InnerException ?? e}"); }
            }
        }

        // Found by name, so the siege works with or without each mod.
        private static System.Reflection.MethodInfo Api(string assembly, string type, string method)
        {
            var found = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type, false);
            return found?.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        }

        // Set when another player's game told us which walls, floor and ceiling the host has.
        internal static Dictionary<EHideoutCustomizationType, string> HostPack;
        private static readonly List<Diz.DependencyManager.DependencyGraph<Diz.Resources.IEasyBundle>.Token> tokens = new List<Diz.DependencyManager.DependencyGraph<Diz.Resources.IEasyBundle>.Token>();
        private static readonly EHideoutCustomizationType[] Surfaces = { EHideoutCustomizationType.Floor, EHideoutCustomizationType.Wall, EHideoutCustomizationType.Ceiling };

        internal static Dictionary<EHideoutCustomizationType, string> Pack()
        {
            SiegeOwner.Ask();
            return HostPack ?? OwnPack();
        }

        /// <summary>This profile's own walls, floor and ceiling.</summary>
        internal static Dictionary<EHideoutCustomizationType, string> OwnPack()
        {
            var pack = new Dictionary<EHideoutCustomizationType, string>();
            var app = Singleton<ClientApplication<IEftSession>>.Instance as TarkovApplication;
            var chosen = app?.Session?.Profile?.Hideout?.GlobalCustomization;
            if (chosen != null)
                foreach (var pair in chosen)
                    if (pair.Value.HasValue) pack[pair.Key] = pair.Value.Value.ToString();
            return pack;
        }

        // Your chosen walls, floor and ceiling, read from disk. On the loading screen for the host, so they go in before Hideout
        // Extensions copies its rooms (which follow the hall's style): put in later, it would build every room again, a long
        // freeze mid-cinematic. The defaults stay up if anything fails.
        private static readonly List<(EHideoutCustomizationType Type, EFT.Customization.HideoutCustomizationItem Item)> loaded = new List<(EHideoutCustomizationType, EFT.Customization.HideoutCustomizationItem)>();
        private static bool installed, loadedAhead, loadedOwn;

        private static async System.Threading.Tasks.Task<bool> Load()
        {
            // What was read is your own, but the host's is wanted: read again.
            if (loadedAhead && loadedOwn && HostPack != null) loadedAhead = false;
            if (loadedAhead) return true;
            bool own = HostPack == null;
            var solver = Singleton<CustomizationSolver>.Instance;
            var pack = Pack();
            if (solver == null || pack.Count == 0) return false;
            var found = new List<(EHideoutCustomizationType, EFT.Customization.HideoutCustomizationItem)>();
            foreach (var surface in Surfaces)
            {
                if (!pack.TryGetValue(surface, out string id)) continue;
                var item = solver.HideoutCustomizationItems.FirstOrDefault(i => i.Id.ToString() == id);
                if (item == null) continue;
                var token = Singleton<Diz.Resources.IEasyAssets>.Instance.Retain(new[] { item.Prefab.path });
                tokens.Add(token);
                await EFT.EasyAssetsExtensions.LoadBundles(token);
                found.Add((surface, item));
            }
            loaded.Clear();
            loaded.AddRange(found);
            loadedAhead = true;
            loadedOwn = own;
            return true;
        }

        /// <summary>On the loading screen, the host's own choices: read in time to go in before the rooms are copied.</summary>
        internal static async System.Threading.Tasks.Task LoadCustomizationAhead()
        {
            try
            {
                SiegeOwner.Ask();
                // A headless has the player's it's hosting for already: those are read ahead the same.
                if ((HostPack == null || SiegeOwner.OnHeadless) && !Net.IsClient && await Load())
                    Plugin.Log.LogInfo($"[map] Hideout copy: {loaded.Count} customised surfaces loaded ahead.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Couldn't read your wall, floor and ceiling styles ahead: " + e.Message); }
        }

        /// <summary>Puts in what was read ahead, if it was: called just before the rooms are copied.</summary>
        internal static void InstallLoaded()
        {
            if (installed || !loadedAhead) return;
            try { Install(); }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Couldn't install your wall, floor and ceiling styles: " + e); }
        }

        /// <summary>Reads (if it wasn't ahead) and puts in the walls, floor and ceiling: the host's, on another player's game.</summary>
        internal static async System.Threading.Tasks.Task InstallCustomization()
        {
            if (installed) return;
            try { if (await Load()) Install(); }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Couldn't install your wall, floor and ceiling styles: " + e); }
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            var installer = Object.FindObjectOfType<HideoutCustomizationItemsInstaller>(true);
            if (installer == null || loaded.Count == 0) return;
            var models = new List<(EHideoutCustomizationType Type, StashItemModel Model)>();
            foreach (var (surface, item) in loaded)
            {
                var model = Singleton<Diz.Resources.IEasyAssets>.Instance.InstantiateAsset<StashItemModel>(item.Prefab);
                if (model != null) models.Add((surface, model));
            }
            if (models.Count == 0) return;
            installer.Init();
            foreach (var (type, model) in models) installer.SetItem(type, model);
            Plugin.Log.LogInfo($"[map] Hideout copy: {models.Count} customised surfaces installed.");
        }

        internal static void Release()
        {
            HostPack = null;
            HostCopies = null;
            copies = null;
            copiesBuilt = false;
            restMoved = Matrix4x4.identity;
            installed = false;
            loadedAhead = false;
            loaded.Clear();
            foreach (var token in tokens)
                try { token.Release(); }
                catch (Exception e) { Plugin.Log.LogWarning(e.Message); }
            tokens.Clear();
        }

        private static void ShowLevel(HideoutArea area, int level)
        {
            var list = area.AreaLevels;
            for (int i = 0; i < list.Length; i++)
                if (i != level && list[i] != null) list[i].gameObject.SetActive(false);
            if (area.AreaDefaultLevel != null) area.AreaDefaultLevel.gameObject.SetActive(level == 0);
            if (level >= list.Length || list[level] == null) return;
            // The way the hideout itself does it. Some levels keep walls and doors outside the highlighted part, such as
            // the Emergency Wall's extension, and without those there are holes in the hideout.
            list[level].Enable(true);
        }
    }
}
