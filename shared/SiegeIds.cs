namespace HideoutSiege
{
    /// <summary>Names both the server and the client need to agree on.</summary>
    internal static class SiegeIds
    {
        // The siege borrows an unused location slot, so mods that skip the real hideout treat it as an ordinary raid.
        public const string LocationId = "Suburbs";
        public const string HideoutPreset = "assets/content/locations/_presets/bunker.scenespreset.bundle";
        public const string RouteState = "/hideoutsiege/state";
        public const string RouteTrigger = "/hideoutsiege/trigger";
        public const string RouteResult = "/hideoutsiege/result";
        public const string RouteGear = "/hideoutsiege/gear";
        public const string RouteDoor = "/hideoutsiege/door";
        public const string RouteDefeatSeen = "/hideoutsiege/defeatseen";
        // A player's hideout, said by their game as they start the defence; and, for a Fika headless, whose siege it's hosting.
        public const string RouteLayout = "/hideoutsiege/layout";
        public const string RouteOwner = "/hideoutsiege/owner";
        // Copies of what's on your real racks, as they appear in a siege, all start with this.
        public const string GearPrefix = "5151e9e0";
        // Your stash in the siege: a box kept inside the Stash station. What you put in it goes to your real stash.
        public const string StashBox = "5151e9e1000000000000057a";
        // A box nobody owns with a big grid that takes anything: the developers' junkbox, renamed "Stash" for the siege.
        public const string StashBoxTemplate = "5c0a5f6c86f774753654890e";
    }
}
