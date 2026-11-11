using System;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;

namespace HideoutSiege
{
    /// <summary>What the Fika link does for the siege. The client plugin works without Fika, so it only knows this much.</summary>
    internal interface ISiegeLink
    {
        bool Online { get; }
        bool IsHost { get; }
        int PeerCount { get; }
        bool HadPeers { get; }
        void SendState();
        void SendDoor(bool blast, UnityEngine.Vector3 at, bool held);
        void SendCarry(string profileId);
        void SendGun(UnityEngine.Vector3 where, UnityEngine.Vector3 aimAt);
        void SendGenerator(bool off, bool cut);
        void SendSandbags(UnityEngine.Vector3 at, float yaw);
        void SendTurret(UnityEngine.Vector3 from, UnityEngine.Vector3 aim, int shots);
    }

    /// <summary>Loads the Fika link only where Fika is installed, the same way Experimental Ordnance does.</summary>
    internal static class Net
    {
        private const string FikaId = "com.fika.core", LinkFile = "HideoutSiege.Fika.dll", LinkType = "HideoutSiege.Fika.FikaLink";
        private static ISiegeLink link;

        internal static bool IsClient => link != null && link.Online && !link.IsHost;
        internal static bool IsHost => link != null && link.Online && link.IsHost;
        internal static int PeerCount => link?.PeerCount ?? 0;
        internal static bool HadPeers => link?.HadPeers ?? false;

        internal static void Load()
        {
            if (!Chainloader.PluginInfos.ContainsKey(FikaId)) return;
            try
            {
                string path = Path.Combine(Plugin.Folder, LinkFile);
                if (!File.Exists(path)) { Plugin.Log.LogWarning($"Fika is installed but {LinkFile} isn't: other players won't see the wave counter."); return; }
                AppDomain.CurrentDomain.AssemblyResolve += AlreadyLoaded;
                link = (ISiegeLink)Activator.CreateInstance(Assembly.LoadFrom(path).GetType(LinkType, true), true);
                Plugin.Log.LogInfo("Fika found: the siege is shared with the other players in the raid.");
            }
            catch (Exception e) { Plugin.Log.LogError("The Fika link couldn't be started: " + e); link = null; }
        }

        private static Assembly AlreadyLoaded(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return a;
            return null;
        }

        // The rest of a co-op raid follows along with what the host's director says.
        internal static void ReceivedState(int phase, int wave, int total, int remaining, int tier, int kills, string banner, float bannerSeconds, float countdownSeconds)
        {
            var previous = SiegeState.Phase;
            var now = (SiegePhase)phase;
            SiegeState.Phase = now;
            SiegeState.Wave = wave;
            SiegeState.TotalWaves = total;
            SiegeState.Remaining = remaining;
            SiegeState.Tier = tier;
            SiegeState.Kills = kills;
            SiegeState.Banner = banner;
            SiegeState.BannerUntil = UnityEngine.Time.time + bannerSeconds;
            SiegeState.CountdownUntil = UnityEngine.Time.time + countdownSeconds;
            if (now == previous) return;
            switch (now)
            {
                case SiegePhase.Cinematic:
                    Plugin.Instance.StartCoroutine(SiegeCinematic.Play());
                    break;
                case SiegePhase.Prep:
                    SiegeSounds.StartAlarm();
                    break;
                case SiegePhase.Fighting:
                    SiegeBreach.Seal();
                    SiegeBreach.Blow();
                    SiegeSounds.StartAlarm();
                    SiegeSounds.QuietAlarm();
                    SiegeSounds.Play("wave");
                    break;
                case SiegePhase.Won:
                    SiegeSounds.Play("victory");
                    SiegeSounds.StopAlarm();
                    SiegeBackend.Report(true, tier, wave, kills);
                    Plugin.Instance.StartCoroutine(LeaveAt(EFT.ExitStatus.Survived));
                    break;
                case SiegePhase.Lost:
                    SiegeBackend.Report(false, tier, wave, kills);
                    SiegeState.CountdownUntil = UnityEngine.Time.time + 7f;
                    Plugin.Instance.StartCoroutine(LeaveAt(EFT.ExitStatus.Killed));
                    break;
            }
        }

