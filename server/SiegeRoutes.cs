using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace HideoutSiege.Server;

public sealed class SiegeConfig
{
    public double MinHoursBetween { get; set; } = 3;
    public double MaxHoursBetween { get; set; } = 8;
    public double HoursAfterDefeat { get; set; } = 2;
    public bool InvadedOnFirstRun { get; set; } = true;
}

public sealed class SiegeProfileState
{
    public long NextInvasion { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    // The last siege's threat level: who came, and so how much they take if they win.
    public int LastTier { get; set; }
    // The last defeat, for the screen that says what they took: kept until the game's shown it.
    public SiegeDefeatReport? LastDefeat { get; set; }
}

public sealed class SiegeTaken
{
    public string Name { get; set; } = "";
    public double Worth { get; set; }
}

/// <summary>Something of your hideout's they wrecked: what it was, and before and after.</summary>
public sealed class SiegeWrecked
{
    public string Name { get; set; } = "";
    public string Was { get; set; } = "";
    public string Now { get; set; } = "";
}

public sealed class SiegeDefeatReport
{
    public List<SiegeTaken> Taken { get; set; } = [];
    public List<SiegeWrecked> Wrecked { get; set; } = [];
    public int Tier { get; set; }
    public int MostTaken { get; set; }
    public double Health { get; set; }
    public double Energy { get; set; }
    public double Hydration { get; set; }
    public int Security { get; set; }
    public int Medstation { get; set; }
    // When it happened: the game shows each defeat once.
    public long When { get; set; }
    public bool Seen { get; set; }
}

public record SiegeResultData : IRequestData
{
    public bool Victory { get; init; }
    public int Tier { get; init; }
    public int Waves { get; init; }
    public int Kills { get; init; }
}

public record SiegeEmptyData : IRequestData;

/// <summary>
/// Keeps each profile's invasion schedule in the mod's own folder (never in the profile). Holding out pays in what you loot
/// and put away in the siege, not by message.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class SiegeStore(RandomUtil random, ISptLogger<SiegeStore> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string folder = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    private readonly object gate = new();
    private SiegeConfig? config;
    private Dictionary<string, SiegeProfileState>? states;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private string StatePath => System.IO.Path.Combine(folder, "state.json");

    private SiegeConfig Config
    {
        get
        {
            if (config is not null) return config;
            string path = System.IO.Path.Combine(folder, "config.json");
            try
            {
                if (File.Exists(path)) config = JsonSerializer.Deserialize<SiegeConfig>(File.ReadAllText(path));
                else File.WriteAllText(path, JsonSerializer.Serialize(new SiegeConfig(), Json));
            }
            catch (Exception e) { logger.Warning("Hideout Last Stand: couldn't read config.json, using defaults: " + e.Message); }
            return config ??= new SiegeConfig();
        }
    }

    private SiegeProfileState Get(string id)
    {
        if (states is null)
        {
            try { states = File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, SiegeProfileState>>(File.ReadAllText(StatePath)) : null; }
            catch (Exception e) { logger.Warning("Hideout Last Stand: couldn't read state.json: " + e.Message); }
            states ??= [];
        }
        if (!states.TryGetValue(id, out var state))
        {
            state = new SiegeProfileState { NextInvasion = Config.InvadedOnFirstRun ? 0 : Now + NextGap() };
            states[id] = state;
            Save();
        }
        return state;
    }

    private void Save()
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(states, Json)); }
        catch (Exception e) { logger.Warning("Hideout Last Stand: couldn't save state.json: " + e.Message); }
    }

    private long NextGap() => (long)(random.GetDouble(Config.MinHoursBetween, Config.MaxHoursBetween) * 3600);

    public object Status(string id)
    {
        lock (gate)
        {
            var s = Get(id);
            return new { Invaded = Now >= s.NextInvasion, s.NextInvasion, Now, s.Wins, s.Losses, Defeat = s.LastDefeat is { Seen: false } ? s.LastDefeat : null };
        }
    }

    public void RecordDefeat(string id, SiegeDefeatReport report)
    {
        lock (gate)
        {
            report.When = Now;
            Get(id).LastDefeat = report;
            Save();
        }
    }

    /// <summary>The game's shown what they took: it's not shown again.</summary>
    public void DefeatSeen(string id)
    {
        lock (gate)
        {
            if (Get(id).LastDefeat is not { } last || last.Seen) return;
            last.Seen = true;
            Save();
        }
    }

    public void Trigger(string id)
    {
        lock (gate)
        {
            Get(id).NextInvasion = 0;
            Save();
        }
    }

    public void Finish(MongoId session, SiegeResultData result)
    {
        lock (gate)
        {
            var s = Get(session.ToString());
            s.LastTier = result.Tier;
            if (result.Victory)
            {
                s.Wins++;
                s.NextInvasion = Now + NextGap();
            }
            else
            {
                s.Losses++;
                s.NextInvasion = Now + (long)(Config.HoursAfterDefeat * 3600);
            }
            Save();
        }
        logger.Info($"Hideout Last Stand: {(result.Victory ? "defended" : "lost")} at tier {result.Tier}, wave {result.Waves}, {result.Kills} kills.");
    }

    /// <summary>The threat level of this profile's last siege (2 if it's not known).</summary>
    public int LastTier(string id)
    {
        lock (gate)
        {
            int tier = Get(id).LastTier;
            return tier > 0 ? tier : 2;
        }
    }
}

[Injectable]
public sealed class SiegeRouter(JsonUtil json, SiegeStore store, SiegeRackGear gear) : StaticRouter(json,
[
    new RouteAction<SiegeGearData>(SiegeIds.RouteGear, (_, data, session, _, _) =>
    {
        gear.Finish(session, data);
        return new ValueTask<string>("{}");
    }),
    new RouteAction<SiegeEmptyData>(SiegeIds.RouteState, (_, _, session, _, _) => new ValueTask<string>(JsonSerializer.Serialize(store.Status(session.ToString())))),
    new RouteAction<SiegeEmptyData>(SiegeIds.RouteDefeatSeen, (_, _, session, _, _) =>
    {
        store.DefeatSeen(session.ToString());
        return new ValueTask<string>("{}");
    }),
    new RouteAction<SiegeEmptyData>(SiegeIds.RouteTrigger, (_, _, session, _, _) =>
    {
        store.Trigger(session.ToString());
        return new ValueTask<string>("{}");
    }),
    new RouteAction<SiegeResultData>(SiegeIds.RouteResult, (_, data, session, _, _) =>
    {
        store.Finish(session, data);
        return new ValueTask<string>("{}");
    }),
]);
