using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace HideoutSiege.Server;

/// <summary>One of the front door's upgrades: built in the hideout like an area's, from the stash and with time to wait.</summary>
public sealed record DoorUpgrade(string Id, string Name, string Description, int Security, (string Template, int Count)[] Cost, long Seconds)
{
    public MongoId Key => new(Id);
}

/// <summary>
/// The front door's upgrades. Each one makes the siege's front door take one more charge before it goes. They're built
/// from inside the hideout, at the front door, the way a hideout area is upgraded: the client asks with the game's own
/// improvement request, and this takes the items and records it in the profile's hideout improvements.
/// </summary>
public static class DoorUpgrades
{
    private const string Roubles = "5449016a4bdc2d6f028b456f", Nails = "590c31c586f774245e3141b2", Bolts = "57347c5b245977448d35f6e1",
        Drill = "59e35de086f7741778269d84", Screws = "59e35ef086f7741777737012", Toolset = "590c2e1186f77425357b6124";

    public static readonly DoorUpgrade[] All =
    [
        new("6a5e6e1e00000000000d0001", "Reinforced front door", "Steel plate bolted over the front door: the first charge on it won't bring it down.",
            2, [(Roubles, 75000), (Bolts, 4), (Nails, 2), (Toolset, 1)], 3600),
        new("6a5e6e1e00000000000d0002", "Barred front door", "Steel bars across the reinforced door: it takes a third charge to bring it down.",
            3, [(Roubles, 150000), (Bolts, 6), (Nails, 3), (Screws, 2), (Drill, 1)], 7200),
    ];

    public static DoorUpgrade? Find(MongoId id) => All.FirstOrDefault(u => u.Key == id);

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static HideoutImprovement? Progress(PmcData pmc, DoorUpgrade upgrade)
        => pmc.Hideout?.Improvements != null && pmc.Hideout.Improvements.TryGetValue(upgrade.Key, out var progress) ? progress : null;

    public static bool Built(PmcData pmc, DoorUpgrade upgrade)
    {
        var progress = Progress(pmc, upgrade);
        return progress != null && (progress.Completed == true || (progress.ImproveCompleteTimestamp is long done && done > 0 && done <= Now));
    }

    /// <summary>How many of them are built, in order.</summary>
    public static int Level(PmcData pmc)
    {
        int level = 0;
        foreach (var upgrade in All) { if (!Built(pmc, upgrade)) break; level++; }
        return level;
    }

    public static string? Locked(PmcData pmc, DoorUpgrade upgrade)
    {
        int index = Array.IndexOf(All, upgrade);
        if (index > 0 && !Built(pmc, All[index - 1])) return $"Build the {All[index - 1].Name.ToLowerInvariant()} first";
        int security = pmc.Hideout?.Areas?.FirstOrDefault(a => a.Type == HideoutAreas.Security)?.Level ?? 0;
        if (security < upgrade.Security) return $"Needs Security level {upgrade.Security}";
        return null;
    }
}

public sealed record SiegeDoorRequest : IRequestData;

/// <summary>/hideoutsiege/door: the upgrades, their cost, and where this player is with each.</summary>
[Injectable]
public sealed class DoorRouter(JsonUtil json, ProfileHelper profiles) : StaticRouter(json, [
    new RouteAction<SiegeDoorRequest>(SiegeIds.RouteDoor, (_, _, session, _, _) => new ValueTask<string>(Describe(profiles, session)))
])
{
    private static string Describe(ProfileHelper profiles, MongoId session)
    {
        PmcData? pmc = profiles.GetPmcProfile(session);
        var list = DoorUpgrades.All.Select(u =>
        {
            var progress = pmc != null ? DoorUpgrades.Progress(pmc, u) : null;
            string state = pmc != null && DoorUpgrades.Built(pmc, u) ? "built" : progress != null ? "building" : "none";
            return new Dictionary<string, object?>
            {
                ["id"] = u.Id, ["name"] = u.Name, ["description"] = u.Description, ["security"] = u.Security, ["seconds"] = u.Seconds,
                ["cost"] = u.Cost.Select(c => new Dictionary<string, object> { ["template"] = c.Template, ["count"] = c.Count }).ToList(),
                ["state"] = state, ["completeAt"] = progress?.ImproveCompleteTimestamp ?? 0,
                ["locked"] = pmc != null && state == "none" ? DoorUpgrades.Locked(pmc, u) : null,
            };
        }).ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["level"] = pmc != null ? DoorUpgrades.Level(pmc) : 0, ["upgrades"] = list });
    }
}

