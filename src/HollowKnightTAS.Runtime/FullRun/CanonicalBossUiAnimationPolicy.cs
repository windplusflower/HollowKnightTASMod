using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Core.Movie;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;

namespace HollowKnightTAS.Runtime.FullRun
{
    // R2/S2: discard only an originally subscribed, subsequently destroyed
    // transient challenge UI. Live and unregistered callbacks stay in D's
    // ordinary fail-closed path. This deliberately changes dead UI effects.
    internal sealed class CanonicalBossUiAnimationPolicy : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private sealed class References : IEqualityComparer<object> {
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private sealed class Registration {
            internal Tk2dWatchAnimationEvents Action = null!;
            internal PlayMakerFSM Component = null!;
            internal UnityEngine.GameObject Owner = null!;
            internal Fsm Fsm = null!;
            internal FsmState State = null!;
            internal int StateOrdinal;
        }
        private readonly Dictionary<object, Registration> registered = new Dictionary<object, Registration>(new References());
        private readonly Hook? hook;
        private readonly bool enabled;
        private Exception? failure;
        private int originalCalls;
        private bool disposed;
        private static object? Read(object owner, Type type, string field) => type.GetField(field, Flags)!.GetValue(owner);
        private static FsmState[] States(Fsm fsm) => (FsmState[]?)Read(fsm, typeof(Fsm), "states") ?? Array.Empty<FsmState>();
        private static FsmStateAction[] Actions(FsmState state) => (FsmStateAction[]?)Read(state, typeof(FsmState), "actions") ?? Array.Empty<FsmStateAction>();
        private static int StateOrdinal(Fsm fsm, FsmState state, Tk2dWatchAnimationEvents action)
        {
            if (fsm.Name != "GG Boss UI") return -1;
            var ordinal = state.Name == "Challenge" ? 10 : state.Name == "Challenge Audio" ? 11 : -1;
            if (ordinal < 0) return -1;
            var states = States(fsm); var actions = Actions(state);
            if (states.Length != 14 || actions.Length != 2 || !ReferenceEquals(states[ordinal], state) || !ReferenceEquals(actions[1], action))
                throw new InvalidDataException("Challenge UI original action directory differs.");
            return ordinal;
        }
        private static MethodInfo Method(string name, int token) {
            var result = typeof(Tk2dWatchAnimationEvents).GetMethod(name, Flags);
            if (result == null || result.MetadataToken != token || result.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid)
                throw new InvalidDataException("Original challenge UI animation method differs.");
            return result;
        }
        internal CanonicalBossUiAnimationPolicy()
        {
            enabled = NativeExecutionProfile.UsesCanonicalRetiredUiCallbacks(NativeExecutionProfile.SelectedProfileId);
            if (!enabled) return;
            Method("AnimationCompleteDelegate", 0x060034d3); Method("AnimationEventDelegate", 0x060034d2);
            hook = new Hook(Method("DoWatchAnimationWithEvents", 0x060034d1),
                (Action<Action<Tk2dWatchAnimationEvents>, Tk2dWatchAnimationEvents>)Watch);
        }
        private void Watch(Action<Tk2dWatchAnimationEvents> original, Tk2dWatchAnimationEvents action)
        {
            originalCalls++; original(action);
            try {
            if (action.GetType() != typeof(Tk2dWatchAnimationEvents)) return;
            var component = Read(action, typeof(FsmStateAction), "fsmComponent") as PlayMakerFSM;
            var owner = Read(action, typeof(FsmStateAction), "owner") as UnityEngine.GameObject;
            var fsm = Read(action, typeof(FsmStateAction), "fsm") as Fsm;
            var state = Read(action, typeof(FsmStateAction), "fsmState") as FsmState;
            if (ReferenceEquals(component, null) || ReferenceEquals(owner, null) || fsm == null || state == null) return;
            var ordinal = StateOrdinal(fsm, state, action);
            if (ordinal < 0) return;
            var hero = HeroController.SilentInstance;
            if (component == null || owner == null || hero == null) return;
            if (!ReferenceEquals(Read(component, typeof(PlayMakerFSM), "fsm"), fsm) || !ReferenceEquals(component.gameObject, owner))
                throw new InvalidDataException("Challenge UI original action directory differs.");
            var animator = hero.GetComponent<tk2dSpriteAnimator>();
            if (animator == null || !ReferenceEquals(Read(action, typeof(Tk2dWatchAnimationEvents), "_sprite"), animator)) return;
            bool Has(Delegate? value) => (value?.GetInvocationList() ?? Array.Empty<Delegate>()).Any(call => ReferenceEquals(call.Target, action));
            if (!Has(animator.AnimationCompleted) && !Has(animator.AnimationEventTriggered)) return;
            if (registered.Count >= 256 && !registered.ContainsKey(action)) throw new InvalidDataException("Challenge UI registration bound exceeded.");
            registered[action] = new Registration { Action = action, Component = component, Owner = owner, Fsm = fsm, State = state, StateOrdinal = ordinal };
            } catch (Exception error) { failure = error; throw; }
        }
        internal object Reset()
        {
            if (disposed && enabled) throw new InvalidOperationException("Canonical UI callback session has ended.");
            if (!enabled) return new { enabled = false, removedComplete = 0, removedEvent = 0 };
            if (failure != null) throw new InvalidDataException("Challenge UI registration previously failed.", failure);
            var hero = HeroController.SilentInstance ?? throw new InvalidDataException("Canonical UI callback Hero is absent.");
            var animator = hero.GetComponent<tk2dSpriteAnimator>() ?? throw new InvalidDataException("Canonical UI callback animator is absent.");
            bool Remove(Delegate call, bool complete) {
                if (call.Target == null || !registered.TryGetValue(call.Target, out var entry)) return false;
                if (call.Method != Method(complete ? "AnimationCompleteDelegate" : "AnimationEventDelegate", complete ? 0x060034d3 : 0x060034d2))
                    throw new InvalidDataException("Registered UI callback method changed.");
                if (entry.Component != null || entry.Owner != null) return false;
                if (!ReferenceEquals(Read(entry.Action, typeof(FsmStateAction), "fsm"), entry.Fsm)
                    || !ReferenceEquals(Read(entry.Action, typeof(FsmStateAction), "fsmState"), entry.State)
                    || !ReferenceEquals(Read(entry.Action, typeof(FsmStateAction), "fsmComponent"), entry.Component)
                    || !ReferenceEquals(Read(entry.Action, typeof(FsmStateAction), "owner"), entry.Owner)
                    || StateOrdinal(entry.Fsm, entry.State, entry.Action) != entry.StateOrdinal)
                    throw new InvalidDataException("Retired challenge UI action provenance changed.");
                return true;
            }
            var complete = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>?)CanonicalRetiredCallbackFilter.Filter(
                animator.AnimationCompleted, call => Remove(call, true), out var removedComplete);
            var events = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>?)CanonicalRetiredCallbackFilter.Filter(
                animator.AnimationEventTriggered, call => Remove(call, false), out var removedEvent);
            animator.AnimationCompleted = complete; animator.AnimationEventTriggered = events;
            if (!ReferenceEquals(animator.AnimationCompleted, complete) || !ReferenceEquals(animator.AnimationEventTriggered, events))
                throw new InvalidOperationException("Canonical UI callback slot readback differs.");
            return new { enabled = true, policy = NativeExecutionProfile.CanonicalRetiredUiCallbackPolicyId,
                originalCalls, registered = registered.Count, removedComplete, removedEvent, eventDataCleared = false, completeStateCoverage = false };
        }
        public void Dispose() { if (disposed) return; disposed = true; hook?.Dispose(); registered.Clear(); }
    }
}
