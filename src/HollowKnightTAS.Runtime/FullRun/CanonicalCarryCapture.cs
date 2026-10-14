using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Core.Movie;
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
        private readonly CanonicalHudJournalPolicy journal;
        internal CanonicalCarryCapture(CanonicalHeroAssetTemplates assetTemplates, CanonicalVisualFsmTemplates visualTemplates, CanonicalHudJournalPolicy journal)
        { this.assetTemplates = assetTemplates; this.visualTemplates = visualTemplates; this.journal = journal; }
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
        internal bool DeviceManagersPrepared { get; private set; }
        internal int DeviceManagerCount { get; private set; }
        internal bool GraphPairPrepared { get; private set; }
        internal bool SceneLoadCallbacksPrepared { get; private set; }
        internal string[] BlockingQualifications { get; private set; } = Array.Empty<string>();
        private Func<CanonicalCarryGraphData, CanonicalCarryGraphData, MovieV2Document, Func<bool>, Func<CanonicalSchedulerCarry, Action>, Action>? prepareTargetRestore;
        internal CanonicalInputEpoch? PreparedRestoreEpoch { get; private set; }
        private bool targetRestorePrepared;
        private CanonicalActualDExperiment? actualSourceValidation;
        internal Action PrepareActualRestore(CanonicalCarryGraphData main, CanonicalCarryGraphData inputGraph,
            MovieV2Document nextMovie, Func<bool> admitted, Func<CanonicalSchedulerCarry, Action> prepareScheduler)
            => (prepareTargetRestore ?? throw new InvalidOperationException("Target D owner context has not been captured."))(
                main, inputGraph, nextMovie, admitted, prepareScheduler);
        private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo RawFsm = Required(typeof(PlayMakerFSM), "fsm");
        private static FieldInfo Required(Type type, string name) => type.GetField(name, Instance)
            ?? throw new InvalidDataException("Carry field binding is absent: " + type.FullName + "/" + name);
        internal CanonicalDataPreservationGuard PrepareDataPreservationGuard()
        {
            IReadOnlyDictionary<string, object> Roots()
            {
                var hero = HeroController.SilentInstance ?? throw new InvalidDataException("Protected Hero is absent.");
                var manager = GameManager.instance ?? throw new InvalidDataException("Protected GameManager is absent.");
                var player = PlayerData.instance;
                if (player == null || hero.cState == null || manager.sceneData == null || !ReferenceEquals(hero.playerData, player)
                    || !ReferenceEquals(manager.playerData, player)) throw new InvalidDataException("Protected gameplay data aliases differ.");
                return new SortedDictionary<string, object>(StringComparer.Ordinal) {
                    ["PlayerData"] = player, ["SceneData"] = manager.sceneData, ["D/HeroControllerStates"] = hero.cState };
            }
            return new CanonicalDataPreservationGuard(codec, Roots, CanonicalGameplayCarry.CaptureHeroPreservationScalars, CanonicalGameplayCarry.HeroPreservationOwners);
        }

        internal CanonicalCarryGraphData Capture(BoundaryObjectIdentity identities, CanonicalInputEpoch inputEpoch,
            HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter input, HollowKnightTAS.Runtime.Input.FullRunMouseBridge mouse,
            CanonicalSchedulerCarry scheduler, CheckpointHeroInvulnerability invulnerability, bool targetDirectoryOnly = false,
            PlayerData? originalBootstrapPlayer = null, bool sourceRequiresDetachedPlayer = false,
            GameManager.EnterSceneEvent? retainedTargetTitleCallback = null)
        {
            InputPreparedLeaseActions = 0; InputPreparedCursor = false; InputPreparedManagerScalars = false; InputPreparedCallbacks = false;
            SceneLoadScalarsPrepared = false; MainGraphPrepared = false; GameplayPrepared = false; EventStaticSlotsGuarded = false;
            InvulnerabilityPrepared = false;
            SceneMappingPrepared = false; SceneMappingCallbacks = 0;
            MousePrepared = false;
            DeviceManagersPrepared = false; DeviceManagerCount = 0;
            GraphPairPrepared = false;
            SceneLoadCallbacksPrepared = false;
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
            var loadCallbacks = LoadingSerialSceneProbe.ReadCanonicalCallbackBindings();
            roots.Add(CanonicalSceneLoadCallbacks.Root, loadCallbacks.Data);
            var sceneMapping = new CanonicalSceneMappingBindings(retainedTargetTitleCallback);
            roots.Add(CanonicalSceneMappingCarry.Root, sceneMapping.Data);
            if (sceneMapping.HasDetachedPlayer) roots.Add(CanonicalSceneMappingCarry.DetachedPlayerRoot, sceneMapping.OwnedPlayer);
            else if (targetDirectoryOnly && sourceRequiresDetachedPlayer) {
                if (originalBootstrapPlayer == null || originalBootstrapPlayer.GetType() != typeof(PlayerData)
                    || ReferenceEquals(originalBootstrapPlayer, player))
                    throw new InvalidDataException("Source detached PlayerData requires the target's actual retained title instance.");
                roots.Add(CanonicalSceneMappingCarry.DetachedPlayerRoot, originalBootstrapPlayer);
            }
            SceneMappingCallbacks = sceneMapping.Data.Items.Length;
            var observer = CheckpointClockObserverState.Decode(clock.Observer);
            ClockCarryAudit = new { nativeBytes = clock.Native.Length, randomBytes = clock.Random.Length, observerBytes = clock.Observer.Length,
                observer.UnityFrameObserved, observer.LastUnityFrameCount, observer.CaptureUnityFrameCount, observer.AdvanceSequence,
                observedReadback = true, restoreBindingQualified = false };
            var inputRoots = new SortedDictionary<string, object>(StringComparer.Ordinal);
            InputCarryAudit = CanonicalInputCarryRoots.Add(inputRoots);
            input.AddCanonicalSourceRoots(inputRoots);
            mouse.AddCanonicalRoots(inputRoots);
            var deviceManagers = new CanonicalDeviceManagerBindings();
            inputRoots.Add(CanonicalDeviceManagerCarry.Root, deviceManagers.Data);
            DeviceManagerCount = deviceManagers.Data.Managers.Length;
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
            CanonicalCarryGraphData Empty() => new CanonicalCarryGraphData { Roots = Array.Empty<CanonicalCarryGraphData.Root>(),
                Nodes = Array.Empty<CanonicalCarryGraphData.Node>(), Externals = Array.Empty<CanonicalCarryGraphData.External>(), Unsupported = Array.Empty<string>() };
            var result = targetDirectoryOnly ? Empty() : codec.Capture(roots, bindings.Bind);
            var mainBindings = new SortedDictionary<string, object>(InputCarryAudit.FixedBindings.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
            foreach (var pair in roots) mainBindings.Add("canonical-main-root/" + pair.Key, pair.Value);
            var inputExternal = new ExternalBindings(identities, assetTemplates, canonicalSenders, mainBindings);
            InputGraph = targetDirectoryOnly ? Empty() : inputCodec.Capture(inputRoots, inputExternal.Bind);
            var sourceEpoch = inputEpoch.Copy();
            CanonicalPreparedWritePlan? mainPlan = null, inputPlan = null;
            if (!targetDirectoryOnly && result.Unsupported.Length == 0)
                mainPlan = codec.PrepareGraph(result, roots, bindings.ResolveCaptured, view => {
                    var source = CanonicalCarryContinuationContract.Read(view);
                    inputEpoch.RequireReadback(source.Epoch, inputEpoch.SourceNative);
                    CanonicalSchedulerRestorePlan.Prepare(source.Scheduler, scheduler);
                    LoadingSerialSceneProbe.PrepareCanonicalLoadScalars(source.SceneLoad);
                    loadCallbacks.RequireSource(CanonicalSceneLoadCallbacks.FromPrepared(view), source.SceneLoad);
                    SceneLoadCallbacksPrepared = true;
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
            if (!targetDirectoryOnly && InputGraph.Unsupported.Length == 0)
                inputPlan = inputCodec.PrepareGraph(InputGraph, inputRoots, inputExternal.ResolveCaptured, view => {
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
                    deviceManagers.PrepareApply(CanonicalDeviceManagerCarry.FromPrepared(view));
                    DeviceManagersPrepared = true;
                });
            guardInputSlots();
            guardEventSlots(); EventStaticSlotsGuarded = true;
            sceneMapping.Guard();
            deviceManagers.Guard();
            loadCallbacks.Guard();
            // The closure belongs exclusively to this target process and its
            // actual parked owners. Source packets contain data, never owners.
            prepareTargetRestore = (sourceMain, sourceInput, nextMovie, admitted, prepareScheduler) => {
                if (targetRestorePrepared || admitted == null || prepareScheduler == null || !admitted())
                    throw new InvalidOperationException("Actual D preparation requires its parked target admission.");
                targetRestorePrepared = true;
                var domainActions = new List<Action>();
                var sourceChecks = new List<Action>();
                var targetMainRoots = SelectTargetRoots(sourceMain, roots, allowRetiredEventCacheEntries: true);
                var targetInputRoots = SelectTargetRoots(sourceInput, inputRoots, allowRetiredEventCacheEntries: false);
                CanonicalCarryContinuationContract.Prepared? continuation = null;
                CanonicalEventCarryRoots.ValidateGraph(sourceMain);
                var main = codec.PrepareOwnedGraph(sourceMain, targetMainRoots, bindings.ResolveTarget, view => {
                    continuation = CanonicalCarryContinuationContract.Read(view);
                    PreparedRestoreEpoch = continuation.Epoch.Copy();
                    var source = continuation;
                    if (actualSourceValidation != null && (source.Epoch.SourceNative != actualSourceValidation.NativeFrame
                        || !source.Clock.Native.SequenceEqual(actualSourceValidation.Clock.Clock.Native)
                        || !source.Clock.Random.SequenceEqual(actualSourceValidation.Clock.Clock.Random)
                        || !source.Clock.Observer.SequenceEqual(actualSourceValidation.Clock.Clock.Observer)))
                        throw new InvalidDataException("Actual D graph clock/epoch disagrees with its sealed source envelope.");
                    domainActions.Add(LoadingSerialSceneProbe.PrepareCanonicalLoadScalars(source.SceneLoad));
                    var callbacks = CanonicalSceneLoadCallbacks.FromPrepared(view);
                    loadCallbacks.RequireSource(callbacks, source.SceneLoad);
                    sourceChecks.Add(() => loadCallbacks.RequireAppliedSource(callbacks));
                    var gameplay = CanonicalGameplayCarry.FromPrepared(view);
                    domainActions.Add(CanonicalGameplayCarry.PrepareApply(gameplay, identities));
                    byte[] GameplayBytes(CanonicalGameplayCarry.Data data) => codec.Encode(codec.Capture(
                        new SortedDictionary<string, object>(StringComparer.Ordinal) { [CanonicalGameplayCarry.Root] = data }, _ => null));
                    var gameplayBits = GameplayBytes(gameplay);
                    sourceChecks.Add(() => {
                        if (!gameplayBits.SequenceEqual(GameplayBytes(CanonicalGameplayCarry.Capture(identities))))
                            throw new InvalidOperationException("Actual gameplay source raw readback differs.");
                    });
                    domainActions.Add(CheckpointHeroInvulnerability.FromCanonicalPrepared(view, source.Scheduler).PrepareCanonicalPulseApply(hero));
                    var mapping = CanonicalSceneMappingCarry.FromPrepared(view);
                    var mappingPlayer = mapping.Items.Length == 0 ? null :
                        (PlayerData)view.Root(mapping.Items[0].UsesCurrentPlayerData ? "PlayerData" : CanonicalSceneMappingCarry.DetachedPlayerRoot);
                    domainActions.Add(sceneMapping.PrepareApply(mapping, mappingPlayer));
                    sourceChecks.Add(() => sceneMapping.RequireAppliedSource(mapping, mappingPlayer));
                    domainActions.Add(journal.PrepareSourceDependencies(admitted));
                    domainActions.Add(prepareScheduler(source.Scheduler));
                });
                var sdk = inputCodec.PrepareOwnedGraph(sourceInput, targetInputRoots, inputExternal.ResolveTarget, view => {
                    if (continuation == null) throw new InvalidOperationException("Actual input graph has no source main continuation.");
                    input.ValidateCanonicalSourceBindings(view, targetInputRoots);
                    var state = CanonicalInputAdapterState.FromPrepared(view);
                    if (actualSourceValidation != null && state.MovieFrame != actualSourceValidation.MovieFrame)
                        throw new InvalidDataException("Actual D input graph cursor disagrees with its sealed source envelope.");
                    domainActions.Add(input.PrepareCanonicalContinuation(state, nextMovie));
                    var managerSource = CanonicalInputManagerScalars.FromPrepared(view);
                    CanonicalInputManagerScalars.RequireEpoch(managerSource, continuation.Epoch);
                    domainActions.Add(CanonicalInputManagerScalars.PrepareApply(managerSource));
                    CanonicalInputCallbackContract.RequireSame(CanonicalInputCallbackContract.FromPrepared(view), InputCarryAudit!.CallbackData);
                    domainActions.Add(mouse.PrepareCanonicalContinuation(CanonicalMouseCarry.FromPrepared(view)));
                    domainActions.Add(deviceManagers.PrepareApply(CanonicalDeviceManagerCarry.FromPrepared(view)));
                });
                var actual = new CanonicalCarryPairPreparation(main, sdk, () => {
                    guardInputSlots(); guardEventSlots(); sceneMapping.Guard(); deviceManagers.Guard(); loadCallbacks.Guard();
                }, admitted);
                return () => actual.Apply(() => {
                    foreach (var action in domainActions) {
                        if (!admitted()) throw new InvalidOperationException("Actual D domain lost paused admission.");
                        action();
                    }
                }, () => { foreach (var check in sourceChecks) check(); });
            };
            if (mainPlan != null && inputPlan != null) {
                var pair = new CanonicalCarryPairPreparation(mainPlan, inputPlan, () => {
                    guardInputSlots(); guardEventSlots(); sceneMapping.Guard(); deviceManagers.Guard(); loadCallbacks.Guard();
                }, () => false); // Source-only audit has no restore admission.
                pair.Guard(); GraphPairPrepared = true;
            }
            InputGraph.Unsupported = InputGraph.Unsupported.Concat(InputCarryAudit.Unsupported).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!targetDirectoryOnly) EventGraphAudit = CanonicalEventCarryRoots.ValidateGraph(result);
            EventBindingAudit = bindings.Events.ToArray();
            // Shared event objects use reference equality in PlayMaker. They
            // cannot be cloned as local action data or considered immutable
            // until the global table/cache and every consumer are closed.
            result.Unsupported = result.Unsupported.Concat(EventDataAudit.Unsupported)
                .Concat(InputCarryAudit.Unsupported).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            BlockingQualifications = new[] { "R6/InputTAS-lease-and-original-bindings-rebinding-unqualified",
                    "R6/FsmEvent-consumer-and-static-root-rebinding-unqualified",
                    "D/native-scheduler-timestamp-queue-and-wait-restoration-unqualified",
                    "D/HeroInvulnerability-original-iterator-and-native-wait-restoration-unqualified",
                    "D/SceneManager-entry-mapping-delegate-and-ghost-restoration-unqualified",
                    "D/SceneLoad-continuation-references-and-native-wait-rebinding-unqualified" }
                .Concat(InputCarryAudit.BlockingQualifications).ToArray();
            return result;
        }
        private static SortedDictionary<string, object> SelectTargetRoots(CanonicalCarryGraphData source,
            IReadOnlyDictionary<string, object> directory, bool allowRetiredEventCacheEntries)
        {
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var root in source.Roots) {
                if (!directory.TryGetValue(root.Binding, out var owner) || result.ContainsKey(root.Binding))
                    throw new InvalidDataException("Source D root has no unique actual target owner: " + root.Binding);
                result.Add(root.Binding, owner);
            }
            foreach (var extra in directory.Where(pair => !result.ContainsKey(pair.Key)))
                if (!allowRetiredEventCacheEntries || !extra.Key.StartsWith("R6/FsmEvent-cache/", StringComparison.Ordinal)
                    || !(extra.Value is FsmEvent))
                    throw new InvalidDataException("Actual target has an unregistered extra D root: " + extra.Key);
            // Extra history-only cached events are detached by writing the
            // complete source lookup/list backing fields. Never append them to
            // the source packet or allocate replacement source event owners.
            return result;
        }
        internal byte[] Encode(CanonicalCarryGraphData value) => codec.Encode(value);
        internal Action PrepareActualRestore(CanonicalActualDExperiment source, MovieV2Document nextMovie,
            Func<bool> admitted, Func<CanonicalSchedulerCarry, Action> prepareScheduler)
        {
            if (actualSourceValidation != null) throw new InvalidOperationException("Actual D source envelope was already admitted.");
            actualSourceValidation = source;
            return PrepareActualRestore(codec.Decode(source.Main), inputCodec.Decode(source.Input), nextMovie, admitted, prepareScheduler);
        }
        internal byte[] EncodeInput() => inputCodec.Encode(InputGraph ?? throw new InvalidDataException("Input carry domain was not captured."));

        private sealed class ExternalBindings
        {
            private readonly BoundaryObjectIdentity identities;
            private readonly Dictionary<Type, UnityEngine.Object[]> assets = new Dictionary<Type, UnityEngine.Object[]>();
            private readonly Dictionary<object, string> known = new Dictionary<object, string>(new References());
            private readonly Dictionary<string, object> nativeDirectory = new Dictionary<string, object>(StringComparer.Ordinal);
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
            internal object ResolveTarget(string binding)
            {
                var cached = known.Where(pair => pair.Value == binding).Select(pair => pair.Key).ToArray();
                if (cached.Length > 1) throw new InvalidDataException("Target external directory is ambiguous: " + binding);
                if (cached.Length == 1) return cached[0];
                object value;
                if (binding.StartsWith("HeroAssetOrigin/v1/", StringComparison.Ordinal)) value = assetTemplates.Resolve(binding);
                else if (canonicalSenders.Snapshot().Slots.Any(slot => slot.Key == binding)) value = canonicalSenders.Resolve(binding);
                else if (binding.StartsWith("metadata-", StringComparison.Ordinal) || binding.StartsWith("original-callback|", StringComparison.Ordinal)
                    || binding == "runtime-comparer|string-default") value = metadata.Resolve(binding);
                else {
                    if (!nativeDirectory.TryGetValue(binding, out value!))
                        throw new InvalidDataException("Target native/FSM external has no unique registered identity: " + binding);
                }
                if (value == null) throw new InvalidDataException("Target external is absent: " + binding);
                known.Add(value, binding); return value;
            }
            internal ExternalBindings(BoundaryObjectIdentity identities, CanonicalHeroAssetTemplates assetTemplates, CanonicalVisualSenderBindings canonicalSenders,
                IReadOnlyDictionary<string, object> fixedInputBindings)
            {
                this.identities = identities;
                this.assetTemplates = assetTemplates;
                this.canonicalSenders = canonicalSenders;
                void Register(string key, object owner) {
                    if (nativeDirectory.Count >= CanonicalCarryGraphCodec.MaximumNodes || nativeDirectory.ContainsKey(key))
                        throw new InvalidDataException("Target native owner directory is ambiguous or oversized: " + key);
                    nativeDirectory.Add(key, owner);
                }
                foreach (var go in identities.Objects) {
                    Register(identities.Get(go), go);
                    foreach (var component in go.GetComponents<Component>()) {
                        if (component == null) continue;
                        Register(identities.Get(component), component);
                        if (component is PlayMakerFSM fsm) Register("FSM/" + identities.Get(fsm), RawFsm.GetValue(fsm));
                    }
                }
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
