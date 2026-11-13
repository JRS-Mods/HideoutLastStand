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
        // A player's game says whose siege it's in, and is told which stash box is theirs.
        public const string RouteJoin = "/hideoutsiege/join";
        // The stash boxes in the siege, kept inside the Stash station: one each, of a set size. What a player puts in
        // theirs goes to their real stash. Each one's id is this and its number (two hex digits).
        public const string StashBoxPrefix = "5151e9e1000000000000b0";
        public const int StashBoxes = 8;
        public static string StashBoxId(int number) => StashBoxPrefix + number.ToString("x2");
        public static bool IsStashBox(string id) => id != null && id.StartsWith(StashBoxPrefix);
        // A box nobody owns with a big grid that takes anything: the developers' junkbox, renamed "Stash" for the siege.
        public const string StashBoxTemplate = "5c0a5f6c86f774753654890e";
    }
}
