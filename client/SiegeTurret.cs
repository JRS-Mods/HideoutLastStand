using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace HideoutSiege
{
    /// <summary>
    /// The mounted gun minds the door by itself while nobody's on it. It swings to the nearest attacker it can see in front of
    /// it, takes a moment to settle, and fires short bursts of real 12.7 mm from a box of its own, topped up every break; it
    /// won't fire with a defender in the way. Anyone can still take it over with Operate. The host's gun does the shooting;
    /// everyone else hears the bursts and sees the flashes and tracers.
    /// </summary>
    internal sealed class SiegeTurret : MonoBehaviour
    {
        // Left to itself it's a deterrent, not a wave-clearer: slow to swing and settle, short bursts that scatter, and a box
        // that runs dry partway through a wave. Someone on it does far better.
        private const float Range = 24f, TurnSpeed = 55f, AimedWithin = 2.5f, Spread = 1.8f, ShotGap = .1f, BurstPause = 1.3f, Settle = 1f;
        // It keeps on one attacker while it can see any of them, or saw them less than Memory ago (someone ducking behind cover
        // for a moment), so the nearest changing hands or a peek doesn't start the settling over.
        private const float LookEvery = .2f, ReturnAfter = 2.5f, MostPitch = 25f, Memory = 1.2f;
        private const int Burst = 4, PerWave = 30;

        private static SiegeTurret current;
        private StationaryWeapon gun;
        private Transform muzzle, weapon;
        private Quaternion restRotation;
        private Vector3 restPosition, pivot, restAim, aim;
        private float yawLimit;
        private Ammo round;
        private BotOwner target;
        private Transform aimBone;
        private Vector3 lastPoint;
        private bool inSight;
        private float seenSince, nextShot, nextLook, lastSeen;
        private readonly RaycastHit[] hits = new RaycastHit[16];
        private int burstLeft = Burst, rounds = PerWave;
        private AudioSource sound;
        private Light flash;
        private float flashUntil;

        internal static void Attach(GameObject spawned, StationaryWeapon gun, Vector3 aimAt)
        {
            var turret = spawned.AddComponent<SiegeTurret>();
            turret.gun = gun;
            turret.weapon = gun.WeaponTransform;
            turret.muzzle = Muzzle(spawned.transform, turret.weapon, aimAt);
            turret.restRotation = turret.weapon.rotation;
            turret.restPosition = turret.weapon.position;
            turret.pivot = gun.Hinge != null ? gun.Hinge.position : turret.weapon.position;
            turret.restAim = turret.aim = (aimAt - turret.muzzle.position).normalized;
            turret.yawLimit = gun.YawTolerance > 1f ? gun.YawTolerance - 2f : 60f;
            current = turret;
            Plugin.Log.LogInfo($"[siege] The mounted gun minds the door by itself: muzzle {turret.muzzle.name}, turns {turret.yawLimit:F0} degrees each way.");
        }

        // The barrel's end: the model's fireport if it has one, otherwise the front of the gun's own meshes along where it points.
        private static Transform Muzzle(Transform root, Transform weapon, Vector3 aimAt)
        {
            var named = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.ToLowerInvariant().Contains("fireport"));
            if (named != null) return named;
            Vector3 way = aimAt - weapon.position;
            way.y = 0f;
            way = way.sqrMagnitude > .001f ? way.normalized : weapon.forward;
            float reach = .6f, height = 0f;
            foreach (var r in weapon.GetComponentsInChildren<Renderer>(true))
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3(i % 2 * 2 - 1, i / 2 % 2 * 2 - 1, i / 4 * 2 - 1));
                    float along = Vector3.Dot(corner - weapon.position, way);
                    if (along > reach) { reach = along; height = b.center.y - weapon.position.y; }
                }
            }
            var end = new GameObject("siege muzzle").transform;
            end.SetParent(weapon, false);
            end.position = weapon.position + way * Mathf.Min(reach, 2.2f) + Vector3.up * Mathf.Clamp(height, -.2f, .3f);
            return end;
        }

        private void Awake()
        {
            sound = gameObject.AddComponent<AudioSource>();
            sound.spatialBlend = 1f;
            sound.rolloffMode = AudioRolloffMode.Logarithmic;
            sound.minDistance = 4f;
            sound.maxDistance = 90f;
            sound.dopplerLevel = 0f;
            if (Singleton<BetterAudio>.Instantiated) sound.outputAudioMixerGroup = Singleton<BetterAudio>.Instance.VeryStandartMixerGroup;
            flash = new GameObject(SiegeLights.OwnLight + " muzzle flash").AddComponent<Light>();
            flash.transform.SetParent(transform, false);
            flash.type = LightType.Point;
            flash.range = 9f;
            flash.intensity = 5f;
            flash.color = new Color(1f, .78f, .45f);
            flash.shadows = LightShadows.None;
            flash.enabled = false;
        }

        private void OnDestroy()
        {
            if (current == this) current = null;
        }

        private void Update()
        {
            if (flash.enabled && Time.time >= flashUntil) flash.enabled = false;
            if (Net.IsClient || gun == null) return;
            // Topped up while they're not coming in.
            if (SiegeState.Phase == SiegePhase.Prep || SiegeState.Phase == SiegePhase.Intermission) rounds = PerWave;
            // It runs on the hideout's power: with that cut, it's only a gun someone has to work.
            if (gun.Locked || SiegeState.Phase != SiegePhase.Fighting || SiegeCinematic.Active || SiegeLights.PowerOut)
            {
                target = null;
                inSight = false;
                return;
            }
            if (Time.time >= nextLook)
            {
                nextLook = Time.time + LookEvery;
                Look();
            }
            Vector3 point = target != null && aimBone != null && inSight ? aimBone.position : lastPoint;
            Vector3 wanted = target != null ? (point - muzzle.position).normalized : Time.time - lastSeen > ReturnAfter ? restAim : aim;
            aim = Vector3.RotateTowards(aim, wanted, TurnSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f);
            Pose();
            if (target == null || !inSight || rounds <= 0 || Time.time < nextShot) return;
            if (Vector3.Angle(aim, wanted) > AimedWithin || Time.time - seenSince < Settle) return;
            if (burstLeft == Burst) Net.SendTurret(muzzle.position, aim, Burst);
            Fire(aim);
            if (--rounds == 0) Plugin.Log.LogInfo("[siege] The mounted gun is out for this wave.");
            if (--burstLeft > 0) nextShot = Time.time + ShotGap;
            else { burstLeft = Burst; nextShot = Time.time + BurstPause; }
        }

        // Keeps on who it's got while it can, otherwise takes the nearest it can see.
        private void Look()
        {
            if (target != null && Usable(target))
            {
                if (Sight(target, out var bone))
                {
                    aimBone = bone;
                    lastPoint = bone.position;
                    lastSeen = Time.time;
                    inSight = true;
                    return;
                }
                inSight = false;
                if (Time.time - lastSeen < Memory) return;
            }
            var found = Choose(out var foundBone);
            if (found == null)
            {
                target = null;
                inSight = false;
                return;
            }
            if (found != target)
            {
                seenSince = Time.time;
                burstLeft = Burst;
                Plugin.Log.LogInfo($"[siege] The mounted gun has a {found.Profile.Info.Settings.Role} {Vector3.Distance(found.Position, muzzle.position):F0} m away ({rounds} rounds).");
            }
            target = found;
            aimBone = foundBone;
            lastPoint = foundBone.position;
            lastSeen = Time.time;
            inSight = true;
        }

        private static bool Usable(BotOwner bot) => bot != null && !bot.IsDead && bot.GetPlayer != null && !SiegeDirector.Waiting.Contains(bot);

        // The nearest attacker in front of the gun that it can see some of, with no defender in the line of fire.
        private BotOwner Choose(out Transform bone)
        {
            bone = null;
            if (!Singleton<IBotGame>.Instantiated) return null;
            var bots = Singleton<IBotGame>.Instance.BotsController?.Bots?.BotOwners;
            if (bots == null) return null;
            BotOwner best = null;
            float bestDistance = float.MaxValue;
            foreach (var bot in bots)
            {
                if (!Usable(bot)) continue;
                float distance = Vector3.Distance(bot.Position, muzzle.position);
                if (distance > Range || distance > bestDistance || distance < 1f) continue;
                if (!Sight(bot, out var seen)) continue;
                best = bot;
                bone = seen;
                bestDistance = distance;
            }
            return best;
        }

        // Which of their chest, head or hips it has a clear shot at, if any.
        private bool Sight(BotOwner bot, out Transform bone)
        {
            bone = null;
            var bones = bot.GetPlayer.PlayerBones;
            if (bones == null) return false;
            Vector3 from = muzzle.position;
            var defenders = SiegeDirector.Defenders().ToList();
            foreach (var part in new[] { bones.Spine3, bones.Head, bones.Pelvis })
            {
                if (part == null) continue;
                Vector3 to = part.position, way = to - from;
                float distance = way.magnitude;
                if (distance > Range || distance < .5f) continue;
                Vector3 flat = new Vector3(way.x, 0f, way.z), rest = new Vector3(restAim.x, 0f, restAim.z);
                if (Vector3.Angle(flat, rest) > yawLimit) continue;
                if (Mathf.Abs(Mathf.Asin(Mathf.Clamp(way.y / distance, -1f, 1f)) * Mathf.Rad2Deg) > MostPitch) continue;
                if (Blocked(from, way / distance, distance)) continue;
                if (defenders.Any(d => InTheWay(d, from, to, distance))) continue;
                bone = part.Original;
                return true;
            }
            return false;
        }

        // Anything solid between, apart from the gun itself.
        private bool Blocked(Vector3 from, Vector3 way, float distance)
        {
            int count = Physics.RaycastNonAlloc(from, way, hits, distance - .3f, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (!hits[i].collider.transform.IsChildOf(transform)) return true;
            return false;
        }

        private static bool InTheWay(Player defender, Vector3 from, Vector3 to, float distance)
        {
            Vector3 at = defender.Position + Vector3.up * 1.2f;
            float along = Vector3.Dot(at - from, (to - from) / distance);
            if (along < 0f || along > distance) return false;
            return (from + (to - from) / distance * along - at).sqrMagnitude < 1.2f * 1.2f;
        }

        // The gun swung about its hinge: left and right as far as the tripod lets it, and up and down from level.
        private void Pose()
        {
            Vector3 flatRest = new Vector3(restAim.x, 0f, restAim.z).normalized, flatAim = new Vector3(aim.x, 0f, aim.z).normalized;
            if (flatAim.sqrMagnitude < .001f) return;
            float yaw = Mathf.Clamp(Vector3.SignedAngle(flatRest, flatAim, Vector3.up), -yawLimit, yawLimit);
            float pitch = Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg - Mathf.Asin(Mathf.Clamp(restAim.y, -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 right = Vector3.Cross(Vector3.up, flatRest);
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(-pitch, right);
            try { gun.ManualUpdate(pivot + turn * (restPosition - pivot), turn * restRotation); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[siege] The mounted gun couldn't turn: " + e.Message); enabled = false; }
        }

        // One round, through the game's own ballistics: it hits, wounds and kills as the gun would in anyone's hands.
        private void Fire(Vector3 way)
        {
            var world = Singleton<GameWorld>.Instance;
            if (world == null) return;
            if (round == null) round = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), SiegeGun.RoundTemplate, null) as Ammo;
            Vector3 shot = Quaternion.Euler(Random.Range(-Spread, Spread), Random.Range(-Spread, Spread), 0f) * way;
            try
            {
                if (round != null) world.SharedBallisticsCalculator.Shoot(round, muzzle.position, shot, world.MainPlayer?.ProfileId, gun.Item, 1f, 0);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[siege] The mounted gun misfired: " + e.Message); }
            Show(muzzle.position, shot);
        }

        // What everyone sees and hears of a round: the bang, the flash, and a tracer to where it hit.
        private void Show(Vector3 from, Vector3 way)
        {
            var clip = SiegeSounds.Clip("turret");
            if (clip != null) sound.PlayOneShot(clip, Plugin.Volume.Value);
            flash.transform.position = from + way * .2f;
            flash.enabled = true;
            flashUntil = Time.time + .045f;
            int mask = LayersMaskController.HighPolyWithTerrainMask;
            Vector3 end = Physics.Raycast(from, way, out var hit, Range * 1.5f, mask, QueryTriggerInteraction.Ignore) ? hit.point : from + way * Range;
            StartCoroutine(Tracer(from + way * .3f, end));
        }

        private static Material tracerLook;

        private static IEnumerator Tracer(Vector3 from, Vector3 to)
        {
            var go = new GameObject(SiegeLights.OwnLight + " tracer");
            var line = go.AddComponent<LineRenderer>();
            if (tracerLook == null) tracerLook = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
            line.sharedMaterial = tracerLook;
            line.positionCount = 2;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.widthMultiplier = .025f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            for (float t = 0f; t < .07f; t += Time.deltaTime)
            {
                var c = new Color(1f, .85f, .5f, 1f - t / .07f);
                line.startColor = line.endColor = c;
                yield return null;
            }
            Destroy(go);
        }

        /// <summary>On another player's game: the host's gun fired a burst along this line.</summary>
        internal static void Heard(Vector3 from, Vector3 way, int shots)
        {
            if (current != null) current.StartCoroutine(current.Echo(from, way, shots));
        }

        private IEnumerator Echo(Vector3 from, Vector3 way, int shots)
        {
            for (int i = 0; i < shots; i++)
            {
                Show(from, Quaternion.Euler(Random.Range(-Spread, Spread), Random.Range(-Spread, Spread), 0f) * way);
                yield return new WaitForSeconds(ShotGap);
            }
        }
    }
}
