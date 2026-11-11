using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using UnityEngine;
using UnityEngine.AI;

namespace HideoutSiege
{
    // New phases go on the end: the number is what goes over the network.
    internal enum SiegePhase { None, Cinematic, Intermission, Fighting, Won, Lost, Prep }

    /// <summary>What the HUD shows. The host's director writes it, and Fika carries it to the other players.</summary>
    internal static class SiegeState
    {
        internal static SiegePhase Phase;
        internal static int Wave, TotalWaves, Remaining, Tier, Kills;
        internal static string Banner = "", Notice = "";
        internal static float BannerUntil, CountdownUntil, NoticeUntil;

        internal static void Reset()
        {
            Phase = SiegePhase.None;
            Wave = TotalWaves = Remaining = Tier = Kills = 0;
            Banner = Notice = "";
            BannerUntil = CountdownUntil = NoticeUntil = 0f;
        }

        /// <summary>A small line for this player alone, never sent to the others.</summary>
        internal static void Notify(string text, float seconds = 2.5f)
        {
            Notice = text;
            NoticeUntil = Time.time + seconds;
        }

        internal static void Announce(string text, float seconds)
        {
            Banner = text;
            BannerUntil = Time.time + seconds;
        }
    }

    internal sealed class WaveSpec
    {
        internal readonly int Scavs, Raiders, BlackDivision;
        internal WaveSpec(int scavs, int raiders, int blackDivision) { Scavs = scavs; Raiders = raiders; BlackDivision = blackDivision; }
        internal int Total => Scavs + Raiders + BlackDivision;
    }

    /// <summary>
    /// Runs the attack on the host (or alone). The first few attackers wait outside the front door while the defenders
    /// wake up and get ready. Then the door goes, and every wave comes down the stairwell and through it.
    /// </summary>
    internal sealed class SiegeDirector : MonoBehaviour
    {
        // Black Division's bot types, added by that mod when it's installed.
        // Black Division's assault troops and its raiders: the two it gives gear to (its lead, breacher and support types have
        // none yet, and arrive empty-handed).
        private static readonly int[] BlackDivisionRoles = { 848421, 848426 };
        private const float RetargetEvery = 4f, PlantSeconds = 1.2f, WalkingSpeed = .15f, WalkGivesUp = 6f, StartOfWalk = 4.6f, AtTheDoorDistance = .6f;

        private readonly List<BotOwner> attackers = new List<BotOwner>();
        private readonly List<SpawnPointMarker> spawnPoints = new List<SpawnPointMarker>();
        private WaveSpec[] waves;
        private BotSpawner spawner;
        private int spawning;
        private readonly List<float> arriving = new List<float>();
        private float nextRetarget;
        private bool finished;
        private const float DoorClearsAfter = .5f, RushGivesUp = 8f, ArrivedWithin = 1.5f, StartsFightingWithin = 7f;
        private const float BehindTheDoor = 2f, SecondEntryAtLeast = 5f;
        private const int SecondEntryFromWave = 1, SecondEntryShare = 2;
        // They cut the power this long before they come in.
        private const float PowerCutBefore = 4f;
        private const int HandsAttempts = 3;
        private const float HandsGiveUp = 2f;
        private const float StageGivesUp = 15f, LeftByTheDoor = 1f, HoldingApart = .8f, ReleaseApart = .3f;
        private const float DoorCheckEvery = 1f, FuseSeconds = 5f, StuckSeconds = 8f, BreachWalkSeconds = 15f;
        internal const float InnerFuse = PlantSeconds + FuseSeconds;
        // From a reinforced door holding to the next charge on it going off.
        private const float Rebreach = 14f;
        private readonly Dictionary<BotOwner, (Vector3 Where, float When)> lastMoved = new Dictionary<BotOwner, (Vector3, float)>();
        private int staged;
        private float nextDoorCheck;
        private readonly List<BotOwner> toWalk = new List<BotOwner>();
        private bool breachInProgress, staging;
        private List<Vector3> holding;
        private Vector3 emergencyWay = Vector3.forward;
        // Which way each waiting attacker comes in, how many of the wave being staged go through the tunnel, and how many
        // have gone to each place so far.
        private enum Way { Stairs, Gate, Second }
        private enum Second { None, Tunnel, Emergency }
        private readonly Dictionary<BotOwner, Way> wayIn = new Dictionary<BotOwner, Way>();
        // The second way in this wave: where its share waits (staged with the rest during the break, so nothing heavy
        // happens as the wave starts), and for the Emergency Wall which side of its door they're on.
        private Second second;
        private readonly List<Vector3> secondSpots = new List<Vector3>();
        private int secondQuota, gateStaged, secondStaged;
        private float emergencySide;
        private Vector3 emergencyCentre;
        private bool gateCharged;
        internal static readonly HashSet<BotOwner> Waiting = new HashSet<BotOwner>(), Breachers = new HashSet<BotOwner>();

        internal static SiegeDirector Instance;

        internal static void Begin()
        {
            if (Instance != null) return;
            Instance = new GameObject("Hideout Siege Director").AddComponent<SiegeDirector>();
        }

        internal static void End()
        {
            if (Instance != null) Destroy(Instance.gameObject);
            Instance = null;
        }

        private void Awake()
        {
            Instance = this;
            SiegeState.Reset();
            SiegePush.Reset();
        }

        internal bool IsAttacker(BotOwner bot) => attackers.Contains(bot);

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SiegePush.Reset();
            Waiting.Clear();
            Breachers.Clear();
            wayIn.Clear();
            rail?.Off();
        }

