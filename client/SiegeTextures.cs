using EFT.Settings.Graphics;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// Room for textures in the siege. The game keeps what it loads of each texture within a budget (1024 MB, by your
    /// texture quality), sized for a raid's map, where nearly everything is loaded a bit at a time as it's needed. The
    /// hideout's own textures aren't: they're always there in full, about 1600 MB of them, and they count against the same
    /// budget. So in a siege it was spent before a single gun was drawn: guns and attackers were held at their roughest
    /// textures, and a gun's picture in a loot panel, which waits for its sharpest ones before it's drawn, waited the whole
    /// 200 frames the game allows (about 3 seconds) every time, for every gun. Here the budget is what the game set plus
    /// what the hideout holds, so there's the room a raid would have; it goes back as the siege ends.
    /// </summary>
    internal static class SiegeTextures
    {
        private const float Megabyte = 1024f * 1024f, Step = 64f, ForAPicture = 100f;
        // What the game had set, and what it's been raised to (under 0: not raised).
        private static float gamesOwn, raisedTo = -1f;

        /// <summary>Once a second.</summary>
        internal static void Tick()
        {
            if (!SiegeLauncher.InSiege)
            {
                if (raisedTo < 0f) return;
                // Only if it's still ours: the game sets its own again when the graphics settings change.
                if (Mathf.Approximately(GraphicsSettingsController.MipStreamingMemoryBudget, raisedTo)) Set(gamesOwn);
                raisedTo = -1f;
                return;
            }
            float now = GraphicsSettingsController.MipStreamingMemoryBudget;
            bool ours = raisedTo >= 0f && Mathf.Approximately(now, raisedTo);
            if (!ours) gamesOwn = now;
            float wanted = gamesOwn + Mathf.Ceil(Texture.nonStreamingTextureMemory / Megabyte / Step) * Step;
            if (ours && wanted <= raisedTo) return;
            raisedTo = wanted;
            Set(wanted);
            Plugin.Log.LogInfo($"[siege] Room for textures: {wanted:F0} MB (the game's {gamesOwn:F0} and the {Texture.nonStreamingTextureMemory / Megabyte:F0} the hideout always holds).");
        }

        private static void Set(float budget)
        {
            GraphicsSettingsController.MipStreamingMemoryBudget = budget;
            // While a picture's being drawn the game has it this much higher, and puts it back from the figure above after.
            QualitySettings.streamingMipmapsMemoryBudget = budget + (ItemIconCreator.requests > 0 ? ForAPicture : 0f);
        }
    }
}
