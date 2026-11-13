using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using Diz.Resources;
using EFT;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HideoutSiege
{
    /// <summary>Loads what the mounted gun is made of, while the screen's black.</summary>
    internal static class SiegeDefences
    {
        internal static bool Loaded { get; private set; }

        internal static async void Load()
        {
            Loaded = false;
            try
            {
                var factory = Singleton<ItemFactory>.Instance;
                var keys = new List<ResourceKey> { new ResourceKey { path = SiegeGun.Bundle } };
                foreach (string template in new[] { SiegeGun.BeltTemplate, SiegeGun.RoundTemplate })
                    keys.Add(factory.CreateItem(MongoID.Generate(), template, null).Prefab);
                await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local, keys.ToArray(), Diz.Jobs.JobYieldPriority.General, null, ObjectsFactory.DefaultCancellationToken);
                Loaded = true;
                Plugin.Log.LogInfo("[siege] The mounted gun is loaded.");
                SiegeGun.SpawnIfWaiting();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't load the mounted gun: " + e); }
        }

        // How the hideout's concrete takes a bullet, copied once from its walls.
        private static EFT.Ballistics.BallisticCollider bulletSample;

        /// <summary>A box that stops bullets like the hideout's walls (with its own surface's hits and dust). False if there's nothing to copy.</summary>
        internal static bool BulletBox(Transform parent, Vector3 centre, Vector3 size, EFT.Ballistics.MaterialType surface)
        {
            if (bulletSample == null)
                bulletSample = UnityEngine.Object.FindObjectsOfType<EFT.Ballistics.BallisticCollider>()
                    .FirstOrDefault(b => b.name.StartsWith("Hideout_Wall_", StringComparison.Ordinal) && b.name.IndexOf("BALLISTIC", StringComparison.Ordinal) >= 0);
            if (bulletSample == null) return false;
            var go = new GameObject("Hideout Siege bullet surface") { layer = bulletSample.gameObject.layer };
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = centre;
            box.size = size;
            var hit = go.AddComponent<EFT.Ballistics.BallisticCollider>();
            hit._presetId = bulletSample._presetId;
            hit.HitType = bulletSample.HitType;
            hit.TypeOfMaterial = surface;
            hit.PenetrationLevel = 100f;
            hit.PenetrationChance = 0f;
            hit.RicochetChance = bulletSample.RicochetChance;
            hit.FragmentationChance = bulletSample.FragmentationChance;
            hit.TrajectoryDeviationChance = bulletSample.TrajectoryDeviationChance;
            hit.TrajectoryDeviation = bulletSample.TrajectoryDeviation;
            go.SetActive(true);
            return true;
        }

        internal static void Reset()
        {
            Loaded = false;
            bulletSample = null;
            SiegeSandbags.Reset();
            SiegeWireTrap.Reset();
            SiegeDecor.Reset();
            SiegeGun.Reset();
            SiegeStations.Reset();
            SiegeMannequins.Reset();
            SiegeMoves.Reset();
            SiegeStash.Reset();
            SiegeAsk.Reset();
        }
    }

    /// <summary>The Medstation and Nutrition Unit each work once per break for each player, and better the more they're upgraded.</summary>
    internal static class SiegeStations
    {
        private static readonly float[] Healing = { 0f, .4f, .7f, 1f }, Feeding = { 0f, 30f, 60f, 100f };
        private static readonly Dictionary<EAreaType, int> lastUsed = new Dictionary<EAreaType, int>();

        internal static bool Works(EAreaType type) => type == EAreaType.MedStation || type == EAreaType.Kitchen || type == EAreaType.Stash;

        internal static AvailableInteractionState ActionsFor(Component part, GamePlayerOwner owner)
        {
            var area = part.GetComponentInParent<HideoutArea>();
            if (area == null || !Works(area.AreaTemplate.Type)) return null;
            var type = area.AreaTemplate.Type;
            if (type == EAreaType.Stash) return SiegeStash.ActionsFor(owner);
            HideoutCopy.Levels().TryGetValue(type, out int level);
            if (level < 1) return null;
            level = Mathf.Min(level, Healing.Length - 1);
            string name = type == EAreaType.MedStation ? "Use the Medstation" : "Use the Nutrition Unit";
            if (SiegeState.Phase != SiegePhase.Prep && SiegeState.Phase != SiegePhase.Intermission)
                return new AvailableInteractionState { Error = "Only between waves" };
            if (lastUsed.TryGetValue(type, out int wave) && wave == SiegeState.Wave)
                return new AvailableInteractionState { Error = "Used: it'll be ready next break" };
            return new AvailableInteractionState
            {
                Actions = new List<InteractionAction>
                {
                    new InteractionAction
                    {
                        Name = name,
                        Action = () =>
                        {
                            lastUsed[type] = SiegeState.Wave;
                            Use(type, level, owner.Player);
                            owner.Player.UpdateInteractionCast();
                        },
                    },
                },
            };
        }

        private static void Use(EAreaType type, int level, Player player)
        {
            var health = player.ActiveHealthController;
            if (health == null) return;
            if (type == EAreaType.Kitchen)
            {
                health.ChangeEnergy(Feeding[level]);
                health.ChangeHydration(Feeding[level]);
                SiegeState.Notify("Fed and watered");
                return;
            }
            if (Healing[level] >= 1f) health.RestoreFullHealth();
            else
                foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
                {
                    if (part == EBodyPart.Common) continue;
                    health.RestoreBodyPart(part, 0f);
                    health.RemoveNegativeEffects(part);
                    health.ChangeHealth(part, health.GetBodyPartHealth(part).Maximum * Healing[level], default);
                }
            SiegeState.Notify("Patched up");
        }

        internal static void Reset() => lastUsed.Clear();
    }

    /// <summary>
    /// A heavy machine gun, for a Security level of 2 or more, where you put it with Hideout Interiors (and nowhere if you haven't).
    /// It's the game's own emplacement from the gun's bundle, as in a normal raid, so Operate, the belt and the networking are all
    /// the game's. Only the host says where it goes.
    /// </summary>
    internal static class SiegeGun
    {
        internal const string Bundle = "assets/content/weapons/utes/weapon_ckib_nsv_utes_127x108_container.bundle";
        internal const string BeltTemplate = "5cffa483d7ad1a049e54ef1c", RoundTemplate = "5cde8864d7f00c0010373be1";
        private const string GunTemplate = "5cdeb229d7f00c000e7ce174", Asset = "weapon_ckib_nsv_utes_127x108_world", Id = "hideoutsiege_gun";
        // Everyone has to call it the same thing, so the ids are fixed: only the end changes.
        private const string GunItem = "6a5e6e1e00000000000000", BeltItem = "6a5e6e1e00000000000100", RoundItem = "6a5e6e1e00000000000200";
        private const int LeastSecurity = 2;
        // In the prefab its origin is this far above where the tripod's feet stand.
        private const float OriginAboveFloor = .92f;

        private static (Vector3 Where, Vector3 AimAt)? waiting;
        private static GameObject spawned;

        internal static bool Allowed => HideoutCopy.Levels().TryGetValue(EAreaType.Security, out int level) && level >= LeastSecurity;

        // ---- placed with Hideout Interiors ---------------------------------------------------------------------

        internal const string DecorKey = "hideoutsiege.turret";
        // Which way the gun points, in its model's own space (worked out when the pattern's made).
        private static Vector3 decorFacing = Vector3.forward;
        private static bool offering;

        /// <summary>
        /// In the menu, where Hideout Interiors is installed: loads the gun's model once (kept for the session) and adds it to
        /// the decor panel, so you can put your mounted gun where you want it. Called every frame until it's done.
        /// </summary>
        internal static async void OfferAsDecor()
        {
            if (offering || !Singleton<IEasyAssets>.Instantiated) return;
            offering = true;
            if (!SiegeDecor.Installed) return;
            try
            {
                var token = Singleton<IEasyAssets>.Instance.Retain(new[] { Bundle });
                await EFT.EasyAssetsExtensions.LoadBundles(token);
                var prefab = Singleton<IEasyAssets>.Instance.GetAsset<GameObject>(Bundle, Asset);
                if (prefab == null) { Plugin.Log.LogWarning("[siege] The mounted gun's model didn't load, so it's not in the decor panel."); return; }
                // Only the one: the siege has one gun, and it stands where the first is.
                SiegeDecor.Offer(DecorKey, "Mounted gun", 1, () => DecorPattern(prefab), "Tools");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't offer the mounted gun to Hideout Interiors: " + e.Message); }
        }

        // The gun as a thing to look at: its scripts and colliders off before it ever wakes up.
        private static GameObject DecorPattern(GameObject prefab)
        {
            var holder = new GameObject("Hideout Siege gun pattern holder");
            holder.SetActive(false);
            var copy = Object.Instantiate(prefab, holder.transform);
            copy.name = "Hideout Siege mounted gun (decor)";
            var gun = copy.GetComponentInChildren<StationaryWeapon>(true);
            if (gun != null && gun.TripodView != null)
            {
                Vector3 facing = copy.transform.InverseTransformDirection(gun.TripodView.position - gun.OperatorPosition);
                facing.y = 0f;
                if (facing.sqrMagnitude > .001f) decorFacing = facing.normalized;
            }
            for (int pass = 0; pass < 3; pass++)
                foreach (var c in copy.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform || c is MeshFilter || c is Renderer || c is LODGroup) continue;
                    if (pass == 0 && !(c is MonoBehaviour)) continue;
                    try { Object.DestroyImmediate(c); } catch (Exception) { }
                }
            copy.transform.SetParent(null, false);
            Object.Destroy(holder);
            return copy;
        }

        // The guns put in with Hideout Interiors, in this raid's copy of the hideout.
        private static List<Transform> Placed()
        {
            var api = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HideoutInteriors")?.GetType("HideoutInteriors.HideoutInteriorsApi", false);
            try { return (api?.GetMethod("DecorOf")?.Invoke(null, new object[] { DecorKey }) as List<Transform>)?.Where(t => t != null).ToList() ?? new List<Transform>(); }
            catch (Exception) { return new List<Transform>(); }
        }

        // The ones that are only for show: the real gun takes the first one's place.
        private static void HidePlaced()
        {
            foreach (var t in Placed()) t.gameObject.SetActive(false);
        }

        // Host only: where you put it with Hideout Interiors, and only if you did. A hideout without one placed has no gun.
        internal static void Place()
        {
            if (!Allowed) { HidePlaced(); return; }
            var mine = Placed().FirstOrDefault();
            if (mine == null)
            {
                Plugin.Log.LogInfo("[siege] No mounted gun: you haven't put one in your hideout (Hideout Interiors' Defences tab).");
                return;
            }
            int floorMask = EFTHardSettings.Instance.MOVEMENT_MASK;
            Vector3 feet = Physics.Raycast(mine.position + Vector3.up * .5f, Vector3.down, out var ground, 3f, floorMask, QueryTriggerInteraction.Ignore) ? ground.point : mine.position - Vector3.up * OriginAboveFloor;
            Vector3 facing = mine.rotation * decorFacing;
            facing.y = 0f;
            if (facing.sqrMagnitude < .001f) facing = mine.forward;
            Vector3 aim = feet + facing.normalized * 10f + Vector3.up;
            Plugin.Log.LogInfo($"[siege] The mounted gun goes where you put it: {feet:F2}.");
            Set(feet, aim);
            Net.SendGun(feet, aim);
        }

        internal static void Set(Vector3 where, Vector3 aimAt)
        {
            waiting = (where, aimAt);
            SpawnIfWaiting();
        }

        internal static void SpawnIfWaiting()
        {
            if (!waiting.HasValue || !SiegeDefences.Loaded || spawned != null) return;
            var (where, aimAt) = waiting.Value;
            waiting = null;
            try { Spawn(where, aimAt); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] Couldn't set up the mounted gun: " + e); }
        }

        private static void Spawn(Vector3 where, Vector3 aimAt)
        {
            var world = Singleton<GameWorld>.Instance;
            var prefab = Singleton<IEasyAssets>.Instance.GetAsset<GameObject>(Bundle, Asset);
            if (prefab == null) throw new Exception("the gun's bundle has no " + Asset);
            HidePlaced();
            spawned = Object.Instantiate(prefab);
            spawned.name = "Hideout Siege mounted gun";
            spawned.SetActive(true);
            var gun = spawned.GetComponentInChildren<StationaryWeapon>(true);
            if (gun == null) throw new Exception("the gun has no emplacement");
            gun.IdEditable = Id;

            // The tripod's in front and whoever's firing it is behind: turn it so that line points at the door.
            Vector3 facing = gun.TripodView.position - gun.OperatorPosition;
            facing.y = 0f;
            Vector3 wanted = aimAt - where;
            wanted.y = 0f;
            if (facing.sqrMagnitude > .001f && wanted.sqrMagnitude > .001f)
                spawned.transform.rotation = Quaternion.FromToRotation(facing.normalized, wanted.normalized) * spawned.transform.rotation;
            spawned.transform.position = where + Vector3.up * OriginAboveFloor;

            var item = MakeItem();
            LootItem.CreateStationaryWeapon(gun, item, item.ShortName, world, Id);
            var scene = LocationScene.LoadedScenes.FirstOrDefault();
            // The hideout's scene has an empty list of these already, so it's replaced rather than added to.
            if (scene != null) HarmonyLib.Traverse.Create(scene).Field<Dictionary<Type, Array>>("_typeToContainer").Value[typeof(StationaryWeapon)] = new[] { gun };
            Plugin.Log.LogInfo($"[siege] Mounted gun at {where:F2}, facing {wanted.normalized:F2}, operator at {gun.OperatorPosition:F2}, {item.GetCurrentMagazine()?.Count} rounds.");
            try { SiegeTurret.Attach(spawned, gun, aimAt); }
            catch (Exception e) { Plugin.Log.LogWarning("[siege] The mounted gun can't mind the door by itself: " + e); }
        }

        private static Weapon MakeItem()
        {
            var factory = Singleton<ItemFactory>.Instance;
            var gun = (Weapon)factory.CreateItem(GunItem + "00", GunTemplate, null);
            var belt = (Magazine)factory.CreateItem(BeltItem + "00", BeltTemplate, null);
            int left = belt.MaxCount, stack = 0;
            while (left > 0)
            {
                var rounds = factory.CreateItem(RoundItem + stack.ToString("x2"), RoundTemplate, null);
                rounds.StackObjectsCount = Mathf.Min(left, rounds.StackMaxSize);
                left -= rounds.StackObjectsCount;
                stack++;
                var added = belt.Cartridges.Add(rounds, false);
                if (added.Failed) throw new Exception("couldn't load the belt: " + added.Error);
            }
            var loaded = gun.GetMagazineSlot().AddWithoutRestrictions(belt);
            if (loaded.Failed) throw new Exception("couldn't load the gun: " + loaded.Error);
            return gun;
        }

        internal static void Reset()
        {
            waiting = null;
            spawned = null;
        }
    }
}

