using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using InControl;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Installed during Mod construction, before ModLoader enters its preload coroutine.
    // Ordinary launches never construct this guard.
    internal sealed class ModStartupCompatibility : IDisposable
    {
        private readonly Dictionary<string, uint> ordinals = new Dictionary<string, uint>();
        private readonly int gameThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private readonly ILHook randomConstructor;
        private readonly DeterministicSceneFetch? synchronousFetch;
        private readonly DeterministicCinematics cinematics;
        private readonly LoadingSynchronousFetchProbe? experimentalFetch;
        private readonly LoadingSerialSceneProbe? serialScenes;
        private readonly LoadingAudioLifetimePolicy? audioLifetimes;
        private bool suppressInput = true;
        private long suppressedUpdates;

        public ModStartupCompatibility()
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_SYNCHRONOUS_FETCH") == "1")
                experimentalFetch = new LoadingSynchronousFetchProbe();
            else synchronousFetch = new DeterministicSceneFetch();
            cinematics = new DeterministicCinematics();
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_SERIAL_SCENES") == "1")
                serialScenes = new LoadingSerialSceneProbe();
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_VIRTUAL_AUDIO") == "1")
                audioLifetimes = new LoadingAudioLifetimePolicy();
            if (InputManager.CurrentTick != 0)
                throw new InvalidOperationException("Mod startup input guard was installed too late.");
            randomConstructor = new ILHook(typeof(Random).GetConstructor(Type.EmptyTypes), il =>
            {
                var cursor = new ILCursor(il);
                if (!cursor.TryGotoNext(MoveType.Before, instruction =>
                    instruction.MatchCall(typeof(Environment), "get_TickCount")))
                    throw new InvalidOperationException("Unsupported System.Random constructor.");
                cursor.Remove();
                cursor.EmitDelegate<Func<int>>(NextSeed);
            });
            On.InControl.InputManager.UpdateInternal += OnInputUpdate;
            Modding.Logger.Log("[HKTAS] Early mod input and random guards installed.");
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
                var payload = AppDomain.CurrentDomain.GetAssemblies()
                    .SingleOrDefault(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload")
                    ?? throw new InvalidOperationException("V5 clock payload is unavailable.");
                var verify = payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!
                    .GetMethod("Verify", BindingFlags.Static | BindingFlags.Public)
                    ?? throw new MissingMethodException("V5 Mod wall-clock verifier is unavailable.");
                verify.Invoke(null, null);
                if (Environment.GetEnvironmentVariable("HKTAS_LOADING_VERIFY_ONLINE") == "1")
                    payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!
                        .GetMethod("TestSessionOnline", BindingFlags.Static | BindingFlags.Public)!
                        .Invoke(null, null);
                Modding.Logger.Log("[HKTAS] V5 registered Mod wall-clock policy verified before input opens.");
            }
            if (InputManager.CurrentTick != 0)
                throw new InvalidOperationException("Input advanced outside the preload guard.");
            suppressInput = false;
            synchronousFetch?.Milestone("runtime-preloading-ready");
            On.InControl.InputManager.UpdateInternal -= OnInputUpdate;
            Modding.Logger.Log("[HKTAS] Preload input guard released; suppressed updates=" + suppressedUpdates);
        }

        private int NextSeed()
        {
            // Separate streams by the actual mod call site: unrelated framework RNGs and
            // constructors in another mod must not shift a boss's stream.
            foreach (var frame in new StackTrace(false).GetFrames() ?? Array.Empty<StackFrame>())
            {
                var method = frame.GetMethod();
                var assembly = method?.DeclaringType?.Assembly;
                if (assembly == null || assembly == typeof(ModStartupCompatibility).Assembly
                    || assembly.IsDynamic) continue;
                string location;
                try { location = assembly.Location; } catch { continue; }
                if (location.IndexOf(Path.DirectorySeparatorChar + "Mods" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != gameThread)
                    throw new InvalidOperationException("TAS cannot order background mod random constructors: " + method);
                var key = assembly.GetName().Name + ":" + method!.DeclaringType!.FullName
                    + ":" + method + ":" + frame.GetILOffset();
                ordinals.TryGetValue(key, out var ordinal);
                ordinals[key] = checked(ordinal + 1);
                unchecked
                {
                    uint hash = 2166136261;
                    foreach (var character in key) hash = (hash ^ character) * 16777619;
                    hash = (hash ^ ordinal) * 16777619;
                    return (int)(hash & 0x7fffffff);
                }
            }
            return Environment.TickCount;
        }

        public void Dispose()
        {
            On.InControl.InputManager.UpdateInternal -= OnInputUpdate;
            randomConstructor.Dispose();
            synchronousFetch?.Dispose();
            experimentalFetch?.Dispose();
            cinematics.Dispose();
            serialScenes?.Dispose();
            audioLifetimes?.Dispose();
        }
    }
}
