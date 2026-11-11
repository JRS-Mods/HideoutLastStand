using System.Collections;
using EFT;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// The one who sets the charge on the camera, kept on the line straight out from the middle of the front door. He
    /// walks on his own legs, and after every step he's put back on the line, as far along it as that step took him: so he
    /// comes down the middle of the stairs square on to the door, stops where he's meant to, and nothing can take him off
    /// to one side or through the door. Which way he faces is set here and by nothing else: his thinking, his steering and
    /// the game's walking for bots are all stopped while he's on it (he's one of those waiting).
    /// </summary>
    internal sealed class SiegeRail
    {
        // The most a step can take him in a second (anything more isn't a step), and how long he can go without getting
        // anywhere before that's as far as he's going. Coming round to the way he's going takes about this long, at most
        // this many degrees a second.
        private const float FastestStep = 3.5f, StoppedAfter = .35f, ComingRound = .22f, FastestTurn = 320f;

        private readonly BotOwner bot;
        private readonly Player player;
        private float at, low, high;
        private Vector3 last;
        private bool on;

        internal SiegeRail(BotOwner bot)
        {
            this.bot = bot;
            player = bot.GetPlayer;
            at = low = high = Out(bot.Position);
            last = bot.Position;
            Square(player, player.Rotation.x);
            player.MovementContext.OnMotionApplied += Applied;
            on = true;
        }

        /// <summary>How far out from the door he is.</summary>
        internal float At => at;

        private static float Out(Vector3 p) => Vector3.Dot(p - SiegeBreach.Doorway, SiegeBreach.Outward);

        private bool Alive => on && bot != null && !bot.IsDead && player != null && player.MovementContext != null;

        /// <summary>
        /// Looking this way, with his body turned to it as well. Standing still, the game only brings a body round once
        /// it's a long way off where its owner's looking, so one that's been put somewhere stands half turned away.
        /// </summary>
        internal static void Square(Player player, float yaw)
        {
            if (player == null || player.MovementContext == null) return;
            player.Rotation = new Vector2(yaw, 0f);
            player.MovementContext.ApplyRotation(Quaternion.Euler(0f, yaw, 0f));
        }

        // After each step: only how far along the line his legs took him counts, and only as far as he's allowed to go.
        // That's read from the stride itself, not from where his body ended up: whatever the game finds in his body's way
        // up there (and it can find something where he stands waiting) would otherwise keep him from ever setting off.
        private float diagNext, diagRaw, diagAsked, diagStride; private int diagTry, diagApplied; private CollisionFlags diagFlags; // TEMP-DIAG
        private void Applied(CollisionFlags flags, Vector3 asked)
        {
            if (!Alive) return;
            Vector3 now = bot.Position;
            float step = FastestStep * Time.deltaTime, stride = Vector3.Dot(player.MovementContext.PlayerAnimatorDeltaPosition, SiegeBreach.Outward);
            diagApplied++; diagRaw += Out(now) - Out(last); diagAsked += Vector3.Dot(asked, SiegeBreach.Outward); diagStride += stride; diagFlags |= flags; // TEMP-DIAG
            at = Mathf.Clamp(at + Mathf.Clamp(stride, -step, step), low, high);
            Vector3 put = SiegeBreach.Doorway + SiegeBreach.Outward * at;
            // Feet on the step he's on. Felt for from where he last stood, not from where his body is now: the game keeps
            // putting a waiting body back where it waits, at the top, and from up there the steps below are out of reach,
            // so he'd walk down the stairwell at the height of the landing.
            put.y = Ground(put, last.y);
            bot.Transform.position = put;
            last = put;
        }

        private static readonly RaycastHit[] hits = new RaycastHit[8];

        // The highest floor under this spot within a step's reach of where his feet were: stairs, not bodies.
        private static float Ground(Vector3 at, float feet)
        {
            const float Above = .6f, Below = 1f;
            int n = Physics.RaycastNonAlloc(new Vector3(at.x, feet + Above, at.z), Vector3.down, hits, Above + Below, LayersMaskController.PlayerCollisionsMask, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
                if (hits[i].collider != null && hits[i].collider.GetComponentInParent<Player>() == null && hits[i].point.y > best) best = hits[i].point.y;
            return float.IsNegativeInfinity(best) ? feet : best;
        }

        /// <summary>
        /// Along the line to this far out from the door, facing the way he's going. Facing the other way to begin with, he
        /// comes round on his feet as he sets off, as anyone turning to walk away does.
        /// </summary>
        internal IEnumerator Walk(float to, float speed, float seconds)
        {
            if (!Alive) yield break;
            Vector3 along = to < at ? -SiegeBreach.Outward : SiegeBreach.Outward;
            float yaw = Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg, giveUp = Time.time + seconds;
            low = Mathf.Min(at, to);
            high = Mathf.Max(at, to);
            float from = at, was = at, since = Time.time, looking = player.Rotation.x, turning = 0f;
            while (Alive && Time.time < giveUp)
            {
                float left = Mathf.Abs(to - at);
                if (left <= .02f) break;
                // Having set off, then getting nowhere: something's in his way (the door), and this is as far as he goes.
                if (Mathf.Abs(at - was) > .01f) { was = at; since = Time.time; }
                else if (Mathf.Abs(at - from) > .1f && Time.time - since > StoppedAfter) break;
                looking = Mathf.SmoothDampAngle(looking, yaw, ref turning, ComingRound, FastestTurn);
                player.Rotation = new Vector2(looking, 0f);
                // The game stops the legs of anyone asking to go forward while hardly moving, taking him to be up against
                // a wall, and lets him go again only once he turns. Walking this slowly and dead straight, setting off
                // and easing to a stop both look like that to it: he'd stand at the top of the stairs for good.
                player.MovementContext.ObstacleCollisionFacade.Reset();
                // TEMP-DIAG
                if (Time.time >= diagNext)
                {
                    diagNext = Time.time + .2f;
                    var m = player.MovementContext;
                    Plugin.Log.LogInfo($"[rail] try {diagTry} at {at:F2}->{to:F2} out {Out(bot.Position):F2} applied {diagApplied} raw {diagRaw:F3} asked {diagAsked:F3} stride {diagStride:F3} flags {diagFlags} pom {(player.POM != null ? player.MovementContext.POMOffset.ToString("F3") : "none")} look {player.Rotation.x:F0} body {m.TransformRotation.eulerAngles.y:F0} speed {player.Speed:F2} clamped {m.ClampedSpeed:F2} smoothed {m.SmoothedCharacterMovementSpeed:F2} canWalk {m.CanWalk} dir {m.MovementDirection} anim {m.PlayerAnimatorDeltaPosition.magnitude / Mathf.Max(Time.deltaTime, .001f):F2} grounded {m.IsGrounded} state {m.CurrentState?.GetType().Name} vis {player.IsVisible}");
                    diagApplied = 0; diagRaw = diagAsked = diagStride = 0f; diagFlags = CollisionFlags.None;
                }
                if (Mathf.Abs(at - from) < .05f && Time.time - since > 1.2f && diagTry < 2)
                {
                    diagTry++;
                    since = Time.time;
                    Plugin.Log.LogWarning($"[rail] Not moving: try {diagTry}.");
                }
                // Easing off over the last bit, so he stops where he's meant to rather than sliding on.
                player.ChangeSpeed(Mathf.Lerp(Mathf.Min(speed, .1f), speed, Mathf.Clamp01(left / .5f)) - player.Speed);
                player.Move(Vector2.up);
                yield return null;
            }
            low = high = at;
            if (Alive) player.Move(Vector2.zero);
        }

        /// <summary>Round on the spot to face this way, all of him, gently off and gently to a stop.</summary>
        internal IEnumerator Turn(Vector3 way, float seconds)
        {
            if (!Alive) yield break;
            float from = player.Rotation.x, round = Mathf.DeltaAngle(from, Mathf.Atan2(way.x, way.z) * Mathf.Rad2Deg);
            for (float t = 0f; t < seconds && Alive; t += Time.deltaTime)
            {
                Square(player, from + round * Mathf.SmoothStep(0f, 1f, t / seconds));
                yield return null;
            }
            if (Alive) Square(player, from + round);
        }

        internal void Off()
        {
            if (!on) return;
            on = false;
            if (player != null && player.MovementContext != null) player.MovementContext.OnMotionApplied -= Applied;
            if (bot != null && !bot.IsDead && player != null) player.Move(Vector2.zero);
        }
    }
}
