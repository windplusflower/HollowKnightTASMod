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
        private readonly Action<CanonicalSchedulerCarry, Func<bool>> restoreHudSourceDependencies;
        private readonly Action<CanonicalSchedulerCarry, Func<bool>, Func<IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>>, Action<Action>, Action, Action> restoreGeoSourceDependencies;
        private readonly Action<CanonicalSchedulerCarry, Func<bool>, Func<IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>>, Action<Action>, Action, Action> restoreMarkerSourceDependencies;
        private readonly CanonicalGeoSourceDomain geoDomain = new CanonicalGeoSourceDomain();
        private readonly CanonicalMapSourceDomain mapDomain = new CanonicalMapSourceDomain();
        private readonly CanonicalMarkerSourceDomain markerDomain = new CanonicalMarkerSourceDomain();
        private readonly Func<LoadingGeoIncomingReferences> consumeMapKeyIncoming;
        private readonly Func<LoadingGeoIncomingReferences> consumeMapKeyParentIncoming, snapshotMapKeyParentIncoming;
        private readonly Action<Action, Action<Tuple<Action, Action>>> restoreMapKeyParentDependencies;
        private readonly Action<Action, LoadingGeoIncomingReferences, Tuple<Action, Action>, Action<Action>> normalizeMapKey;
        private readonly CanonicalMapKeyParentSourceDomain mapKeyParentDomain = new CanonicalMapKeyParentSourceDomain();
        internal byte[] MapKeyParentGraph { get; private set; } = Array.Empty<byte>();
        private readonly CanonicalMapKeyTextSourceDomain mapKeyDomain = new CanonicalMapKeyTextSourceDomain();
        internal byte[] MapKeyTextGraph { get; private set; } = Array.Empty<byte>();
        internal byte[] MarkerGraph { get; private set; } = Array.Empty<byte>();
        internal byte[] MapGraph { get; private set; } = Array.Empty<byte>();
        internal byte[] GeoGraph { get; private set; } = Array.Empty<byte>();
        internal CanonicalCarryCapture(CanonicalHeroAssetTemplates assetTemplates, CanonicalVisualFsmTemplates visualTemplates, CanonicalHudJournalPolicy journal,
            Action<CanonicalSchedulerCarry, Func<bool>> restoreHudSourceDependencies,
            Action<CanonicalSchedulerCarry, Func<bool>, Func<IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>>, Action<Action>, Action, Action> restoreGeoSourceDependencies,
            Action<CanonicalSchedulerCarry, Func<bool>, Func<IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>, IReadOnlyDictionary<string, object>>, Action<Action>, Action, Action> restoreMarkerSourceDependencies, Func<LoadingGeoIncomingReferences> consumeMapKeyIncoming,
            Func<LoadingGeoIncomingReferences> consumeMapKeyParentIncoming, Func<LoadingGeoIncomingReferences> snapshotMapKeyParentIncoming,
            Action<Action, Action<Tuple<Action, Action>>> restoreMapKeyParentDependencies,
            Action<Action, LoadingGeoIncomingReferences, Tuple<Action, Action>, Action<Action>> normalizeMapKey)
        { this.assetTemplates = assetTemplates; this.visualTemplates = visualTemplates; this.journal = journal;
            this.restoreHudSourceDependencies = restoreHudSourceDependencies; this.restoreGeoSourceDependencies = restoreGeoSourceDependencies; this.restoreMarkerSourceDependencies = restoreMarkerSourceDependencies; this.consumeMapKeyIncoming = consumeMapKeyIncoming;
            this.consumeMapKeyParentIncoming = consumeMapKeyParentIncoming; this.snapshotMapKeyParentIncoming = snapshotMapKeyParentIncoming;
            this.restoreMapKeyParentDependencies = restoreMapKeyParentDependencies; this.normalizeMapKey = normalizeMapKey; }
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
        internal string ActualTargetStage { get; set; } = "not-prepared";
        private bool targetRestorePrepared;
        private CanonicalActualDExperiment? actualSourceValidation;
        private CanonicalActualDExperiment? decodedActualEnvelope;
        private CanonicalCarryGraphData? decodedActualMain;
        private string? decodedActualMainHash;
        internal object? HeroAnimationCallbackAudit { get; private set; }
        internal CanonicalCarryGraphData DecodeActualMainForOwnerContext(CanonicalActualDExperiment source)
        {
            if (decodedActualEnvelope != null || actualSourceValidation != null)
                throw new InvalidOperationException("Actual source main graph was already decoded/admitted.");
            decodedActualEnvelope = source;
            decodedActualMainHash = HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(source.Main);
            decodedActualMain = codec.Decode(source.Main);
            return decodedActualMain;
        }
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
            var heroAnimationCallbacks = targetDirectoryOnly ? new CanonicalHeroAnimationCallbacks.Data()
                : CanonicalHeroAnimationCallbacks.Capture(identities);
            roots.Add(CanonicalHeroAnimationCallbacks.Root, heroAnimationCallbacks);
            var heroTriggerCallbacks = targetDirectoryOnly ? new CanonicalHeroTriggerCallbacks.Data()
                : CanonicalHeroTriggerCallbacks.Capture(identities);
            roots.Add(CanonicalHeroTriggerCallbacks.Root, heroTriggerCallbacks);
            HeroAnimationCallbackAudit = new { diagnosticOnly = true, sourceCaptured = !targetDirectoryOnly,
                completeCount = heroAnimationCallbacks.Complete.Length, eventCount = heroAnimationCallbacks.Event.Length,
                triggerSlotCount = heroTriggerCallbacks.Slots.Length, triggerInvocationCount = heroTriggerCallbacks.Slots.Sum(slot => slot.Items.Length),
                completeStateCoverage = false };
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
            var captureEpoch = new object();
            var bindings = new ExternalBindings(identities, assetTemplates, canonicalSenders, inputBindings, captureEpoch);
            CanonicalCarryGraphData Empty() => new CanonicalCarryGraphData { Roots = Array.Empty<CanonicalCarryGraphData.Root>(),
                Nodes = Array.Empty<CanonicalCarryGraphData.Node>(), Externals = Array.Empty<CanonicalCarryGraphData.External>(), Unsupported = Array.Empty<string>() };
            var mainNodes = new Dictionary<object, int>(new References());
            var result = targetDirectoryOnly ? Empty() : codec.Capture(roots, bindings.Bind, (owner, id) => mainNodes.Add(owner, id));
            GeoGraph = targetDirectoryOnly ? Array.Empty<byte>() : geoDomain.Capture(GameCameras.instance.geoCounter,
                CanonicalGeoColdTemplate.Directory(GameCameras.instance.geoCounter.gameObject), mainNodes, bindings.Bind, bindings.AuditGeoAssets);
            MapGraph = targetDirectoryOnly ? Array.Empty<byte>() : mapDomain.Capture(mainNodes, bindings.Bind, bindings.AuditGeoAssets);
            MapKeyTextGraph = targetDirectoryOnly ? Array.Empty<byte>() : mapKeyDomain.Capture(CanonicalMapKeyTextDirectory.Current(), mainNodes, bindings.Bind, bindings.AuditGeoAssets);
            MapKeyParentGraph = targetDirectoryOnly ? Array.Empty<byte>() : mapKeyParentDomain.Capture(CanonicalMapKeyTextDirectory.Current(),
                snapshotMapKeyParentIncoming(), mainNodes, bindings.Bind, bindings.AuditGeoAssets);
            MarkerGraph = targetDirectoryOnly ? Array.Empty<byte>() : markerDomain.Capture(CanonicalMarkerDirectory.Current(), mainNodes, bindings.Bind, bindings.AuditGeoAssets);
            var mainBindings = new SortedDictionary<string, object>(InputCarryAudit.FixedBindings.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
            foreach (var pair in roots) mainBindings.Add("canonical-main-root/" + pair.Key, pair.Value);
            var inputExternal = new ExternalBindings(identities, assetTemplates, canonicalSenders, mainBindings, captureEpoch, bindings.ownerDirectory);
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
                    CanonicalHeroAnimationCallbacks.PrepareApply(CanonicalHeroAnimationCallbacks.FromPrepared(view), identities, () => true, view);
                    CanonicalHeroTriggerCallbacks.PrepareApply(CanonicalHeroTriggerCallbacks.FromPrepared(view), identities, () => true, view);
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
                var prepareTimer = System.Diagnostics.Stopwatch.StartNew();
                var targetMainRoots = SelectTargetRoots(sourceMain, roots, allowRetiredEventCacheEntries: true);
                var targetInputRoots = SelectTargetRoots(sourceInput, inputRoots, allowRetiredEventCacheEntries: false);
                CanonicalCarryContinuationContract.Prepared? continuation = null;
                CanonicalCarryGraphCodec.PreparedView? mainView = null;
                CanonicalCarryGraphCodec.ExternalRebinding? mainRebinding = null, sdkRebinding = null;
                CanonicalEventCarryRoots.ValidateGraph(sourceMain);
                ActualTargetStage = "prepare-main";
                var main = codec.PrepareOwnedGraph(sourceMain, targetMainRoots, bindings.ResolveTarget, view => {
                    mainView = view;
                    continuation = CanonicalCarryContinuationContract.Read(view);
                    PreparedRestoreEpoch = continuation.Epoch.Copy();
                    var source = continuation;
                    if (actualSourceValidation != null && (source.Epoch.SourceNative != actualSourceValidation.NativeFrame
                        || !source.Clock.Native.SequenceEqual(actualSourceValidation.Clock.Clock.Native)
                        || !source.Clock.Random.SequenceEqual(actualSourceValidation.Clock.Clock.Random)
                        || !source.Clock.Observer.SequenceEqual(actualSourceValidation.Clock.Clock.Observer)))
                        throw new InvalidDataException("Actual D graph clock/epoch disagrees with its sealed source envelope.");
                    domainActions.Add(LoadingSerialSceneProbe.PrepareCanonicalLoadScalars(source.SceneLoad));
                    domainActions.Add(() => restoreHudSourceDependencies(source.Scheduler, admitted));
                    var callbacks = CanonicalSceneLoadCallbacks.FromPrepared(view);
                    loadCallbacks.RequireSource(callbacks, source.SceneLoad);
                    sourceChecks.Add(() => loadCallbacks.RequireAppliedSource(callbacks));
                    var gameplay = CanonicalGameplayCarry.FromPrepared(view);
                    domainActions.Add(CanonicalGameplayCarry.PrepareApply(gameplay, identities));
                    var heroCallbacks = CanonicalHeroAnimationCallbacks.FromPrepared(view);
                    domainActions.Add(CanonicalHeroAnimationCallbacks.PrepareApply(heroCallbacks, identities, admitted, view));
                    var triggerCallbacks = CanonicalHeroTriggerCallbacks.FromPrepared(view);
                    domainActions.Add(CanonicalHeroTriggerCallbacks.PrepareApply(triggerCallbacks, identities, admitted, view));
                    byte[] TriggerBytes(CanonicalHeroTriggerCallbacks.Data data) => codec.Encode(codec.Capture(
                        new SortedDictionary<string, object>(StringComparer.Ordinal) { [CanonicalHeroTriggerCallbacks.Root] = data }, _ => null));
                    var triggerBits = TriggerBytes(triggerCallbacks);
                    sourceChecks.Add(() => {
                        if (!triggerBits.SequenceEqual(TriggerBytes(CanonicalHeroTriggerCallbacks.Capture(identities))))
                            throw new InvalidOperationException("Actual Hero trigger source invocation/alias readback differs.");
                    });
                    byte[] CallbackBytes(CanonicalHeroAnimationCallbacks.Data data) => codec.Encode(codec.Capture(
                        new SortedDictionary<string, object>(StringComparer.Ordinal) { [CanonicalHeroAnimationCallbacks.Root] = data }, _ => null));
                    var callbackBits = CallbackBytes(heroCallbacks);
                    sourceChecks.Add(() => {
                        if (!callbackBits.SequenceEqual(CallbackBytes(CanonicalHeroAnimationCallbacks.Capture(identities))))
                            throw new InvalidOperationException("Hero callback complete ordered method/actual target alias readback differs.");
                    });
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
                }, rebind => mainRebinding = rebind);
                var mainPreparedMilliseconds = prepareTimer.ElapsedMilliseconds;
                Modding.Logger.Log("[HKTAS] Actual D main graph preparation timing: elapsedMilliseconds=" + mainPreparedMilliseconds);
                ActualTargetStage = "prepare-input";
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
                }, rebind => sdkRebinding = rebind);
                Modding.Logger.Log("[HKTAS] Actual D input graph preparation timing: elapsedMilliseconds=" + (prepareTimer.ElapsedMilliseconds - mainPreparedMilliseconds));
                domainActions.Add(() => {
                    ActualTargetStage = "geo-source-producer";
                    var envelope = actualSourceValidation ?? throw new InvalidOperationException("Post-main Geo has no sealed source envelope.");
                    void RequireMain() { main.VerifyReadback(); sdk.VerifyReadback(); }
                    Action? applyGeo = null;
                    Action? beginGeoLifecycle = null;
                    Action? requireGeoNative = null;
                    Action? reapplyEventData = null;
                    restoreGeoSourceDependencies(continuation!.Scheduler, admitted, (oldDirectory, producedDirectory) => {
                        var counter = producedDirectory.Values.OfType<GeoCounter>().Single();
                        ActualTargetStage = "geo-prepare-source-graph";
                        IReadOnlyDictionary<string, object>? newDirectory = null;
                        applyGeo = geoDomain.Prepare(envelope.Geo, counter, CanonicalGeoColdTemplate.Directory(counter.gameObject),
                            mainView!, RequireMain, bindings.ResolveTarget, admitted, directory => newDirectory = directory, out var beginLifecycle, out var verifyNative);
                        beginGeoLifecycle = beginLifecycle; requireGeoNative = verifyNative;
                        if (newDirectory == null) throw new InvalidOperationException("Source Geo has no prepared final alias directory.");
                        Dictionary<string, object> Replacements(CanonicalCarryGraphData graph, Func<string, object> resolve) {
                            var replacements = new Dictionary<string, object>(StringComparer.Ordinal);
                            foreach (var external in graph.Externals) {
                                var current = resolve(external.Binding);
                                var matches = oldDirectory.Where(pair => ReferenceEquals(pair.Value, current)).ToArray();
                                if (matches.Length > 1) throw new InvalidDataException("Geo external owner directory contains duplicate identities.");
                                if (matches.Length == 1) replacements.Add(external.Binding, newDirectory[matches[0].Key]);
                            }
                            return replacements;
                        }
                        var mainChanges = Replacements(sourceMain, bindings.ResolveTarget);
                        var sdkChanges = Replacements(sourceInput, inputExternal.ResolveTarget);
                        ActualTargetStage = "geo-main-input-external-rebind";
                        if (mainChanges.Count != 0) mainRebinding!.Prepare(mainChanges).Apply();
                        if (sdkChanges.Count != 0) sdkRebinding!.Prepare(sdkChanges).Apply();
                        RequireMain();
                        reapplyEventData = CanonicalEventDataCarry.PrepareSourceReapply(mainView!, admitted);
                        return newDirectory;
                    }, verifyIncoming => {
                        ActualTargetStage = "geo-reapply-source-event-data";
                        (reapplyEventData ?? throw new InvalidOperationException("Source EventData replay was not prepared."))();
                        // This same-thread protected interval performs only
                        // incoming/native observations. Its one complete main
                        // and SDK check is finalAdmission immediately before
                        // the first Geo write-before snapshot, not repeated here.
                        verifyIncoming();
                        ActualTargetStage = "geo-apply-source-graph";
                        (applyGeo ?? throw new InvalidOperationException("Source Geo final plan was not prepared."))();
                    }, () => (beginGeoLifecycle ?? throw new InvalidOperationException("Geo lifecycle controller is absent."))(),
                        () => (requireGeoNative ?? throw new InvalidOperationException("Geo native source witness is absent."))());
                    mainRebinding!.CompleteGeoPhase(); sdkRebinding!.CompleteGeoPhase();
                });
                domainActions.Add(() => {
                    ActualTargetStage = "marker-source-producer";
                    var envelope = actualSourceValidation ?? throw new InvalidOperationException("Post-main Marker has no sealed source envelope.");
                    void RequireMain() { main.VerifyReadback(); sdk.VerifyReadback(); }
                    Action? applyMarker = null;
                    Action? beginMarkerLifecycle = null;
                    Action? requireMarkerNative = null;
                    Action? reapplyEventData = null;
                    restoreMarkerSourceDependencies(continuation!.Scheduler, admitted, (oldDirectory, producedDirectory) => {
                        var marker = (GameObject)producedDirectory["$"];
                        ActualTargetStage = "marker-prepare-source-graph";
                        IReadOnlyDictionary<string, object>? newDirectory = null;
                        applyMarker = markerDomain.Prepare(envelope.Marker, marker,
                            mainView!, RequireMain, bindings.ResolveTarget, admitted, directory => newDirectory = directory, out var beginLifecycle, out var verifyNative);
                        beginMarkerLifecycle = beginLifecycle; requireMarkerNative = verifyNative;
                        if (newDirectory == null) throw new InvalidOperationException("Source Marker has no prepared final alias directory.");
                        Dictionary<string, object> Replacements(CanonicalCarryGraphData graph, Func<string, object> resolve) {
                            var replacements = new Dictionary<string, object>(StringComparer.Ordinal);
                            foreach (var external in graph.Externals) {
                                var current = resolve(external.Binding);
                                var matches = oldDirectory.Where(pair => ReferenceEquals(pair.Value, current)).ToArray();
                                if (matches.Length > 1) throw new InvalidDataException("Marker external owner directory contains duplicate identities.");
                                if (matches.Length == 1) replacements.Add(external.Binding, newDirectory[matches[0].Key]);
                            }
                            return replacements;
                        }
                        var mainChanges = Replacements(sourceMain, bindings.ResolveTarget);
                        var sdkChanges = Replacements(sourceInput, inputExternal.ResolveTarget);
                        ActualTargetStage = "marker-main-input-external-rebind";
                        if (mainChanges.Count != 0) mainRebinding!.PrepareMarker(mainChanges).Apply();
                        if (sdkChanges.Count != 0) sdkRebinding!.PrepareMarker(sdkChanges).Apply();
                        RequireMain();
                        reapplyEventData = CanonicalEventDataCarry.PrepareSourceReapply(mainView!, admitted);
                        return newDirectory;
                    }, verifyIncoming => {
                        ActualTargetStage = "marker-reapply-source-event-data";
                        (reapplyEventData ?? throw new InvalidOperationException("Source EventData replay was not prepared."))();
                        // This same-thread protected interval performs only
                        // incoming/native observations. Its one complete main
                        // and SDK check is finalAdmission immediately before
                        // the first Marker write-before snapshot, not repeated here.
                        verifyIncoming();
                        ActualTargetStage = "marker-apply-source-graph";
                        (applyMarker ?? throw new InvalidOperationException("Source Marker final plan was not prepared."))();
                    }, () => (beginMarkerLifecycle ?? throw new InvalidOperationException("Marker lifecycle controller is absent."))(),
                        () => (requireMarkerNative ?? throw new InvalidOperationException("Marker native source witness is absent."))());
                    mainRebinding!.CompleteMarkerPhase(); sdkRebinding!.CompleteMarkerPhase();
                });
                domainActions.Add(() => {
                    ActualTargetStage = "map-source-graph-activation";
                    var envelope = actualSourceValidation ?? throw new InvalidOperationException("Map requires the sealed source envelope.");
                    mapDomain.Prepare(envelope.Map, mainView!, () => { main.VerifyReadback(); sdk.VerifyReadback(); }, bindings.ResolveTarget, admitted, out var verifyMap)();
                    sourceChecks.Add(verifyMap);
                });
                domainActions.Add(() => {
                    void Require() { if (!admitted()) throw new InvalidOperationException("Map Key source lost paused admission."); }
                    LoadingSerialSceneProbe.DeriveProtectedSourceMapKeyDirectory(() => {
                    ActualTargetStage = "map-key-parent-source-preparation";
                    var envelope = actualSourceValidation ?? throw new InvalidOperationException("Map Key requires the sealed source envelope.");
                    var keys = CanonicalMapKeyTextDirectory.Current();
                    var oldLeaves = LoadingGeoIncomingReferences.BuildMapKeyDirectory(keys);
                    var leafIncoming = consumeMapKeyIncoming();
                    var parentIncoming = consumeMapKeyParentIncoming();
                    var oldParents = parentIncoming.OwnedDirectory();
                    void RequireMain() { main.VerifyReadback(); sdk.VerifyReadback(); }
                    var parent = mapKeyParentDomain.Prepare(envelope.MapKeyParents, keys, parentIncoming,
                        mainView!, RequireMain, bindings.ResolveTarget, admitted);
                    // Resolve all old external identities while the old native
                    // leaves are still alive. No post-retirement name lookup.
                    var mainOriginal = sourceMain.Externals.ToDictionary(item => item.Binding, item => bindings.ResolveTarget(item.Binding), StringComparer.Ordinal);
                    var sdkOriginal = sourceInput.Externals.ToDictionary(item => item.Binding, item => inputExternal.ResolveTarget(item.Binding), StringComparer.Ordinal);
                    Dictionary<string, object> Replacements(IReadOnlyDictionary<string, object> current,
                        IReadOnlyDictionary<string, object> oldDirectory, IReadOnlyDictionary<string, object> finalDirectory) {
                        var changes = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (var external in current) {
                            var matches = oldDirectory.Where(pair => ReferenceEquals(pair.Value, external.Value)).ToArray();
                            if (matches.Length > 1) throw new InvalidDataException("Map Key external owner identity is ambiguous.");
                            if (matches.Length == 1 && !ReferenceEquals(external.Value, finalDirectory[matches[0].Key]))
                                changes.Add(external.Key, finalDirectory[matches[0].Key]);
                        }
                        return changes;
                    }
                    var mainParentChanges = Replacements(mainOriginal, oldParents, parent.FinalDirectory);
                    var sdkParentChanges = Replacements(sdkOriginal, oldParents, parent.FinalDirectory);
                    var parentIncomingPlan = parentIncoming.Prepare(parent.FinalDirectory);
                    Action? verifyColdIncoming = null;
                    try {
                        restoreMapKeyParentDependencies(() => {
                            var parentDirectory = new CanonicalMapKeyParentDirectory(keys);
                            using var external = new CanonicalMapKeyTextExternalGuard(keys, oldParents,
                                new MonoBehaviour[] { parentDirectory.Control, parentDirectory.Fade }, parentDirectory.Parents);
                            external.PrepareExactIncomingRebind(parent.FinalDirectory);
                            ActualTargetStage = "map-key-parent-main-input-incoming-rebind";
                            parentIncomingPlan.ApplyWithGuardedDependency(() => {
                                if (mainParentChanges.Count != 0) mainRebinding!.PrepareMapKeyParents(mainParentChanges).Apply();
                                if (sdkParentChanges.Count != 0) sdkRebinding!.PrepareMapKeyParents(sdkParentChanges).Apply();
                            });
                            mainRebinding!.CompleteMapKeyParentPhase(); sdkRebinding!.CompleteMapKeyParentPhase();
                            CanonicalEventDataCarry.PrepareSourceReapply(mainView!, admitted)();
                            ActualTargetStage = "map-key-parent-real-source-values-original-OnDisable";
                            parent.ApplyDependencies();
                            parentIncomingPlan.VerifyReadback(); external.Verify(); RequireMain();
                        }, lifecycle => {
                            ActualTargetStage = "map-key-shared-dependent-cold-Canon";
                            normalizeMapKey(parent.RequireReady, leafIncoming, lifecycle, readback => verifyColdIncoming = readback);
                            parent.CompleteColdCanon();
                        });
                        var coldReadback = verifyColdIncoming ?? throw new InvalidOperationException("Dependent Map Key Canon did not expose its real incoming verifier.");
                        var produced = LoadingGeoIncomingReferences.BuildMapKeyDirectory(keys);
                        parent.RebindLeaves(produced);
                        ActualTargetStage = "map-key-text-source-graph";
                        IReadOnlyDictionary<string, object>? finalLeaves = null;
                        var applyMapKey = mapKeyDomain.Prepare(envelope.MapKeyText, keys, mainView!, RequireMain,
                            bindings.ResolveTarget, admitted, value => finalLeaves = value, out var verifyMapKey);
                        if (finalLeaves == null) throw new InvalidDataException("Map Key final prepared leaf directory is absent.");
                        var mainChanges = Replacements(mainOriginal, oldLeaves, finalLeaves);
                        var sdkChanges = Replacements(sdkOriginal, oldLeaves, finalLeaves);
                        if (mainChanges.Count != 0) mainRebinding!.PrepareMapKey(mainChanges).Apply();
                        if (sdkChanges.Count != 0) sdkRebinding!.PrepareMapKey(sdkChanges).Apply();
                        RequireMain();
                        CanonicalEventDataCarry.PrepareSourceReapply(mainView!, admitted)();
                        coldReadback(); applyMapKey(); coldReadback(); parent.CompleteTextSource(); Require();
                        sourceChecks.Add(() => { verifyMapKey(); coldReadback(); parentIncomingPlan.VerifyReadback(); parent.Verify(); });
                    } catch { parent.Fault(); throw; }
                    }, Require);
                });
                domainActions.Add(() => prepareScheduler(continuation!.Scheduler)());
                var actual = new CanonicalCarryPairPreparation(main, sdk, () => {
                    guardInputSlots(); guardEventSlots(); sceneMapping.Guard(); deviceManagers.Guard(); loadCallbacks.Guard();
                }, admitted);
                return () => actual.Apply(() => {
                    for (var index = 0; index < domainActions.Count; index++) {
                        ActualTargetStage = "domain/" + index;
                        if (!admitted()) throw new InvalidOperationException("Actual D domain lost paused admission.");
                        domainActions[index]();
                    }
                }, () => { ActualTargetStage = "verify-source-domains"; foreach (var check in sourceChecks) check(); },
                    stage => ActualTargetStage = stage);
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
            if (!ReferenceEquals(decodedActualEnvelope, source) || decodedActualMain == null
                || decodedActualMainHash != HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(source.Main))
                throw new InvalidOperationException("Actual source main envelope changed after its owner-context decode.");
            actualSourceValidation = source;
            var main = decodedActualMain;
            decodedActualMain = null; // Single consumption; never reuse after fault.
            return PrepareActualRestore(main, inputCodec.Decode(source.Input), nextMovie, admitted, prepareScheduler);
        }
        internal byte[] EncodeInput() => inputCodec.Encode(InputGraph ?? throw new InvalidDataException("Input carry domain was not captured."));

        private sealed class ExternalBindings
        {
            private readonly BoundaryObjectIdentity identities;
            private readonly Dictionary<Type, UnityEngine.Object[]> assets = new Dictionary<Type, UnityEngine.Object[]>();
            private readonly Dictionary<object, string> known = new Dictionary<object, string>(new References());
            // Exactly the same relation as known, indexed in the reverse
            // direction. Multiple distinct owners keep the original ambiguity
            // rejection; no binding or source/native readback is omitted.
            private readonly Dictionary<string, object> reverseKnown = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly HashSet<string> ambiguousKnown = new HashSet<string>(StringComparer.Ordinal);
            private void Remember(object owner, string binding)
            {
                known.Add(owner, binding);
                if (reverseKnown.ContainsKey(binding)) ambiguousKnown.Add(binding);
                else reverseKnown.Add(binding, owner);
            }
            internal readonly CanonicalCaptureOwnerDirectory ownerDirectory;
            private readonly IReadOnlyDictionary<string, object> nativeDirectory;
            private readonly CanonicalCarryReflectionBindings metadata;
            private readonly CanonicalHeroAssetTemplates assetTemplates;
            private readonly CanonicalVisualSenderBindings canonicalSenders;
            internal readonly List<EventAudit> Events = new List<EventAudit>();
            internal bool HasEvents { get; private set; }
            internal object ResolveCaptured(string binding)
            {
                if (ambiguousKnown.Contains(binding) || !reverseKnown.TryGetValue(binding, out var owner))
                    throw new InvalidDataException("Captured carry external binding is absent or ambiguous: " + binding);
                return owner;
            }
            internal object ResolveTarget(string binding)
            {
                if (ambiguousKnown.Contains(binding)) throw new InvalidDataException("Target external directory is ambiguous: " + binding);
                if (reverseKnown.TryGetValue(binding, out var cached)) return cached;
                object value;
                if (binding.StartsWith("HeroAssetOrigin/v1/", StringComparison.Ordinal)) value = assetTemplates.Resolve(binding);
                else if (binding.StartsWith("asset:", StringComparison.Ordinal)) {
                    // Source Bind admits an asset only when its exact concrete
                    // type/name identifies one loaded original. Use that same
                    // contract for every original asset; a scene object with
                    // the same name fails the complete asset-key readback.
                    var asset = CanonicalAssetBindingKey.Resolve(binding, AppDomain.CurrentDomain.GetAssemblies(),
                        type => typeof(UnityEngine.Object).IsAssignableFrom(type));
                    var candidates = AssetCandidates(asset.Item1).Where(owner => owner.name == asset.Item2).ToArray();
                    value = CanonicalAssetBindingKey.UniqueOriginal(candidates, binding, owner => identities.Get(owner));
                }
                else if (canonicalSenders.Snapshot().Slots.Any(slot => slot.Key == binding)) value = canonicalSenders.Resolve(binding);
                else if (binding.StartsWith("metadata-", StringComparison.Ordinal) || binding.StartsWith("original-callback|", StringComparison.Ordinal)
                    || binding == "runtime-comparer|string-default" || binding == "runtime-comparer|int32-default") value = metadata.Resolve(binding);
                else {
                    if (!nativeDirectory.TryGetValue(binding, out value!))
                        throw new InvalidDataException("Target native/FSM external has no unique registered identity: " + binding);
                }
                if (value == null) throw new InvalidDataException("Target external is absent: " + binding);
                Remember(value, binding); return value;
            }
            internal void AuditGeoAssets(CanonicalCarryGraphData graph)
            {
                var schemas = new SortedSet<string>(StringComparer.Ordinal); var count = 0;
                foreach (var external in graph.Externals.Where(item => item.Binding.StartsWith("asset:", StringComparison.Ordinal))) {
                    var parsed = CanonicalAssetBindingKey.Resolve(external.Binding, AppDomain.CurrentDomain.GetAssemblies(),
                        type => typeof(UnityEngine.Object).IsAssignableFrom(type));
                    var actual = ResolveCaptured(external.Binding);
                    var candidates = AssetCandidates(parsed.Item1).Where(owner => owner.name == parsed.Item2).ToArray();
                    var unique = CanonicalAssetBindingKey.UniqueOriginal(candidates, external.Binding, owner => identities.Get(owner));
                    if (CanonicalCarryGraphCodec.Identity(parsed.Item1) != external.Type || actual.GetType() != parsed.Item1
                        || !ReferenceEquals(unique, actual))
                        throw new InvalidDataException("Source Geo original asset type, assembly, identity or unique candidate differs.");
                    schemas.Add(external.Type + "/" + parsed.Item1.Module.ModuleVersionId.ToString("D")); count++;
                }
                Modding.Logger.Log("[HKTAS] Geo source asset external audit: count=" + count + ";typeSchemas="
                    + string.Join(";", schemas) + ";allUniqueOriginalAssets=true;completeStateCoverage=false");
            }
            internal ExternalBindings(BoundaryObjectIdentity identities, CanonicalHeroAssetTemplates assetTemplates, CanonicalVisualSenderBindings canonicalSenders,
                IReadOnlyDictionary<string, object> fixedInputBindings, object captureEpoch, CanonicalCaptureOwnerDirectory? sharedDirectory = null)
            {
                this.identities = identities;
                this.assetTemplates = assetTemplates;
                this.canonicalSenders = canonicalSenders;
                IReadOnlyDictionary<string, object> CaptureNativeDirectory() {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var directory = new Dictionary<string, object>(StringComparer.Ordinal);
                void Register(string key, object owner) {
                    if (directory.Count >= CanonicalCarryGraphCodec.MaximumNodes || directory.ContainsKey(key))
                        throw new InvalidDataException("Target native owner directory is ambiguous or oversized: " + key);
                    directory.Add(key, owner);
                }
                foreach (var go in identities.Objects) {
                    Register(identities.Get(go), go);
                    foreach (var component in go.GetComponents<Component>()) {
                        if (component == null) continue;
                        var identity = identities.Get(component);
                        Register(identity, component);
                        if (component is PlayMakerFSM fsm) Register("FSM/" + identity, RawFsm.GetValue(fsm));
                    }
                }
                Modding.Logger.Log("[HKTAS] Carry external native directory captured: entries=" + directory.Count
                    + ";elapsedMilliseconds=" + elapsed.ElapsedMilliseconds + ";sameCaptureInventories=1;sharedMainInput=true");
                return directory;
                }
                ownerDirectory = sharedDirectory ?? new CanonicalCaptureOwnerDirectory(identities, captureEpoch, CaptureNativeDirectory);
                nativeDirectory = ownerDirectory.Require(identities, captureEpoch);
                foreach (var pair in fixedInputBindings) {
                    // Multiple named roots can alias the same live object.
                    // Select the first canonical name; the root graph retains
                    // every original alias rather than allocating another copy.
                    if (!known.ContainsKey(pair.Value)) Remember(pair.Value, pair.Key);
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
                if (canonicalSlot != null) { Remember(value, canonicalSlot); return canonicalSlot; }
                if (value is UnityEngine.Object native)
                {
                    if (native == null) throw new InvalidDataException("destroyed-Unity-wrapper-needs-rebinding");
                    var key = identities.Get(native);
                    if (key.StartsWith("asset:", StringComparison.Ordinal))
                    {
                        var originalOrigin = assetTemplates.Bind(native);
                        if (originalOrigin != null) { Remember(value, originalOrigin); return originalOrigin; }
                        var type = native.GetType();
                        var candidates = AssetCandidates(type);
                        var same = candidates.Where(owner => owner.name == native.name).ToArray();
                        if (same.Length != 1 || same[0].GetInstanceID() != native.GetInstanceID())
                            throw new InvalidDataException("ambiguous-asset-binding:" + key);
                    }
                    Remember(value, key); return key;
                }
                if (value is Fsm fsm)
                {
                    var owner = Required(typeof(Fsm), "owner").GetValue(fsm) as PlayMakerFSM;
                    if (owner == null || !ReferenceEquals(RawFsm.GetValue(owner), fsm))
                        throw new InvalidDataException("foreign-FSM-needs-explicit-owner-binding");
                    var key = "FSM/" + identities.Get(owner);
                    Remember(value, key); return key;
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
                    Remember(value, key); return key;
                }
                var reflective = metadata.Bind(value);
                if (reflective != null) { Remember(value, reflective); return reflective; }
                return null;
            }
            private UnityEngine.Object[] AssetCandidates(Type type)
            {
                if (!assets.TryGetValue(type, out var candidates)) {
                    candidates = Resources.FindObjectsOfTypeAll(type).Where(owner => owner != null && owner.GetType() == type).ToArray();
                    if (candidates.Length > 100000) throw new InvalidDataException("asset-binding-candidate-budget");
                    assets.Add(type, candidates);
                }
                return candidates;
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
