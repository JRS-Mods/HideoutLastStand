using System.Collections.Concurrent;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Utils;

namespace HideoutSiege.Server;

/// <summary>A player's hideout as their game sees it: station levels, walls, floor and ceiling, and what other mods add.</summary>
public record SiegeLayoutData : IRequestData
{
    // Station (the game's area type, as a number) to level.
    public Dictionary<string, int>? Levels { get; init; }
    // Surface (floor, wall, ceiling, as a number) to the style chosen.
    public Dictionary<string, string>? Surfaces { get; init; }
    // What other mods add to the hideout, the front door's upgrades and the invasion's roll, by name.
    public Dictionary<string, string>? Copies { get; init; }
}

/// <summary>
/// Whose siege a Fika headless is running. A headless hosts the raid for someone else, so the hideout, racks and stash are
/// that player's, not the headless profile's. Fika's server says who asked for the raid; the player's own game told us
/// what their hideout looks like when they started the defence (some of it, such as Hideout Interiors' things, only
/// their game knows).
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 49)]
public sealed class SiegeOwners(ProfileHelper profiles, ISptLogger<SiegeOwners> logger) : IOnLoad
{
    private static SiegeOwners? instance;
    private void Note(string text) => logger.Info(text);
    // A headless's session to the session of the player it's hosting for.
    private static readonly ConcurrentDictionary<string, string> hosting = new();
    private static readonly ConcurrentDictionary<string, (DateTime When, SiegeLayoutData Layout)> layouts = new();
    // Older than this, a player's layout is from some other time they started the defence.
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(2);
    private const string DoorKey = "Hideout Siege front door";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        instance = this;
        var service = AccessTools.TypeByName("FikaServer.Services.Headless.HeadlessService");
        var start = service is null ? null : AccessTools.Method(service, "StartHeadlessRaid");
        if (start is null) return Task.CompletedTask;
        new Harmony("jrs.hideoutsiege.server.owners").Patch(start, prefix: new HarmonyMethod(typeof(SiegeOwners), nameof(HeadlessAsked)));
        logger.Info("Hideout Last Stand: Fika headless found: a siege it hosts is the defence of the hideout of whoever asked for it.");
        return Task.CompletedTask;
    }

    private static void HeadlessAsked(string headlessSessionID, string requesterSessionID)
    {
        if (string.IsNullOrEmpty(headlessSessionID) || string.IsNullOrEmpty(requesterSessionID)) return;
        hosting[headlessSessionID] = requesterSessionID;
        instance?.Note($"Hideout Last Stand: headless {headlessSessionID} is hosting for {requesterSessionID}.");
    }

    /// <summary>The player whose siege this is: for a headless, whoever it's hosting for; otherwise the player themselves.</summary>
    public static string Of(string session) => hosting.TryGetValue(session, out var owner) ? owner : session;

    /// <summary>Whether this is a headless that's hosted a raid for someone.</summary>
    public static bool IsHeadless(string session) => hosting.ContainsKey(session);

    public void Keep(string session, SiegeLayoutData layout)
    {
        layouts[session] = (DateTime.UtcNow, layout);
        logger.Info($"Hideout Last Stand: {session}'s hideout noted for a siege: {layout.Levels?.Count ?? 0} stations, {layout.Copies?.Count ?? 0} other parts.");
    }

    /// <summary>
    /// For a headless: the hideout of the player it's hosting for, with their profile id. For anyone else, nothing: their
    /// own game knows their hideout.
    /// </summary>
    public object? Owner(string session)
    {
        if (!hosting.TryGetValue(session, out var owner)) return null;
        if (layouts.TryGetValue(owner, out var kept) && DateTime.UtcNow - kept.When < Fresh)
            return new { ProfileId = owner, kept.Layout.Levels, kept.Layout.Surfaces, kept.Layout.Copies };
        // Their game didn't say (an older siege, or the raid wasn't started from the alert): what the server knows.
        var pmc = profiles.GetPmcProfile(new SPTarkov.Server.Core.Models.Common.MongoId(owner));
        if (pmc is null) return null;
        var levels = new Dictionary<string, int>();
        foreach (var area in pmc.Hideout?.Areas ?? [])
            levels[((int)area.Type).ToString()] = area.Level ?? 0;
        logger.Warning($"Hideout Last Stand: {owner}'s game didn't say what their hideout looks like, so the headless gets their stations from the server only (no walls or other mods' rooms and things).");
        return new { ProfileId = owner, Levels = levels, Surfaces = new Dictionary<string, string>(), Copies = new Dictionary<string, string> { [DoorKey] = DoorUpgrades.Level(pmc).ToString() } };
    }
}
