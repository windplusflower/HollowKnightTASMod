using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HollowKnightTAS.Core.SceneCheckpoints;
using Modding;
using MonoMod.RuntimeDetour;

namespace HollowKnightTAS.ClockPayload
{
    // Deterministic System.Random() call-site seeding and instance state tracking for mod assemblies.
    // Installed before ModLoader scans and constructs A*-G* mods so all mod
    // constructors share the same protection.
    public static class EarlyModRandomPolicy
    {
        private static readonly object Sync = new object();
        private static ModRandomTracker? tracker;
        private static ILHook? randomConstructor;
        private static int installed;

        public static bool IsInstalled => Volatile.Read(ref installed) != 0;

        public static void Install()
        {
            lock (Sync)
            {
                if (Volatile.Read(ref installed) != 0)
                {
                    return;
                }

                tracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
                randomConstructor = ModRandomConstructorHook.Install(NextSeed, RegisterConstructedInstance);
                Volatile.Write(ref installed, 1);
                UnityEngine.Debug.Log("[HKTAS] EarlyModRandomPolicy installed for System.Random().");
            }
        }

        private static readonly FieldInfo? ModLoaderLoadStateField =
            typeof(ModHooks).Assembly.GetType("Modding.ModLoader", false)
                ?.GetField("LoadState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static bool IsModLoaderLoaded()
        {
            try
            {
                if (ModLoaderLoadStateField == null)
                {
                    return false;
                }
                var state = ModLoaderLoadStateField.GetValue(null);
                return state != null && (Convert.ToInt32(state) & 4) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static int NextSeed()
        {
            var active = tracker;
            if (active == null)
            {
                return Environment.TickCount;
            }

            return active.NextSeed(
                () => EarlyModPreloadBridge.IsPreloadActive || !IsModLoaderLoaded(),
                UnityEngine.Debug.LogWarning,
                typeof(EarlyModRandomPolicy).Assembly);
        }

        private static void RegisterConstructedInstance(Random instance)
        {
            tracker?.RegisterConstructedInstance(instance);
        }

        public static ModRandomStateSnapshot? CaptureState()
        {
            lock (Sync)
            {
                return tracker?.CaptureState(UnityEngine.Debug.LogWarning);
            }
        }

        public static byte[]? CaptureStateBytes()
        {
            return CaptureState()?.Encode();
        }

        public static ModRandomRestoreResult RestoreState(
            IDictionary<string, uint> ordinals,
            ModRandomInstanceSnapshot[] instances)
        {
            if (ordinals == null)
            {
                throw new ArgumentNullException(nameof(ordinals));
            }
            if (instances == null)
            {
                throw new ArgumentNullException(nameof(instances));
            }

            var copy = new Dictionary<string, uint>(ordinals, StringComparer.Ordinal);
            return RestoreState(new ModRandomStateSnapshot(copy, instances));
        }

        public static ModRandomRestoreResult RestoreState(ModRandomStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            lock (Sync)
            {
                if (tracker == null)
                {
                    UnityEngine.Debug.LogWarning("[HKTAS] EarlyModRandomPolicy.RestoreState skipped because tracker is not installed.");
                    return default;
                }

                ModRandomRestoreResult result = tracker.RestoreState(snapshot, UnityEngine.Debug.LogWarning);
                UnityEngine.Debug.Log(
                    $"[HKTAS] EarlyModRandomPolicy restored S0 state: ordinals={result.OrdinalCount};restored={result.RestoredInstanceCount}/{result.SnapshotInstanceCount};missing={result.MissingInstanceCount}");
                return result;
            }
        }

        public static void RestoreStateBytes(byte[]? encodedState)
        {
            if (encodedState == null || encodedState.Length == 0)
            {
                return;
            }

            RestoreState(ModRandomStateSnapshot.Decode(encodedState));
        }

        internal static Dictionary<string, uint> SnapshotOrdinals()
        {
            lock (Sync)
            {
                return tracker?.SnapshotOrdinals() ?? new Dictionary<string, uint>(StringComparer.Ordinal);
            }
        }

        internal static void RestoreOrdinals(IDictionary<string, uint> source)
        {
            lock (Sync)
            {
                tracker?.RestoreOrdinals(source);
            }
        }

        internal static void Dispose()
        {
            lock (Sync)
            {
                randomConstructor?.Dispose();
                randomConstructor = null;
                tracker?.Dispose();
                tracker = null;
                Volatile.Write(ref installed, 0);
            }
        }
    }
}