        private IEnumerator Start()
        {
            while (!Ready()) yield return new WaitForSeconds(.5f);
            spawner = Singleton<IBotGame>.Instance.BotsController.BotSpawner;
            spawnPoints.AddRange(SiegeMap.Entrances);

            int tier = DefenceRating.Tier(DefenceRating.Percent(HideoutCopy.Levels()));
            SiegeState.Tier = tier;
            waves = DefenceRating.Waves(tier);
            SiegeState.TotalWaves = waves.Length;
            Plugin.Log.LogInfo($"[siege] Tier {tier}: {string.Join(", ", waves.Select(w => w.Scavs + "+" + w.Raiders + "+" + w.BlackDivision))}. Black Division bots: {(HaveBlackDivision() ? "mod" : "stand-ins")}.");

            SiegeState.Wave = 1;
            // Everything slow happens now, while the screen's still black: your gear, then the whole first wave.
            yield return SiegeCinematic.GetReady();
            var firstOrder = OrderFor(waves[0]);
            int firstStaged = Mathf.Min(firstOrder.Count, Plugin.MaxAlive.Value);
            float giveUp = Time.time + StageGivesUp;
            yield return Stage(firstOrder, giveUp, .3f);
            yield return new WaitUntil(() => staged >= firstStaged || Time.time >= giveUp);
            Plugin.Log.LogInfo($"[siege] {staged}/{firstStaged} attackers up the stairs.");
            yield return TakeUpTheCharge(toWalk.FirstOrDefault());
            SiegeGun.Place();
            // Worked out now, while the door's still whole.
            var emergencyDoor = SiegeBreach.EmergencyDoor();
            if (emergencyDoor != null) emergencyWay = SiegeBreach.ThroughDoor(emergencyDoor);
            SetPhase(SiegePhase.Cinematic);
            var walk = StartCoroutine(WalkToTheDoor());
            yield return SiegeCinematic.Play();
            // The camera's gone (or was skipped): whoever's not where he waits goes back there, out of sight. Skipped before
            // he'd set it, the charge goes on the door by itself.
            StopCoroutine(walk);
            rail?.Off();
            if (!frontPlanted)
            {
                SiegeBreach.StopCarrying();
                Net.SendCarry("");
                PlantAt(SiegeBreach.FrontCharge);
                frontPlanted = true;
            }
            BackToWaiting();

            SiegeState.CountdownUntil = Time.time + Plugin.PrepTime.Value;
            SiegeBreach.UpdateDoorBlocks();
            SetPhase(SiegePhase.Prep);
            // An upgraded front door holds against a charge for each upgrade: another goes on, and the countdown starts again.
            int holds = HideoutCopy.DoorLevel;
            float nextHeldCheck = 0f;
            while (!finished)
            {
                while (Time.time < SiegeState.CountdownUntil && !finished)
                {
                    // Nobody's inside before the door goes.
                    if (Time.time >= nextHeldCheck) { nextHeldCheck = Time.time + .5f; KeepOutside(); }
                    if (holds == 0 && waves[0].BlackDivision > 0 && SiegeState.CountdownUntil - Time.time <= PowerCutBefore) CutThePower();
                    // The cultist room's gate goes at the same moment as the front door.
                    if (holds == 0 && !gateCharged && SiegeEntries.Gate != null && SiegeState.CountdownUntil - Time.time <= InnerFuse)
                    {
                        gateCharged = true;
                        PlantAt(SiegeEntries.GateCharge);
                    }
                    yield return null;
                }
                if (finished || holds == 0) break;
                holds--;
                SiegeBreach.Held(SiegeBreach.FrontCharge);
                Net.SendDoor(true, SiegeBreach.FrontCharge, true);
                SiegeState.Announce("THE DOOR HELD", 3f);
                SiegeState.CountdownUntil = Time.time + Rebreach;
                Net.Broadcast();
                Plugin.Log.LogInfo($"[siege] The front door held: another charge, {holds} more it'll take.");
                yield return new WaitForSeconds(2f);
                PlantAt(SiegeBreach.FrontCharge);
            }
            if (finished) yield break;

            SiegeBreach.Blow();
            if (gateCharged)
            {
                SiegeBreach.Blast(SiegeEntries.GateCharge);
                Net.SendDoor(true, SiegeEntries.GateCharge);
            }
            SiegeSounds.QuietAlarm();
            SiegeState.Announce("BREACH", 4f);
            SetPhase(SiegePhase.Fighting);
            SiegeSounds.Play("wave");
            StartCoroutine(Release());
            bool firstWaveOver = false;
            StartCoroutine(Tracked(RunWave(firstOrder), () => firstWaveOver = true));
            yield return new WaitUntil(() => firstWaveOver || finished);
            if (finished) yield break;

            for (int i = 1; i < waves.Length; i++)
            {
                SiegeState.Wave = i + 1;
                SetPhase(SiegePhase.Intermission);
                SiegeState.CountdownUntil = Time.time + Plugin.WaveDelay.Value;
                Net.Broadcast();
                // The next wave comes in during the break rather than the fight, and waits up the stairs.
                var order = OrderFor(waves[i]);
                PlanSecondEntry(i, order.Count);
                var comingIn = StartCoroutine(Stage(order, SiegeState.CountdownUntil, .5f));
                while (Time.time < SiegeState.CountdownUntil && !finished)
                {
                    if (HoldBroken()) { SiegeState.CountdownUntil = Time.time; Net.Broadcast(); }
                    if (Time.time >= nextKeptCheck) { nextKeptCheck = Time.time + .5f; KeepOutside(); }
                    if (waves[i].BlackDivision > 0 && SiegeState.CountdownUntil - Time.time <= PowerCutBefore) CutThePower();
                    yield return new WaitForSeconds(.25f);
                }
                StopCoroutine(comingIn);
                if (finished) yield break;

                SetPhase(SiegePhase.Fighting);
                SiegeSounds.Play("wave");
                bool firstBlackDivision = waves[i].BlackDivision > 0 && waves.Take(i).All(w => w.BlackDivision == 0);
                bool firstRaiders = waves[i].Raiders > 0 && waves.Take(i).All(w => w.Raiders == 0 && w.BlackDivision == 0);
                string title = i == waves.Length - 1 ? "FINAL WAVE" : "WAVE " + (i + 1);
                string elite = HaveBlackDivision() ? "BLACK DIVISION" : "RAIDERS";
                SiegeState.Announce(firstBlackDivision ? title + " - " + elite : firstRaiders ? title + " - RAIDERS" : title, 4f);
                Net.Broadcast();
                if (second == Second.Tunnel) StartCoroutine(TunnelEntry());
                else if (second == Second.Emergency) StartCoroutine(EmergencyEntry());
                StartCoroutine(Release());
                yield return RunWave(order);
                if (finished) yield break;
            }
            Victory();
        }

        // Who's in a wave, in the order they turn up.
        private static List<WildSpawnType> OrderFor(WaveSpec wave)
        {
            var order = new List<WildSpawnType>();
            for (int i = 0; i < wave.BlackDivision; i++) order.Add(EliteRole(i));
            for (int i = 0; i < wave.Raiders; i++) order.Add(i % 2 == 0 ? WildSpawnType.pmcBot : WildSpawnType.exUsec);
            for (int i = 0; i < wave.Scavs; i++) order.Add(WildSpawnType.assault);
            return order.OrderBy(_ => UnityEngine.Random.value).ToList();
        }

        private static WildSpawnType EliteRole(int index)
        {
            if (HaveBlackDivision()) return (WildSpawnType)BlackDivisionRoles[index % BlackDivisionRoles.Length];
            return index % 2 == 0 ? WildSpawnType.pmcBot : WildSpawnType.exUsec;
        }

        private static BotDifficulty DifficultyFor(WildSpawnType role, int tier)
        {
            return role == WildSpawnType.assault && tier < 3 ? BotDifficulty.normal : BotDifficulty.hard;
        }

        // Everyone this hideout's siege will send, for loading up front.
        internal static List<CountTypeBotWave> Expected()
        {
            UnarmedAhead.Clear();
            int tier = DefenceRating.Tier(DefenceRating.Percent(HideoutCopy.Levels()));
            var counts = new Dictionary<WildSpawnType, int>();
            foreach (var wave in DefenceRating.Waves(tier))
                foreach (var role in OrderFor(wave))
                {
                    counts.TryGetValue(role, out int count);
                    counts[role] = count + 1;
                }
            return counts.Select(pair => new CountTypeBotWave(pair.Value, pair.Key, DifficultyFor(pair.Key, tier))).ToList();
        }

        // Brings attackers in ahead of time and holds them up the stairs, frozen, so the slow part isn't during a fight.
        private IEnumerator Stage(List<WildSpawnType> order, float until, float apart)
        {
            staging = true;
            staged = 0;
            for (int i = Mathf.Min(order.Count, Plugin.MaxAlive.Value); i > 0 && Time.time < until && !finished; i--)
            {
                SpawnOne(order[order.Count - 1]);
                order.RemoveAt(order.Count - 1);
                yield return new WaitForSeconds(apart);
            }
        }

        // One after another: all of them at once would jam on the stairs.
        private IEnumerator Release()
        {
            staging = false;
            // Route-finding only learns the way through a blown door a moment after it's gone.
            yield return new WaitForSeconds(DoorClearsAfter);
            foreach (var bot in Waiting.ToList())
            {
                if (finished) yield break;
                if (wayIn.TryGetValue(bot, out Way way) && way == Way.Second) continue;
                Waiting.Remove(bot);
                if (bot == null || bot.IsDead) continue;
                Tell(bot);
                StartCoroutine(RushIn(bot));
                yield return new WaitForSeconds(ReleaseApart);
            }
        }

        // From the third wave, with Hideout Extensions' rooms: those in the tunnel blow the wall it ends at (the first time),
        // and come through it into the room.
        private IEnumerator TunnelEntry()
        {
            var group = SecondGroup();
            if (group.Count == 0) yield break;
            if (!SiegeEntries.TunnelOpen)
            {
                SiegeState.Announce("THEY'RE COMING THROUGH A WALL", 4f);
                Net.Broadcast();
                Vector3 at = SiegeEntries.TunnelCharge;
                float planted = Time.time;
                var charge = SiegeCharge.OnItsOwn(at).PutOn(at, SiegeEntries.TunnelOut, () => planted + InnerFuse - Time.time, false);
                yield return new WaitForSeconds(InnerFuse);
                if (charge != null) Destroy(charge);
                if (finished) yield break;
                SiegeBreach.Blast(at);
                Net.SendDoor(true, at);
                yield return new WaitForSeconds(DoorClearsAfter);
            }
            Vector3 inside = NavMesh.SamplePosition(SiegeEntries.TunnelInside, out var hit, 2f, NavMesh.AllAreas) ? hit.position : SiegeEntries.TunnelInside;
            foreach (var bot in group)
            {
                if (finished) yield break;
                Waiting.Remove(bot);
                if (bot == null || bot.IsDead) continue;
                Tell(bot);
                StartCoroutine(RushTo(bot, inside));
                yield return new WaitForSeconds(ReleaseApart);
            }
        }

        private List<BotOwner> SecondGroup() => Waiting.Where(b => b != null && !b.IsDead && wayIn.TryGetValue(b, out Way w) && w == Way.Second).ToList();

        // From the third wave, some of each wave come another way: through the tunnel to Hideout Extensions' room if
        // there is one, otherwise through the Emergency Wall's door from the side nobody's on. Worked out as the break starts.
        private void PlanSecondEntry(int wave, int coming)
        {
            second = Second.None;
            secondSpots.Clear();
            secondStaged = 0;
            secondQuota = 0;
            if (wave < SecondEntryFromWave) return;
            if (SiegeEntries.TunnelUsable)
            {
                second = Second.Tunnel;
                secondSpots.AddRange(SiegeEntries.TunnelHolding);
            }
            else if (EmergencySpots(secondSpots)) second = Second.Emergency;
            if (second != Second.None) secondQuota = Mathf.Max(1, Mathf.Min(coming, Plugin.MaxAlive.Value) / SecondEntryShare);
        }

