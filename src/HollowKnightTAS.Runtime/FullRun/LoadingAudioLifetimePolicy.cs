using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // V7 changes only the completion predicate of the two pooled sound actors.
    // It neither suppresses Play/PlayOneShot nor starts/stops AudioRenderer.
    internal sealed class LoadingAudioLifetimePolicy : IDisposable
    {
        private readonly List<IDisposable> hooks = new List<IDisposable>();
        private readonly Dictionary<AudioSource, PooledAudioLifetime> states = new Dictionary<AudioSource, PooledAudioLifetime>();
        private readonly HashSet<Assembly> inspectedAssemblies = new HashSet<Assembly>();
        private static LoadingAudioLifetimePolicy? instance;
        private string? failure;

        internal LoadingAudioLifetimePolicy()
        {
            if (!NativeExecutionProfile.UsesVirtualAudio(NativeExecutionProfile.SelectedProfileId))
                throw new InvalidOperationException("Virtual audio lifetimes require the V7 profile.");
            instance = this;
            Enable<PlayAudioAndRecycle>();
            Enable<SpawnableAudioSource>();
            ReplaceCompletion(typeof(PlayAudioAndRecycle));
            ReplaceCompletion(typeof(SpawnableAudioSource));
            hooks.Add(new Hook(Method("Play", Type.EmptyTypes), (Action<Action<AudioSource>, AudioSource>)Play));
            hooks.Add(new Hook(Method("Play", new[] { typeof(ulong) }),
                (Action<Action<AudioSource, ulong>, AudioSource, ulong>)PlayWithDelay));
            hooks.Add(new Hook(Method("PlayOneShot", new[] { typeof(AudioClip), typeof(float) }),
                (Action<Action<AudioSource, AudioClip, float>, AudioSource, AudioClip, float>)OneShot));
            hooks.Add(new Hook(Method("Stop", Type.EmptyTypes), (Action<Action<AudioSource>, AudioSource>)Stop));
            RegisterPauseCallsites();
            Modding.Logger.Log("[HKTAS] V7 pooled audio uses virtual completion; Unity playback and AudioRenderer unchanged.");
        }

        private static MethodInfo Method(string name, Type[] parameters)
            => typeof(AudioSource).GetMethod(name, parameters)
                ?? throw new MissingMethodException("AudioSource." + name);

        private void Enable<T>() where T : MonoBehaviour
        {
            var method = typeof(T).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(T).Name + ".OnEnable");
            hooks.Add(new Hook(method, (Action<Action<T>, T>)((original, actor) =>
            {
                var source = actor.GetComponent<AudioSource>();
                if (source == null) throw Fail("Pooled sound actor has no AudioSource.");
                states[source] = new PooledAudioLifetime(Time.unscaledTimeAsDouble);
                if (states.Count > 4096) throw Fail("Pooled sound source budget exceeded.");
                original(actor);
            })));
        }

        private void ReplaceCompletion(Type type)
        {
            var method = type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(type.Name + ".Update");
            hooks.Add(new ILHook(method, il =>
            {
                var cursor = new ILCursor(il);
                if (!cursor.TryGotoNext(MoveType.Before, instruction => instruction.MatchCallvirt(typeof(AudioSource), "get_isPlaying")))
                    throw new InvalidOperationException("Pooled sound completion callsite changed: " + type.Name);
                cursor.Remove();
                cursor.EmitDelegate<Func<AudioSource, bool>>(IsPlaying);
            }));
        }

        private bool Pooled(AudioSource source)
            => source.GetComponent<PlayAudioAndRecycle>() != null || source.GetComponent<SpawnableAudioSource>() != null;

        private PooledAudioLifetime State(AudioSource source)
        {
            if (!states.TryGetValue(source, out var state)) throw Fail("Pooled audio was enabled before its lifetime policy.");
            state.Advance(Time.unscaledTimeAsDouble, source.pitch, AudioListener.pause && !source.ignoreListenerPause);
            return state;
        }

        private void Play(Action<AudioSource> original, AudioSource source)
        {
            original(source);
            if (Pooled(source) && source.clip != null) State(source).Play(Length(source.clip), source.loop, false);
        }

        private void PlayWithDelay(Action<AudioSource, ulong> original, AudioSource source, ulong delay)
        {
            if (delay != 0 && Pooled(source)) throw Fail("Delayed pooled audio requires a registered lifetime adapter.");
            original(source, delay);
            if (Pooled(source) && source.clip != null) State(source).Play(Length(source.clip), source.loop, false);
        }

        private void OneShot(Action<AudioSource, AudioClip, float> original, AudioSource source, AudioClip clip, float volume)
        {
            original(source, clip, volume);
            if (clip != null && Pooled(source)) State(source).Play(Length(clip), false, true);
        }

        private void Stop(Action<AudioSource> original, AudioSource source)
        {
            original(source);
            if (states.TryGetValue(source, out var state)) state.Stop();
        }

        private bool IsPlaying(AudioSource source) => State(source).IsPlaying;
        private void Pause(AudioSource source)
        {
            if (states.ContainsKey(source)) State(source).SetPaused(true);
            source.Pause();
        }
        private void UnPause(AudioSource source)
        {
            if (states.ContainsKey(source)) State(source).SetPaused(false);
            source.UnPause();
        }

        private void RegisterPauseCallsites()
        {
            // Pause/UnPause are body-less internal calls, so RuntimeDetour.Hook
            // cannot copy them. Adapt their managed game/Mod callers instead.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || assembly == typeof(LoadingAudioLifetimePolicy).Assembly
                    || inspectedAssemblies.Contains(assembly)) continue;
                var location = assembly.Location.Replace('\\', '/');
                if (assembly != typeof(HeroController).Assembly
                    && location.IndexOf("/Managed/Mods/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                inspectedAssemblies.Add(assembly);
                using (var definition = AssemblyDefinition.ReadAssembly(assembly.Location))
                foreach (var type in definition.MainModule.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.Instructions.Any(IsPauseCall)) continue;
                    if (type.HasGenericParameters || method.HasGenericParameters)
                        throw Fail("Generic pooled audio pause caller requires an adapter: " + method.FullName);
                    var reflected = assembly.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32());
                    hooks.Add(new ILHook(reflected, il =>
                    {
                        foreach (var call in il.Body.Instructions.Where(IsPauseCall).ToArray())
                        {
                            var pause = ((MethodReference)call.Operand!).Name == "Pause";
                            // Preserve any branch target attached to the call.
                            call.OpCode = OpCodes.Nop; call.Operand = null;
                            var cursor = new ILCursor(il);
                            cursor.Goto(call, MoveType.After);
                            cursor.EmitDelegate<Action<AudioSource>>(pause ? Pause : UnPause);
                        }
                    }));
                    Modding.Logger.LogDebug("[HKTAS] Pooled audio pause callsite: " + method.FullName);
                }
            }
        }

        private static bool IsPauseCall(Instruction instruction)
            => (instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Call)
                && instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == typeof(AudioSource).FullName
                && method.Parameters.Count == 0 && (method.Name == "Pause" || method.Name == "UnPause");

        internal static object[] CaptureState(Func<UnityEngine.Object, string> identity)
        {
            if (instance == null) return Array.Empty<object>();
            return instance.states.OrderBy(pair => identity(pair.Key), StringComparer.Ordinal)
                .Select(pair => (object)new { source = identity(pair.Key), state = pair.Value.Capture() }).ToArray();
        }
        private static double Length(AudioClip clip) => clip.samples / (double)clip.frequency;
        private InvalidOperationException Fail(string message)
        {
            failure = message;
            Modding.Logger.LogError("[HKTAS] V7 audio lifetime failure: " + message);
            return new InvalidOperationException(message);
        }
        internal static void CheckFailure()
        {
            if (instance?.failure != null) throw new InvalidOperationException(instance.failure);
            if (instance == null) return;
            instance.RegisterPauseCallsites();
            // Disabled pooled actors are reinitialized by OnEnable. Retaining
            // their old clocks (or destroyed Unity wrappers) has no lifecycle use.
            foreach (var source in instance.states.Keys.Where(source => source == null
                || !source.gameObject.activeInHierarchy).ToArray()) instance.states.Remove(source);
        }

        internal static void ResetForInProcessRestore()
        {
            if (instance == null) return;
            foreach (var source in instance.states.Keys.ToArray())
            {
                if (source == null) continue;
                if (source.gameObject.activeInHierarchy)
                {
                    source.Stop();
                    if (instance.Pooled(source))
                    {
                        ObjectPool.Recycle(source.gameObject);
                    }
                }
            }
            instance.states.Clear();
        }
        public void Dispose()
        {
            for (var index = hooks.Count - 1; index >= 0; index--) hooks[index].Dispose();
            hooks.Clear(); states.Clear(); inspectedAssemblies.Clear(); instance = null;
        }
    }
}
