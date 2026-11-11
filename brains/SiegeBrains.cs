using System.Collections.Generic;
using System.Text;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;

namespace HideoutSiege.Brains
{
    /// <summary>
    /// The siege's BigBrain layer, loaded only where BigBrain is installed. It sits above SAIN's layers (and the game's), and
    /// is only ever on for a siege attacker the siege wants moving: one with a job (a door to breach, a tunnel to come
    /// through), or one that hasn't seen any of the defenders for a few seconds. The rest of the time the bot's own AI runs.
    /// </summary>
    public static class SiegeBrains
    {
        // Above SAIN's, whose highest is 99.
        private const int Priority = 100;

        // Every brain an attacker might have: scavs, Raiders and Rogues, PMCs, and the bot mods that borrow those.
        private static readonly List<string> BrainNames = new List<string>
        {
            "Assault", "CursAssault", "PMC", "ExUsec", "PmcBear", "PmcUsec", "ArenaFighter", "Obdolbs", "Marksman", "Gifter",
            "BossBully", "BossGluhar", "BossBoar", "BossPartisan", "BossKojaniy", "BossSanitar", "BossKolontay", "BossTest", "Tagilla",
            "Killa", "Knight", "BigPipe", "BirdEye", "FollowerBully", "FollowerGluharAssault", "FollowerGluharProtect", "FollowerGluharScout",
            "FollowerKojaniy", "FollowerSanitar", "TagillaFollower", "FlBoar", "FlBoarCl", "FlBoarSt", "FlKlnAslt", "KolonSec",
            "SectantWarrior", "SectantPriest",
        };

        public static void Register() => BrainManager.AddCustomLayer(typeof(SiegeLayer), BrainNames, Priority);
    }

    public sealed class SiegeLayer : CustomLayer
    {
        public SiegeLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "HideoutSiege";

        public override bool IsActive() => SiegePush.Wants(BotOwner, out _, out _);

        public override Action GetNextAction() => new Action(typeof(SiegeMove), "siege");

        public override bool IsCurrentActionEnding() => !SiegePush.Wants(BotOwner, out _, out _);

        public override void Start()
        {
            BotOwner.PatrollingData?.Pause();
        }

        public override void Stop()
        {
            BotOwner.Sprint(false);
            BotOwner.PatrollingData?.Unpause();
        }

        public override void BuildDebugText(StringBuilder stringBuilder) => stringBuilder.AppendLine("Hideout Siege: going for the defenders");
    }

    public sealed class SiegeMove : CustomLogic
    {
        private float nextOrder;
        private Vector3 sent;

        public SiegeMove(BotOwner botOwner) : base(botOwner) { }

        public override void Start() => nextOrder = 0f;

        public override void Update(CustomLayer.ActionData data)
        {
            if (SiegePush.Wants(BotOwner, out Vector3 target, out bool sprint)) SiegePush.Steer(BotOwner, target, sprint, ref nextOrder, ref sent);
        }
    }
}