        // Out of sight behind the Emergency Wall's door, on the side further from everyone defending.
        private bool EmergencySpots(List<Vector3> spots)
        {
            var door = SiegeBreach.EmergencyDoor();
            var defenders = Defenders().ToList();
            if (door == null || defenders.Count == 0) return false;
            Vector3 centre = SiegeBreach.CentreOf(door), way = emergencyWay;
            Vector3 PointOn(float side) => centre + way * (side * BehindTheDoor);
            float Clearance(Vector3 p) => defenders.Min(d => (d.Position - p).sqrMagnitude);
            float far = Clearance(PointOn(1f)) >= Clearance(PointOn(-1f)) ? 1f : -1f;
            if (!NavMesh.SamplePosition(PointOn(far), out var hit, 1.5f, NavMesh.AllAreas) || Clearance(hit.position) < SecondEntryAtLeast * SecondEntryAtLeast) return false;
            Vector3 across = Vector3.Cross(Vector3.up, way);
            for (int i = 0; i < 6; i++)
            {
                Vector3 spot = hit.position + way * far * (i % 2) * .8f + across * (i / 2 - 1) * .8f;
                spots.Add(NavMesh.SamplePosition(spot, out var near, 1.5f, NavMesh.AllAreas) ? near.position : hit.position);
            }
            emergencySide = far;
            emergencyCentre = centre;
            Plugin.Log.LogInfo($"[siege] Some of this wave come through the Emergency Wall at {centre:F2}, from {hit.position:F2}.");
            return true;
        }

        // They blow the Emergency Wall's door if it's shut, then come through it.
        private IEnumerator EmergencyEntry()
        {
            var group = SecondGroup();
            if (group.Count == 0) yield break;
            var door = SiegeBreach.EmergencyDoor();
            Vector3 PointOn(float side) => emergencyCentre + emergencyWay * (side * BehindTheDoor);
            if (door != null && SiegeBreach.IsShut(door) && !breachInProgress)
            {
                breachInProgress = true;
                SiegeState.Announce("THEY'RE AT THE EMERGENCY WALL", 4f);
                Net.Broadcast();
                Vector3 at = SiegeBreach.ChargeSpot(emergencyCentre, PointOn(emergencySide));
                PlantAt(at);
                yield return new WaitForSeconds(InnerFuse);
                SiegeBreach.Blast(at);
                Net.SendDoor(true, at);
                yield return new WaitForSeconds(DoorClearsAfter);
                breachInProgress = false;
            }
            Vector3 beyond = NavMesh.SamplePosition(PointOn(-emergencySide), out var other, 2f, NavMesh.AllAreas) ? other.position : emergencyCentre;
            foreach (var bot in group)
            {
                if (finished) yield break;
                Waiting.Remove(bot);
                if (bot == null || bot.IsDead) continue;
                Tell(bot);
                StartCoroutine(RushTo(bot, beyond));
                yield return new WaitForSeconds(ReleaseApart);
            }
        }

        // Left to think for themselves on the stairs, attackers who know you're somewhere inside take cover up there and stay.
        // So they come through the door on their own legs first, and start thinking once they're in or someone's close.
        private IEnumerator RushIn(BotOwner bot)
        {
            Vector3 across = Vector3.Cross(Vector3.up, SiegeBreach.Outward);
            Vector3 spot = SiegeBreach.Doorway - SiegeBreach.Outward * UnityEngine.Random.Range(2f, 4f) + across * UnityEngine.Random.Range(-1.5f, 1.5f);
            spot = NavMesh.SamplePosition(spot, out var hit, 2f, NavMesh.AllAreas) ? hit.position : SiegeBreach.Doorway - SiegeBreach.Outward * 1.5f;
            return RushTo(bot, spot);
        }

        private IEnumerator RushTo(BotOwner bot, Vector3 spot)
        {
            if (bot == null || bot.IsDead || Breachers.Contains(bot)) yield break;
            Breachers.Add(bot);
            SiegePush.Order(bot, spot);
            float giveUp = Time.time + RushGivesUp;
            float nextRoute = 0f, stillSince = Time.time;
            Vector3 wasAt = bot.Position;
            // Not past the raid's end, nor once the bot's been taken down (its mover and body go before it's marked dead).
            while (Time.time < giveUp && !finished && bot != null && !bot.IsDead && bot.Mover != null && bot.GetPlayer != null
                && (bot.Position - spot).sqrMagnitude > ArrivedWithin * ArrivedWithin
                && !Defenders().Any(d => (d.Position - bot.Position).sqrMagnitude < StartsFightingWithin * StartsFightingWithin))
            {
                // Its thinking's stopped while it's on this, so it's walked from here, along the siege's floor.
                if (Time.time >= nextRoute) { nextRoute = Time.time + 1f; SiegePush.Route(bot, spot); }
                // Jammed (in a tunnel's tight spots, or against another): a step along the way.
                if ((bot.Position - wasAt).sqrMagnitude > .4f * .4f) { wasAt = bot.Position; stillSince = Time.time; }
                else if (Time.time - stillSince > 3f)
                {
                    var way = new NavMeshPath();
                    if (NavMesh.SamplePosition(bot.Position, out var here, 1.5f, NavMesh.AllAreas) && NavMesh.CalculatePath(here.position, spot, NavMesh.AllAreas, way) && way.corners.Length > 1)
                        SiegeMoves.Step(bot, Vector3.MoveTowards(way.corners[0], way.corners[1], 1.2f), "a step along the way");
                    wasAt = bot.Position;
                    stillSince = Time.time;
                }
                bot.Mover.Sprint(false);
                bot.Mover.SetTargetMoveSpeed(1f);
                bot.Steering.LookToMovingDirection();
                yield return null;
            }
            Breachers.Remove(bot);
            SiegePush.Release(bot);
            Tell(bot);
        }

        // They can't defend themselves while they're held, so nobody gets to pick them off: leave by the door or hit one, and it starts.
        private bool HoldBroken()
        {
            if (Defenders().Any(d => Vector3.Dot(d.Position - SiegeBreach.Doorway, SiegeBreach.Outward) > LeftByTheDoor)) return true;
            foreach (var bot in Waiting)
            {
                if (bot == null || bot.IsDead) return true;
                var health = bot.GetPlayer.ActiveHealthController?.GetBodyPartHealth(EBodyPart.Common);
                if (health != null && health.Value.Current < health.Value.Maximum - 1f) return true;
            }
            return false;
        }

        // Spots near the top of the stairs, apart from each other: people standing inside each other can't walk.
        private Vector3 HoldingSpot(int index)
        {
            if (holding == null)
            {
                Vector3 first = StairSpot(StartOfWalk);
                holding = new List<Vector3> { first };
                // Behind the first if there's room: anyone nearer the door is in the way of the one who walks down on the camera.
                float Out(Vector3 p) => Vector3.Dot(p - SiegeBreach.Doorway, SiegeBreach.Outward);
                foreach (Vector3 p in SiegeMap.Walkable
                    .Where(p => Out(p) >= StartOfWalk - .3f)
                    .OrderBy(p => Out(p) >= StartOfWalk + .2f ? 0 : 1)
                    .ThenBy(p => (p - first).sqrMagnitude))
                {
                    if (holding.Count >= 20) break;
                    if (holding.All(h => (h - p).sqrMagnitude > HoldingApart * HoldingApart)) holding.Add(p);
                }
                Plugin.Log.LogInfo($"[siege] {holding.Count} places to wait up the stairs.");
            }
            return holding[index % holding.Count];
        }

        // On the camera: one of them walks down the stairs, sets the charge, turns round and walks back up, kept to the
        // middle of the stairwell (see SiegeRail): a bot's own movement picks its own speed and way through that narrow space.
        private bool frontPlanted;

        private SiegeRail rail;

        private IEnumerator WalkToTheDoor()
        {
            float started = Time.time;
            var planter = toWalk.FirstOrDefault();
            if (planter == null || planter.IsDead) yield break;
            rail = new SiegeRail(planter);
            yield return new WaitForSeconds(.2f);
            // Straight down the middle to the door, facing it all the way.
            yield return rail.Walk(StandAtTheDoor, WalkingSpeed, WalkGivesUp);
            // Down on his haunches, the charge on, and up again, all by hand. The game's own kneel for planting stops him
            // turning while it lasts, and when it ends it hands him back the walk he was doing before it: toward the door.
            yield return Crouch(planter, CrouchedPose);
            PlantAt(SiegeBreach.FrontCharge);
            frontPlanted = true;
            Plugin.Log.LogInfo($"[siege] Charge going on, {Time.time - started:F1} s after the camera came on, {rail.At:F2} m out from the door.");
            yield return new WaitForSeconds(PlantSeconds);
            yield return Crouch(planter, 1f);
            // Round as he sets off, and back up the same line to where he waits.
            yield return rail.Walk(StartOfWalk, WalkingBackSpeed, WalkGivesUp);
            Plugin.Log.LogInfo($"[siege] Clear of the door, {Time.time - started:F1} s after the camera came on, {rail.At:F2} m out from it.");
            // Round again to wait facing the door, as the rest do.
            yield return rail.Turn(-SiegeBreach.Outward, TurnSeconds);
            rail.Off();
            GunBack(planter);
        }

