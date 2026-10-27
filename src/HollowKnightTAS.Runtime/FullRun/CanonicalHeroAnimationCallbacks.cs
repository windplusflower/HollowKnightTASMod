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
        internal const string SchemaIdentity = "Hero-original-animator-two-slots:Tk2dPlayAnimationWithEvents:13498,13499:Tk2dWatchAnimationEvents:13522,13523:existing-Hero-action-target:ordered-alias-v2";
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo RawStates = typeof(Fsm).GetField("states", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        private static readonly FieldInfo RawActions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        private static FsmState[] States(Fsm owner) => (FsmState[]?)RawStates.GetValue(owner) ?? Array.Empty<FsmState>();
        private static FsmStateAction[] Actions(FsmState owner) => (FsmStateAction[]?)RawActions.GetValue(owner) ?? Array.Empty<FsmStateAction>();
        internal sealed class Data { public Item[] Complete = Array.Empty<Item>(), Event = Array.Empty<Item>(); }
        internal sealed class Item
        {
            public string Fsm = "", StateName = "", Module = "", Method = "", ActionType = "";
            public int State, Action, Token, Alias;
        }
        private static tk2dSpriteAnimator Animator() => (HeroController.SilentInstance
            ?? throw new InvalidDataException("Hero callback owner is absent.")).GetComponent<tk2dSpriteAnimator>()
            ?? throw new InvalidDataException("Original Hero animator is absent.");
        internal sealed class DetachedBenchProof
        {
            internal Fsm Fsm = null!;
            internal FsmStateAction Target = null!;
            internal readonly Dictionary<string, object> Slots = new Dictionary<string, object>(StringComparer.Ordinal);
            internal readonly HashSet<object> Members = new HashSet<object>(new ReferenceComparer());
            internal Action VerifyBefore = null!;
            internal Action VerifyRemoved = null!;
        }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
        internal static DetachedBenchProof? ProbeDetachedBench()
        {
            var animator = Animator(); var chain = animator.AnimationCompleted?.GetInvocationList() ?? Array.Empty<Delegate>();
            var candidates = chain.Where(callback => callback.Target is FsmStateAction action
                && typeof(FsmStateAction).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(action) is Fsm fsm
                && fsm.Name == "Bench Control" && ReferenceEquals(typeof(Fsm).GetField("owner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(fsm), null)).ToArray();
            if (candidates.Length == 0) return null;
            if (chain.Length != 1 || candidates.Length != 1) throw new InvalidDataException("Detached Bench callback invocation shape differs.");
            var target = RequireCallback(candidates[0], true);
            var ownerFsm = (Fsm)typeof(FsmStateAction).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target);
            var states = States(ownerFsm);
            if (states.Sum(state => Actions(state).Count(action => ReferenceEquals(action, target))) != 1)
                throw new InvalidDataException("Detached Bench callback is not the exact original FSM action.");
            var result = new DetachedBenchProof { Fsm = ownerFsm, Target = target };
            var originalComplete = animator.AnimationCompleted; var originalEvent = animator.AnimationEventTriggered;
            result.VerifyBefore = () => {
                if (!ReferenceEquals(Animator(), animator) || !ReferenceEquals(animator.AnimationCompleted, originalComplete)
                    || !ReferenceEquals(animator.AnimationEventTriggered, originalEvent))
                    throw new InvalidOperationException("Original detached Bench callback changed before source replacement.");
            };
            result.Members.Add(ownerFsm);
            var actionData = typeof(FsmState).GetField("actionData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            var parameterList = typeof(ActionData).GetField("fsmOwnerDefaultParams", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            for (var i = 0; i < states.Length; i++) {
                var state = states[i]; result.Members.Add(state);
                foreach (var action in Actions(state)) if (action != null) result.Members.Add(action);
                var data = actionData.GetValue(state); if (data == null) continue;
                var list = parameterList.GetValue(data); if (list == null) continue;
                var items = list.GetType().GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(list) as Array
                    ?? throw new InvalidDataException("Detached Bench original parameter array is absent.");
                if (items.Length > 4096) throw new InvalidDataException("Detached Bench original parameter array exceeds bound.");
                for (var j = 0; j < items.Length; j++) if (items.GetValue(j) is FsmOwnerDefault item)
                    result.Slots.Add("state[" + i + "]:" + state.Name + ".actionData.fsmOwnerDefaultParams._items[" + j + "].gameObject", item);
            }
            result.VerifyRemoved = () => {
                if (!ReferenceEquals(Animator(), animator) || (animator.AnimationCompleted?.GetInvocationList() ?? Array.Empty<Delegate>()).Any(callback => ReferenceEquals(callback.Target, target))
                    || (animator.AnimationEventTriggered?.GetInvocationList() ?? Array.Empty<Delegate>()).Any(callback => ReferenceEquals(callback.Target, target)))
                    throw new InvalidOperationException("Original detached Bench action remains reachable from Hero animator callbacks.");
            };
            return result;
        }
        private static MethodInfo Method(Type actionType, bool complete)
        {
            var play = actionType == typeof(Tk2dPlayAnimationWithEvents);
            if (!play && actionType != typeof(Tk2dWatchAnimationEvents))
                throw new InvalidDataException("Unregistered Hero animation action type.");
            var method = actionType.GetMethod(complete ? "AnimationCompleteDelegate" : "AnimationEventDelegate",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (method == null || method.MetadataToken != (play ? (complete ? 0x060034bb : 0x060034ba) : (complete ? 0x060034d3 : 0x060034d2))
                || method.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid)
                throw new InvalidDataException("Original Hero animation method layout differs.");
            return method;
        }
        private static FsmStateAction RequireCallback(Delegate callback, bool complete)
        {
            if (!(callback.Target is FsmStateAction target)) throw new InvalidDataException("Hero animation callback is not a closed action instance.");
            var expected = Method(target.GetType(), complete);
            if (callback.Method != expected)
                throw new InvalidDataException("Unregistered source Hero animation callback method: " + callback.Method);
            return target;
        }
        private static Item FindExistingAction(FsmStateAction target, IReadOnlyDictionary<string, PlayMakerFSM> fsms)
        {
            foreach (var pair in fsms) {
                var fsm = (Fsm)Raw.GetValue(pair.Value);
                var states = States(fsm);
                for (var state = 0; state < states.Length; state++)
                    for (var action = 0; action < Actions(states[state]).Length; action++)
                        if (ReferenceEquals(Actions(states[state])[action], target)) return new Item {
                            Fsm = pair.Key, State = state, Action = action, StateName = states[state].Name,
                            ActionType = CanonicalCarryGraphCodec.Identity(target.GetType()) };
            }
            throw new InvalidDataException("Source Hero animation callback target has no existing Hero FSM action binding;provenance="
                + UnboundProvenance(target, fsms));
        }
        internal static string UnboundProvenance(FsmStateAction target, IReadOnlyDictionary<string, PlayMakerFSM> fsms)
        {
            // Error branch only: inspect this closed action's own existing
            // fields and the already-captured retirement directory. No world
            // search, invocation, state creation or raw address is involved.
            try {
                object? Field(string name) => typeof(FsmStateAction).GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
                var fsm = Field("fsm") as Fsm; var state = Field("fsmState") as FsmState;
                object Owner(object? value) {
                    if (ReferenceEquals(value, null)) return new { present = false };
                    if (!(value is UnityEngine.Object unity)) return new { present = true, type = value!.GetType().FullName };
                    var destroyed = unity == null;
                    var retired = LoadingSerialSceneProbe.RetiredSourceOwner(unity!.GetInstanceID());
                    return new { present = true, type = value.GetType().FullName, destroyed,
                        retiredIdentity = retired == null ? null : retired.Substring(0, Math.Min(1024, retired.Length)),
                        liveName = destroyed ? null : unity.name,
                        liveScene = !destroyed && unity is UnityEngine.Component component ? component.gameObject.scene.name
                            : !destroyed && unity is UnityEngine.GameObject go ? go.scene.name : null };
                }
                var states = fsm == null ? Array.Empty<FsmState>() : States(fsm);
                var actions = state == null ? Array.Empty<FsmStateAction>() : Actions(state);
                var activeState = fsm == null ? null : typeof(Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm) as FsmState;
                var heroOwners = fsms.Where(pair => ReferenceEquals(Raw.GetValue(pair.Value), fsm)).Select(pair => pair.Key).Take(2).ToArray();
                return Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    actionType = target.GetType().FullName, fsmName = fsm?.Name, stateName = state?.Name,
                    ownStateIndex = states.Length <= 4096 ? Array.IndexOf(states, state) : -2,
                    ownActionIndex = actions.Length <= 4096 ? Array.IndexOf(actions, target) : -2,
                    activeStateIndex = states.Length <= 4096 ? Array.IndexOf(states, activeState) : -2,
                    activeStateName = activeState?.Name,
                    states = states.Length, actions = actions.Length, heroFsmOwners = heroOwners,
                    fsmComponent = Owner(Field("fsmComponent")), owner = Owner(Field("owner")) });
            } catch (Exception error) {
                return "diagnostic-unavailable:" + error.GetType().FullName;
            }
        }
        internal static Data Capture(BoundaryObjectIdentity identities)
        {
            var animator = Animator();
            var fsms = CanonicalHeroFsmBindings.Snapshot(identities);
            var aliases = new List<object>();
            Item[] Slots(Delegate? value, bool complete) => (value?.GetInvocationList() ?? Array.Empty<Delegate>()).Select(callback => {
                var target = RequireCallback(callback, complete);
                if (aliases.Count > 256) throw new InvalidDataException("Hero animation callback bound exceeded.");
                var alias = aliases.FindIndex(value => ReferenceEquals(value, target));
                if (alias < 0) { alias = aliases.Count; aliases.Add(target); }
                var item = FindExistingAction(target, fsms);
                item.Module = callback.Method.Module.ModuleVersionId.ToString("D"); item.Method = callback.Method.Name;
                item.Token = callback.Method.MetadataToken; item.Alias = alias; return item;
            }).ToArray();
            return new Data { Complete = Slots(animator.AnimationCompleted, true), Event = Slots(animator.AnimationEventTriggered, false) };
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            return new Data { Complete = (Item[])view.Field(root, typeof(Data), nameof(Data.Complete))!,
                Event = (Item[])view.Field(root, typeof(Data), nameof(Data.Event))! };
        }
        internal static Action PrepareApply(Data source, BoundaryObjectIdentity identities, Func<bool> admitted,
            CanonicalCarryGraphCodec.PreparedView view)
        {
            Method(typeof(Tk2dPlayAnimationWithEvents), true); Method(typeof(Tk2dPlayAnimationWithEvents), false);
            Method(typeof(Tk2dWatchAnimationEvents), true); Method(typeof(Tk2dWatchAnimationEvents), false);
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
                    if (item == null) throw new InvalidDataException("Hero callback item is absent.");
                    var actionType = item.ActionType == CanonicalCarryGraphCodec.Identity(typeof(Tk2dPlayAnimationWithEvents)) ? typeof(Tk2dPlayAnimationWithEvents)
                        : item.ActionType == CanonicalCarryGraphCodec.Identity(typeof(Tk2dWatchAnimationEvents)) ? typeof(Tk2dWatchAnimationEvents)
                        : throw new InvalidDataException("Source Hero callback action type is unregistered.");
                    var method = Method(actionType, complete);
                    if (item.Module != method.Module.ModuleVersionId.ToString("D") || item.Token != method.MetadataToken
                        || item.Method != method.Name || item.Alias < 0 || item.Alias > 511 || !fsms.TryGetValue(item.Fsm, out var owner))
                        throw new InvalidDataException("Source Hero callback method or owner differs.");
                    var fsm = (Fsm)Raw.GetValue(owner);
                    if (!ReferenceEquals(view.Root("HeroFSM/" + item.Fsm), fsm))
                        throw new InvalidDataException("Prepared Hero animation FSM alias differs.");
                    var states = (FsmState[])view.Field(fsm, typeof(Fsm), "states")!;
                    if (item.State < 0 || item.State >= states.Length || states[item.State].Name != item.StateName
                        || item.Action < 0 || item.Action >= Actions(states[item.State]).Length
                        || Actions(states[item.State])[item.Action].GetType() != actionType)
                        throw new InvalidDataException("Source Hero callback action cannot bind to the actual target instance.");
                    var target = Actions(states[item.State])[item.Action];
                    if (aliases.TryGetValue(item.Alias, out var shared)) {
                        if (!ReferenceEquals(shared, target)) throw new InvalidDataException("Hero callback shared target alias differs.");
                    } else {
                        if (aliases.Values.Any(value => ReferenceEquals(value, target))) throw new InvalidDataException("Hero callback distinct target alias collapsed.");
                        aliases.Add(item.Alias, target);
                    }
                    // Main graph installation must not leave this callback
                    // attached to a superseded pre-import action instance.
                    actionGuards.Add(() => owner != null && ReferenceEquals(Raw.GetValue(owner), fsm)
                        && ReferenceEquals(States(fsm), states) && item.State < States(fsm).Length && item.Action < Actions(States(fsm)[item.State]).Length
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
