using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Core.SceneCheckpoints;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // This is actual data, with explicit unsupported values, never a payload
    // inferred from a digest. Native/asset/R6 closure remains a separate gate.
    internal sealed class CanonicalCarryCapture
    {
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create();
        private readonly CanonicalCarryGraphCodec inputCodec = CanonicalCarryFieldRegistry.Create(inputDomain: true);
        private readonly CanonicalHeroAssetTemplates assetTemplates;
        private readonly CanonicalVisualFsmTemplates visualTemplates;
        internal CanonicalCarryCapture(CanonicalHeroAssetTemplates assetTemplates, CanonicalVisualFsmTemplates visualTemplates)
        { this.assetTemplates = assetTemplates; this.visualTemplates = visualTemplates; }
        internal EventAudit[] EventBindingAudit { get; private set; } = Array.Empty<EventAudit>();
        internal CanonicalEventCarryRoots.Audit? EventGlobalsAudit { get; private set; }
        internal CanonicalEventGraphContract.Audit? EventGraphAudit { get; private set; }
        internal CanonicalEventDataCarry.Audit? EventDataAudit { get; private set; }
        internal CanonicalVisualSenderBindings.Audit? CanonicalSenderAudit { get; private set; }
        internal object? ClockCarryAudit { get; private set; }
        internal CanonicalInputCarryRoots.Audit? InputCarryAudit { get; private set; }
        internal CanonicalCarryGraphData? InputGraph { get; private set; }
        internal int InputPreparedLeaseActions { get; private set; }
        internal bool InputPreparedCursor { get; private set; }
        internal bool InputPreparedManagerScalars { get; private set; }
        internal bool InputPreparedCallbacks { get; private set; }
        internal bool SceneLoadScalarsPrepared { get; private set; }
        internal bool MainGraphPrepared { get; private set; }
        internal bool GameplayPrepared { get; private set; }
        internal bool EventStaticSlotsGuarded { get; private set; }
        internal bool InvulnerabilityPrepared { get; private set; }
        internal bool SceneMappingPrepared { get; private set; }
        internal int SceneMappingCallbacks { get; private set; }
        internal bool MousePrepared { get; private set; }
        private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo RawFsm = Required(typeof(PlayMakerFSM), "fsm");
        private static FieldInfo Required(Type type, string name) => type.GetField(name, Instance)
            ?? throw new InvalidDataException("Carry field binding is absent: " + type.FullName + "/" + name);

        internal CanonicalCarryGraphData Capture(BoundaryObjectIdentity identities, CanonicalInputEpoch inputEpoch,
            HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter input, HollowKnightTAS.Runtime.Input.FullRunMouseBridge mouse,
            CanonicalSchedulerCarry scheduler, CheckpointHeroInvulnerability invulnerability)
        {
            InputPreparedLeaseActions = 0; InputPreparedCursor = false; InputPreparedManagerScalars = false; InputPreparedCallbacks = false;
            SceneLoadScalarsPrepared = false; MainGraphPrepared = false; GameplayPrepared = false; EventStaticSlotsGuarded = false;
            InvulnerabilityPrepared = false;
            SceneMappingPrepared = false; SceneMappingCallbacks = 0;
            MousePrepared = false;
            var hero = HeroController.SilentInstance ?? throw new InvalidDataException("Carry Hero is absent.");
            var manager = GameManager.instance ?? throw new InvalidDataException("Carry GameManager is absent.");
            var player = PlayerData.instance;
            if (player == null || manager.sceneData == null || !ReferenceEquals(hero.playerData, player)
                || !ReferenceEquals(manager.playerData, player)) throw new InvalidDataException("Carry data root aliases differ.");
            var roots = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                ["PlayerData"] = player, ["SceneData"] = manager.sceneData };
            if (hero.cState == null) throw new InvalidDataException("Carry Hero state root is absent.");
            roots.Add("D/HeroControllerStates", hero.cState);
            roots.Add(CanonicalGameplayCarry.Root, CanonicalGameplayCarry.Capture(identities));
            inputEpoch.Validate(inputEpoch.SourceNative);
            roots.Add("D/InputEpoch", inputEpoch.Copy());
            var clock = CanonicalClockCarry.Capture(inputEpoch.SourceNative);
            roots.Add("D/Clock", clock);
            scheduler.RequireEpoch(inputEpoch); roots.Add(CanonicalSchedulerCarry.Root, scheduler);
            roots.Add("D/HeroInvulnerability", invulnerability);
            LoadingSerialSceneProbe.AddCanonicalLoadInfo(roots);
            LoadingSerialSceneProbe.PrepareCanonicalLoadScalars((CanonicalSceneLoadCarry.Data)roots[CanonicalSceneLoadCarry.Root]);
            SceneLoadScalarsPrepared = true;
            var sceneMapping = new CanonicalSceneMappingBindings();
            roots.Add(CanonicalSceneMappingCarry.Root, sceneMapping.Data);
            if (sceneMapping.HasDetachedPlayer) roots.Add(CanonicalSceneMappingCarry.DetachedPlayerRoot, sceneMapping.OwnedPlayer);
            SceneMappingCallbacks = sceneMapping.Data.Items.Length;
            var observer = CheckpointClockObserverState.Decode(clock.Observer);
            ClockCarryAudit = new { nativeBytes = clock.Native.Length, randomBytes = clock.Random.Length, observerBytes = clock.Observer.Length,
                observer.UnityFrameObserved, observer.LastUnityFrameCount, observer.CaptureUnityFrameCount, observer.AdvanceSequence,
                observedReadback = true, restoreBindingQualified = false };
            var inputRoots = new SortedDictionary<string, object>(StringComparer.Ordinal);
            InputCarryAudit = CanonicalInputCarryRoots.Add(inputRoots);
            input.AddCanonicalSourceRoots(inputRoots);
            mouse.AddCanonicalRoots(inputRoots);
            foreach (var pair in CanonicalHeroFsmBindings.Snapshot(identities))
            {
                var fsm = (Fsm)RawFsm.GetValue(pair.Value);
                if (!CanonicalHeroFsmBindingRegistry.IsCanonicalVisual(pair.Key, fsm.Name)) roots.Add("HeroFSM/" + pair.Key, fsm);
            }
            var globals = typeof(PlayMakerGlobals).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            var initialized = typeof(PlayMakerGlobals).GetField("<Initialized>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            if (globals == null || !Equals(initialized, true)) throw new InvalidDataException("Original PlayMaker globals are not initialized.");
            roots.Add("PlayMakerGlobals/variables", Required(typeof(PlayMakerGlobals), "variables").GetValue(globals));
            EventGlobalsAudit = CanonicalEventCarryRoots.Add(roots, globals);
            var canonicalSenders = CanonicalVisualSenderBindings.Create(visualTemplates.CurrentResetOwners(), identities);
            CanonicalSenderAudit = canonicalSenders.Snapshot();
            EventDataAudit = CanonicalEventDataCarry.Add(roots, canonicalSenders.Bind);
            var guardEventSlots = CanonicalEventStaticSlots.PrepareGuard(roots);
            var inputBindings = CanonicalCarryRootBindings.Merge(InputCarryAudit.FixedBindings, inputRoots);
            var bindings = new ExternalBindings(identities, assetTemplates, canonicalSenders, inputBindings);
            var result = codec.Capture(roots, bindings.Bind);
            var mainBindings = new SortedDictionary<string, object>(InputCarryAudit.FixedBindings.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
            foreach (var pair in roots) mainBindings.Add("canonical-main-root/" + pair.Key, pair.Value);
            var inputExternal = new ExternalBindings(identities, assetTemplates, canonicalSenders, mainBindings);
            InputGraph = inputCodec.Capture(inputRoots, inputExternal.Bind);
            var sourceEpoch = inputEpoch.Copy();
            if (result.Unsupported.Length == 0)
                codec.PrepareApply(result, roots, bindings.ResolveCaptured, view => {
                    var source = CanonicalCarryContinuationContract.Read(view);
                    inputEpoch.RequireReadback(source.Epoch, inputEpoch.SourceNative);
                    CanonicalSchedulerRestorePlan.Prepare(source.Scheduler, scheduler);
                    LoadingSerialSceneProbe.PrepareCanonicalLoadScalars(source.SceneLoad);
                    CanonicalGameplayCarry.PrepareApply(CanonicalGameplayCarry.FromPrepared(view), identities);
                    GameplayPrepared = true;
                    CheckpointHeroInvulnerability.FromCanonicalPrepared(view, source.Scheduler).PrepareCanonicalPulseApply(hero);
                    InvulnerabilityPrepared = true;
                    var sourceMapping = CanonicalSceneMappingCarry.FromPrepared(view);
                    sceneMapping.PrepareApply(sourceMapping, sourceMapping.Items.Length == 0 ? null :
                        (PlayerData)view.Root(sourceMapping.Items[0].UsesCurrentPlayerData ? "PlayerData" : CanonicalSceneMappingCarry.DetachedPlayerRoot));
                    SceneMappingPrepared = true;
                    sourceEpoch = source.Epoch;
                    MainGraphPrepared = true;
                });
            var guardInputSlots = CanonicalInputStaticSlots.PrepareGuard(inputRoots, InputCarryAudit.FixedBindings);
            // Prepare against the same parked source only as a read-only
            // consumer audit. Never execute the returned application here.
            // Qualification records are appended below and remain blocking.
            if (InputGraph.Unsupported.Length == 0)
                inputCodec.PrepareApply(InputGraph, inputRoots, inputExternal.ResolveCaptured, view => {
                    InputPreparedLeaseActions = input.ValidateCanonicalSourceBindings(view, inputRoots);
                    InputPreparedCursor = input.PrepareCanonicalCursorAudit(view);
                    var managerSource = CanonicalInputManagerScalars.FromPrepared(view);
                    CanonicalInputManagerScalars.RequireEpoch(managerSource, sourceEpoch);
                    CanonicalInputManagerScalars.PrepareApply(managerSource);
                    InputPreparedManagerScalars = true;
                    CanonicalInputCallbackContract.RequireSame(CanonicalInputCallbackContract.FromPrepared(view), InputCarryAudit.CallbackData);
                    InputPreparedCallbacks = true;
                    mouse.PrepareCanonicalContinuation(CanonicalMouseCarry.FromPrepared(view));
                    MousePrepared = true;
                });
            guardInputSlots();
            guardEventSlots(); EventStaticSlotsGuarded = true;
            sceneMapping.Guard();
            InputGraph.Unsupported = InputGraph.Unsupported.Concat(InputCarryAudit.Unsupported).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            InputGraph.Unsupported = InputGraph.Unsupported.Concat(new[] { "R6/InputTAS-lease-and-original-bindings-rebinding-unqualified" }).ToArray();
            EventGraphAudit = CanonicalEventCarryRoots.ValidateGraph(result);
            EventBindingAudit = bindings.Events.ToArray();
            // Shared event objects use reference equality in PlayMaker. They
            // cannot be cloned as local action data or considered immutable
            // until the global table/cache and every consumer are closed.
            result.Unsupported = result.Unsupported.Concat(EventDataAudit.Unsupported)
                .Concat(InputCarryAudit.Unsupported)
                .Concat(new[] { "R6/FsmEvent-consumer-and-static-root-rebinding-unqualified",
                    "D/native-scheduler-timestamp-queue-and-wait-restoration-unqualified",
                    "D/HeroInvulnerability-original-iterator-and-native-wait-restoration-unqualified",
                    "D/SceneManager-entry-mapping-delegate-and-ghost-restoration-unqualified",
                    "D/SceneLoad-continuation-references-and-native-wait-rebinding-unqualified" })
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return result;
        }
        internal byte[] Encode(CanonicalCarryGraphData value) => codec.Encode(value);
        internal byte[] EncodeInput() => inputCodec.Encode(InputGraph ?? throw new InvalidDataException("Input carry domain was not captured."));

        private sealed class ExternalBindings
        {
            private readonly BoundaryObjectIdentity identities;
            private readonly Dictionary<Type, UnityEngine.Object[]> assets = new Dictionary<Type, UnityEngine.Object[]>();
            private readonly Dictionary<object, string> known = new Dictionary<object, string>(new References());
            private readonly CanonicalCarryReflectionBindings metadata;
            private readonly CanonicalHeroAssetTemplates assetTemplates;
            private readonly CanonicalVisualSenderBindings canonicalSenders;
            internal readonly List<EventAudit> Events = new List<EventAudit>();
            internal bool HasEvents { get; private set; }
            internal object ResolveCaptured(string binding)
            {
                var matches = known.Where(pair => pair.Value == binding).Select(pair => pair.Key).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Captured carry external binding is absent or ambiguous: " + binding);
                return matches[0];
            }
            internal ExternalBindings(BoundaryObjectIdentity identities, CanonicalHeroAssetTemplates assetTemplates, CanonicalVisualSenderBindings canonicalSenders,
                IReadOnlyDictionary<string, object> fixedInputBindings)
            {
                this.identities = identities;
                this.assetTemplates = assetTemplates;
                this.canonicalSenders = canonicalSenders;
                foreach (var pair in fixedInputBindings) {
                    // Multiple named roots can alias the same live object.
                    // Select the first canonical name; the root graph retains
                    // every original alias rather than allocating another copy.
                    if (!known.ContainsKey(pair.Value)) known.Add(pair.Value, pair.Key);
                }
                var action = typeof(CheckCanDreamWarpInScene);
                var checks = Required(action, "sceneCheckFunctions").GetValue(actionInstance()) as Dictionary<string, Func<bool>>
                    ?? throw new InvalidDataException("Original dream-warp action checks are absent.");
                // Dictionary.Keys lazily caches a KeyCollection in the live
                // dictionary. Enumerating the original key/value slots does
                // not create that cache or write a game field.
                if (!checks.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(new[] {
                    "GG_Atrium", "GG_Atrium_Roof", "GG_Blue_Room", "GG_Land_of_Storms", "GG_Unlock_Wastes", "GG_Workshop" }))
                    throw new InvalidDataException("Original dream-warp check binding differs.");
                var compiler = action.Assembly.GetType("CheckCanDreamWarpInScene+<>c", true)!;
                var callbackSlots = action.GetFields(BindingFlags.Static | BindingFlags.NonPublic).Concat(
                    compiler.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    .Where(field => field.FieldType == typeof(Func<bool>)).ToArray();
                metadata = new CanonicalCarryReflectionBindings(new[] { typeof(object).Assembly, action.Assembly,
                    typeof(Fsm).Assembly, typeof(GameObject).Assembly, typeof(AudioClip).Assembly, typeof(Rigidbody2D).Assembly,
                    typeof(RaycastHit).Assembly }, callbackSlots);
            }
            private static CheckCanDreamWarpInScene actionInstance()
            {
                // Raw fields only: accessing state.Actions can instantiate
                // previously unloaded actions and is forbidden in this reader.
                foreach (var component in HeroController.SilentInstance.GetComponentsInChildren<PlayMakerFSM>(true))
                {
                    var fsm = (Fsm)RawFsm.GetValue(component);
                    foreach (var state in (FsmState[])Required(typeof(Fsm), "states").GetValue(fsm))
                    {
                        var actions = Required(typeof(FsmState), "actions").GetValue(state) as FsmStateAction[];
                        var result = actions?.OfType<CheckCanDreamWarpInScene>().FirstOrDefault();
                        if (result != null) return result;
                    }
                }
                throw new InvalidDataException("Original initialized dream-warp action is absent.");
            }
            internal string? Bind(object value)
            {
                if (known.TryGetValue(value, out var bound)) return bound;
                var canonicalSlot = canonicalSenders.Bind(value);
                if (canonicalSlot != null) { known.Add(value, canonicalSlot); return canonicalSlot; }
                if (value is UnityEngine.Object native)
                {
                    if (native == null) throw new InvalidDataException("destroyed-Unity-wrapper-needs-rebinding");
                    var key = identities.Get(native);
                    if (key.StartsWith("asset:", StringComparison.Ordinal))
                    {
                        var originalOrigin = assetTemplates.Bind(native);
                        if (originalOrigin != null) { known.Add(value, originalOrigin); return originalOrigin; }
                        var type = native.GetType();
                        if (!assets.TryGetValue(type, out var candidates))
                        {
                            candidates = Resources.FindObjectsOfTypeAll(type).Where(owner => owner != null && owner.GetType() == type).ToArray();
                            if (candidates.Length > 100000) throw new InvalidDataException("asset-binding-candidate-budget");
                            assets.Add(type, candidates);
                        }
                        var same = candidates.Where(owner => owner.name == native.name).ToArray();
                        if (same.Length != 1 || same[0].GetInstanceID() != native.GetInstanceID())
                            throw new InvalidDataException("ambiguous-asset-binding:" + key);
                    }
                    known.Add(value, key); return key;
                }
                if (value is Fsm fsm)
                {
                    var owner = Required(typeof(Fsm), "owner").GetValue(fsm) as PlayMakerFSM;
                    if (owner == null || !ReferenceEquals(RawFsm.GetValue(owner), fsm))
                        throw new InvalidDataException("foreign-FSM-needs-explicit-owner-binding");
                    var key = "FSM/" + identities.Get(owner);
                    known.Add(value, key); return key;
                }
                if (value is FsmEvent fsmEvent)
                {
                    var name = (string)Required(typeof(FsmEvent), "name").GetValue(fsmEvent);
                    var table = typeof(FsmEvent).GetField("_eventLookup", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)
                        as Dictionary<string, FsmEvent>;
                    var cached = name != null && table != null && table.TryGetValue(name, out var found) ? found : null;
                    Events.Add(new EventAudit { Name = name, IsGlobal = (bool)Required(typeof(FsmEvent), "isGlobal").GetValue(fsmEvent),
                        IsSystemEvent = (bool)Required(typeof(FsmEvent), "isSystemEvent").GetValue(fsmEvent),
                        Path = (string?)Required(typeof(FsmEvent), "<Path>k__BackingField").GetValue(fsmEvent),
                        CachedNameExists = cached != null, SameCachedReference = ReferenceEquals(cached, fsmEvent) });
                    HasEvents = true;
                    if (cached == null && CanonicalCarryFieldRegistry.IsLocalEmptyEvent(fsmEvent)) return null;
                    if (cached == null || !ReferenceEquals(cached, fsmEvent))
                        throw new InvalidDataException("unregistered-global-FSM-event");
                    var key = "FsmEvent/" + name;
                    known.Add(value, key); return key;
                }
                var reflective = metadata.Bind(value);
                if (reflective != null) { known.Add(value, reflective); return reflective; }
                return null;
            }
        }
        internal sealed class EventAudit
        {
            public string? Name { get; set; }
            public bool IsGlobal { get; set; }
            public bool IsSystemEvent { get; set; }
            public string? Path { get; set; }
            public bool CachedNameExists { get; set; }
            public bool SameCachedReference { get; set; }
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