        // Gun on his back, both hands free, charge in his hand. It's in everyone's game: the others see his hands through the game's own networking.
        private static IEnumerator TakeUpTheCharge(BotOwner bot)
        {
            if (bot == null || bot.IsDead) yield break;
            var player = bot.GetPlayer;
            for (int attempt = 0; attempt < HandsAttempts && !(player.HandsController is IEmptyHandsController); attempt++)
            {
                bool answered = false;
                player.SetEmptyHands(_ => answered = true);
                float giveUp = Time.time + HandsGiveUp;
                while (!answered && Time.time < giveUp) yield return null;
                if (bot == null || bot.IsDead) yield break;
            }
            if (!(player.HandsController is IEmptyHandsController))
            {
                Plugin.Log.LogWarning("[siege] The one with the charge couldn't free his hands: it'll just appear on the door.");
                yield break;
            }
            SiegeBreach.Carry(player);
            Net.SendCarry(bot.ProfileId);
        }

        private static void GunBack(BotOwner bot)
        {
            if (bot == null || bot.IsDead || !(bot.GetPlayer.HandsController is IEmptyHandsController)) return;
            try { bot.WeaponManager.Selector.TakeMainWeapon(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't get a gun back in his hands: " + e.Message); }
        }

        // All of him: his body as well as where he's looking.
        private static void Face(BotOwner bot, Vector3 way)
        {
            if (bot != null && !bot.IsDead) SiegeRail.Square(bot.GetPlayer, Mathf.Atan2(way.x, way.z) * Mathf.Rad2Deg);
        }

        // How low he gets to set the charge on the front door (1 is standing), and how long getting down or up takes.
        private const float CrouchedPose = .1f, PoseSeconds = .45f;

        // Down or up, smoothly, as someone would. Nothing else about him changes: he can still turn and walk the moment he's up.
        private static IEnumerator Crouch(BotOwner bot, float to)
        {
            if (bot == null || bot.IsDead) yield break;
            var movement = bot.GetPlayer.MovementContext;
            float from = movement.PoseLevel;
            for (float t = 0f; t < PoseSeconds; t += Time.deltaTime)
            {
                if (bot == null || bot.IsDead) yield break;
                movement.SetPoseLevel(Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / PoseSeconds)));
                yield return null;
            }
            if (bot != null && !bot.IsDead) movement.SetPoseLevel(to);
        }

