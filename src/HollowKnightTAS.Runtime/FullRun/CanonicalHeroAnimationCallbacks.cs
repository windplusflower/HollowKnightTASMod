using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace HollowKnightTAS.Runtime.FullRun
{
    // These are real animator delegate slots, not events to replay. Retired
    // scene actions can remain subscribed after their FSM owner is destroyed.
    internal static class CanonicalHeroAnimationCallbacks
    {
        internal const string Root = "D/Hero-animation-callbacks";
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Item), typeof(Item[]) };
        internal const string SchemaIdentity = "Hero-original-animator-two-slots:Tk2dPlayAnimationWithEvents:13498,13499:existing-Hero-action-target:ordered-alias-v1";
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo RawStates = typeof(Fsm).GetField("states", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        private static readonly FieldInfo RawActions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        private static FsmState[] States(Fsm owner) => (FsmState[]?)RawStates.GetValue(owner) ?? Array.Empty<FsmState>();
        private static FsmStateAction[] Actions(FsmState owner) => (FsmStateAction[]?)RawActions.GetValue(owner) ?? Array.Empty<FsmStateAction>();
        internal sealed class Data { public Item[] Complete = Array.Empty<Item>(), Event = Array.Empty<Item>(); }
        internal sealed class Item
        {
            public string Fsm = "", StateName = "", Module = "", Method = "";
            public int State, Action, Token, Alias;
        }
        private static tk2dSpriteAnimator Animator() => (HeroController.SilentInstance
            ?? throw new InvalidDataException("Hero callback owner is absent.")).GetComponent<tk2dSpriteAnimator>()
            ?? throw new InvalidDataException("Original Hero animator is absent.");
        private static MethodInfo Method(bool complete)
        {
            var method = typeof(Tk2dPlayAnimationWithEvents).GetMethod(complete ? "AnimationCompleteDelegate" : "AnimationEventDelegate",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (method == null || method.MetadataToken != (complete ? 0x060034bb : 0x060034ba)
                || method.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid)
                throw new InvalidDataException("Original Hero animation method layout differs.");
            return method;
        }
        internal static Data Capture(BoundaryObjectIdentity identities)
        {
            var animator = Animator();
            var fsms = CanonicalHeroFsmBindings.Snapshot(identities);
            var aliases = new List<object>();
            Item[] Slots(Delegate? value, bool complete) => (value?.GetInvocationList() ?? Array.Empty<Delegate>()).Select(callback => {
                var expected = Method(complete);
                if (callback.Method.MetadataToken != expected.MetadataToken || callback.Method.Module.ModuleVersionId != expected.Module.ModuleVersionId
                    || callback.Method.DeclaringType != expected.DeclaringType || !(callback.Target is Tk2dPlayAnimationWithEvents target))
                    throw new InvalidDataException("Unregistered source Hero animation callback: " + callback.Method.DeclaringType?.FullName + "/" + callback.Method.Name);
                if (aliases.Count > 256) throw new InvalidDataException("Hero animation callback bound exceeded.");
                var alias = aliases.FindIndex(value => ReferenceEquals(value, target));
                if (alias < 0) { alias = aliases.Count; aliases.Add(target); }
                foreach (var pair in fsms) {
                    var fsm = (Fsm)Raw.GetValue(pair.Value);
                    var states = States(fsm);
                    for (var state = 0; state < states.Length; state++)
                        for (var action = 0; action < Actions(states[state]).Length; action++)
                            if (ReferenceEquals(Actions(states[state])[action], target)) return new Item {
                                Fsm = pair.Key, State = state, Action = action, StateName = states[state].Name,
                                Module = callback.Method.Module.ModuleVersionId.ToString("D"), Method = callback.Method.Name,
                                Token = callback.Method.MetadataToken, Alias = alias };
                }
                throw new InvalidDataException("Source Hero animation callback target has no existing Hero FSM action binding.");
            }).ToArray();
            return new Data { Complete = Slots(animator.AnimationCompleted, true), Event = Slots(animator.AnimationEventTriggered, false) };
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            return new Data { Complete = (Item[])view.Field(root, typeof(Data), nameof(Data.Complete))!,
                Event = (Item[])view.Field(root, typeof(Data), nameof(Data.Event))! };
        }
        internal static Action PrepareApply(Data source, BoundaryObjectIdentity identities, Func<bool> admitted)
        {
            Method(true); Method(false); // Fixed original contract also guards two empty source slots.
            var animator = Animator();
            var oldComplete = animator.AnimationCompleted; var oldEvent = animator.AnimationEventTriggered;
            var fsms = CanonicalHeroFsmBindings.Snapshot(identities);
            var aliases = new Dictionary<int, object>();
            var actionGuards = new List<Func<bool>>();
            Delegate? Slots(Item[] items, bool complete)
            {
                if (items == null || items.Length > 256) throw new InvalidDataException("Hero callback invocation bound differs.");
                Delegate? result = null;
                foreach (var item in items) {
                    var method = Method(complete);
                    if (item == null || item.Module != method.Module.ModuleVersionId.ToString("D") || item.Token != method.MetadataToken
                        || item.Method != method.Name || item.Alias < 0 || item.Alias > 511 || !fsms.TryGetValue(item.Fsm, out var owner))
                        throw new InvalidDataException("Source Hero callback method or owner differs.");
                    var fsm = (Fsm)Raw.GetValue(owner);
                    var states = States(fsm);
                    if (item.State < 0 || item.State >= states.Length || states[item.State].Name != item.StateName
                        || item.Action < 0 || item.Action >= Actions(states[item.State]).Length
                        || !(Actions(states[item.State])[item.Action] is Tk2dPlayAnimationWithEvents target))
                        throw new InvalidDataException("Source Hero callback action cannot bind to the actual target instance.");
                    if (aliases.TryGetValue(item.Alias, out var shared)) {
                        if (!ReferenceEquals(shared, target)) throw new InvalidDataException("Hero callback shared target alias differs.");
                    } else {
                        if (aliases.Values.Any(value => ReferenceEquals(value, target))) throw new InvalidDataException("Hero callback distinct target alias collapsed.");
                        aliases.Add(item.Alias, target);
                    }
                    // Main graph installation must not leave this callback
                    // attached to a superseded pre-import action instance.
                    actionGuards.Add(() => owner != null && ReferenceEquals(Raw.GetValue(owner), fsm)
                        && item.State < States(fsm).Length && item.Action < Actions(States(fsm)[item.State]).Length
                        && ReferenceEquals(Actions(States(fsm)[item.State])[item.Action], target));
                    var type = complete ? typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>)
                        : typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>);
                    result = Delegate.Combine(result, Delegate.CreateDelegate(type, target, method));
                }
                return result;
            }
            var nextComplete = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>?)Slots(source.Complete, true);
            var nextEvent = (Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, int>?)Slots(source.Event, false);
            var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("Hero callback prepared scope was already consumed.");
                consumed = true;
                if (!admitted() || actionGuards.Any(guard => !guard()) || !ReferenceEquals(Animator(), animator) || !ReferenceEquals(animator.AnimationCompleted, oldComplete)
                    || !ReferenceEquals(animator.AnimationEventTriggered, oldEvent))
                    throw new InvalidOperationException("Hero callback actual target guard changed.");
                animator.AnimationCompleted = nextComplete; animator.AnimationEventTriggered = nextEvent;
                if (!admitted() || !ReferenceEquals(animator.AnimationCompleted, nextComplete)
                    || !ReferenceEquals(animator.AnimationEventTriggered, nextEvent))
                    throw new InvalidOperationException("Hero callback source slot readback differs.");
            };
        }
    }
}
