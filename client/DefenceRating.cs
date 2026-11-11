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
        private static string Elite => SiegeDirector.HaveBlackDivision() ? "Black Division" : "Raider";

        internal static string TierName(int tier)
        {
            switch (tier)
            {
                case 1: return $"Scavs, then a {Elite} squad";
                case 2: return $"Scavs, then {Elite}";
                case 3: return $"Scavs, then a {Elite} push";
                default: return $"Scavs, then a full {Elite} assault";
            }
        }

        // Every siege is the same story: scavs for the first two waves, then Black Division from the third (Raiders and Rogues
        // stand in without that mod). The better defended the hideout, the more waves and the bigger they are.
        internal static WaveSpec[] Waves(int tier)
        {
            switch (tier)
            {
                case 1: return new[] { new WaveSpec(2, 0, 0), new WaveSpec(3, 0, 0), new WaveSpec(0, 0, 2) };
                case 2: return new[] { new WaveSpec(3, 0, 0), new WaveSpec(3, 0, 0), new WaveSpec(0, 0, 3) };
                case 3: return new[] { new WaveSpec(3, 0, 0), new WaveSpec(4, 0, 0), new WaveSpec(0, 0, 3), new WaveSpec(0, 0, 4) };
                default: return new[] { new WaveSpec(3, 0, 0), new WaveSpec(4, 0, 0), new WaveSpec(0, 0, 3), new WaveSpec(0, 0, 4), new WaveSpec(0, 0, 5) };
            }
        }
    }
}