namespace HideoutSiege
{
    // The game ends a pickup when the item's move reports back. Fika forgets who to report to if the move doesn't finish instantly, as the
    // first gun's doesn't, so you'd stay in the pickup with no mouse look until its 5 second limit. A pickup's over soon after the item's moved.
    [HarmonyLib.HarmonyPatch(typeof(EFT.PickUpState), nameof(EFT.PickUpState.ManualAnimatorMoveUpdate))]
    internal static class PickupEndsPatch
    {
        private const float Overrun = .3f;

        [HarmonyLib.HarmonyPostfix]
        private static void Postfix(EFT.PickUpState __instance)
        {
            if (!SiegeLauncher.InSiege || __instance.MovementContext.PickupAction != null) return;
            if (Time.realtimeSinceStartup - __instance._enterTime < __instance._timeForPikupAnimation + Overrun) return;
            if (__instance.MovementContext.CurrentState == __instance) __instance.MovementContext.ExitOverridenState();
        }
    }

    // One press takes everything a mannequin has, instead of one thing at a time.
    [HarmonyLib.HarmonyPatch(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions), new[] { typeof(GamePlayerOwner), typeof(LootItem) })]
    internal static class TakeLoadoutPatch
    {
        [HarmonyLib.HarmonyPostfix]
        private static void Postfix(GamePlayerOwner owner, LootItem lootItem, ref AvailableInteractionState __result)
        {
            if (!SiegeLauncher.InSiege || __result?.Actions == null) return;
            int mannequin = SiegeGear.MannequinOf(lootItem);
            if (mannequin == 0) return;
            __result.Actions.Add(new InteractionAction
            {
                Name = $"Take Mannequin {mannequin}'s loadout",
                Action = () => Plugin.Instance.StartCoroutine(SiegeGear.TakeLoadout(owner.Player, mannequin)),
            });
        }
    }
}
