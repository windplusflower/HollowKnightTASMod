using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Explicit serial loading policy; experimental checkpoint code uses its C.
    // The original SceneLoad still owns target activation and all entry callbacks.
    internal sealed partial class LoadingSerialSceneProbe : IDisposable
    {
        private const string StagingName = "__HKTAS_SerialBoundary_v6";
        private delegate IEnumerator OriginalBegin(SceneLoad self);
        private delegate IEnumerator OriginalTransition(GameManager self, GameManager.SceneLoadInfo info);
        private delegate bool OriginalUnload(string name);
        private delegate void OriginalActiveSceneChanged(Scene previousActiveScene, Scene newActiveScene);
        private delegate void OriginalSceneLoaded(Scene scene, LoadSceneMode mode);
        private delegate void OriginalSceneUnloaded(Scene scene);
        private static readonly FieldInfo SceneLoadRunnerField = typeof(SceneLoad).GetField("runner", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("SceneLoad.runner");
        private static readonly MethodInfo SceneLoadBeginRoutineMethod = typeof(SceneLoad).GetMethod("BeginRoutine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("SceneLoad.BeginRoutine");
        private readonly Hook beginHook;
        private readonly Hook loadBeginHook;
        private readonly Hook unloadHook;
        private readonly Hook transitionHook;
        private readonly Hook lightingHook;
        private readonly Hook activeSceneChangedHook;
        private readonly Hook sceneLoadedHook;
        private readonly Hook sceneUnloadedHook;
        private readonly SerialSceneEventFilter<Scene> eventFilter;
        private OriginalActiveSceneChanged? deferredActiveSceneChangedInvoker;
        private readonly string? report;
        private static LoadingSerialSceneProbe? instance;
        private SceneLoad? transition;
        private GameManager.SceneLoadInfo? checkpointLoadInfo;
        private IEnumerator? serialBeginRoutine;
        private Coroutine? serialBeginCoroutine;
        private Scene source;
        private string sourceName = "";
        private Scene staging;
        private bool sourceUnloaded;
        private bool awaitingBoundary;
        private bool observedBoundary;
        private CanonicalBoundaryMutationScope? protectedClockCanonScope;
        private bool protectedSourceCharmDerivationConsumed;
        private bool protectedSourceGeoDerivationConsumed;
        private bool protectedSourceMarkerDerivationConsumed;
        private bool protectedSourceMapKeyDerivationConsumed;
        private readonly System.Collections.Generic.Dictionary<int, string> retiredSourceOwners = new System.Collections.Generic.Dictionary<int, string>();
        internal static string? RetiredSourceOwner(int instanceId) => instance != null
            && instance.sourceUnloaded && instance.retiredSourceOwners.TryGetValue(instanceId, out var identity) ? identity : null;
        internal static string? RetiringSourceOwner(int instanceId) => instance != null && instance.transition != null
            && instance.retiredSourceOwners.TryGetValue(instanceId, out var identity) ? identity : null;
        internal static bool TryCachedSourceOwner(UnityEngine.Object owner, out string? provenance)
        {
            // Some pinned Unity versions do not retain a managed instance ID.
            // Preserve the oracle's optional metadata rule; never infer an ID
            // from m_CachedPtr or emit a native pointer.
            var field = typeof(UnityEngine.Object).GetField("m_InstanceID", BindingFlags.Instance | BindingFlags.NonPublic);
            var cached = field == null ? 0 : field.FieldType == typeof(int) ? (int)field.GetValue(owner)!
                : throw new InvalidOperationException("Optional Unity cached instance identity type differs.");
            provenance = cached == 0 ? null : RetiringSourceOwner(cached);
            return cached != 0;
        }
        internal static void RequireCanonicalMutationBoundary()
            => throw new InvalidOperationException("Legacy canonical cleanup is retired; use the resident boundary coordinator.");
        private int ordinal;
        private int rows;
        private string? failure;
        private static readonly long visualFrame = long.TryParse(
            Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_END"), out var visualEnd) ? visualEnd - 1 : -1;

        internal LoadingSerialSceneProbe()
        {
            if (!NativeExecutionProfile.UsesSerialScenes(NativeExecutionProfile.SelectedProfileId))
                throw new InvalidOperationException("Serial scenes require their own V6 execution profile.");
            if (NativeExecutionProfile.UsesCanonicalBoundary(NativeExecutionProfile.SelectedProfileId))
            {
                HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointEnvironmentPolicy.ValidateMods(
                    NativeExecutionProfile.SelectedProfileId, Modding.ModHooks.GetAllMods(onlyEnabled: false, allowLoadError: true)
                        .Select(mod => mod.GetName()));
                // The per-object legacy policy is retired. Every serial load
                // enters the same typed resident coordinator below.
            }
            if (instance != null) throw new InvalidOperationException("Serial scene policy is already installed.");
            var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            report = string.IsNullOrWhiteSpace(output) ? null : Path.Combine(output,
                "serial-scenes-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".jsonl");
            var begin = typeof(SceneLoad).GetMethod("BeginRoutine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("SceneLoad.BeginRoutine");
            var loadBegin = typeof(SceneLoad).GetMethod("Begin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("SceneLoad.Begin");
            var unload = typeof(USceneManager).GetMethod("UnloadScene", new[] { typeof(string) })
                ?? throw new MissingMethodException("SceneManager.UnloadScene(string)");
            var start = typeof(GameManager).GetMethod("BeginSceneTransitionRoutine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("GameManager.BeginSceneTransitionRoutine");
            beginHook = new Hook(begin, (Func<OriginalBegin, SceneLoad, IEnumerator>)Begin);
            loadBeginHook = new Hook(loadBegin, (Action<Action<SceneLoad>, SceneLoad>)BeginLoad);
            unloadHook = new Hook(unload, (Func<OriginalUnload, string, bool>)Unload);
            transitionHook = new Hook(start, (Func<OriginalTransition, GameManager, GameManager.SceneLoadInfo, IEnumerator>)Start);
            var sceneStart = typeof(global::SceneManager).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("SceneManager.Start");
            lightingHook = new Hook(sceneStart, (Action<Action<global::SceneManager>, global::SceneManager>)SceneStart);
            eventFilter = new SerialSceneEventFilter<Scene>(IsStagingScene, static scene => scene.name);
            var internalActiveSceneChanged = typeof(USceneManager).GetMethod(
                "Internal_ActiveSceneChanged",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(Scene), typeof(Scene) },
                null) ?? throw new MissingMethodException("SceneManager.Internal_ActiveSceneChanged");
            var internalSceneLoaded = typeof(USceneManager).GetMethod(
                "Internal_SceneLoaded",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(Scene), typeof(LoadSceneMode) },
                null) ?? throw new MissingMethodException("SceneManager.Internal_SceneLoaded");
            var internalSceneUnloaded = typeof(USceneManager).GetMethod(
                "Internal_SceneUnloaded",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(Scene) },
                null) ?? throw new MissingMethodException("SceneManager.Internal_SceneUnloaded");
            activeSceneChangedHook = new Hook(
                internalActiveSceneChanged,
                (Action<OriginalActiveSceneChanged, Scene, Scene>)OnInternalActiveSceneChanged);
            sceneLoadedHook = new Hook(
                internalSceneLoaded,
                (Action<OriginalSceneLoaded, Scene, LoadSceneMode>)OnInternalSceneLoaded);
            sceneUnloadedHook = new Hook(
                internalSceneUnloaded,
                (Action<OriginalSceneUnloaded, Scene>)OnInternalSceneUnloaded);
            instance = this;
            Write("installed", null);
            Modding.Logger.Log("[HKTAS] V6 serial scene experiment installed.");
        }
        // Retained for callers that also support the retired legacy profile.
        internal static void EndCanonicalUiCallbacks() { }
        internal static void EndPoolDiagnostic() { }

        private IEnumerator Start(OriginalTransition original, GameManager manager, GameManager.SceneLoadInfo info)
        {
            // For example StagTravel gates activation on FetchComplete. Waiting
            // for activation before fetch would deadlock; it needs its own adapter.
            // Refuse before any destruction rather than silently using another policy.
            if (info.GetType() != typeof(GameManager.SceneLoadInfo))
            {
                Write("unsupported-load-info", new { type = info.GetType().FullName });
                throw Fail("V6 serial prototype requires a load-info adapter: " + info.GetType().FullName);
            }
            checkpointLoadInfo = info;
            // Bounded route metadata identifies the actual entry family; scene
            // names alone do not establish the gate or leaving direction.
            if (report != null) File.AppendAllText(report + ".transitions.jsonl", JsonConvert.SerializeObject(new {
                ordinal = ordinal + 1, engineFrame = Time.frameCount,
                source = USceneManager.GetActiveScene().name, target = info.SceneName,
                entryGate = info.EntryGateName, leaveDirection = info.HeroLeaveDirection?.ToString(),
                visualization = info.Visualization.ToString(), firstLevel = info.IsFirstLevelForPlayer }) + "\n");
            if (checkpointTransitionRoutine != null) throw Fail("Scene transition continuation reentered.");
            return TrackCheckpointTransition(original(manager, info));
        }

        private void BeginLoad(Action<SceneLoad> original, SceneLoad load)
        {
            var runner = SceneLoadRunnerField.GetValue(load) as MonoBehaviour
                ?? throw new InvalidOperationException("SceneLoad.runner is null.");
            var routine = (IEnumerator)SceneLoadBeginRoutineMethod.Invoke(load, null)!;
            serialBeginCoroutine = runner.StartCoroutine(routine);
        }

        private IEnumerator Begin(OriginalBegin original, SceneLoad load)
        {
            if (serialBeginRoutine != null) throw Fail("Serial boundary iterator reentered.");
            serialBeginRoutine = BeginSerialRoutine(original, load);
            return serialBeginRoutine;
        }

        private IEnumerator BeginSerialRoutine(OriginalBegin original, SceneLoad load)
        {
            if (transition != null) throw Fail("Serial scene transition reentered.");
            transition = load;
            ordinal++;
            sourceUnloaded = false;
            awaitingBoundary = false;
            observedBoundary = false;
            eventFilter.Reset();
            deferredActiveSceneChangedInvoker = null;
            try
            {
                // Finish speculative work before starting destruction. This routine
                // is also called by the original loader; the second call sees no pending work.
                yield return ScenePreloader.FinishPendingOperations();
                while (!load.IsFetchAllowed || !load.IsActivationAllowed) yield return null;
                source = USceneManager.GetActiveScene();
                sourceName = source.name;
                if (!source.IsValid() || !source.isLoaded || source.name == StagingName)
                    throw Fail("Serial scene source is invalid.");
                if (!staging.IsValid() || !staging.isLoaded) staging = USceneManager.CreateScene(StagingName);
                Inventory("before-unload");
                if (!USceneManager.SetActiveScene(staging)) throw Fail("Cannot activate staging scene.");
                Write("unload-requested", new { source = sourceName, target = load.TargetSceneName });
                var operation = USceneManager.UnloadSceneAsync(source);
                if (operation == null) throw Fail("Source unload operation was not created.");
                // Do not leave the completed native AsyncOperation retained by
                // Unity's coroutine scheduler at the checkpoint boundary.
                // Poll completion through the same PlayerLoop instead.
                while (!operation.isDone) yield return null;
                if (source.isLoaded) throw Fail("Source scene remained loaded.");
                operation = null;
                sourceUnloaded = true;
                // Return through PlayerLoop once so destruction callbacks unwind.
                yield return null;
                awaitingBoundary = true;
                while (!observedBoundary) yield return null;
                awaitingBoundary = false;
                Write("target-load-start", new { source = sourceName, target = load.TargetSceneName });
                var routine = original(load);
                var activated = false;
                try
                {
                    while (routine.MoveNext())
                    {
                        var wait = routine.Current;
                        // In the pinned SceneLoad routine the first direct async
                        // yield is target activation. FinishPendingOperations is
                        // yielded as a Coroutine; later asset cleanup is kept intact.
                        if (!activated && wait is AsyncOperation activation)
                        {
                            LoadingSynchronousFetchProbe.CompleteSerialActivation(activation);
                            var target = USceneManager.GetSceneByName(load.TargetSceneName);
                            if (!activation.isDone || !target.IsValid() || !target.isLoaded)
                                throw Fail("Synchronous scene activation did not finish the target.");
                            activated = true;
                            Write("target-activation-synchronized", new { target = load.TargetSceneName });
                            // Fixed continuation phase instead of a variable number
                            // of PlayerLoops waiting for async activation completion.
                            yield return null;
                        }
                        else yield return wait;
                    }
                }
                finally { (routine as IDisposable)?.Dispose(); }
                if (!activated) throw Fail("Serial loader did not observe target activation.");
                if (USceneManager.GetActiveScene().name != load.TargetSceneName || staging.rootCount != 0)
                    throw Fail("Target did not replace the staging scene.");
                Inventory("target-finished");
                Write("complete", new { source = sourceName, target = load.TargetSceneName });
            }
            finally
            {
                eventFilter.Reset();
                deferredActiveSceneChangedInvoker = null;
                awaitingBoundary = false;
                sourceUnloaded = false;
                source = default;
                sourceName = "";
                transition = null;
                checkpointLoadInfo = null;
                checkpointTransitionRoutine = null;
                checkpointTransitionFinished = false;
                checkpointBossBindings = null;
                serialBeginRoutine = null;
                serialBeginCoroutine = null;
            }
        }

        private bool Unload(OriginalUnload original, string name)
        {
            // GameManager's ActivationComplete subscriber still runs its tilemap,
            // asset/GC and social callbacks. Only its already-completed unload is suppressed.
            if (transition != null && sourceUnloaded && name == sourceName)
            {
                Write("duplicate-source-unload-suppressed", new { source = name });
                // Vanilla's removal of the old active scene also selects the target.
                // Suppressing only that removal leaves staging active, causing future
                // objects (and the next transition) to bind to staging instead.
                var target = USceneManager.GetSceneByName(transition.TargetSceneName);
                RenderState("before-late-select");
                if (!target.IsValid() || !target.isLoaded
                    || (USceneManager.GetActiveScene() != target && !USceneManager.SetActiveScene(target)))
                    throw Fail("Cannot select the activated serial target scene.");
                RenderState("after-late-select");
                // Keep one empty carrier scene. CreateScene does not emit sceneLoaded;
                // unloading it sends an unmatched sceneUnloaded event to Mod listeners.
                // Only activation-time temporary objects need the old source lifetime.
                if (!staging.IsValid() || !staging.isLoaded)
                    throw Fail("Serial staging scene is unavailable.");
                var temporaryRoots = staging.GetRootGameObjects();
                if (report != null) File.AppendAllText(report + ".staging.jsonl", JsonConvert.SerializeObject(new {
                    ordinal, target = target.name, roots = temporaryRoots.Select(root => new {
                        root.name, objects = root.GetComponentsInChildren<Transform>(true).Select(t => new {
                            id = Identity(t.gameObject), t.gameObject.activeSelf, t.gameObject.activeInHierarchy,
                            components = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().FullName).ToArray()
                        }).ToArray()
                    }).ToArray()
                }) + "\n");
                foreach (var root in temporaryRoots) UnityEngine.Object.Destroy(root);
                Write("staging-roots-cleared", new { target = target.name, count = temporaryRoots.Length });
                return true;
            }
            return original(name);
        }

        internal static void MarkLeanS0BoundaryObserved(bool reschedule = false)
        {
            var self = instance ?? throw new InvalidOperationException("Serial scene probe instance is absent.");
            if (!self.awaitingBoundary)
            {
                throw new InvalidOperationException("Serial scene probe is not awaiting a boundary.");
            }
            self.observedBoundary = true;
            if (reschedule && self.serialBeginCoroutine != null && self.serialBeginRoutine != null && self.transition != null)
            {
                var runner = SceneLoadRunnerField.GetValue(self.transition) as MonoBehaviour;
                if (runner != null)
                {
                    runner.StopCoroutine(self.serialBeginCoroutine);
                    self.serialBeginCoroutine = runner.StartCoroutine(RescheduleAfterClockRewind(self.serialBeginRoutine));
                }
            }
        }

        private static IEnumerator RescheduleAfterClockRewind(IEnumerator inner)
        {
            yield return null;
            try
            {
                while (inner.MoveNext())
                {
                    yield return inner.Current;
                }
            }
            finally
            {
                (inner as IDisposable)?.Dispose();
            }
        }

        internal static void SetLeanS0Ordinal(int boundaryOrdinal)
        {
            if (instance != null)
            {
                instance.ordinal = boundaryOrdinal;
            }
        }

        internal static bool ObserveNativeBoundary(RuntimeFullRunSession session, long movie, long native)
        {
            var self = instance;
            if (self?.failure != null) throw new InvalidOperationException(self.failure);
            if (self == null || !self.awaitingBoundary || self.observedBoundary) return false;
            if (self.source.isLoaded || self.transition == null)
                throw new InvalidOperationException("Serial native boundary still has a source scene.");
            var target = USceneManager.GetSceneByName(self.transition.TargetSceneName);
            if (target.IsValid() && target.isLoaded)
                throw new InvalidOperationException("Serial native boundary already has the target scene.");
            if (session == null) throw new ArgumentNullException(nameof(session));
            self.Inventory("native-empty-boundary", movie, native);
            LoadingBoundaryInventory.Capture(self.ordinal, self.sourceName, self.transition.TargetSceneName, movie, native);
            S0SemanticProbe.CaptureIfEnabled(
                self.ordinal,
                self.sourceName,
                self.transition.TargetSceneName,
                self.checkpointLoadInfo?.EntryGateName ?? "",
                self.checkpointLoadInfo?.HeroLeaveDirection?.ToString(),
                self.checkpointLoadInfo?.Visualization.ToString() ?? "",
                movie,
                native);
            if (session.TryEnterLeanS0Boundary(
                    self.ordinal,
                    self.sourceName,
                    self.transition.TargetSceneName,
                    self.checkpointLoadInfo,
                    movie,
                    native,
                    out var parkedForLeanS0))
            {
                if (parkedForLeanS0)
                {
                    return true;
                }
                self.observedBoundary = true;
                return true;
            }
            self.observedBoundary = true;
            return true;
        }

        internal static bool OwnsBoundaryIterator(IEnumerator iterator)
        {
            return instance != null && instance.awaitingBoundary && ReferenceEquals(iterator, instance.serialBeginRoutine);
        }
        internal static string ExperimentalTargetStage => "no-canon";
        internal static Tuple<int, GameManager.SceneLoadInfo, string> GetCheckpointBoundary()
        {
            CanonicalPhaseOneTrace.RequireCheckpointAllowed(CanonicalPhaseOneTrace.Enabled);
            return RequireActiveUnloadedBoundary();
        }
        internal static Tuple<int, GameManager.SceneLoadInfo, string> GetProtectedObservationBoundary()
        {
            CanonicalPhaseOneTrace.RequireContext(Environment.GetEnvironmentVariable);
            if (!CanonicalPhaseOneTrace.Enabled || Environment.GetEnvironmentVariable("HKTAS_LOADING_S0_OBSERVING") != "1")
                throw new InvalidOperationException("Protected S0 boundary identity is unavailable.");
            return RequireActiveUnloadedBoundary();
        }
        private static Tuple<int, GameManager.SceneLoadInfo, string> RequireActiveUnloadedBoundary()
        {
            var self = instance;
            if (self == null || !self.awaitingBoundary || !self.observedBoundary || self.source.isLoaded || self.transition == null || self.checkpointLoadInfo == null)
                throw new InvalidOperationException("No serial source-unloaded checkpoint boundary is active.");
            var target = USceneManager.GetSceneByName(self.transition.TargetSceneName);
            if ((target.IsValid() && target.isLoaded) || self.checkpointLoadInfo.SceneName != self.transition.TargetSceneName)
                throw new InvalidOperationException("Checkpoint target load has already started or its entry parameters differ.");
            return Tuple.Create(self.ordinal, self.checkpointLoadInfo, self.sourceName);
        }
        internal static void DeriveProtectedSourceCharmDirectory(CanonicalHudCharmPolicy charms,
            CanonicalRetiredMeshPolicy meshes, Action retireStarts, Action applySourceAndBuild, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedSourceCharmDerivationConsumed)
                throw new InvalidOperationException("Source charm derivation requires its single protected actual D mutation scope.");
            self.protectedSourceCharmDerivationConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                charms.Retire();
                meshes.Flush(owner => owner.StartsWith("canonical-equipped-charm-retirement/", StringComparison.Ordinal));
                requireAdmission(); retireStarts(); requireAdmission();
                applySourceAndBuild(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        private bool protectedCompletedCoroutineDerivationConsumed;
        internal static void DeriveProtectedCompletedCoroutines(Action createOriginalIdentities, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedCompletedCoroutineDerivationConsumed)
                throw new InvalidOperationException("Completed Coroutine creation requires its single protected Actual D scope.");
            self.protectedCompletedCoroutineDerivationConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                createOriginalIdentities(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        internal static void DeriveProtectedSourceGeoDirectory(Action deriveAndApplySource, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedSourceGeoDerivationConsumed)
                throw new InvalidOperationException("Source Geo derivation requires its single protected actual D mutation scope.");
            self.protectedSourceGeoDerivationConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                deriveAndApplySource(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        internal static void DeriveProtectedSourceMarkerDirectory(Action deriveAndApplySource, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedSourceMarkerDerivationConsumed)
                throw new InvalidOperationException("Source Marker derivation requires its single protected actual D mutation scope.");
            self.protectedSourceMarkerDerivationConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                deriveAndApplySource(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        private bool protectedSourceBorderDerivationConsumed;
        internal static void DeriveProtectedSourceBorderDirectory(Action deriveAndApplySource, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedSourceBorderDerivationConsumed)
                throw new InvalidOperationException("Source Border derivation requires its single protected actual D mutation scope.");
            self.protectedSourceBorderDerivationConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                deriveAndApplySource(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        internal static void DeriveProtectedSourceMapKeyDirectory(Action deriveAndApplySource, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedSourceMapKeyDerivationConsumed)
                throw new InvalidOperationException("Source Map Key derivation requires its single protected actual D mutation scope.");
            self.protectedSourceMapKeyDerivationConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                deriveAndApplySource(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        private bool protectedFinalVisualVisibilityConsumed;
        private bool protectedHudControlsConsumed;
        internal static void ApplyProtectedHudControls(Action applyControls, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedHudControlsConsumed)
                throw new InvalidOperationException("HUD controls require their single protected Actual D mutation scope.");
            if (applyControls == null) throw new ArgumentNullException(nameof(applyControls));
            self.protectedHudControlsConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                applyControls(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        internal static void ApplyProtectedFinalVisualVisibility(Action applyVisibility, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            var self = instance!;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalClockApplyExperiment.Enabled || self.protectedClockCanonScope != null
                || self.protectedFinalVisualVisibilityConsumed)
                throw new InvalidOperationException("Final visual visibility requires its single protected Actual D mutation scope.");
            if (applyVisibility == null) throw new ArgumentNullException(nameof(applyVisibility));
            self.protectedFinalVisualVisibilityConsumed = true;
            using var scope = new CanonicalBoundaryMutationScope(requireAdmission);
            scope.Enter(); self.protectedClockCanonScope = scope;
            try {
                RequireCanonicalMutationBoundary();
                applyVisibility(); requireAdmission();
                GetProtectedObservationBoundary(); requireAdmission();
            }
            finally { self.protectedClockCanonScope = null; }
        }
        internal static CanonicalS0ObservationSnapshot RecaptureRegisteredS0(Action admission)
        {
            GetProtectedObservationBoundary();
            throw new InvalidOperationException("Original Canon is absent; the legacy recapture bridge is retired.");
        }

        internal static CanonicalS0ObservationSnapshot ReapplyProtectedClockCanon(long movie, long native, string sessionDirectory, long origin,
            CanonicalInputEpoch controlOrigin, HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter input,
            HollowKnightTAS.Runtime.Input.FullRunMouseBridge mouse, Action requireAdmission)
        {
            GetProtectedObservationBoundary();
            throw new InvalidOperationException("Original Canon is absent; the legacy clock reapply bridge is retired.");
        }

        internal static GameObject[] LoadedGameObjects()
        {
            var scenes = new List<Scene>();
            for (var i = 0; i < USceneManager.sceneCount; i++)
            {
                var scene = USceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded) scenes.Add(scene);
            }
            var manager = GameManager.UnsafeInstance;
            if (manager == null) throw new InvalidDataException("Serial scene loader is absent.");
            var persistent = manager.gameObject.scene;
            if (!persistent.IsValid() || !persistent.isLoaded || persistent.name != "DontDestroyOnLoad")
                throw new InvalidDataException("Scene loader has no live persistent scene.");
            if (!scenes.Contains(persistent)) scenes.Add(persistent);
            var owners = new List<GameObject>();
            foreach (var scene in scenes)
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                        owners.Add(transform.gameObject);
            return owners.ToArray();
        }

        private void Inventory(string phase, long? movie = null, long? native = null)
        {
            if (report == null) return;
            var objects = LoadedGameObjects();
            if (objects.Length > 100000) throw new InvalidOperationException("Serial inventory exceeded object budget.");
            var components = objects.SelectMany(go => go.GetComponents<Component>()).Where(c => c != null).ToArray();
            var groups = objects.GroupBy(go => go.scene.name).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { scene = g.Key, objects = g.Count(), roots = g.Count(go => go.transform.parent == null) }).ToArray();
            var types = components.GroupBy(c => c.GetType().FullName).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { type = g.Key, count = g.Count() }).ToArray();
            // Keep this first experiment independent of native layout recovery.
            // Counts establish domain reduction; they do not certify Box2D caches are empty.
            var physicsComponents = new { bodies = components.OfType<Rigidbody2D>().Count(),
                colliders = components.OfType<Collider2D>().Count(), joints = components.OfType<Joint2D>().Count(),
                activeSimulatedBodies = components.OfType<Rigidbody2D>().Count(c => c.gameObject.activeInHierarchy && c.simulated),
                activeEnabledColliders = components.OfType<Collider2D>().Count(c => c.gameObject.activeInHierarchy && c.enabled) };
            // Aggregate equal-named DDOL roots: Unity can report sibling index zero
            // for several hidden roots, so their display paths are not unique IDs.
            var rootGroups = objects.GroupBy(go => go.scene.name + "/" + go.transform.root.name)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new { rootName = g.Key,
                    roots = g.Count(go => go.transform.parent == null), objects = g.Count(),
                    activeObjects = g.Count(go => go.activeInHierarchy) }).ToArray();
            var audio = components.OfType<AudioSource>()
                .Where(c => c.gameObject.name.StartsWith("Audio Player", StringComparison.Ordinal))
                .OrderBy(Identity, StringComparer.Ordinal).Select(c => new {
                    id = Identity(c), active = c.gameObject.activeInHierarchy, c.enabled,
                    c.isPlaying, c.timeSamples, c.pitch, c.loop,
                    clip = c.clip == null ? null : c.clip.name,
                    behaviours = c.GetComponents<MonoBehaviour>().Where(b => b != null)
                        .Select(b => b.GetType().FullName).ToArray()
                }).ToArray();
            // Audio playback state is diagnostic evidence, not removed from the
            // state contract just because it can progress independently of TAS.
            File.AppendAllText(report + ".audio.jsonl", JsonConvert.SerializeObject(new {
                phase, ordinal, engineFrame = Time.frameCount, dspTime = AudioSettings.dspTime, audio
            }) + "\n");
            Write(phase, new { movie, native, activeScene = USceneManager.GetActiveScene().name,
                source = sourceName, target = transition?.TargetSceneName,
                objects = objects.Length, activeObjects = objects.Count(go => go.activeInHierarchy),
                components = components.Length, scenes = groups, componentTypes = types, rootGroups,
                roots = objects.Where(go => go.transform.parent == null).Select(Identity).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                physicsComponents, nativePhysicsStateCovered = false,
                completeStateCoverage = false, restorable = false });
        }

        private static string Identity(UnityEngine.Object value)
        {
            var component = value as Component;
            var go = value as GameObject ?? component?.gameObject;
            if (go == null) return value.GetType().FullName + ":" + value.name;
            var segments = new List<string>();
            for (var node = go.transform; node != null; node = node.parent)
                segments.Add(node.name + "[" + node.GetSiblingIndex() + "]");
            segments.Reverse();
            return go.scene.name + "/" + string.Join("/", segments)
                + (component == null ? "" : ":" + component.GetType().FullName + "["
                    + Array.IndexOf(go.GetComponents(component.GetType()), component) + "]");
        }

        private void SceneStart(Action<global::SceneManager> original, global::SceneManager manager)
        {
            RenderState("scene-manager-before-start:" + manager.gameObject.scene.name);
            original(manager);
            RenderState("scene-manager-start:" + manager.gameObject.scene.name);
        }

        private bool IsStagingScene(Scene scene)
            => (staging.IsValid() && scene == staging) || string.Equals(scene.name, StagingName, StringComparison.Ordinal);

        private void OnInternalActiveSceneChanged(
            OriginalActiveSceneChanged original,
            Scene previousActiveScene,
            Scene newActiveScene)
        {
            if (eventFilter.IsDeferringTargetActivationChange)
            {
                deferredActiveSceneChangedInvoker = original;
            }

            eventFilter.HandleActiveSceneChanged(
                previousActiveScene,
                newActiveScene,
                (prev, next) => original(prev, next));
        }

        private void OnInternalSceneLoaded(
            OriginalSceneLoaded original,
            Scene scene,
            LoadSceneMode mode)
        {
            var serialTargetSceneName = transition != null && sourceUnloaded ? transition.TargetSceneName : null;
            try
            {
                eventFilter.HandleSceneLoaded(
                    scene,
                    mode == LoadSceneMode.Single,
                    serialTargetSceneName,
                    SelectAndRefreshTargetScene,
                    () => original(scene, mode),
                    (prev, next) =>
                    {
                        var invoker = deferredActiveSceneChangedInvoker
                            ?? activeSceneChangedHook.GenerateTrampoline<OriginalActiveSceneChanged>();
                        deferredActiveSceneChangedInvoker = null;
                        invoker(prev, next);
                    });
            }
            finally
            {
                deferredActiveSceneChangedInvoker = null;
            }
        }

        private void OnInternalSceneUnloaded(OriginalSceneUnloaded original, Scene scene)
        {
            eventFilter.HandleSceneUnloaded(scene, s => original(s));
        }

        private void SelectAndRefreshTargetScene(Scene scene)
        {
            // Select the target and refresh its dimensions before Start builds
            // scene borders from GameManager tilemap bounds.
            RenderState("before-early-select");
            if (!USceneManager.SetActiveScene(scene)) throw Fail("Cannot select serial target before Start.");
            // SceneManager.Start builds the four black scene borders from the
            // GameManager tilemap dimensions. The serialized activation yields
            // before ActivationComplete, so publish the target dimensions now,
            // before Start can use the smaller source-scene bounds.
            var manager = GameManager.UnsafeInstance;
            if (manager == null) throw Fail("Serial target has no GameManager.");
            manager.RefreshTilemapInfo(scene.name);
            RenderState("after-early-select");
        }

        private void RenderState(string phase)
        {
            if (report == null) return;
            var color = RenderSettings.ambientLight;
            File.AppendAllText(report + ".render.jsonl", JsonConvert.SerializeObject(new {
                phase, ordinal, engineFrame = Time.frameCount, activeScene = USceneManager.GetActiveScene().name,
                sceneWidth = GameManager.UnsafeInstance == null ? -1f : GameManager.UnsafeInstance.sceneWidth,
                sceneHeight = GameManager.UnsafeInstance == null ? -1f : GameManager.UnsafeInstance.sceneHeight,
                ambient = new[] { color.r, color.g, color.b, color.a }, RenderSettings.ambientIntensity,
                mode = RenderSettings.ambientMode.ToString()
            }) + "\n");
        }

        internal static void CaptureVisualState(long movie)
        {
            var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (output == null || movie != visualFrame) return;
            var path = Path.Combine(output, "visual-state-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
            if (File.Exists(path)) return;
            var color = RenderSettings.ambientLight;
            File.WriteAllText(path, JsonConvert.SerializeObject(new {
                movie, activeScene = USceneManager.GetActiveScene().name,
                ambient = new[] { color.r, color.g, color.b }, mode = RenderSettings.ambientMode.ToString(),
                pixelLights = QualitySettings.pixelLightCount,
                gates = TransitionPoint.TransitionPoints.Where(c => c != null && c.gameObject.scene.IsValid())
                    .Select(c => new { id = Identity(c), scene = c.gameObject.scene.name,
                        c.gameObject.activeInHierarchy, c.isADoor, c.targetScene, c.entryPoint,
                        c.alwaysEnterLeft, c.alwaysEnterRight, c.hardLandOnExit,
                        visualization = c.sceneLoadVisualization.ToString(),
                        position = new[] { c.transform.position.x, c.transform.position.y },
                        entryOffset = new[] { c.entryOffset.x, c.entryOffset.y }
                    }).ToArray(),
                sprites = Resources.FindObjectsOfTypeAll<SpriteRenderer>().Where(c => c.gameObject.scene.IsValid()
                    && c.gameObject.activeInHierarchy && c.enabled
                    && (c.bounds.size.y > 20 || c.gameObject.name == "Knight" || Identity(c).Contains("Marmu")))
                    .Select(c => new { id = Identity(c), c.forceRenderingOff, c.sortingLayerName, c.sortingOrder,
                        color = new[] { c.color.r, c.color.g, c.color.b, c.color.a },
                        sprite = c.sprite == null ? null : c.sprite.name,
                        texture = c.sprite == null || c.sprite.texture == null ? null : c.sprite.texture.name,
                        position = new[] { c.transform.position.x, c.transform.position.y, c.transform.position.z },
                        size = new[] { c.bounds.size.x, c.bounds.size.y, c.bounds.size.z }
                    }).ToArray(),
                cameras = Resources.FindObjectsOfTypeAll<Camera>().Where(c => c.gameObject.scene.IsValid()).Select(c => new {
                    id = Identity(c), c.enabled, c.gameObject.activeInHierarchy, c.cullingMask,
                    renderingPath = c.actualRenderingPath.ToString(), c.depth, c.nearClipPlane, c.farClipPlane,
                    position = new[] { c.transform.position.x, c.transform.position.y, c.transform.position.z }
                }).ToArray(),
                lights = Resources.FindObjectsOfTypeAll<Light>().Where(c => c.gameObject.scene.IsValid()).Select(c => new {
                    id = Identity(c), c.enabled, c.gameObject.activeInHierarchy, c.intensity, c.range,
                    c.cullingMask, type = c.type.ToString(), color = new[] { c.color.r, c.color.g, c.color.b },
                    position = new[] { c.transform.position.x, c.transform.position.y, c.transform.position.z }
                }).ToArray(),
                renderers = Resources.FindObjectsOfTypeAll<Renderer>().Where(c => c.gameObject.scene.name == "GG_Workshop"
                    && c.gameObject.activeInHierarchy && Identity(c).Contains("Statue")).Select(c => new {
                    id = Identity(c), c.enabled, c.gameObject.layer, c.lightmapIndex,
                    materials = c.sharedMaterials.Select(m => m == null ? null : new {
                        m.name, shader = m.shader == null ? null : m.shader.name,
                        texture = m.mainTexture == null ? null : m.mainTexture.name
                    }).ToArray()
                }).ToArray()
            }));
        }

        private void Write(string phase, object? data)
        {
            if (report == null) return;
            if (++rows > 256) throw new InvalidOperationException("Serial scene diagnostic budget exceeded.");
            File.AppendAllText(report, JsonConvert.SerializeObject(new { phase, ordinal,
                engineFrame = Time.frameCount, data }) + "\n");
        }

        [ThreadStatic] private static bool canonicalStartupAdmission = false;
        internal static void RequireCanonicalStartupAdmission()
        {
            if (!canonicalStartupAdmission) throw new InvalidOperationException("Canonical UI close is outside the admitted original pre-unload startup call.");
        }


        private InvalidOperationException Fail(string message)
        {
            failure = message;
            Write("failed", new { message });
            return new InvalidOperationException(message);
        }

        public void Dispose()
        {
            instance = null;
            sceneUnloadedHook.Dispose();
            sceneLoadedHook.Dispose();
            activeSceneChangedHook.Dispose();
            lightingHook.Dispose();
            transitionHook.Dispose();
            loadBeginHook.Dispose();
            beginHook.Dispose();
            unloadHook.Dispose();

            eventFilter.Reset();
            deferredActiveSceneChangedInvoker = null;
            transition = null;
            checkpointLoadInfo = null;
            checkpointTransitionRoutine = null;
            checkpointTransitionFinished = false;
            checkpointBossBindings = null;
            serialBeginRoutine = null;
            serialBeginCoroutine = null;
            source = default;
            sourceName = "";
            staging = default;
            sourceUnloaded = false;
            awaitingBoundary = false;
            observedBoundary = false;
            protectedClockCanonScope = null;
            protectedSourceCharmDerivationConsumed = false;
            protectedCompletedCoroutineDerivationConsumed = false;
            protectedSourceGeoDerivationConsumed = false;
            protectedSourceMarkerDerivationConsumed = false;
            protectedSourceBorderDerivationConsumed = false;
            protectedSourceMapKeyDerivationConsumed = false;
            protectedFinalVisualVisibilityConsumed = false;
            protectedHudControlsConsumed = false;
            retiredSourceOwners.Clear();
            ordinal = 0;
            rows = 0;
            failure = null;
        }
    }
}