        // The game's own kneeling and fiddling with something, as when a player plants an item.
        private static void Kneel(BotOwner bot)
        {
            if (bot == null || bot.IsDead) return;
            try { bot.GetPlayer.CurrentManagedState.Plant(true, false, PlantSeconds + .3f, _ => { }); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
        }

        // The middle of a body on the stairs side of the shut front door is never nearer it than this: the door's in the way.
        // Nearer, something's put him through it.
        private const float KeepOutFromDoor = .1f;

        // Further than this from where he waits, someone waiting has been moved.
        private const float WanderedFrom = 1.5f;

        // How far out from the door he stands to set the charge: far enough that getting up again doesn't bump it.
        private const float StandAtTheDoor = .5f;
        // Down the stairs at a walk, and back up them rather quicker: the fuse is lit. Turning round takes this long.
        private const float WalkingBackSpeed = .3f, TurnSeconds = .8f;

        // On the stairs this far out from the door. The nearest walked floor, unless that's in or past the doorway: then
        // straight down on to the steps.
        private static Vector3 StairSpot(float outward)
        {
            Vector3 spot = SiegeBreach.Doorway + SiegeBreach.Outward * outward + Vector3.up * .7f;
            if (NavMesh.SamplePosition(spot, out var hit, 1.5f, NavMesh.AllAreas) && Vector3.Dot(hit.position - SiegeBreach.Doorway, SiegeBreach.Outward) > KeepOutFromDoor + .1f)
                return hit.position;
            return Physics.Raycast(spot + Vector3.up * .5f, Vector3.down, out var floor, 3f, EFTHardSettings.Instance.MOVEMENT_MASK, QueryTriggerInteraction.Ignore) ? floor.point : spot;
        }

        private static void PlantAt(Vector3 at)
        {
            SiegeBreach.Plant(at);
            Net.SendDoor(false, at);
        }

        private static IEnumerator Tracked(IEnumerator routine, System.Action done)
        {
            yield return routine;
            done();
        }

        private static bool Ready()
        {
            return Singleton<IBotGame>.Instantiated && Singleton<GameWorld>.Instantiated
                && Singleton<IBotGame>.Instance.BotsController?.BotSpawner != null
                && Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started
                && Defenders().Any();
        }

        private static void SetPhase(SiegePhase phase)
        {
            SiegeState.Phase = phase;
            Plugin.Log.LogInfo($"[siege] {phase}, wave {SiegeState.Wave}/{SiegeState.TotalWaves}");
            Net.Broadcast();
        }

        private IEnumerator RunWave(List<WildSpawnType> order)
        {
            int queued = order.Count;
            while (!finished)
            {
                attackers.RemoveAll(b => b == null || b.IsDead);
                arriving.RemoveAll(sent => Time.time - sent > 45f);
                int alive = attackers.Count + spawning + arriving.Count;
                while (queued > 0 && alive < Plugin.MaxAlive.Value)
                {
                    SpawnOne(order[--queued]);
                    alive++;
                    yield return new WaitForSeconds(.35f);
                }
                int remaining = alive + queued;
                if (remaining != SiegeState.Remaining) { SiegeState.Remaining = remaining; Net.Broadcast(); }
                if (remaining == 0) yield break;
                if (Time.time >= nextRetarget) { nextRetarget = Time.time + RetargetEvery; Retarget(); }
                if (Time.time >= nextDoorCheck) { nextDoorCheck = Time.time + DoorCheckEvery; CheckDoors(); CheckDefences(); CheckStuck(); }
                if (!Defenders().Any()) { Defeat(); yield break; }
                yield return new WaitForSeconds(.5f);
            }
        }

        private readonly HashSet<WildSpawnType> unarmed = new HashSet<WildSpawnType>(UnarmedAhead);

        /// <summary>Roles whose profiles came without weapons on the loading screen, with stand-ins loaded for them there.</summary>
        internal static readonly HashSet<WildSpawnType> UnarmedAhead = new HashSet<WildSpawnType>();

        internal static bool Armed(Profile profile)
        {
            var equipment = profile?.Inventory?.Equipment;
            if (equipment == null) return true;
            return equipment.GetSlot(EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon).ContainedItem != null
                || equipment.GetSlot(EFT.InventoryLogic.EquipmentSlot.SecondPrimaryWeapon).ContainedItem != null
                || equipment.GetSlot(EFT.InventoryLogic.EquipmentSlot.Holster).ContainedItem != null;
        }

        private WildSpawnType StandIn(WildSpawnType role) => StandIn(role, unarmed);

        // A Black Division role with no gear falls back to their assault troops, then Raiders and Rogues, then scavs.
        internal static WildSpawnType StandIn(WildSpawnType role, ICollection<WildSpawnType> unarmed)
        {
            var troops = (WildSpawnType)BlackDivisionRoles[0];
            if (HaveBlackDivision() && role != troops && role != WildSpawnType.pmcBot && role != WildSpawnType.exUsec && role != WildSpawnType.assault && !unarmed.Contains(troops)) return troops;
            if (role == WildSpawnType.assault || (unarmed.Contains(WildSpawnType.pmcBot) && unarmed.Contains(WildSpawnType.exUsec))) return WildSpawnType.assault;
            return UnityEngine.Random.value < .5f && !unarmed.Contains(WildSpawnType.pmcBot) ? WildSpawnType.pmcBot : !unarmed.Contains(WildSpawnType.exUsec) ? WildSpawnType.exUsec : WildSpawnType.pmcBot;
        }

        private async void SpawnOne(WildSpawnType role)
        {
            for (int i = 0; i < 4 && unarmed.Contains(role); i++) role = StandIn(role);
            spawning++;
            try
            {
                var difficulty = DifficultyFor(role, SiegeState.Tier);
                BotCreationData data = null;
                for (int attempt = 0; attempt < 4 && !finished && (data == null || data.Count == 0); attempt++)
                {
                    // The server can come back empty-handed when asked for several at once: it's fine a moment later.
                    if (attempt > 0) await Task.Delay(700);
                    var ask = attempt < 2 ? role : WildSpawnType.assault;
                    data = await BotCreationData.Create(new GetProfileDataParams(EPlayerSide.Savage, ask, difficulty, 0f, new BotSpawnParams { Id_spawn = "hideoutsiege" }), spawner._botCreator, 1, spawner);
                    if ((data == null || data.Count == 0) && attempt == 1 && role != WildSpawnType.assault) Plugin.Log.LogWarning($"[siege] The server has no {role} bots: sending a scav instead.");
                }
                if (data == null || data.Count == 0) { Plugin.Log.LogError("[siege] Couldn't get a bot from the server."); return; }
                if (finished) return;
                foreach (var profile in data.Profiles) SiegeNvg.Equip(profile);
                var point = spawnPoints[0];
                arriving.Add(Time.time);
                spawner.SpawnBotsInZoneOnPositions(new List<ISpawnPoint> { point.SpawnPoint }, SiegeMap.Zone, data, OnSpawned);
            }
            catch (Exception e) { Plugin.Log.LogError("[siege] Spawning failed: " + e); }
            finally { spawning--; }
        }

        // Not straight away: the game is still in the middle of registering this bot when we find out.
        private static IEnumerator RemoveSoon(BotOwner bot)
        {
            yield return new WaitForSeconds(1.5f);
            try { if (bot != null && !bot.IsDead) bot.LeaveData.RemoveFromMap(); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't remove an unarmed bot: " + e.Message); }
        }

        private static bool Armed(BotOwner bot) => Armed(bot.Profile);

        private void OnSpawned(BotOwner bot)
        {
            if (arriving.Count > 0) arriving.RemoveAt(0);
            // A bot with nothing to shoot never wakes up, and the wave would wait for it forever.
            // That happens when a bot mod's gear comes from another mod that isn't installed.
            var role = bot.Profile.Info.Settings.Role;
            if (!Armed(bot))
            {
                if (unarmed.Add(role)) Plugin.Log.LogWarning($"[siege] {role} bots came without weapons (is a gear mod they rely on missing?). Using stand-ins for them.");
                StartCoroutine(RemoveSoon(bot));
                SpawnOne(StandIn(role));
                return;
            }
            attackers.Add(bot);
            bot.GetPlayer.OnPlayerDeadOrUnspawn += _ => { SiegeState.Kills++; };
            Plugin.Log.LogInfo($"[siege] {bot.Profile.Info.Settings.Role} in at {bot.Position}. Alive: {attackers.Count}");
            if (staging)
            {
                Wait(bot);
                return;
            }
            Tell(bot);
            StartCoroutine(RushIn(bot));
        }

        // Held out of sight up the stairs. The first of the first wave is the one on the camera.
        private void Wait(BotOwner bot)
        {
            Waiting.Add(bot);
            Vector3 spot;
            if (SiegeState.Phase == SiegePhase.None && toWalk.Count == 0)
            {
                toWalk.Add(bot);
                wayIn[bot] = Way.Stairs;
                spot = HoldingSpot(0);
            }
            else if (secondStaged < secondQuota && secondSpots.Count > 0)
            {
                wayIn[bot] = Way.Second;
                spot = secondSpots[secondStaged++ % secondSpots.Count];
                Plugin.Log.LogInfo($"[siege] A {bot.Profile.Info.Settings.Role} waits to come the second way at {spot:F2}.");
            }
            else if (SiegeEntries.GateHolding.Count > 0)
            {
                wayIn[bot] = Way.Gate;
                spot = SiegeEntries.GateHolding[gateStaged++ % SiegeEntries.GateHolding.Count];
            }
            else
            {
                wayIn[bot] = Way.Stairs;
                spot = HoldingSpot(staged);
            }
            SiegeMoves.Put(bot, spot);
            // The one on the camera already faces down the stairs, so he sets off as it comes on rather than turning first.
            if (toWalk.Count > 0 && toWalk[0] == bot) Face(bot, StairSpot(AtTheDoorDistance) - spot);
            waitingAt[bot] = spot;
            staged++;
        }

        private readonly Dictionary<BotOwner, Vector3> waitingAt = new Dictionary<BotOwner, Vector3>();

        // After the camera: the one who set the charge, and anyone else not where he waits, goes back there. Nobody can see
        // the stairs or the cultist room from the bed.
        private void BackToWaiting()
        {
            foreach (var bot in Waiting.ToList())
            {
                if (bot == null || bot.IsDead || !waitingAt.TryGetValue(bot, out Vector3 spot)) continue;
                if (Breachers.Remove(bot))
                {
                    bot.Mover.Stop();
                    bot.GetPlayer.Move(Vector2.zero);
                }
                if ((bot.Position - spot).sqrMagnitude < .6f * .6f) continue;
                Plugin.Log.LogInfo($"[siege] A {bot.Profile.Info.Settings.Role} was at {bot.Position:F1} after the camera, not where he waits: back to {spot:F1}.");
                SiegeMoves.Put(bot, spot);
            }
            GunBack(toWalk.FirstOrDefault());
        }

        private float nextKeptCheck;

        // While the front door's shut, any of them inside it goes back to where he waits. So does anyone waiting to come the
        // second way who's got away from where he waits (one waiting in the tunnel turned up in the room beside it).
        private void KeepOutside()
        {
            foreach (var bot in Waiting)
            {
                if (bot == null || bot.IsDead || !waitingAt.TryGetValue(bot, out Vector3 spot)) continue;
                if (wayIn.TryGetValue(bot, out Way way) && way == Way.Second)
                {
                    if ((bot.Position - spot).sqrMagnitude > WanderedFrom * WanderedFrom && !Breachers.Contains(bot))
                    {
                        Plugin.Log.LogWarning($"[siege] A {bot.Profile.Info.Settings.Role} waiting to come the second way was at {bot.Position:F2}, not where he waits: back to {spot:F2}.");
                        SiegeMoves.Put(bot, spot);
                    }
                    continue;
                }
                if (Vector3.Dot(bot.Position - SiegeBreach.Doorway, SiegeBreach.Outward) > -.2f) continue;
                Plugin.Log.LogWarning($"[siege] A {bot.Profile.Info.Settings.Role} waiting to come in was inside the front door at {bot.Position:F1}: back to {spot:F1}.");
                SiegeMoves.Put(bot, spot);
            }
        }

        // A wave only ends when everyone in it is dead, so nobody can be left standing around. Still on the stairs and going
        // nowhere gets you moved inside the door. Already inside gets you sent after the nearest defender.
        private void CheckStuck()
        {
            var defenders = Defenders().ToList();
            if (defenders.Count == 0) return;
            // The walls the bots see by too: with the movement layers alone, a wall not on them (Hideout Extensions' rooms
            // may have some) let an attacker behind it count as in sight of you, and it was never moved on.
            int mask = EFTHardSettings.Instance.MOVEMENT_MASK | LayersMaskController.HighPolyWithTerrainMask;
            foreach (var bot in attackers)
            {
                if (bot == null || bot.IsDead || Breachers.Contains(bot)) continue;
                Vector3 now = bot.Position;
                if (!lastMoved.TryGetValue(bot, out var seen) || (now - seen.Where).sqrMagnitude > 1f)
                {
                    lastMoved[bot] = (now, Time.time);
                    continue;
                }
                if (Time.time - seen.When < StuckSeconds) continue;
                lastMoved[bot] = (now, Time.time);
                if (defenders.Any(d => !Physics.Linecast(now + Vector3.up * 1.4f, d.Position + Vector3.up * 1.4f, mask, QueryTriggerInteraction.Ignore))) continue;
                bool outside = Vector3.Dot(now - SiegeBreach.Doorway, SiegeBreach.Outward) > .5f;
                Vector3 inside = SiegeBreach.Doorway - SiegeBreach.Outward * 1.5f;
                if (outside && NavMesh.SamplePosition(inside, out var hit, 1.5f, NavMesh.AllAreas))
                    SiegeMoves.Unseen(bot, hit.position, "stuck on the stairs");
                else
                {
                    Player closest = defenders.OrderBy(d => (d.Position - now).sqrMagnitude).First();
                    if (!SiegePush.Active) bot.GoToPoint(closest.Position, false, -1f, false, false);
                    // Nowhere it can walk from here leads to you: somewhere out of sight that does.
                    else if (CutOff(now, closest) && !WalledOff(now, closest) && Rejoin(closest, now, defenders, mask, out Vector3 spot) && (spot - now).sqrMagnitude > 3f * 3f)
                        SiegeMoves.Unseen(bot, spot, "going nowhere");
                }
            }
        }

        // Cut off by your sandbags or steel: they're blown (CheckDefences), never got round by moving someone past them.
        private static bool WalledOff(Vector3 from, Player defender)
        {
            if (SiegeDecor.Standing().Count == 0) return false;
            if (!NavMesh.SamplePosition(from, out var here, 1.5f, NavMesh.AllAreas) || !NavMesh.SamplePosition(defender.Position, out var there, 1.5f, NavMesh.AllAreas)) return false;
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(here.position, there.position, NavMesh.AllAreas, path) && path.corners.Length > 0 && SiegeDecor.Stopping(path.corners[path.corners.Length - 1]) != null;
        }

        // No walk from here gets to them: only then is it worth moving. Holding still with a way through is its own AI's call.
        private static bool CutOff(Vector3 from, Player defender)
        {
            if (!NavMesh.SamplePosition(from, out var here, 1.5f, NavMesh.AllAreas) || !NavMesh.SamplePosition(defender.Position, out var there, 1.5f, NavMesh.AllAreas)) return true;
            var path = new NavMeshPath();
            return !NavMesh.CalculatePath(here.position, there.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete;
        }

        // The walked floor nearest this bot that joins up with where the defender is, a little way from them and out of
        // everyone's sight.
        private static bool Rejoin(Player defender, Vector3 from, List<Player> defenders, int mask, out Vector3 spot)
        {
            spot = default;
            if (!NavMesh.SamplePosition(defender.Position, out var there, 1.5f, NavMesh.AllAreas)) return false;
            var path = new NavMeshPath();
            int tried = 0;
            foreach (var cell in SiegeMap.Walkable.Where((c, i) => i % 7 == 0)
                         .Where(c => { float d = (c - defender.Position).sqrMagnitude; return d > 8f * 8f && d < 25f * 25f; })
                         .OrderBy(c => (c - from).sqrMagnitude))
            {
                if (++tried > 20) break;
                if (defenders.Any(d => !Physics.Linecast(cell + Vector3.up * 1.4f, d.Position + Vector3.up * 1.4f, mask, QueryTriggerInteraction.Ignore))) continue;
                if (!NavMesh.CalculatePath(there.position, cell, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                spot = cell;
                return true;
            }
            return false;
        }

        // Someone's shut a door, and it's why an attacker can't get to them. One goes and blows it.
        private void CheckDoors()
        {
            SiegeBreach.UpdateDoorBlocks();
            if (breachInProgress || Time.time < nextBreach) return;
            var defenders = Defenders().ToList();
            if (defenders.Count == 0) return;
            var path = new NavMeshPath();
            var alive = attackers.Where(b => b != null && !b.IsDead && !Waiting.Contains(b) && !Breachers.Contains(b)).ToList();
            var shut = SiegeBreach.InnerDoors().Where(d => SiegeBreach.IsShut(d.Door)).ToList();
            if (shut.Count == 0) return;
            int looked = 0;
            // Those nearest one of you first, and only a couple a check: each costs a few paths worked out.
            foreach (var bot in alive.OrderBy(b => defenders.Min(d => (d.Position - b.Position).sqrMagnitude)))
            {
                if (looked >= 2) break;
                Player closest = defenders.OrderBy(d => (d.Position - bot.Position).sqrMagnitude).First();
                if (!NavMesh.SamplePosition(closest.Position, out var target, 1.5f, NavMesh.AllAreas)) continue;
                if (!NavMesh.CalculatePath(bot.Position, target.position, NavMesh.AllAreas, path) || path.status == NavMeshPathStatus.PathComplete || path.corners.Length == 0) continue;
                looked++;
                var door = DoorTowards(bot.Position, target.position, shut, path);
                if (door == null)
                {
                    if (Time.time >= nextNoDoorNote)
                    {
                        nextNoDoorNote = Time.time + 20f;
                        Plugin.Log.LogInfo($"[siege] No way through to you from {bot.Position:F1}: " + string.Join("; ", shut.Select(d => DoorNote(bot.Position, target.position, d.Door, d.Centre, path))));
                    }
                    continue;
                }
                var (way, centre, side) = door.Value;
                var breacher = alive.Where(b => !SiegePush.Sees(b)).OrderBy(b => (b.Position - centre).sqrMagnitude).FirstOrDefault();
                if (breacher == null) return;
                Plugin.Log.LogInfo($"[siege] {way.transform.parent?.name ?? way.name} is in the way: sending someone to breach it.");
                StartCoroutine(BreachInnerDoor(way, centre, breacher, side));
                return;
            }
        }

        private readonly Dictionary<EFT.Interactive.Door, Vector3> throughDoor = new Dictionary<EFT.Interactive.Door, Vector3>();
        private float nextNoDoorNote;

        private string DoorNote(Vector3 from, Vector3 target, EFT.Interactive.Door door, Vector3 centre, NavMeshPath path)
        {
            bool one = Sides(from, door, centre, path, out Vector3 a, out Vector3 b, out bool toA, out _, out _);
            return $"{door.name} at {centre:F1} {(one ? toA ? "near" : "far" : "neither/both")} {Flat(a - target).magnitude:F0}/{Flat(b - target).magnitude:F0}";
        }

        // The two sides of a door, and whether exactly one of them can be walked to from here. Across the door's thinnest way
        // first, then the other: the way through isn't always the thinnest way across a door's parts.
        private bool Sides(Vector3 from, EFT.Interactive.Door door, Vector3 centre, NavMeshPath path, out Vector3 a, out Vector3 b, out bool toA, out float viaA, out float viaB)
        {
            if (!throughDoor.TryGetValue(door, out Vector3 through)) throughDoor[door] = through = SiegeBreach.Across(door);
            a = b = centre;
            toA = false;
            viaA = viaB = 0f;
            foreach (var way in new[] { through, Vector3.Cross(Vector3.up, through) })
            {
                a = OnTheFloor(centre + way * 1.1f, centre.y);
                b = OnTheFloor(centre - way * 1.1f, centre.y);
                toA = Walk(from, a, path, out viaA);
                bool toB = Walk(from, b, path, out viaB);
                if (toA == toB) continue;
                if (way != through) throughDoor[door] = way;
                return true;
            }
            return false;
        }

        // Which shut door leads toward them. The walk to them stops wherever's nearest them in a straight line, which can be
        // the far wall of a room from its door; so of the shut doors this bot can walk up to (and not round), the one whose
        // other side is nearest them, and nearer than the bot is: the hideout's rooms open off one another, so that's the way.
        private (EFT.Interactive.Door, Vector3, Vector3)? DoorTowards(Vector3 from, Vector3 target, List<(EFT.Interactive.Door Door, Vector3 Centre)> shut, NavMeshPath path)
        {
            float here = Flat(from - target).magnitude;
            (EFT.Interactive.Door, Vector3, Vector3)? best = null, anyway = null;
            float bestScore = float.MaxValue, anywayScore = float.MaxValue;
            foreach (var (door, centre) in shut)
            {
                if (!Sides(from, door, centre, path, out Vector3 a, out Vector3 b, out bool toA, out float viaA, out float viaB)) continue;
                Vector3 far = toA ? b : a;
                float beyond = Flat(far - target).magnitude;
                float score = (toA ? viaA : viaB) + beyond;
                if (score < anywayScore) { anywayScore = score; anyway = (door, centre, toA ? a : b); }
                if (beyond >= here) continue;
                if (score < bestScore) { bestScore = score; best = (door, centre, toA ? a : b); }
            }
            return best ?? anyway;
        }

        private static Vector3 OnTheFloor(Vector3 p, float doorMiddle)
        {
            if (Physics.Raycast(new Vector3(p.x, doorMiddle + .3f, p.z), Vector3.down, out var hit, 2.5f, EFTHardSettings.Instance.MOVEMENT_MASK, QueryTriggerInteraction.Ignore))
                return hit.point;
            return new Vector3(p.x, doorMiddle - 1.1f, p.z);
        }

        private static bool Walk(Vector3 from, Vector3 to, NavMeshPath path, out float length)
        {
            length = 0f;
            // Floor right there, not somewhere the far side of a wall it found by looking further.
            if (!NavMesh.SamplePosition(to, out var hit, .8f, NavMesh.AllAreas) || Flat(hit.position - to).sqrMagnitude > .8f * .8f) return false;
            if (!NavMesh.CalculatePath(from, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) return false;
            for (int i = 1; i < path.corners.Length; i++) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            return true;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // Your sandbags and steel walled them off from you: no route gets past them, and it stops at one. One goes and blows it.
        private void CheckDefences()
        {
            if (breachInProgress || Time.time < nextBreach) return;
            var standing = SiegeDecor.Standing();
            if (standing.Count == 0) return;
            var defenders = Defenders().ToList();
            if (defenders.Count == 0) return;
            var path = new NavMeshPath();
            var alive = attackers.Where(b => b != null && !b.IsDead && !Waiting.Contains(b) && !Breachers.Contains(b)).ToList();
            int looked = 0;
            foreach (var bot in alive.OrderBy(b => defenders.Min(d => (d.Position - b.Position).sqrMagnitude)))
            {
                if (looked >= 2) break;
                Player closest = defenders.OrderBy(d => (d.Position - bot.Position).sqrMagnitude).First();
                if (!NavMesh.SamplePosition(closest.Position, out var target, 1.5f, NavMesh.AllAreas) || !NavMesh.SamplePosition(bot.Position, out var from, 1.5f, NavMesh.AllAreas)) continue;
                if (!NavMesh.CalculatePath(from.position, target.position, NavMesh.AllAreas, path) || path.status == NavMeshPathStatus.PathComplete || path.corners.Length == 0) continue;
                looked++;
                Vector3 end = path.corners[path.corners.Length - 1];
                // A shut door there is the door check's job.
                if (SiegeBreach.InnerDoors().Any(d => SiegeBreach.IsShut(d.Door) && (d.Centre - end).sqrMagnitude < 3f * 3f)) continue;
                var wall = SiegeDecor.Stopping(end);
                if (wall == null) continue;
                // It's between where they're stopped and you, or it isn't what's stopping them.
                Vector3 toYou = Flat(closest.Position - end), toWall = Flat(wall.position - end);
                if (Vector3.Dot(toYou, toWall) <= 0f || toWall.magnitude > toYou.magnitude + .5f) continue;
                // The rest who can't get to you gather where this walk stops, in front of it.
                SiegePush.ShutOut(end);
                // One who isn't busy with one of you: anyone in sight of you fights you instead.
                var breacher = alive.Where(b => !SiegePush.Sees(b)).OrderBy(b => (b.Position - wall.position).sqrMagnitude).FirstOrDefault();
                if (breacher == null) return;
                Plugin.Log.LogInfo($"[siege] Your {SiegeDecor.Kind(wall)} at {wall.position:F1} has them shut out: sending someone to blow it.");
                StartCoroutine(BreachDefence(wall, breacher, end));
                return;
            }
        }

        // As a door: walks up to it on the side it's coming from, sets the charge, walks back, and it blows.
        private IEnumerator BreachDefence(Transform wall, BotOwner bot, Vector3 side)
        {
            breachInProgress = true;
            Breachers.Add(bot);
            var takingUp = StartCoroutine(TakeUpTheCharge(bot));
            var (stand, at, way) = SiegeDecor.ChargeOn(wall, side);
            stand = NavMesh.SamplePosition(stand, out var hit, 1.5f, NavMesh.AllAreas) ? hit.position : bot.Position;
            SiegePush.Order(bot, stand);
            SiegePush.Rally = stand;
            float giveUp = Time.time + BreachWalkSeconds, nextOrder = 0f;
            while (Time.time < giveUp && bot != null && !bot.IsDead && wall != null && wall.gameObject.activeInHierarchy && (bot.Position - stand).sqrMagnitude > .3f * .3f)
            {
                if (SawYou(bot)) { StopCoroutine(takingUp); JobOff(bot); yield break; }
                if (Time.time >= nextOrder) { nextOrder = Time.time + 1f; SiegePush.Route(bot, stand); }
                bot.Mover.SetTargetMoveSpeed(1f);
                bot.Steering.LookToMovingDirection();
                yield return null;
            }
            if (wall == null || !wall.gameObject.activeInHierarchy) { StopCoroutine(takingUp); EndBreach(bot); yield break; }
            // Shot on the way: no charge, and nothing to tell you, as he never got there.
            if (!Setting(bot)) { StopCoroutine(takingUp); BreachStopped(bot, false); yield break; }
            // Didn't get there: the charge goes on by hand or not at all, never from across the room.
            if (!Reached(bot, stand)) { StopCoroutine(takingUp); CouldntGetThere(bot, SiegeDecor.Kind(wall), stand); yield break; }
            SiegePush.Order(bot, bot.Position);
            bot.Mover.Stop();
            Face(bot, -way);
            yield return takingUp;
            if (!Setting(bot)) { BreachStopped(bot, false); yield break; }
            if (SawYou(bot)) { JobOff(bot); yield break; }
            // Only now, with him at it and kneeling.
            SiegeState.Announce("THEY'RE BREACHING YOUR DEFENCES", 3f);
            Net.Broadcast();
            Kneel(bot);
            float setBy = Time.time + .3f + PlantSeconds;
            while (Time.time < setBy && Setting(bot) && !SawYou(bot)) yield return null;
            if (!Setting(bot)) { BreachStopped(bot, true); yield break; }
            if (SawYou(bot)) { JobOff(bot); yield break; }
            // The charge in his hand goes on the wall, its lamp blinking down to the blast: from here it goes off whatever happens to him.
            SiegeBreach.StopCarrying();
            Net.SendCarry("");
            float planted = Time.time;
            var charge = SiegeCharge.OnItsOwn(at).PutOn(at, way, () => planted + FuseSeconds - Time.time, false);
            GunBack(bot);
            if (Setting(bot))
            {
                Vector3 away = NavMesh.SamplePosition(stand + way * 4f, out hit, 3f, NavMesh.AllAreas) ? hit.position : bot.Position;
                SiegePush.Order(bot, away);
                nextOrder = 0f;
                while (Time.time < planted + FuseSeconds && bot != null && !bot.IsDead)
                {
                    if (Time.time >= nextOrder) { nextOrder = Time.time + 1f; SiegePush.Route(bot, away); }
                    bot.Mover.SetTargetMoveSpeed(1f);
                    bot.Steering.LookToMovingDirection();
                    yield return null;
                }
            }
            while (Time.time < planted + FuseSeconds) yield return null;
            if (charge != null) Destroy(charge);
            if (!finished)
            {
                SiegeBreach.Blast(at);
                Net.SendDoor(true, at);
            }
            EndBreach(bot);
        }

        // Walks up to it, sets the charge, walks back, and it blows. If someone opens the door first, the job's off.
        private IEnumerator BreachInnerDoor(EFT.Interactive.Door door, Vector3 centre, BotOwner bot, Vector3? side = null)
        {
            breachInProgress = true;
            Breachers.Add(bot);
            var takingUp = StartCoroutine(TakeUpTheCharge(bot));
            // The side it's breached from: the one they can get to.
            Vector3 toBot = (side ?? bot.Position) - centre;
            toBot.y = 0f;
            toBot = toBot.sqrMagnitude > .01f ? toBot.normalized : Vector3.forward;
            Vector3 stand = NavMesh.SamplePosition(centre + toBot * .8f, out var hit, 1.5f, NavMesh.AllAreas) ? hit.position : bot.Position;

            SiegePush.Order(bot, stand);
            SiegePush.Rally = stand;
            float giveUp = Time.time + BreachWalkSeconds, nextOrder = 0f;
            while (Time.time < giveUp && bot != null && !bot.IsDead && SiegeBreach.IsShut(door) && (bot.Position - stand).sqrMagnitude > .25f)
            {
                if (SawYou(bot)) { StopCoroutine(takingUp); JobOff(bot); yield break; }
                if (Time.time >= nextOrder) { nextOrder = Time.time + 1f; SiegePush.Route(bot, stand); }
                bot.Mover.SetTargetMoveSpeed(1f);
                bot.Steering.LookToMovingDirection();
                yield return null;
            }
            bool alive = bot != null && !bot.IsDead;
            // Stopped, not left running: a slow change of hands mustn't end with a second charge in them after this one's gone on.
            if (!SiegeBreach.IsShut(door)) { StopCoroutine(takingUp); EndBreach(bot); yield break; }
            Vector3 at = SiegeBreach.ChargeSpot(centre, centre + toBot);
            // Shot on the way: no charge, and nothing to tell you, as he never got there.
            if (!alive) { StopCoroutine(takingUp); BreachStopped(bot, false); yield break; }
            // Didn't get there: the charge goes on by hand or not at all, never from across the room.
            if (!Reached(bot, stand)) { StopCoroutine(takingUp); CouldntGetThere(bot, door.name, stand); yield break; }
            SiegePush.Order(bot, bot.Position);
            bot.Mover.Stop();
            // Only when it's still running: waiting on one that's been stopped never ends. It finishes by itself if he dies.
            yield return takingUp;
            if (!SiegeBreach.IsShut(door)) { EndBreach(bot); yield break; }
            if (!Setting(bot)) { BreachStopped(bot, false); yield break; }
            if (SawYou(bot)) { JobOff(bot); yield break; }
            // Only now, with him at the door and kneeling.
            SiegeState.Announce("THEY'RE BREACHING", 3f);
            Net.Broadcast();
            Kneel(bot);
            // Setting it takes a moment, and he can be stopped meanwhile: it's only on the door, fuse lit, once he's done.
            float setBy = Time.time + .3f + PlantSeconds;
            while (Time.time < setBy && Setting(bot) && !SawYou(bot)) yield return null;
            if (!Setting(bot)) { BreachStopped(bot, true); yield break; }
            if (SawYou(bot)) { JobOff(bot); yield break; }
            PlantAt(at);
            GunBack(bot);
            // The charge's lamp counts down the whole of InnerFuse from when it went on.
            float blowsAt = Time.time + InnerFuse;
            if (Setting(bot))
            {
                Vector3 away = NavMesh.SamplePosition(centre + toBot * 5f, out hit, 3f, NavMesh.AllAreas) ? hit.position : bot.Position;
                SiegePush.Order(bot, away);
                nextOrder = 0f;
                while (Time.time < blowsAt && bot != null && !bot.IsDead)
                {
                    if (Time.time >= nextOrder) { nextOrder = Time.time + 1f; SiegePush.Route(bot, away); }
                    bot.Mover.SetTargetMoveSpeed(1f);
                    bot.Steering.LookToMovingDirection();
                    yield return null;
                }
            }
            while (Time.time < blowsAt) yield return null;
            SiegeBreach.Blast(at);
            Net.SendDoor(true, at);
            Plugin.Log.LogInfo($"[siege] {door.name} at {centre:F1} is blown.");
            EndBreach(bot);
        }

        // Still up to it: alive, there, and not the raid over.
        private bool Setting(BotOwner bot) => !finished && bot != null && !bot.IsDead && bot.GetPlayer != null;

        // Put down before the charge was on: no breach, and a few seconds before the next of them tries.
        private const float BreachAgainAfter = 6f;
        private float nextBreach;

        // On his way to breach, or setting it, and one of you's in sight: the job's off, and he fights you.
        private static bool SawYou(BotOwner bot) => bot != null && !bot.IsDead && SiegePush.Sees(bot);

        private void JobOff(BotOwner bot)
        {
            Plugin.Log.LogInfo($"[siege] The {bot?.Profile?.Info?.Settings?.Role} going to breach has one of you in sight: fighting instead.");
            nextBreach = Time.time + BreachAgainAfter;
            EndBreach(bot);
        }

        // Killed setting it (you were told he was at it) or on his way there (you weren't, so nothing's said now either).
        private void BreachStopped(BotOwner bot, bool atIt)
        {
            if (!finished)
            {
                if (atIt)
                {
                    SiegeState.Announce("BREACH STOPPED", 2.5f);
                    Net.Broadcast();
                }
                Plugin.Log.LogInfo($"[siege] The one with the charge went down {(atIt ? "setting it" : "on his way")}: no breach.");
            }
            nextBreach = Time.time + BreachAgainAfter;
            EndBreach(bot);
        }

        // Where he's meant to stand to set it, near enough to reach.
        private static bool Reached(BotOwner bot, Vector3 stand) => bot != null && Flat(bot.Position - stand).sqrMagnitude < 1f && Mathf.Abs(bot.Position.y - stand.y) < 1.2f;

        private void CouldntGetThere(BotOwner bot, string what, Vector3 stand)
        {
            Plugin.Log.LogInfo($"[siege] The {bot?.Profile?.Info?.Settings?.Role} going to breach {what} didn't get to {stand:F1} (he's at {bot?.Position:F1}): no charge, someone else tries.");
            nextBreach = Time.time + BreachAgainAfter;
            EndBreach(bot);
        }

        private void EndBreach(BotOwner bot)
        {
            breachInProgress = false;
            Breachers.Remove(bot);
            SiegePush.Release(bot);
            SiegePush.Rally = null;
            SiegeBreach.StopCarrying();
            Net.SendCarry("");
            GunBack(bot);
            Tell(bot);
        }

        // Tell attackers who they're here for, or they'd patrol until they happened to see someone. And knowing you're
        // somewhere isn't enough: out of sight, a bot still goes off on patrol. So those come looking.
        private void Retarget()
        {
            var defenders = Defenders().ToList();
            foreach (var bot in attackers)
            {
                Tell(bot);
                if (!Breachers.Contains(bot) && !Waiting.Contains(bot)) GunBack(bot);
                if (defenders.Count == 0 || bot == null || bot.IsDead || Breachers.Contains(bot) || Waiting.Contains(bot) || (bot.Memory?.GoalEnemy != null && bot.Memory.GoalEnemy.IsVisible)) continue;
                if (SiegePush.Active) continue;
                Player closest = defenders.OrderBy(d => (d.Position - bot.Position).sqrMagnitude).First();
                SiegePush.Route(bot, closest.Position);
            }
        }

        private static void Tell(BotOwner bot)
        {
            if (bot == null || bot.IsDead || bot.BotsGroup == null) return;
            foreach (var player in Defenders())
            {
                try { bot.BotsGroup.AddEnemy(player, EBotEnemyCause.addPlayer); }
                catch (Exception e) { Plugin.Log.LogWarning("[siege] " + e.Message); }
            }
        }

        internal static IEnumerable<Player> Defenders()
        {
            var world = Singleton<GameWorld>.Instance;
            if (world == null) yield break;
            foreach (var p in world.AllAlivePlayersList)
                if (p != null && !p.IsAI && p.HealthController != null && p.HealthController.IsAlive) yield return p;
        }

        internal static bool HaveBlackDivision() => Enum.IsDefined(typeof(WildSpawnType), BlackDivisionRoles[0]);

        // Black Division cut the power before they come in (nobody else does), and it stays cut: the generator's switch
        // won't bring it back for the rest of the raid. Even if you'd already switched it off yourself.
        private void CutThePower()
        {
            if (SiegeGenerator.Cut) return;
            SiegeGenerator.CutForGood();
            SiegeState.Announce("THEY'VE CUT THE POWER", 3f);
            Net.Broadcast();
            Plugin.Log.LogInfo("[siege] They cut the power.");
        }

        private void Victory()
        {
            finished = true;
            SiegeState.Remaining = 0;
            SetPhase(SiegePhase.Won);
            SiegeSounds.Play("victory");
            SiegeSounds.StopAlarm();
            SiegeState.Announce("HIDEOUT HELD", 8f);
            SiegeState.Notify("Loot them: what you put in your stash or a bag from your racks is kept", 10f);
            SiegeState.CountdownUntil = Time.time + Plugin.LootTime.Value;
            Net.Broadcast();
            SiegeBackend.Report(true, SiegeState.Tier, SiegeState.Wave, SiegeState.Kills);
            StartCoroutine(LeaveAfterLooting());
        }

        private IEnumerator LeaveAfterLooting()
        {
            yield return new WaitUntil(() => Time.time >= SiegeState.CountdownUntil);
            SiegeExit.Leave(ExitStatus.Survived);
        }

        private IEnumerator LeaveDead()
        {
            yield return new WaitForSeconds(4f);
            SiegeExit.Leave(ExitStatus.Killed);
        }

        private void Defeat()
        {
            finished = true;
            SetPhase(SiegePhase.Lost);
            SiegeState.Announce("HIDEOUT OVERRUN", 4f);
            Net.Broadcast();
            SiegeBackend.Report(false, SiegeState.Tier, SiegeState.Wave, SiegeState.Kills);
            StartCoroutine(LeaveDead());
        }
    }
}

namespace HideoutSiege
{
    // The game starts each bot's creation without waiting on it, so a failure in there would otherwise vanish silently.
    [HarmonyLib.HarmonyPatch(typeof(BotCreatorClient), nameof(BotCreatorClient.CreateBot))]
    internal static class BotCreateWatchPatch
    {
        [HarmonyLib.HarmonyPostfix]
        private static void Postfix(System.Threading.Tasks.Task __result)
        {
            if (!SiegeLauncher.Selected || __result == null) return;
            __result.ContinueWith(t => Plugin.Log.LogError("[siege] A bot failed to spawn: " + t.Exception), System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}

namespace HideoutSiege
{
    // Every bot in a siege is attacking you, whatever faction it's from, so they don't fight each other.
    [HarmonyLib.HarmonyPatch(typeof(BotsGroup), nameof(BotsGroup.AddEnemy))]
    internal static class AttackersAreFriendsPatch
    {
        [HarmonyLib.HarmonyPrefix]
        private static bool Prefix(IPlayer person, ref bool __result)
        {
            if (person == null || !person.IsAI || !SiegeLauncher.Selected) return true;
            __result = false;
            return false;
        }
    }
}