/// <summary>
/// The client starts a door upgrade the way it starts any hideout improvement (HideoutImproveArea with the items you handed
/// over), giving the upgrade's id. For one of those this checks it may start, that the items cover its cost, takes them
/// as the game does (money from a stack, everything else whole) and starts it. Any other id goes on (to the game, or to
/// another mod doing the same, such as Hideout Extensions).
/// </summary>
[Injectable]
public sealed class DoorUpgradePatch : AbstractPatch
{
    private static EventOutputHolder? outputs;
    private static HttpResponseUtil? responses;
    private static InventoryHelper? inventory;
    private static PaymentHelper? payments;
    private static ISptLogger<DoorUpgradePatch>? log;

    public DoorUpgradePatch(EventOutputHolder eventOutputs, HttpResponseUtil http, InventoryHelper inventoryHelper, PaymentHelper paymentHelper, ISptLogger<DoorUpgradePatch> logger)
    { outputs = eventOutputs; responses = http; inventory = inventoryHelper; payments = paymentHelper; log = logger; }

    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(HideoutController), nameof(HideoutController.ImproveArea));

    [PatchPrefix]
    public static bool Prefix(MongoId sessionId, PmcData pmcData, HideoutImproveAreaRequestData request, ref ItemEventRouterResponse __result)
    {
        DoorUpgrade? upgrade = DoorUpgrades.Find(request.AreaId);
        if (upgrade == null || outputs == null || responses == null || inventory == null || payments == null) return true;
        ItemEventRouterResponse output = outputs.GetOutput(sessionId);
        string? refused = Refusal(pmcData, upgrade, request.Items ?? new List<HideoutItem>());
        if (refused != null)
        {
            log?.Warning($"[HideoutSiege] Didn't start the {upgrade.Name.ToLowerInvariant()}: {refused}.");
            __result = responses.AppendErrorToOutput(output, upgrade.Name + ": " + refused);
            return false;
        }
        foreach (HideoutItem handed in request.Items ?? new List<HideoutItem>())
        {
            Item item = pmcData.Inventory!.Items!.First(i => i.Id == handed.Id);
            if (payments.IsMoneyTpl(item.Template) && item.Upd?.StackObjectsCount is double stack && stack > (handed.Count ?? 0))
            {
                item.Upd.StackObjectsCount -= handed.Count ?? 0;
                continue;
            }
            inventory.RemoveItem(pmcData, item.Id, sessionId, output);
        }
        var started = new HideoutImprovement { Completed = false, ImproveCompleteTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + upgrade.Seconds };
        pmcData.Hideout!.Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
        pmcData.Hideout.Improvements[upgrade.Key] = started;
        output.ProfileChanges[sessionId].Improvements ??= new Dictionary<MongoId, HideoutImprovement>();
        output.ProfileChanges[sessionId].Improvements![upgrade.Key] = started;
        log?.Info($"[HideoutSiege] Building the {upgrade.Name.ToLowerInvariant()} (ready in {upgrade.Seconds / 3600.0:0.##} h).");
        __result = output;
        return false;
    }

    private static string? Refusal(PmcData pmc, DoorUpgrade upgrade, List<HideoutItem> handed)
    {
        if (DoorUpgrades.Progress(pmc, upgrade) != null) return DoorUpgrades.Built(pmc, upgrade) ? "it's already built" : "it's already being built";
        string? locked = DoorUpgrades.Locked(pmc, upgrade);
        if (locked != null) return locked;
        var given = new Dictionary<MongoId, double>();
        foreach (HideoutItem h in handed)
        {
            Item? item = pmc.Inventory?.Items?.FirstOrDefault(i => i.Id == h.Id);
            if (item == null) return "an item you chose is gone";
            if (upgrade.Cost.All(c => new MongoId(c.Template) != item.Template)) return $"{item.Template} isn't part of its cost";
            double count = payments!.IsMoneyTpl(item.Template) ? h.Count ?? 0 : item.Upd?.StackObjectsCount ?? 1;
            given[item.Template] = given.GetValueOrDefault(item.Template) + count;
        }
        foreach (var (template, count) in upgrade.Cost)
            if (given.GetValueOrDefault(new MongoId(template)) < count) return $"not enough of {template} handed over";
        return null;
    }
}

[Injectable(TypePriority = OnLoadOrder.Preload + 12)]
public sealed class DoorPatches(IEnumerable<IRuntimePatch> patches) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (IRuntimePatch patch in patches.Where(p => p is DoorUpgradePatch)) patch.Enable();
        return Task.CompletedTask;
    }
}
