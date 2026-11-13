using System.Collections.Generic;
using System.Linq;
using EFT;

namespace HideoutSiege
{
    /// <summary>How strong your hideout's defences are, and what that brings to your door.</summary>
    internal static class DefenceRating
    {
        // Station, the level it counts up to, and how much it counts.
        private static readonly (EAreaType Area, int Max, float Weight)[] Parts =
        {
            (EAreaType.Security, 3, 4f),
            (EAreaType.EmergencyWall, 6, 3f),
            (EAreaType.IntelligenceCenter, 3, 2f),
            (EAreaType.ShootingRange, 3, 2f),
            (EAreaType.Generator, 3, 1f),
            (EAreaType.Illumination, 3, 1f),
        };

        internal static float Percent(IReadOnlyDictionary<EAreaType, int> levels)
        {
            float total = Parts.Sum(p => p.Weight), have = 0;
            foreach (var part in Parts)
            {
                levels.TryGetValue(part.Area, out int level);
                have += part.Weight * UnityEngine.Mathf.Clamp01(level / (float)part.Max);
            }
            return have / total * 100f;
        }

        internal static int Tier(float percent)
        {
            if (percent < 25f) return 1;
            if (percent < 50f) return 2;
            if (percent < 75f) return 3;
            return 4;
        }

        // Black Division's bots come from another mod. Raiders and Rogues stand in without it, so say who's really coming.
        private static string Elite => SiegeDirector.HaveBlackDivision() ? "Black Division" : "Raiders and Rogues";

        internal static string TierName(int tier)
        {
            switch (tier)
            {
                case 1: return "Scavs and a Raider, then Reshala or Tagilla";
                case 2: return "Scavs, Raiders and PMCs, then a boss";
                case 3: return $"Up to Smugglers and Rogues, then a boss or {Elite}";
                default: return $"Up to Smugglers and Rogues, then a boss or a full {Elite} assault";
            }
        }

        private static WaveSpec Wave(params (Rung, int)[] parts) => new WaveSpec(parts);

        // Every siege builds the same way: mixed waves that get harder (scavs with a Raider among them, then Raiders and
        // PMCs, then Smugglers and Rogues), as many as the hideout's defences call for, and then the last wave, which is
        // this invasion's roll: one of the bosses with his people, or Black Division (Raiders and Rogues stand in for them
        // without that mod).
        internal static WaveSpec[] Waves(int tier, int? seed)
        {
            WaveSpec[] build;
            switch (tier)
            {
                case 1: build = new[] { Wave((Rung.Scavs, 2)), Wave((Rung.Scavs, 2), (Rung.Raiders, 1)) }; break;
                case 2: build = new[] { Wave((Rung.Scavs, 2), (Rung.Raiders, 1)), Wave((Rung.Scavs, 1), (Rung.Raiders, 1), (Rung.Pmcs, 1)) }; break;
                case 3: build = new[] { Wave((Rung.Scavs, 2), (Rung.Raiders, 1)), Wave((Rung.Raiders, 2), (Rung.Pmcs, 2)), Wave((Rung.Smugglers, 2), (Rung.Rogues, 1)) }; break;
                default: build = new[] { Wave((Rung.Scavs, 2), (Rung.Raiders, 1)), Wave((Rung.Raiders, 2), (Rung.Pmcs, 2)), Wave((Rung.Pmcs, 1), (Rung.Smugglers, 2)), Wave((Rung.Smugglers, 2), (Rung.Rogues, 2)) }; break;
            }
            return build.Concat(new[] { LastWave(tier, seed) }).ToArray();
        }

        // How many Black Division close a siege at each defence level. They're stronger than any boss, so they only come
        // from 50% defences; the smaller numbers are for a siege from a host too old to roll for its last wave.
        private static readonly int[] BlackDivisionCloses = { 2, 3, 4, 5 };
        internal const int BlackDivisionFromTier = 3;

