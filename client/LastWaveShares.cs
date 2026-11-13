using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;

namespace HideoutSiege
{
    /// <summary>
    /// The F12 sliders for who leads the last wave: Black Division and each boss, as shares that always add up to 100.
    /// Move one and the rest make room for it, or take up what it gave back, keeping their sizes next to each other.
    /// </summary>
    internal static class LastWaveShares
    {
        internal const string BlackDivision = "BLACK DIVISION";

        private sealed class Share
        {
            internal string Key;
            internal int Default;
            internal ConfigEntry<int> Entry;
            // The slider's size next to the others, kept exactly: the sliders show whole numbers, and one pushed to 100
            // shows the rest at 0 without forgetting how they stood, so they come back the same when it's pulled down.
            internal double Weight;
        }

        private static readonly List<Share> shares = new List<Share>();
        private static bool balancing;

        internal static void Bind(ConfigFile config)
        {
            Add(config, BlackDivision, "Black Division (%)", 20, "Black Division (Raiders and Rogues without that mod). They're stronger than any boss, so they only come once your defences reach 50%.");
            foreach (var pack in DefenceRating.Bosses)
            {
                string name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(pack.Name.ToLowerInvariant());
                string from = pack.FromTier <= 1 ? "at any defence level" : $"once your defences reach {(pack.FromTier - 1) * 25}%";
                Add(config, pack.Name, name + " (%)", 10, $"{name} and his people. He comes {from}.");
            }

            foreach (var share in shares) share.Weight = Math.Max(0, share.Entry.Value);
            if (shares.Sum(s => s.Weight) <= 0) foreach (var share in shares) share.Weight = share.Default;
            Show(shares, 100);

            foreach (var share in shares)
            {
                var moved = share;
                share.Entry.SettingChanged += (_, __) => Moved(moved);
            }
        }

        private static void Add(ConfigFile config, string key, string label, int byDefault, string who)
        {
            var entry = config.Bind("Last wave", label, byDefault, new ConfigDescription(
                $"{who} The chance he's the last wave. All the sliders here add up to 100: move one and the others make room, keeping their sizes next to each other. " +
                "If someone can't come yet at your defence level, his share is split among those who can. 0 means never. The host's setting counts.",
                new AcceptableValueRange<int>(0, 100)));
            shares.Add(new Share { Key = key, Default = byDefault, Entry = entry });
        }

        /// <summary>A slider's value (by a boss's name in the wave list, or <see cref="BlackDivision"/>), 0 to 100.</summary>
        internal static int Of(string key) => Math.Max(0, shares.FirstOrDefault(s => s.Key == key)?.Entry.Value ?? 0);

        private static void Moved(Share moved)
        {
            if (balancing) return;
            balancing = true;
            try
            {
                int value = Math.Max(0, Math.Min(100, moved.Entry.Value));
                moved.Weight = value;
                var others = shares.Where(s => s != moved).ToList();
                int rest = 100 - value;
                // Nothing left to share among the others: only what they show changes, so they come back as they were.
                if (rest > 0 && others.Sum(s => s.Weight) <= 0) foreach (var other in others) other.Weight = 1;
                if (rest > 0)
                {
                    double sum = others.Sum(s => s.Weight);
                    foreach (var other in others) other.Weight = other.Weight * rest / sum;
                }
                Show(others, rest);
                if (moved.Entry.Value != value) moved.Entry.Value = value;
            }
            finally { balancing = false; }
        }

        // Shows these sliders as whole numbers adding up to exactly this, by their weights: the ones rounded down the
        // most get the points left over.
        private static void Show(List<Share> these, int total)
        {
            double sum = these.Sum(s => s.Weight);
            var exact = these.Select(s => sum > 0 ? s.Weight * total / sum : (double)total / these.Count).ToList();
            var shown = exact.Select(x => (int)Math.Floor(x)).ToList();
            int left = total - shown.Sum();
            foreach (int i in Enumerable.Range(0, these.Count).OrderByDescending(i => exact[i] - shown[i]).Take(left)) shown[i]++;
            bool was = balancing;
            balancing = true;
            try
            {
                for (int i = 0; i < these.Count; i++)
                    if (these[i].Entry.Value != shown[i]) these[i].Entry.Value = shown[i];
            }
            finally { balancing = was; }
        }
    }
}
