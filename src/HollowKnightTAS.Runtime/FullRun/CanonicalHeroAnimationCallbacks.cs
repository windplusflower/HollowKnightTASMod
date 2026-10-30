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
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Item), typeof(Item[]), typeof(object[]), typeof(byte[][]), typeof(GlobalEnums.ActorStates) };
        internal const string SchemaIdentity = "Hero-original-animator-two-slots:Tk2dPlayAnimationWithEvents:13498,13499:Tk2dWatchAnimationEvents:13522,13523:existing-Hero-action-target:HeroAnimationController:654:full17-controller-11-animator-fields:ordered-alias-v3";
        private const string ControllerBinding = "@original-HeroAnimationController";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo RawStates = typeof(Fsm).GetField("states", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        private static readonly FieldInfo RawActions = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        private static FsmState[] States(Fsm owner) => (FsmState[]?)RawStates.GetValue(owner) ?? Array.Empty<FsmState>();
        private static FsmStateAction[] Actions(FsmState owner) => (FsmStateAction[]?)RawActions.GetValue(owner) ?? Array.Empty<FsmStateAction>();
        internal sealed class Data {
            public Item[] Complete = Array.Empty<Item>(), Event = Array.Empty<Item>();
            public object?[] ControllerValues = Array.Empty<object?>(), AnimatorValues = Array.Empty<object?>();
            public byte[][] Lifecycle = Array.Empty<byte[]>();
            public int ClipIndex = -1, GlobalState;
            public string ClipHash = "";
        }
        internal sealed class Item
        {
            public string Fsm = "", StateName = "", Module = "", Method = "", ActionType = "";
            public int State, Action, Token, Alias;
        }
        private static tk2dSpriteAnimator Animator() => (HeroController.SilentInstance
            ?? throw new InvalidDataException("Hero callback owner is absent.")).GetComponent<tk2dSpriteAnimator>()
            ?? throw new InvalidDataException("Original Hero animator is absent.");
        internal static FieldInfo[] ControllerFields()
        {
            var type = typeof(HeroAnimationController);
            var fields = type.GetFields(Instance | BindingFlags.DeclaredOnly);
            var expected = new[] { "<actorState>k__BackingField", "<prevActorState>k__BackingField", "<stateBeforeControl>k__BackingField",
                "animator", "heroCtrl", "cState", "pd", "<controlEnabled>k__BackingField", "playLanding", "playRunToIdle", "playDashToIdle",
                "playBackDashToIdleEnd", "wasAttacking", "wasFacingRight", "setEntryAnim", "changedClipFromLastFrame", "attackComplete" };
            var types = new[] { typeof(GlobalEnums.ActorStates), typeof(GlobalEnums.ActorStates), typeof(GlobalEnums.ActorStates),
                typeof(tk2dSpriteAnimator), typeof(HeroController), typeof(HeroControllerStates), typeof(PlayerData) };
            if (type.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid || !fields.Select(field => field.Name).SequenceEqual(expected)
                || fields.Where((field, index) => field.FieldType != (index < types.Length ? types[index] : typeof(bool)) || field.IsInitOnly).Any())
                throw new InvalidDataException("Original Hero animation controller seventeen-field ABI differs.");
            return fields;
        }
        private static HeroAnimationController Controller()
        {
            var hero = HeroController.SilentInstance ?? throw new InvalidDataException("Hero animation controller owner absent.");
            var owners = hero.GetComponents<HeroAnimationController>();
            if (owners.Length != 1 || owners[0].GetType() != typeof(HeroAnimationController)) throw new InvalidDataException("Hero animation controller original owner differs.");
            return owners[0];
        }
        internal static MethodInfo ControllerMethod()
        {
            var method = typeof(HeroAnimationController).GetMethod("AnimationCompleteDelegate", Instance)!;
            if (method == null || method.IsStatic || method.MetadataToken != 0x0600028e || method.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid
                || method.ReturnType != typeof(void) || !method.GetParameters().Select(value => value.ParameterType).SequenceEqual(new[] { typeof(tk2dSpriteAnimator), typeof(tk2dSpriteAnimationClip) }))
                throw new InvalidDataException("Original Hero controller animation callback ABI differs.");
            return method;
        }
        internal static string? BindReadbackReference(Data expected, object value)
        {
            if (value is string || value.GetType().IsValueType) return null;
            if (expected.AnimatorValues.Length == 8 && ReferenceEquals(value, expected.AnimatorValues[0])) return "Hero-animation/original-library";
            for (var i = 0; i < expected.ControllerValues.Length; i++)
                if (ReferenceEquals(value, expected.ControllerValues[i])) return "Hero-animation/source-reference[" + i + "]";
            return null;
        }
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
        internal static MethodInfo Method(Type actionType, bool complete)
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
                var controllerCallback = complete && callback.Target is HeroAnimationController;
                var target = controllerCallback ? (object)Controller() : RequireCallback(callback, complete);
                if (controllerCallback && (!ReferenceEquals(callback.Target, target) || callback.Method != ControllerMethod()))
                    throw new InvalidDataException("Source Hero controller callback has a foreign target or method.");
                if (aliases.Count > 256) throw new InvalidDataException("Hero animation callback bound exceeded.");
                var alias = aliases.FindIndex(value => ReferenceEquals(value, target));
                if (alias < 0) { alias = aliases.Count; aliases.Add(target); }
                var item = controllerCallback ? new Item { Fsm = ControllerBinding, State = -1, Action = -1,
                    ActionType = CanonicalCarryGraphCodec.Identity(typeof(HeroAnimationController)) } : FindExistingAction((FsmStateAction)target, fsms);
                item.Module = callback.Method.Module.ModuleVersionId.ToString("D"); item.Method = callback.Method.Name;
                item.Token = callback.Method.MetadataToken; item.Alias = alias; return item;
            }).ToArray();
            var data = new Data { Complete = Slots(animator.AnimationCompleted, true), Event = Slots(animator.AnimationEventTriggered, false) };
            if (HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesCanonicalBossChallengeContext(HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)) {
                var controller = Controller(); var fields = CanonicalPersistentSpriteContinuation.AnimatorFields(); var values = fields.Select(field => field.GetValue(animator)).ToArray();
                var library = values[0] as tk2dSpriteAnimation;
                if (library == null || library.clips == null || library.clips.Length > 4096) throw new InvalidDataException("Hero original animation library differs.");
                var clip = values[3] as tk2dSpriteAnimationClip;
                var indices = Enumerable.Range(0, library.clips.Length).Where(index => ReferenceEquals(library.clips[index], clip)).ToArray();
                if (clip != null && indices.Length != 1 || values[10] != null && !ReferenceEquals(values[10], animator.GetComponent<tk2dSprite>()))
                    throw new InvalidDataException("Hero original clip/sprite aliases differ.");
                data.ClipIndex = clip == null ? -1 : indices[0]; data.ClipHash = CanonicalPersistentSpriteAnimationDiagnostic.ClipHash(clip) ?? "";
                data.AnimatorValues = new object?[] { values[0], values[1], values[2], values[4], values[5], values[6], Convert.ToInt32(values[9]), values[10] != null };
                data.ControllerValues = ControllerFields().Select(field => field.GetValue(controller)).ToArray();
                using (LoadingEngineClockProbe.ReadOnlyCheckpointScope()) data.Lifecycle = new[] { BoundaryMonoLifecycle.Read(animator), BoundaryMonoLifecycle.Read(controller) };
                data.GlobalState = Convert.ToInt32(typeof(tk2dSpriteAnimator).GetField("globalState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null));
            }
            return data;
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            var data = new Data();
            foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(Data))) field.SetValue(data, view.Field(root, field.DeclaringType!, field.Name));
            return data;
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
                    if (item.Fsm == ControllerBinding) {
                        var controllerMethod = ControllerMethod(); var controllerTarget = Controller();
                        if (!complete || item.State != -1 || item.Action != -1 || item.StateName != ""
                            || item.ActionType != CanonicalCarryGraphCodec.Identity(controllerTarget.GetType()) || item.Module != controllerMethod.Module.ModuleVersionId.ToString("D")
                            || item.Token != controllerMethod.MetadataToken || item.Method != controllerMethod.Name || item.Alias < 0 || item.Alias > 511)
                            throw new InvalidDataException("Hero original controller callback descriptor differs.");
                        if (aliases.TryGetValue(item.Alias, out var previous) ? !ReferenceEquals(previous, controllerTarget) : aliases.Values.Any(value => ReferenceEquals(value, controllerTarget)))
                            throw new InvalidDataException("Hero controller callback shared target aliases differ.");
                        aliases[item.Alias] = controllerTarget;
                        actionGuards.Add(() => ReferenceEquals(Controller(), controllerTarget));
                        result = Delegate.Combine(result, Delegate.CreateDelegate(typeof(Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip>), controllerTarget, controllerMethod));
                        continue;
                    }
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
            var restoreControl = PrepareControl(source, animator);
            var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("Hero callback prepared scope was already consumed.");
                consumed = true;
                if (!admitted() || actionGuards.Any(guard => !guard()) || !ReferenceEquals(Animator(), animator) || !ReferenceEquals(animator.AnimationCompleted, oldComplete)
                    || !ReferenceEquals(animator.AnimationEventTriggered, oldEvent))
                    throw new InvalidOperationException("Hero callback actual target guard changed.");
                restoreControl();
                animator.AnimationCompleted = nextComplete; animator.AnimationEventTriggered = nextEvent;
                if (!admitted() || !ReferenceEquals(animator.AnimationCompleted, nextComplete)
                    || !ReferenceEquals(animator.AnimationEventTriggered, nextEvent))
                    throw new InvalidOperationException("Hero callback source slot readback differs.");
            };
        }
        private static Action PrepareControl(Data source, tk2dSpriteAnimator animator)
        {
            if (source.AnimatorValues.Length == 0 && source.ControllerValues.Length == 0 && source.Lifecycle.Length == 0) return () => { };
            var controller = Controller(); var fields = ControllerFields(); var animationFields = CanonicalPersistentSpriteContinuation.AnimatorFields();
            if (source.ControllerValues.Length != 17 || source.AnimatorValues.Length != 8 || source.Lifecycle.Length != 2)
                throw new InvalidDataException("Hero animation complete control directory differs.");
            for (var i = 0; i < fields.Length; i++) if (source.ControllerValues[i] == null || !fields[i].FieldType.IsInstanceOfType(source.ControllerValues[i]))
                throw new InvalidDataException("Hero animation controller source field type differs.");
            var indices = new[] { 0, 1, 2, 4, 5, 6 };
            for (var i = 0; i < indices.Length; i++) if (source.AnimatorValues[i] == null || !animationFields[indices[i]].FieldType.IsInstanceOfType(source.AnimatorValues[i]))
                throw new InvalidDataException("Hero animator source field type differs.");
            var library = animationFields[0].GetValue(animator) as tk2dSpriteAnimation;
            if (!ReferenceEquals(source.AnimatorValues[0], library) || library == null || library.clips == null || source.ClipIndex < -1 || source.ClipIndex >= library.clips.Length
                || !ReferenceEquals(source.ControllerValues[3], animator) || !ReferenceEquals(source.ControllerValues[4], HeroController.SilentInstance)
                || !ReferenceEquals(source.ControllerValues[5], HeroController.SilentInstance!.cState) || !(source.AnimatorValues[6] is int state) || !(source.AnimatorValues[7] is bool cached))
                throw new InvalidDataException("Hero animation original owner/library aliases differ.");
            var clip = source.ClipIndex < 0 ? null : library.clips[source.ClipIndex];
            if ((CanonicalPersistentSpriteAnimationDiagnostic.ClipHash(clip) ?? "") != source.ClipHash) throw new InvalidDataException("Hero animation original clip content differs.");
            var stateMask = Enum.GetValues(animationFields[9].FieldType).Cast<object>().Aggregate(0, (mask, value) => mask | Convert.ToInt32(value));
            if ((state & ~stateMask) != 0) throw new InvalidDataException("Hero animator source state has unregistered bits.");
            var sprite = animator.GetComponent<tk2dSprite>();
            void NativeGuard() {
                using var validation = LoadingEngineClockProbe.ReadOnlyCheckpointScope();
                if (!source.Lifecycle[0].SequenceEqual(BoundaryMonoLifecycle.Read(animator)) || !source.Lifecycle[1].SequenceEqual(BoundaryMonoLifecycle.Read(controller))
                    || source.GlobalState != Convert.ToInt32(typeof(tk2dSpriteAnimator).GetField("globalState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)))
                    throw new InvalidDataException("Hero animation native lifecycle/global state differs.");
            }
            NativeGuard();
            return () => {
                NativeGuard();
                for (var i = 0; i < fields.Length; i++) fields[i].SetValue(controller, source.ControllerValues[i]);
                for (var i = 1; i < indices.Length; i++) animationFields[indices[i]].SetValue(animator, source.AnimatorValues[i]);
                animationFields[3].SetValue(animator, clip); animationFields[9].SetValue(animator, Enum.ToObject(animationFields[9].FieldType, state));
                animationFields[10].SetValue(animator, cached ? sprite : null);
                for (var i = 0; i < fields.Length; i++) if (!SameControlValue(fields[i].GetValue(controller), source.ControllerValues[i]))
                    throw new InvalidOperationException("Hero animation controller source field readback differs.");
                for (var i = 0; i < indices.Length; i++) if (!SameControlValue(animationFields[indices[i]].GetValue(animator), source.AnimatorValues[i]))
                    throw new InvalidOperationException("Hero animator source field readback differs.");
                if (!ReferenceEquals(animationFields[3].GetValue(animator), clip) || Convert.ToInt32(animationFields[9].GetValue(animator)) != state
                    || !ReferenceEquals(animationFields[10].GetValue(animator), cached ? sprite : null)) throw new InvalidOperationException("Hero animator clip/state/sprite readback differs.");
                NativeGuard();
            };
        }
        private static bool SameControlValue(object? actual, object? expected)
        {
            if (actual is float a && expected is float b) return BitConverter.ToInt32(BitConverter.GetBytes(a), 0) == BitConverter.ToInt32(BitConverter.GetBytes(b), 0);
            return actual != null && actual.GetType().IsValueType ? actual.Equals(expected) : ReferenceEquals(actual, expected);
        }
    }
}