        // Who might lead the last wave, and from which defence level up. Killa and Tagilla work alone; Reshala brings scavs,
        // Glukhar Raiders. Guards are fewer than they'd have on their own maps: the hideout's a small place.
        internal static readonly BossPack[] Bosses =
        {
            new BossPack { Name = "RESHALA", Boss = WildSpawnType.bossBully, Guards = new[] { (WildSpawnType.followerBully, 2) }, Escort = new[] { (Rung.Scavs, 3) }, FromTier = 1 },
            new BossPack { Name = "KILLA", Boss = WildSpawnType.bossKilla, FromTier = 2 },
            new BossPack { Name = "TAGILLA", Boss = WildSpawnType.bossTagilla, FromTier = 1 },
            new BossPack { Name = "SANITAR", Boss = WildSpawnType.bossSanitar, Guards = new[] { (WildSpawnType.followerSanitar, 2) }, Escort = new[] { (Rung.Scavs, 2) }, FromTier = 2 },
            new BossPack { Name = "GLUKHAR", Boss = WildSpawnType.bossGluhar, Guards = new[] { (WildSpawnType.followerGluharAssault, 2), (WildSpawnType.followerGluharSecurity, 1) }, Escort = new[] { (Rung.Raiders, 2) }, FromTier = 3 },
            new BossPack { Name = "KABAN", Boss = WildSpawnType.bossBoar, Guards = new[] { (WildSpawnType.followerBoar, 3) }, Escort = new[] { (Rung.Scavs, 2) }, FromTier = 3 },
            new BossPack { Name = "KOLLONTAY", Boss = WildSpawnType.bossKolontay, Guards = new[] { (WildSpawnType.followerKolontayAssault, 2), (WildSpawnType.followerKolontaySecurity, 1) }, Escort = new[] { (Rung.Scavs, 2) }, FromTier = 3 },
            new BossPack { Name = "THE GOONS", Boss = WildSpawnType.bossKnight, Guards = new[] { (WildSpawnType.followerBigPipe, 1), (WildSpawnType.followerBirdEye, 1) }, FromTier = 4 },
        };

        /// <summary>
        /// The last wave, on this invasion's roll: one of the bosses this defence level brings, or Black Division from 50%,
        /// by the Last wave sliders. The same answer every time it's asked, so who's loaded on the loading screen is who
        /// turns up. With no roll (an older host's siege), Black Division.
        /// </summary>
        /// <summary>For the test driver: the last wave the next sieges get, whatever the roll (a boss's name, or "bd").</summary>
        internal static string ForceLast;

        private static WaveSpec LastWave(int tier, int? seed)
        {
            var blackDivision = Wave((Rung.BlackDivision, BlackDivisionCloses[UnityEngine.Mathf.Clamp(tier, 1, 4) - 1]));
            if (!string.IsNullOrEmpty(ForceLast))
            {
                var forced = Bosses.FirstOrDefault(b => string.Equals(b.Name, ForceLast, System.StringComparison.OrdinalIgnoreCase));
                return forced != null ? new WaveSpec(forced.Escort, forced) : blackDivision;
            }
            if (seed == null) return blackDivision;
            // Who can come at this defence level, each with his slider; anyone who can't yet leaves his share to them.
            var open = new List<(int share, WaveSpec wave)>();
            if (tier >= BlackDivisionFromTier) open.Add((LastWaveShares.Of(LastWaveShares.BlackDivision), blackDivision));
            foreach (var boss in Bosses.Where(b => b.FromTier <= tier)) open.Add((LastWaveShares.Of(boss.Name), new WaveSpec(boss.Escort, boss)));
            // Everyone who could come is at 0: an even pick among them, as it has to be someone.
            if (open.Sum(o => o.share) <= 0) open = open.Select(o => (1, o.wave)).ToList();
            var dice = new System.Random(seed.Value ^ 0x0B055E5);
            int roll = dice.Next(open.Sum(o => o.share));
            foreach (var (share, wave) in open)
            {
                if (roll < share) return wave;
                roll -= share;
            }
            return open[open.Count - 1].wave;
        }
    }
}
