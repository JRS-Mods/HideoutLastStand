using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace HideoutSiege.Timings
{
    /// <summary>
    /// A temporary measuring tool, never packaged. Writes every stalled frame and every pickup to the log.
    /// Can also time the game's code around items, hands and bodies, to say which calls a stall's time went on. That's off,
    /// because setting it up compiles all that code ahead of time, which hides the freezes that come from compiling it in play.
    /// </summary>
    [BepInPlugin("jrs.hideoutsiege.timings", "Hideout Siege timings", "0.0.1")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class TimingsPlugin : BaseUnityPlugin
    {
        private const bool TimeMethods = false;
        private const float SlowFrameMs = 120f;
        private const double SlowCallMs = 10d;
        private const int LeastCode = 24;
        private const int MostShown = 60;

        private static readonly string[] ByName =
        {
            "Fika.Core.Main.Players.FikaPlayer",
            "Fika.Core.Main.BaseClasses.BaseInventoryController",
            "Fika.Core.Main.HostClasses.HostInventoryController",
        };

        private static ManualLogSource log;
        private static int mainThread, depth;
        private static List<(int Depth, double Ms, string Name)> thisFrame = new List<(int, double, string)>();
        private static List<(int Depth, double Ms, string Name)> lastFrame = new List<(int, double, string)>();
        private int lastCollections;

        private void Awake()
        {
            log = Logger;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            var harmony = new Harmony("jrs.hideoutsiege.timings");
            var before = new HarmonyMethod(typeof(TimingsPlugin), nameof(Before));
            var after = new HarmonyMethod(typeof(TimingsPlugin), nameof(After));
            int timed = 0, refused = 0;
            var watch = Stopwatch.StartNew();
            foreach (var type in TimeMethods ? Targets() : Enumerable.Empty<Type>())
                foreach (var method in AccessTools.GetDeclaredMethods(type).Where(Worthwhile))
                {
                    try
                    {
                        harmony.Patch(method, before, after);
                        timed++;
                    }
                    catch { refused++; }
                }

            var take = AccessTools.Method(typeof(InteractionContextHelper), "smethod_10");
            if (take != null) harmony.Patch(take, new HarmonyMethod(typeof(TimingsPlugin), nameof(Taking)));
            else Logger.LogWarning("[timing] Couldn't find the Take action, so pickups won't be marked in the log.");
            Logger.LogInfo($"[timing] Timing {timed} methods ({refused} refused), set up in {watch.ElapsedMilliseconds} ms.");
        }

        private static IEnumerable<Type> Targets()
        {
            var types = new List<Type>
            {
                typeof(Player), typeof(PlayerBody), typeof(InventoryController), typeof(ObjectsFactory), typeof(AssetPoolObject),
                typeof(GameWorld), typeof(LootItem), typeof(ObservedLootItem), typeof(ItemManipulator), typeof(InteractionContextHelper),
                typeof(GamePlayerOwner), typeof(FirearmsAnimator), typeof(WeaponPrefab),
            };
            // The hands controllers and the like, but not the compiler's closures: what those call is timed anyway.
            types.AddRange(typeof(Player).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => !t.Name.StartsWith("CG_") && !t.Name.StartsWith("<")));
            types.AddRange(ByName.Select(AccessTools.TypeByName).Where(t => t != null));
            return types.Where(t => !t.ContainsGenericParameters && !t.IsInterface).Distinct();
        }

        private static bool Worthwhile(MethodInfo method)
        {
            if (method.IsAbstract || method.IsSpecialName || method.ContainsGenericParameters) return false;
            try
            {
                var body = method.GetMethodBody();
                return body != null && body.GetILAsByteArray().Length >= LeastCode;
            }
            catch { return false; }
        }

        private static void Before(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (Thread.CurrentThread.ManagedThreadId == mainThread) depth++;
        }

        private static void After(long __state, MethodBase __originalMethod)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) return;
            depth--;
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000d / Stopwatch.Frequency;
            if (ms >= SlowCallMs) thisFrame.Add((depth, ms, __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name));
        }

        private static void Taking(Item rootItem)
        {
            log.LogInfo($"[timing] TAKE {rootItem?.GetType().Name} at {Time.realtimeSinceStartup:F2}");
        }

        private void Update()
        {
            float frame = Time.unscaledDeltaTime * 1000f;
            int collections = GC.CollectionCount(0);
            if (frame >= SlowFrameMs)
            {
                var calls = lastFrame.Concat(thisFrame).ToList();
                var text = new StringBuilder($"[timing] STALL {frame:F0} ms at {Time.realtimeSinceStartup:F2}: GC ran {collections - lastCollections} times, heap {GC.GetTotalMemory(false) / 1048576} MB, slow calls {calls.Count}");
                foreach (var (level, ms, name) in calls.Take(MostShown))
                    text.Append($"\n    {new string(' ', Math.Min(level, 20) * 2)}{ms:F0} ms {name}");
                log.LogInfo(text.ToString());
            }
            lastCollections = collections;
            var spare = lastFrame;
            lastFrame = thisFrame;
            thisFrame = spare;
            thisFrame.Clear();
            // A method that threw never gets to count itself out.
            depth = 0;
        }
    }
}