        private static System.Collections.IEnumerator LeaveAt(EFT.ExitStatus status)
        {
            while (UnityEngine.Time.time < SiegeState.CountdownUntil - 3f) yield return null;
            SiegeExit.Leave(status);
        }

        internal static async void ReceivedLayout(int[] types, int[] levels, int[] surfaceTypes, string[] surfaceIds, string[] copyNames, string[] copySnapshots)
        {
            var pack = new System.Collections.Generic.Dictionary<EFT.Hideout.EHideoutCustomizationType, string>();
            for (int i = 0; i < surfaceTypes.Length; i++) pack[(EFT.Hideout.EHideoutCustomizationType)surfaceTypes[i]] = surfaceIds[i];
            HideoutCopy.HostPack = pack;
            // The host's walls go in before its rooms are copied, so Hideout Extensions builds them once, in that style.
            await HideoutCopy.InstallCustomization();
            var layout = new System.Collections.Generic.Dictionary<EFT.EAreaType, int>();
            for (int i = 0; i < types.Length; i++) layout[(EFT.EAreaType)types[i]] = levels[i];
            HideoutCopy.HostLayout = layout;
            // What other mods add to the host's hideout (a host without them, or an older siege, sends none).
            var copies = new System.Collections.Generic.Dictionary<string, string>();
            for (int i = 0; i < (copyNames?.Length ?? 0); i++) copies[copyNames[i]] = copySnapshots[i];
            bool firstCopies = !HideoutCopy.CopiesBuilt;
            HideoutCopy.HostCopies = copies;
            // Rooms put in now need the floor walked again (where you wake up, and the way in for the camera).
            if (firstCopies && copies.Count > 0) SiegeMap.PrepareToWatch(true);
            else HideoutCopy.Apply();
        }

        internal static void ReceivedDoor(bool blast, UnityEngine.Vector3 at, bool held = false)
        {
            if (held) SiegeBreach.Held(at);
            else if (blast) SiegeBreach.Blast(at);
            else SiegeBreach.Plant(at);
        }

        internal static void SendGun(UnityEngine.Vector3 where, UnityEngine.Vector3 aimAt)
        {
            if (link == null || !link.Online) return;
            try { link.SendGun(where, aimAt); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the other players: " + e.Message); }
        }

        // Nobody, when there's no name.
        internal static void ReceivedCarry(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
            {
                SiegeBreach.StopCarrying();
                return;
            }
            var world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            SiegeBreach.Carry(world?.AllAlivePlayersList.Find(p => p != null && p.ProfileId == profileId));
        }

        internal static void SendCarry(string profileId)
        {
            if (link == null || !link.Online) return;
            try { link.SendCarry(profileId); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the other players: " + e.Message); }
        }

        // Anyone can flip the switch, so this can come from any player, by way of the host.
        // A player put sandbags down: another player's game asks the host, which puts them in and tells everyone.
        internal static void SendSandbags(UnityEngine.Vector3 at, float yaw)
        {
            if (link == null || !link.Online) return;
            try { link.SendSandbags(at, yaw); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
        }

        internal static void ReceivedSandbags(UnityEngine.Vector3 at, float yaw)
        {
            SiegeSandbags.Place(at, yaw);
            if (IsHost) SendSandbags(at, yaw);
        }

        // The host's turret fired a burst: the others hear it and see the flash.
        internal static void SendTurret(UnityEngine.Vector3 from, UnityEngine.Vector3 aim, int shots)
        {
            if (!IsHost) return;
            try { link.SendTurret(from, aim, shots); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
        }

        internal static void SendGenerator(bool off, bool cut)
        {
            if (link == null || !link.Online) return;
            try { link.SendGenerator(off, cut); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the other players: " + e.Message); }
        }

        internal static void SendDoor(bool blast, UnityEngine.Vector3 at, bool held = false)
        {
            if (link == null || !link.Online || !link.IsHost) return;
            try { link.SendDoor(blast, at, held); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the other players: " + e.Message); }
        }

        internal static void Broadcast()
        {
            if (link == null || !link.Online || !link.IsHost) return;
            try { link.SendState(); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the other players: " + e.Message); }
        }
    }
}
