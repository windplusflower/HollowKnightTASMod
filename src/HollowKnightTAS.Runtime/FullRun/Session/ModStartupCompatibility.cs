using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HollowKnightTAS.Core.SceneCheckpoints;
using InControl;
using MonoMod.RuntimeDetour;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Installed during Mod construction, before ModLoader enters its preload coroutine.
    // Ordinary launches never construct this guard.
    internal sealed class ModStartupCompatibility : IDisposable
    {
        private const string ClockPayloadAssemblyName = "HollowKnightTAS.ClockPayload";
        private const string PreloadBridgeTypeName = "HollowKnightTAS.ClockPayload.EarlyModPreloadBridge";
        private const string RandomPolicyTypeName = "HollowKnightTAS.ClockPayload.EarlyModRandomPolicy";
        private const string BootstrapTypeName = "HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap";

        private static readonly object FallbackSync = new object();
        private static ModRandomTracker? activeFallbackTracker;

        private static Func<bool>? cachedIsPreloadActive;
        private static Func<bool>? cachedPreloadOccurred;
        private static Func<bool>? cachedHasEarlyInputGuard;
        private static Func<bool>? cachedIsModRandomPolicyInstalled;
        private static Action? cachedReleaseEarlyInputGuard;
        private static Action? cachedRebaselineParticles;
        private static Func<byte[]?>? cachedCaptureModRandomState;
        private static Action<byte[]?>? cachedRestoreModRandomState;

        private readonly ModRandomTracker? fallbackTracker;
        private readonly ILHook? randomConstructor;
        private readonly DeterministicSceneFetch? synchronousFetch;
        private readonly DeterministicCinematics cinematics;
        private readonly LoadingSynchronousFetchProbe? experimentalFetch;
        private readonly LoadingSerialSceneProbe? serialScenes;
        private readonly LoadingAudioLifetimePolicy? audioLifetimes;
        private readonly bool usingEarlyInputGuard;
        private bool ownInputGuardInstalled;
        private bool suppressInput = true;
        private long suppressedUpdates;

        public ModStartupCompatibility()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("HKTAS_LOADING_SYNCHRONOUS_FETCH") == "1")
                    experimentalFetch = new LoadingSynchronousFetchProbe();
                else synchronousFetch = new DeterministicSceneFetch();
                cinematics = new DeterministicCinematics();
                if (Environment.GetEnvironmentVariable("HKTAS_LOADING_SERIAL_SCENES") == "1")
                    serialScenes = new LoadingSerialSceneProbe();
                if (Environment.GetEnvironmentVariable("HKTAS_LOADING_VIRTUAL_AUDIO") == "1")
                    audioLifetimes = new LoadingAudioLifetimePolicy();
                StartupObjectPoolRegistry.InstallHooks();
                if (InputManager.CurrentTick != 0)
                    throw new InvalidOperationException("Mod startup input guard was installed too late.");
                LoadingInputCounterObservation.Log("mod-constructor");

                if (TryQueryIsModRandomPolicyInstalled())
                {
                    Modding.Logger.Log("[HKTAS] Reusing early System.Random() policy from ClockPayload.");
                }
                else
                {
                    fallbackTracker = new ModRandomTracker(Thread.CurrentThread.ManagedThreadId);
                    lock (FallbackSync)
                    {
                        activeFallbackTracker = fallbackTracker;
                    }
                    randomConstructor = ModRandomConstructorHook.Install(
                        NextSeed,
                        instance => fallbackTracker.RegisterConstructedInstance(instance));
                }

                if (TryQueryHasEarlyInputGuard())
                {
                    usingEarlyInputGuard = true;
                    Modding.Logger.Log("[HKTAS] Reusing early startup input guard from ClockPayload.");
                }
                else
                {
                    On.InControl.InputManager.UpdateInternal += OnInputUpdate;
                    ownInputGuardInstalled = true;
                    Modding.Logger.Log("[HKTAS] Early mod input and random guards installed (Runtime fallback).");
                }
            }
            catch
            {
                StartupObjectPoolRegistry.UninstallHooks();
                LoadingSerialSceneProbe.EndCanonicalUiCallbacks();
                throw;
            }
        }

        private static Assembly? FindClockPayloadAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SingleOrDefault(assembly => assembly.GetName().Name == ClockPayloadAssemblyName);
        }

        private static Func<bool>? ResolveBoolGetter(ref Func<bool>? cache, string typeName, string propertyName)
        {
            if (cache != null) return cache;
            var payload = FindClockPayloadAssembly();
            var getter = payload?.GetType(typeName, false)
                ?.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public)
                ?.GetGetMethod(false);
            if (getter == null) return null;
            cache = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
            return cache;
        }

        private static Action? ResolveAction(ref Action? cache, string typeName, string methodName)
        {
            if (cache != null) return cache;
            var payload = FindClockPayloadAssembly();
            var method = payload?.GetType(typeName, false)
                ?.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null) return null;
            cache = (Action)Delegate.CreateDelegate(typeof(Action), method);
            return cache;
        }

        private static Func<byte[]?>? ResolveBytesGetter(ref Func<byte[]?>? cache, string typeName, string methodName)
        {
            if (cache != null) return cache;
            var payload = FindClockPayloadAssembly();
            var method = payload?.GetType(typeName, false)
                ?.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null) return null;
            cache = (Func<byte[]?>)Delegate.CreateDelegate(typeof(Func<byte[]?>), method);
            return cache;
        }

        private static Action<byte[]?>? ResolveBytesSetter(ref Action<byte[]?>? cache, string typeName, string methodName)
        {
            if (cache != null) return cache;
            var payload = FindClockPayloadAssembly();
            var method = payload?.GetType(typeName, false)
                ?.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(byte[]) }, null);
            if (method == null) return null;
            cache = (Action<byte[]?>)Delegate.CreateDelegate(typeof(Action<byte[]?>), method);
            return cache;
        }

        internal static bool TryQueryIsPreloadActive()
            => ResolveBoolGetter(ref cachedIsPreloadActive, PreloadBridgeTypeName, "IsPreloadActive")?.Invoke() ?? false;

        internal static bool TryQueryPreloadOccurred()
            => ResolveBoolGetter(ref cachedPreloadOccurred, PreloadBridgeTypeName, "PreloadOccurred")?.Invoke() ?? false;

        internal static bool TryQueryHasEarlyInputGuard()
            => ResolveBoolGetter(ref cachedHasEarlyInputGuard, PreloadBridgeTypeName, "HasEarlyInputGuard")?.Invoke() ?? false;

        internal static bool TryQueryIsModRandomPolicyInstalled()
            => ResolveBoolGetter(ref cachedIsModRandomPolicyInstalled, RandomPolicyTypeName, "IsInstalled")?.Invoke() ?? false;

        internal static void TryReleaseEarlyInputGuard()
            => ResolveAction(ref cachedReleaseEarlyInputGuard, PreloadBridgeTypeName, "ReleaseStartupInputGuard")?.Invoke();

        internal static void TryRebaselineParticlesAfterPreload()
            => ResolveAction(ref cachedRebaselineParticles, BootstrapTypeName, "RebaselineParticlesAfterPreload")?.Invoke();

        internal static LeanS0ModRandomState? CaptureModRandomState()
        {
            ModRandomStateSnapshot? snapshot = null;
            if (TryQueryIsModRandomPolicyInstalled())
            {
                var getter = ResolveBytesGetter(ref cachedCaptureModRandomState, BootstrapTypeName, "CaptureModRandomState");
                byte[]? bytes = getter?.Invoke();
                if (bytes != null && bytes.Length > 0)
                {
                    snapshot = ModRandomStateSnapshot.Decode(bytes);
                }
            }
            else
            {
                ModRandomTracker? localTracker;
                lock (FallbackSync)
                {
                    localTracker = activeFallbackTracker;
                }
                snapshot = localTracker?.CaptureState(Modding.Logger.LogWarn);
            }

            if (snapshot == null)
            {
                return null;
            }

            return ToDto(snapshot);
        }

        internal static void RestoreModRandomState(LeanS0ModRandomState? state)
        {
            state ??= new LeanS0ModRandomState();

            ModRandomStateSnapshot snapshot = FromDto(state);
            if (TryQueryIsModRandomPolicyInstalled())
            {
                var setter = ResolveBytesSetter(ref cachedRestoreModRandomState, BootstrapTypeName, "RestoreModRandomState");
                if (setter == null)
                {
                    Modding.Logger.LogWarn("[HKTAS] ClockPayload RestoreModRandomState bridge is unavailable.");
                    return;
                }

                setter(snapshot.Encode());
                return;
            }

            ModRandomTracker? localTracker;
            lock (FallbackSync)
            {
                localTracker = activeFallbackTracker;
            }

            if (localTracker == null)
            {
                if (state.Ordinals.Count > 0 || state.Instances.Length > 0)
                {
                    Modding.Logger.LogWarn("[HKTAS] No active ModRandomTracker available to restore C14_ModRandom.");
                }
                return;
            }

            ModRandomRestoreResult result = localTracker.RestoreState(snapshot, Modding.Logger.LogWarn);
            Modding.Logger.Log(
                $"[HKTAS] Runtime fallback restored S0 mod random state: ordinals={result.OrdinalCount};restored={result.RestoredInstanceCount}/{result.SnapshotInstanceCount};missing={result.MissingInstanceCount}");
        }

        private static LeanS0ModRandomState ToDto(ModRandomStateSnapshot snapshot)
        {
            return new LeanS0ModRandomState
            {
                Ordinals = new Dictionary<string, uint>(snapshot.Ordinals, StringComparer.Ordinal),
                Instances = snapshot.Instances.Select(inst => new LeanS0ModRandomInstanceState
                {
                    CallsiteKey = inst.CallsiteKey,
                    Ordinal = inst.Ordinal,
                    Inext = inst.Inext,
                    Inextp = inst.Inextp,
                    SeedArray = (int[])inst.SeedArray.Clone()
                }).ToArray()
            };
        }

        private static ModRandomStateSnapshot FromDto(LeanS0ModRandomState dto)
        {
            var ordinals = dto.Ordinals != null
                ? new Dictionary<string, uint>(dto.Ordinals, StringComparer.Ordinal)
                : new Dictionary<string, uint>(StringComparer.Ordinal);
            var instances = (dto.Instances ?? Array.Empty<LeanS0ModRandomInstanceState>())
                .Select(inst => new ModRandomInstanceSnapshot(
                    inst.CallsiteKey,
                    inst.Ordinal,
                    inst.Inext,
                    inst.Inextp,
                    inst.SeedArray))
                .ToArray();
            return new ModRandomStateSnapshot(ordinals, instances);
        }

        private void OnInputUpdate(On.InControl.InputManager.orig_UpdateInternal original)
        {
            if (suppressInput) { suppressedUpdates++; return; }
            original();
        }

        public void FinishPreloading()
        {
            if (HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesModWallClock(
                HollowKnightTAS.Core.Movie.MovieProtocolV2.SelectedNativeProfileId))
            {
                var payload = FindClockPayloadAssembly()
                    ?? throw new InvalidOperationException("V5 clock payload is unavailable.");
                var verify = payload.GetType(BootstrapTypeName, true)!
                    .GetMethod("Verify", BindingFlags.Static | BindingFlags.Public)
                    ?? throw new MissingMethodException("V5 Mod wall-clock verifier is unavailable.");
                verify.Invoke(null, null);
                if (Environment.GetEnvironmentVariable("HKTAS_LOADING_VERIFY_ONLINE") == "1")
                    payload.GetType(BootstrapTypeName, true)!
                        .GetMethod("TestSessionOnline", BindingFlags.Static | BindingFlags.Public)!
                        .Invoke(null, null);
                Modding.Logger.Log("[HKTAS] V5 registered Mod wall-clock policy verified before input opens.");
            }
            if (InputManager.CurrentTick != 0)
                throw new InvalidOperationException("Input advanced outside the preload guard.");
            LoadingInputCounterObservation.Log("preloads-finished");
            synchronousFetch?.Milestone("runtime-preloading-ready");
            if (usingEarlyInputGuard)
            {
                TryReleaseEarlyInputGuard();
            }
            if (ownInputGuardInstalled)
            {
                suppressInput = false;
                ownInputGuardInstalled = false;
                On.InControl.InputManager.UpdateInternal -= OnInputUpdate;
                Modding.Logger.Log("[HKTAS] Preload input guard released; suppressed updates=" + suppressedUpdates);
            }
        }

        private static readonly FieldInfo? ModLoaderLoadStateField =
            typeof(Modding.ModHooks).Assembly.GetType("Modding.ModLoader", false)
                ?.GetField("LoadState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static bool IsModLoaderLoaded()
        {
            try
            {
                if (ModLoaderLoadStateField == null) return false;
                var state = ModLoaderLoadStateField.GetValue(null);
                return state != null && (Convert.ToInt32(state) & 4) != 0;
            }
            catch
            {
                return false;
            }
        }

        private int NextSeed()
        {
            if (fallbackTracker == null)
            {
                return Environment.TickCount;
            }

            return fallbackTracker.NextSeed(
                () => TryQueryIsPreloadActive() || !IsModLoaderLoaded(),
                Modding.Logger.LogWarn,
                typeof(ModStartupCompatibility).Assembly);
        }

        public void Dispose()
        {
            if (ownInputGuardInstalled)
            {
                On.InControl.InputManager.UpdateInternal -= OnInputUpdate;
                ownInputGuardInstalled = false;
            }
            if (usingEarlyInputGuard)
            {
                TryReleaseEarlyInputGuard();
            }
            randomConstructor?.Dispose();
            if (fallbackTracker != null)
            {
                lock (FallbackSync)
                {
                    if (ReferenceEquals(activeFallbackTracker, fallbackTracker))
                    {
                        activeFallbackTracker = null;
                    }
                }
                fallbackTracker.Dispose();
            }
            synchronousFetch?.Dispose();
            experimentalFetch?.Dispose();
            cinematics.Dispose();
            serialScenes?.Dispose();
            audioLifetimes?.Dispose();
            StartupObjectPoolRegistry.UninstallHooks();
        }
    }
}
