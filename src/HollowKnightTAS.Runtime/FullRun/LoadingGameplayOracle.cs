using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HollowKnightTAS.Core.SceneCheckpoints;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // P0's independent observation path. Native schedulers/physics internals,
    // arbitrary Mod statics and asset aliasing remain explicit coverage gaps.
    internal sealed partial class LoadingGameplayOracle : IDisposable
    {
        private readonly string directory;
        private readonly long first, end;
        private readonly LoadingOracleSamplingWindows samplingWindows;
        private long gameplaySamples;
        private long lastSampleMovieFrame = -1;
        private readonly Func<long> movieFrame, nativeFrame;
        private readonly long controlledNativeOrigin;
        private readonly Func<string> gameplayRng;
        private readonly MemoryStream buffer = new MemoryStream();
        private readonly MemoryStream suffixRandomTimeline = new MemoryStream();
        private int suffixRandomRows;
        private LoadingSuffixTweenCausality? suffixTweenCausality;
        private LoadingSuffixParticleCausality? suffixParticleCausality;
        private readonly LoadingFieldDigest fields;
        private readonly LoadingObservationIdentity observationIdentity;
        private readonly LoadingNativeValueObservation nativeValues;
        private readonly Dictionary<Fsm, string> fsmIds = new Dictionary<Fsm, string>(ReferenceComparer<Fsm>.Instance);
        private readonly Dictionary<object, string> fsmMemberIds = new Dictionary<object, string>(ReferenceComparer<object>.Instance);
        private readonly Dictionary<object, string> unityIds = new Dictionary<object, string>(ReferenceComparer<object>.Instance);
        private readonly Dictionary<string, int> sceneObjectOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly LoadingSceneRootIdentity sceneRoots = new LoadingSceneRootIdentity();
        private readonly bool boundaryCapture;
        private readonly bool suffixCapture;
        private BoundaryObjectIdentity? canonicalIdentities;
        private Dictionary<int, string>? hudSchedulerObjects;
        private Dictionary<IntPtr, string>? hudSchedulerBehaviours;
        private Tuple<Component, IntPtr>[]? hudSchedulerOwners;
        private bool hudSchedulerOwnerReuseAvailable;
        private long boundaryMovieFrame, boundaryNativeFrame;
        private readonly Dictionary<int, string> gameObjectPaths = new Dictionary<int, string>();
        private readonly LoadingUnityAssetIdentity assetIdentities = new LoadingUnityAssetIdentity();
        private readonly LoadingAssetProvenanceObservation assetProvenance = new LoadingAssetProvenanceObservation();
        private readonly List<Tuple<UnityEngine.Object, string>> assetRoots = new List<Tuple<UnityEngine.Object, string>>();
        private readonly CanonicalCoverageCatalog coverage = new CanonicalCoverageCatalog();
        private readonly List<CanonicalS0Observation.Root> s0Roots = new List<CanonicalS0Observation.Root>();
        private object? s0EventLock;
        private CanonicalS0ObservationSnapshot? s0Sample;
        private readonly List<string> s0InvariantFailures = new List<string>();
        private readonly Dictionary<UnityEngine.Object, string> unityCoverageLabels = new Dictionary<UnityEngine.Object, string>(ReferenceComparer<UnityEngine.Object>.Instance);
        private readonly IReadOnlyDictionary<string, string>? coverageHeroBindings;
        private CanonicalCoverageCatalog.Snapshot? lastCoverage;
        private long coverageMovie = -1, coverageNative = -1;
        private string coverageRoot = "", coverageField = "";
        private readonly Dictionary<Type, FieldInfo[]> coverageProvenanceFields = new Dictionary<Type, FieldInfo[]>();
        private object? coverageReadAudit;
        private readonly HashSet<int> assetInstances = new HashSet<int>();
        private readonly LoadingAssetValueObservation assetValues = new LoadingAssetValueObservation();
        private readonly BoundaryAssetArrayRegistry assetArrays = new BoundaryAssetArrayRegistry();
        private readonly SortedSet<string> omissions = new SortedSet<string>(StringComparer.Ordinal);
        private readonly SortedSet<string> detachedFsmReferences = new SortedSet<string>(StringComparer.Ordinal);
        private readonly LoadingGeoIncomingReferences geoIncoming = new LoadingGeoIncomingReferences();
        private readonly LoadingGeoIncomingReferences frozenGeoIncoming = new LoadingGeoIncomingReferences();
        private readonly LoadingGeoIncomingReferences markerIncoming = new LoadingGeoIncomingReferences();
        private readonly LoadingGeoIncomingReferences frozenMarkerIncoming = new LoadingGeoIncomingReferences();
        private bool frozenMarkerSample;
        private readonly LoadingGeoIncomingReferences frozenMenuFlashIncoming = new LoadingGeoIncomingReferences();
        private bool frozenMenuFlashSample;
        private readonly LoadingGeoIncomingReferences mapKeyIncoming = new LoadingGeoIncomingReferences();
        private readonly LoadingGeoIncomingReferences frozenMapKeyIncoming = new LoadingGeoIncomingReferences();
        private bool frozenMapKeySample;
        private readonly LoadingGeoIncomingReferences mapKeyParentIncoming = new LoadingGeoIncomingReferences();
        private bool readingMapKeyManagedRoots;
        private bool frozenGeoSample;
        private readonly List<object> detachedFsmMetadata = new List<object>();
        private readonly List<Tuple<object, string>> detachedFsmRoots = new List<Tuple<object, string>>();
        private int rows;
        private Stopwatch? captureProgress;
        private readonly List<object> captureStages = new List<object>();
        private long captureStageStarted;
        private string capturePhase = "", captureBoundary = "";
        private object? priorCapture;
        private bool sealedTrace;
        private object[] lastInventory = Array.Empty<object>();
        private object[] rootSurvey = Array.Empty<object>();
        private readonly SortedDictionary<string, int> nativeComponentSurvey = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private readonly List<object> nativeComponentDetails = new List<object>();
        private readonly List<object> textCacheDetails = new List<object>();
        private object[] lastFieldDiagnostics = Array.Empty<object>();
        private readonly HashSet<string> detailedTypes = new HashSet<string>(new[] {
            "DesktopPlatform", "GlobalPrefabDefaults", "ObjectPool", "InputHandler", "InControl.HollowKnightInputModule", "CheckpointSprite"
        }, StringComparer.Ordinal);
        private readonly Type? payloadType;
        private readonly LoadingNativeSchedulerDigest? nativeScheduler;
        private readonly LoadingNativePhysicsDigest? nativePhysics;
        private readonly LoadingRigidbodyObservation rigidbodies;
        private readonly bool fieldDetails;
        private readonly bool nativePhysicsOnly;
        private readonly bool serialBoundaryOnly;
        private readonly List<Action> detach = new List<Action>();
        private SceneLoad? currentTransition;
        private string transitionSource = "", transitionTarget = "";
        private int transitionOrdinal;
        private bool transitionSourceUnloaded, candidateCaptured;

        private LoadingGameplayOracle(string directory, long first, long end,
            Func<long> movieFrame, Func<long> nativeFrame, Func<string> gameplayRng, bool boundaryCapture = false, long? inputNativeOrigin = null, bool suffixCapture = false, string? identitySessionDirectory = null, bool physicsFollowOn = false)
        {
            this.physicsFollowOn = physicsFollowOn;
            this.boundaryCapture = boundaryCapture; this.suffixCapture = suffixCapture;
            if (boundaryCapture) { canonicalIdentities = new BoundaryObjectIdentity(); coverageHeroBindings = CanonicalHeroFsmBindingRegistry.Snapshot(); }
            this.directory = directory; this.first = first; this.end = end;
            samplingWindows = LoadingOracleSamplingWindows.Parse(
                boundaryCapture || suffixCapture ? null : Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_WINDOWS"), first, end);
            this.movieFrame = movieFrame; this.nativeFrame = nativeFrame; this.gameplayRng = gameplayRng;
            controlledNativeOrigin = inputNativeOrigin ?? nativeFrame();
            if (controlledNativeOrigin < 0) throw new InvalidOperationException("Input native frame origin is invalid.");
            fieldDetails = Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_FIELD_DETAILS") == "1";
            nativePhysicsOnly = !boundaryCapture && Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS_ONLY") == "1";
            serialBoundaryOnly = boundaryCapture || (!suffixCapture && Environment.GetEnvironmentVariable("HKTAS_LOADING_SERIAL_BOUNDARY_ONLY") == "1");
            if (serialBoundaryOnly && samplingWindows.ExplicitWindows)
                throw new InvalidOperationException("Candidate-only diagnostics cannot declare gameplay sampling windows.");
            observationIdentity = new LoadingObservationIdentity(identitySessionDirectory ?? directory);
            nativeValues = new LoadingNativeValueObservation(Reference);
            rigidbodies = new LoadingRigidbodyObservation();
            fields = new LoadingFieldDigest(Reference, type => type == typeof(MonoBehaviour)
                || type == typeof(UnityEngine.Object), (owner, field, value) =>
                    nativeValues.Field(owner, field, value) ?? observationIdentity.Field(owner, field, value), IsObservationCallback,
                    packedArrayElement: LoadingPackedUnityArrays.Enabled ? LoadingPackedUnityArrays.Registered : (Func<Type, bool>?)null,
                    // The registered native pointers, roots, GUIDs and integer
                    // epochs do not bind any Single/Double instance field.
                    directFloatingField: _ => true,
                    slotObservation: (owner, field, index, value, path) => {
                        geoIncoming.ObserveSlot(owner, field, index, value, path, fields!.DiagnosticRoot);
                        markerIncoming.ObserveSlot(owner, field, index, value, path, fields.DiagnosticRoot);
                        if (readingMapKeyManagedRoots) {
                            mapKeyIncoming.ObserveSlot(owner, field, index, value, path, fields.DiagnosticRoot);
                            mapKeyParentIncoming.ObserveSlot(owner, field, index, value, path, fields.DiagnosticRoot);
                            if (frozenMapKeySample) frozenMapKeyIncoming.ObserveSlot(owner, field, index, value, path, fields.DiagnosticRoot);
                        }
                        if (frozenMarkerSample) frozenMarkerIncoming.ObserveSlot(owner, field, index, value, path, fields.DiagnosticRoot);
                        if (frozenGeoSample) frozenGeoIncoming.ObserveSlot(owner, field, index, value, path, fields.DiagnosticRoot);
                        if (frozenMenuFlashSample) frozenMenuFlashIncoming.ObserveSlot(owner, field, index, value, path, fields.DiagnosticRoot);
                    }, observeSlotValue: value => geoIncoming.Owns(value) || markerIncoming.Owns(value) || mapKeyIncoming.Owns(value) || mapKeyParentIncoming.Owns(value) || frozenMapKeySample && frozenMapKeyIncoming.Owns(value) || frozenMarkerSample && frozenMarkerIncoming.Owns(value) || frozenGeoSample && frozenGeoIncoming.Owns(value) || frozenMenuFlashSample && frozenMenuFlashIncoming.Owns(value));
            if (!physicsFollowOn && (boundaryCapture || Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_SCHEDULER") == "1"))
                nativeScheduler = new LoadingNativeSchedulerDigest();
            if (physicsFollowOn || boundaryCapture || Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS") == "1")
                nativePhysics = new LoadingNativePhysicsDigest();
            if (nativePhysicsOnly && nativePhysics == null)
                throw new InvalidOperationException("Native-physics-only diagnostics require NativePhysics.");
            payloadType = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload")
                ?.GetType("HollowKnightTAS.ClockPayload.ClockController", false);
            if (boundaryCapture) return;
            if (physicsFollowOn) {
                Write(new { kind = "header", schema = "hktas-physics-follow-on-v1", first, end,
                    samplingScope = "after-original-input-at-absolute-movie-frame", completeStateCoverage = false });
                return;
            }
            Write(new { kind = "header", schema = "hktas-loading-oracle-v1", first, end,
                observerModuleId = typeof(LoadingGameplayOracle).Module.ModuleVersionId.ToString("D"),
                nativeProfile = HollowKnightTAS.Core.Movie.MovieProtocolV2.SelectedNativeProfileId,
                coverage = nativePhysicsOnly ? "native-physics-only-diagnostic-scope-managed-roots-not-observed-v1"
                    : "active-managed-fields-current-fsm-states-actions-and-all-roots-states-actions-at-window-endpoints-willactivate-loadfinish-v4",
                referencePolicy = LoadingSceneRootIdentity.Policy + "; assets-per-type-name-alias-ordinal; destroyed-wrapper-alias-ordinal; TAS-controller-references-by-type; readonly-probe-subscriptions-excluded-v1; protected-root-binding; registered-input-device-guid; input-tick-age; native-scheduler-timestamp-age-v1; dictionary-slots-v1; gradient-active-mode-v1; controlled-native-cursor-from-StartInputAt",
                nativeScheduler = nativeScheduler != null,
                nativeValueObservation = "curve-rect-text-guistyle-copy-queries; validated-steam-delegate-tables; pinned-enumerated-shader-property-sheet-v1",
                detachedFsmObservation = "expand-reachable-managed-FSM-state-action-graphs-with-shared-aliases-v1",
                hierarchyObservation = "loaded-GameObject-transform-recttransform-native-public-configuration-and-animator-particle-copy-v3",
                registeredStaticObservation = "initialized-ModLoader-ModHooks-InputManager-CustomKnight-SwapManager-EnviousMarmu-v1",
                copyValueReferencePolicy = "expand-anonymous-value-records-and-all-array-contents-v3-type-and-field-schema-identity",
                leafArrayValueEncoding = LoadingPackedUnityArrays.Enabled
                    ? "registered-padding-free-layout-schema-and-full-little-endian-bits-sha256-v1-logical-node-depth-budgets-retained"
                    : "legacy-recursive-field-tokens",
                hashSetReferencePolicy = "rebind-active-hashes-buckets-links-preserve-slots-free-order-capacity-version-comparer-v1",
                activeInputListEpochPolicy = "registered-list-origin-retain-enumerator-modular-version-delta-v1",
                assetAliasPolicy = "first-encounter-per-type-name-native-aliases-and-zero-instance-managed-wrapper-aliases-v2-values-not-closed",
                assetProvenancePolicy = "pinned-persistent-remapper-read-only-source-binding-v1-live-values-not-closed",
                nativePhysics = nativePhysics == null ? "disabled" : "pinned-box2d-bound-values-and-explicit-native-order-v2-diagnostic",
                rigidbodyObservation = LoadingRigidbodyValueBits.Policy,
                rigidbodyValueRanges = new { owner = LoadingRigidbodyValueBits.OwnerRanges, body = LoadingRigidbodyValueBits.BodyRanges,
                    pointerBindings = nativePhysics == null ? "unobserved" : "separate-native-physics-oracle" },
                candidateBoundary = HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesSerialScenes(
                    HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)
                    ? "serial-source-unloaded-target-not-started-native-before-frame-v1"
                    : nativePhysics == null ? "disabled" : "first-native-before-player-loop-SceneTransition-after-source-unload-with-target-active-v1",
                fieldDiagnostics = fieldDetails,
                nativePhysicsEntryDiagnostics = Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS_DETAILS") == "1",
                diagnosticInventoryEncoding = LoadingInventoryRootEncoding.Format,
                nativePhysicsInventorySampling = "full-boundaries-and-window-endpoints; all-value-and-order-digests-every-sample-v1",
                samplingScope = suffixCapture ? "actual-d-suffix-all-loaded-three-after-v1" : serialBoundaryOnly ? "serial-candidate-boundaries-only-diagnostic"
                    : samplingWindows.ExplicitWindows ? "bounded-transition-windows-v1" : "bounded-window",
                gameplayWindows = samplingWindows.ExplicitWindows ? samplingWindows.Snapshot() : null,
                managedFieldGraph = "ordered-component-fsm-root-shared-references-v1; scheduler-and-SceneLoad-scopes-independent",
                completeStateCoverage = false,
                knownMissing = new[] { "native-coroutine-running-callstacks-and-unregistered-schedulers", "native-physics-contact-caches",
                    "arbitrary-mod-statics", "ambiguous-asset-identities", "ambiguous-scene-object-identities", "dormant-roots-between-window-endpoints-willactivate-loadfinish",
                    "TAS-controller-internals-require-separate-cursor-input-codecs",
                    "native-Unity-components-and-mutable-assets", "external-Steam-event-queue-and-account-state" } });
            if (suffixCapture) return; // The suffix observer adds no game callback or coroutine.
            GameManager.SceneTransitionBegan += TransitionBegan;
            USceneManager.sceneLoaded += SceneLoaded;
            USceneManager.sceneUnloaded += SceneUnloaded;
            USceneManager.activeSceneChanged += ActiveSceneChanged;
        }

        internal static LoadingGameplayOracle? Create(string directory, Func<long> movieFrame,
            Func<long> nativeFrame, Func<string> gameplayRng)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_GAMEPLAY_ORACLE") != "1") return null;
            // Actual D retains the independent full canonical boundary reader and
            // creates its separate suffix reader at resume; avoid duplicate B scans.
            if (CanonicalActualDExperiment.Enabled) return null;
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CLOCK_ROUNDTRIP") == "1")
                throw new InvalidOperationException("The read-only gameplay oracle cannot run with clock-write diagnostics.");
            if (!long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_FIRST"), out var first)
                || !long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_END"), out var end)
                || first < 0 || end <= first || end - first > 10000)
                throw new InvalidOperationException("P0 gameplay oracle requires a bounded Movie window.");
            return new LoadingGameplayOracle(directory, first, end, movieFrame, nativeFrame, gameplayRng);
        }

        internal static LoadingGameplayOracle CreateBoundaryObserver(string directory, long inputNativeOrigin)
        {
            var rng = HollowKnightTAS.Runtime.Rng.UnityRandomStateCodec_1_5_78_11833.Resolve().Codec
                ?? throw new InvalidOperationException("Boundary digest RNG codec is unavailable.");
            LoadingGameplayOracle? observer = null;
            observer = new LoadingGameplayOracle(directory, 0, 1,
                () => observer?.boundaryMovieFrame ?? 0, () => observer?.boundaryNativeFrame ?? 0,
                () => rng.CaptureCurrent().Sha256, boundaryCapture: true, inputNativeOrigin: inputNativeOrigin);
            return observer;
        }

        internal static LoadingGameplayOracle CreateActualDSuffix(string directory, string sessionDirectory, long first, long end,
            long inputNativeOrigin, Func<long> movieFrame, Func<long> nativeFrame, Func<string> gameplayRng)
        {
            if (first < 0 || end != first + 3)
                throw new InvalidOperationException("Actual D suffix observation requires its bounded original window.");
            Directory.CreateDirectory(directory);
            return new LoadingGameplayOracle(directory, first, end, movieFrame, nativeFrame, gameplayRng,
                inputNativeOrigin: inputNativeOrigin, suffixCapture: true, identitySessionDirectory: sessionDirectory);
        }

        private Func<int, string?>? boundaryRetiredOwner;
        internal BoundaryDigestSnapshot CaptureBoundaryFrame(long movie, long native, Func<int, bool> ownsNewPoolStart,
            Func<int, string?>? retiredOwner = null)
        {
            if (!boundaryCapture) throw new InvalidOperationException("The observer is not bound to a canonical boundary.");
            if (native < controlledNativeOrigin) throw new InvalidOperationException("Boundary native cursor precedes its input frame origin.");
            using var validation = new LoadingEngineClockProbe.CheckpointValidation();
            boundaryMovieFrame = movie; boundaryNativeFrame = native;
            boundaryRetiredOwner = retiredOwner;
            captureProgress = Stopwatch.StartNew(); captureStages.Clear(); captureStageStarted = 0;
            capturePhase = "canonical-boundary"; captureBoundary = "source-unloaded-target-not-started";
            var groups = CaptureState(fullInventory: true);
            s0Sample = new CanonicalS0Observation(CanonicalS0FieldOwnership.Classify, S0ExternalBinding,
                fields.Fields, fields.Read, CanonicalS0FieldOwnership.KnownOwner).Capture(s0Roots);
            s0Sample.InvariantFailures = s0InvariantFailures.ToArray();
            groups["movieFrame"] = movie.ToString(CultureInfo.InvariantCulture);
            groups["nativeFrame"] = native.ToString(CultureInfo.InvariantCulture);
            var observedCoverage = coverage.Capture(omissions);
            var repeatedCoverage = lastCoverage != null && coverageMovie == movie && coverageNative == native;
            var coverageEqual = !repeatedCoverage || lastCoverage!.ReadSha256 == observedCoverage.ReadSha256;
            coverageReadAudit = new { repeatedRead = repeatedCoverage, observedEqual = coverageEqual,
                expectedReadSha256 = repeatedCoverage ? lastCoverage!.ReadSha256 : null,
                actualReadSha256 = observedCoverage.ReadSha256, records = observedCoverage.Records.Length,
                completeStateCoverage = false };
            if (!coverageEqual) throw new InvalidOperationException("Boundary coverage ownership reader changed its frozen sample.");
            lastCoverage = observedCoverage; coverageMovie = movie; coverageNative = native;
            // The capture remains unqualified while P0's explicitly reported
            // native/asset domains and the new carry-field registry are open.
            return new BoundaryDigestSnapshot(groups, lastInventory, omissions.ToArray(),
                nativeScheduler!.Inventory, nativePhysics!.Inventory,
                nativeScheduler.CanonicalWaitIssues(HeroController.SilentInstance, ownsNewPoolStart));
        }

        internal Action RequireFrozenS0Scheduler() => (nativeScheduler ?? throw new InvalidOperationException("Missing original scheduler observation.")).RequireFrozenCapturedQueue();
        internal LoadingGeoIncomingReferences FreezeGeoIncoming() => geoIncoming.Freeze();
        internal LoadingGeoIncomingReferences FreezeMarkerIncoming() => markerIncoming.Freeze();
        internal LoadingGeoIncomingReferences FreezeMapKeyIncoming() => mapKeyIncoming.Freeze();
        internal LoadingGeoIncomingReferences FreezeMapKeyParentIncoming() => mapKeyParentIncoming.Freeze();
        internal Action PrepareOwnedHudLifecycle(IReadOnlyDictionary<int, MonoBehaviour> oldOwners, Func<int, bool> ownsNewStart, out Action retireStarts)
            => (nativeScheduler ?? throw new InvalidOperationException("Missing original scheduler observation.")).BeginOwnedStartLifecycle(oldOwners, ownsNewStart, out retireStarts);
        // The original owned-lifecycle recapture already assigned these exact
        // owner names. No native owner lifecycle or PlayerLoop intervenes before
        // the controlled queue write. Reuse only this one frozen directory.
        internal (string Prefix, IReadOnlyDictionary<int, string> Names, Action Guard) PrepareProducedGeoSchedulerNames(
            GeoCounter root, IReadOnlyDictionary<int, MonoBehaviour> produced)
            => PrepareProducedSchedulerNames(root.gameObject, produced);
        internal (string Prefix, IReadOnlyDictionary<int, string> Names, Action Guard) PrepareProducedMarkerSchedulerNames(
            GameObject root, IReadOnlyDictionary<int, MonoBehaviour> produced)
        {
            var exact = CanonicalMarkerDirectory.Build(root).Values.OfType<MonoBehaviour>().ToArray();
            if (produced.Count != 8 || exact.Length != 8 || exact.Any(owner => !produced.TryGetValue(owner.GetInstanceID(), out var actual) || !ReferenceEquals(owner, actual)))
                throw new InvalidOperationException("Marker produced scheduler requires its exact eight original owners.");
            return PrepareProducedSchedulerNames(root, produced);
        }
        private (string Prefix, IReadOnlyDictionary<int, string> Names, Action Guard) PrepareProducedSchedulerNames(
            GameObject root, IReadOnlyDictionary<int, MonoBehaviour> produced)
        {
            var objects = hudSchedulerObjects; var behaviours = hudSchedulerBehaviours; var frozen = hudSchedulerOwners;
            if (objects == null || behaviours == null || frozen == null || !hudSchedulerOwnerReuseAvailable
                || !objects.TryGetValue(root.GetInstanceID(), out var prefix))
                throw new InvalidOperationException("Produced Geo has no unused frozen scheduler owner directory.");
            var pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var selected = produced.ToDictionary(pair => pair.Key, pair => frozen.Single(item => ReferenceEquals(item.Item1, pair.Value)));
            var names = produced.ToDictionary(pair => pair.Key, pair => objects[pair.Key]);
            void Guard() {
                if (!hudSchedulerOwnerReuseAvailable || !ReferenceEquals(objects, hudSchedulerObjects)
                    || !ReferenceEquals(behaviours, hudSchedulerBehaviours) || !ReferenceEquals(frozen, hudSchedulerOwners)
                    || !objects.TryGetValue(root.GetInstanceID(), out var currentPrefix) || currentPrefix != prefix)
                    throw new InvalidOperationException("Produced Geo scheduler directory was consumed or replaced.");
                foreach (var pair in produced) {
                    var owner = pair.Value; var saved = selected[pair.Key];
                    if (owner is null || owner == null) throw new InvalidOperationException("Produced Geo frozen owner was destroyed.");
                    var currentPointer = (IntPtr)pointer.GetValue(owner);
                    var name = CanonicalGeoSourceQueueContract.ResolveFrozenOwner(saved.Item1, saved.Item2, names[pair.Key],
                        owner, currentPointer, Loaded(owner.gameObject));
                    if (owner.GetInstanceID() != pair.Key || !objects.TryGetValue(pair.Key, out var currentName) || currentName != name
                        || !behaviours.TryGetValue(currentPointer, out var nativeName) || nativeName != name)
                        throw new InvalidOperationException("Produced Geo native pointer and logical owner aliases differ.");
                }
            }
            Guard();
            return (prefix, names, Guard);
        }
        internal Action RestoreAbsentProducedGeoStarts(CanonicalSchedulerCarry source, IReadOnlyDictionary<int, MonoBehaviour> produced,
            string prefix, IReadOnlyDictionary<int, string> names, Action requireDirectory, Action admission, Action requireSourceNative)
            => (nativeScheduler ?? throw new InvalidOperationException("Missing original Geo scheduler observation.")).RestoreAbsentProducedGeoStarts(
                source, produced, prefix, names, requireDirectory,
                HeroController.SilentInstance ?? throw new InvalidOperationException("Missing Geo source Hero."), admission, requireSourceNative);
        internal Action RestoreAbsentProducedMarkerStarts(CanonicalSchedulerCarry source, IReadOnlyDictionary<int, MonoBehaviour> produced,
            string prefix, IReadOnlyDictionary<int, string> names, Action requireDirectory, Action admission, Action requireSourceNative)
            => (nativeScheduler ?? throw new InvalidOperationException("Missing Marker scheduler observation.")).RestoreAbsentProducedMarkerStarts(
                source, produced, prefix, names, requireDirectory,
                HeroController.SilentInstance ?? throw new InvalidOperationException("Missing Marker source Hero."), admission, requireSourceNative);
        internal void RecaptureOwnedHudScheduler(Action admission, Action requireTransition)
        {
            if (!boundaryCapture || nativeScheduler == null) throw new InvalidOperationException("HUD lifecycle requires its original boundary reader.");
            admission();
            var identities = new BoundaryObjectIdentity();
            var owners = Resources.FindObjectsOfTypeAll<Component>().Where(value => value != null && Loaded(value.gameObject)).ToArray();
            if (owners.Length > 100000) throw new InvalidOperationException("HUD scheduler owner inventory exceeded its bound.");
            var pointers = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var objects = new Dictionary<int, string>(); var behaviours = new Dictionary<IntPtr, string>();
            foreach (var owner in owners) {
                var id = identities.Get(owner); objects[owner.GetInstanceID()] = id;
                objects[owner.gameObject.GetInstanceID()] = identities.Get(owner.gameObject);
                var pointer = (IntPtr)pointers.GetValue(owner);
                if (owner is MonoBehaviour && pointer != IntPtr.Zero) behaviours.Add(pointer, id);
            }
            hudSchedulerObjects = objects; hudSchedulerBehaviours = behaviours;
            hudSchedulerOwners = owners.Select(owner => Tuple.Create(owner, (IntPtr)pointers.GetValue(owner))).ToArray();
            hudSchedulerOwnerReuseAvailable = true;
            // Asset callbacks are not implicitly admitted in this explicit
            // lifecycle. All surviving original callbacks must remain bound.
            nativeScheduler.Capture(fields, id => objects.TryGetValue(id, out var value) ? value : null,
                pointer => behaviours.TryGetValue(pointer, out var value) ? value : null);
            admission(); requireTransition();
            if (nativeScheduler.Unsupported.Length != 0) throw new InvalidOperationException("HUD lifecycle introduced native scheduler coverage gaps.");
        }
        // Only between the registered scheduler scalar write and subsequent
        // object birth: no managed/native owner lifecycle occurs in this slice.
        // Reuse identities, never a captured queue, prepared guard or readback.
        internal void RecaptureHudSchedulerAfterScalarWrite(Action admission) => RecaptureHudSchedulerAfterNonOwningWrite(admission);
        // Original pending-Start cancellation changes only scheduler nodes.
        // The preceding owned lifecycle already froze these real Unity owners;
        // every loaded/native pointer is checked and the complete queue is new.
        internal void RecaptureHudSchedulerAfterQueueWrite(Action admission, Action requireQueueReadback)
        {
            RecaptureHudSchedulerAfterNonOwningWrite(admission);
            requireQueueReadback();
        }
        private void RecaptureHudSchedulerAfterNonOwningWrite(Action admission)
        {
            if (!boundaryCapture || nativeScheduler == null || !hudSchedulerOwnerReuseAvailable
                || hudSchedulerObjects == null || hudSchedulerBehaviours == null || hudSchedulerOwners == null)
                throw new InvalidOperationException("HUD scalar readback has no unused actual owner directory.");
            hudSchedulerOwnerReuseAvailable = false;
            admission();
            var pointers = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var owner in hudSchedulerOwners) {
                if (owner.Item1 == null || !Loaded(owner.Item1.gameObject) || (IntPtr)pointers.GetValue(owner.Item1) != owner.Item2)
                    throw new InvalidOperationException("HUD scalar write changed an actual native owner identity.");
            }
            nativeScheduler.Capture(fields, id => hudSchedulerObjects.TryGetValue(id, out var value) ? value : null,
                pointer => hudSchedulerBehaviours.TryGetValue(pointer, out var value) ? value : null);
            admission();
            if (nativeScheduler.Unsupported.Length != 0)
                throw new InvalidOperationException("HUD scalar fresh native readback has a coverage gap.");
        }
        internal Action PrepareActualScheduler(CanonicalSchedulerCarry source, Func<bool> admission)
            => (nativeScheduler ?? throw new InvalidOperationException("Missing original scheduler observation.")).PrepareCanonicalRestore(source,
                HeroController.SilentInstance ?? throw new InvalidOperationException("Missing actual Hero."), admission);
        internal Action PrepareActualSchedulerVerification(CanonicalSchedulerCarry source, Func<bool> admission)
            => (nativeScheduler ?? throw new InvalidOperationException("Missing original scheduler observation.")).PrepareCanonicalVerification(source,
                HeroController.SilentInstance ?? throw new InvalidOperationException("Missing actual Hero."), admission);
        internal CanonicalS0ObservationSnapshot RecaptureBoundaryS0()
        {
            var sample = new CanonicalS0Observation(CanonicalS0FieldOwnership.Classify, S0ExternalBinding,
                fields.Fields, fields.Read, CanonicalS0FieldOwnership.KnownOwner).Capture(s0Roots);
            sample.InvariantFailures = s0InvariantFailures.ToArray();
            return sample;
        }
        internal object BoundaryFieldSchema()
        {
            if (!boundaryCapture) throw new InvalidOperationException("Field schema is not bound to a canonical boundary.");
            return fields.Schema();
        }
        internal CanonicalS0ObservationSnapshot BoundaryS0Observation() => s0Sample
            ?? throw new InvalidOperationException("No canonical S0 observation is available.");
        internal object BoundaryDetachedFsmDiagnostic() => new {
            diagnosticOnly = true, nativeFrame = boundaryNativeFrame,
            references = detachedFsmReferences.ToArray(), metadata = detachedFsmMetadata.ToArray(),
            completeStateCoverage = false };
        private string? S0ExternalBinding(object value)
        {
            if (ReferenceEquals(value, s0EventLock)) return "canonical-lock:FsmEvent.syncObj";
            if (value is Type metadataType)
                return "metadata-type:" + metadataType.Module.ModuleVersionId.ToString("D") + "/" + metadataType.FullName;
            if (unityIds.TryGetValue(value, out var unityBinding))
            {
                // Destroyed wrappers have no closed logical rebind contract.
                // In particular, a fake-null ordinal from the raw graph must
                // never become an S0 identity that depends on preceding D.
                if (value is UnityEngine.Object unityValue && unityCoverageLabels.TryGetValue(unityValue, out var issue)
                    && issue.StartsWith("destroyed-unity-wrapper", StringComparison.Ordinal)) return null;
                return "unity:" + unityBinding;
            }
            var owner = value as Fsm ?? (value is FsmState || value is FsmStateAction ? fields.Read(value, "fsm") as Fsm : null);
            if (owner != null && s0Roots.Any(root => root.Domain == CanonicalS0Observation.Ownership.D && ReferenceEquals(root.Value, owner)))
            {
                if (value is Fsm && fsmIds.TryGetValue(owner, out var fsmBinding)) return "D-fsm:" + fsmBinding;
                if (fsmMemberIds.TryGetValue(value, out var memberBinding)) return "D-fsm-member:" + memberBinding;
            }
            return null;
        }
        internal bool BoundarySchedulerRestorePrepared { get; private set; }
        internal CanonicalSchedulerCarry BoundarySchedulerCarry(CanonicalInputEpoch epoch)
        {
            BoundarySchedulerRestorePrepared = false;
            if (!boundaryCapture || epoch.SourceNative != boundaryNativeFrame || nativeScheduler == null)
                throw new InvalidOperationException("Scheduler carry is outside its original boundary observation.");
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Scheduler carry Hero is absent.");
            var data = nativeScheduler.CaptureCanonicalCarry(boundaryNativeFrame, hero);
            data.RequireEpoch(epoch);
            // Source-side read-only binding audit. The deliberately false
            // pause guard also prevents accidental execution of this action.
            nativeScheduler.PrepareCanonicalRestore(data, hero, () => false);
            BoundarySchedulerRestorePrepared = true;
            return data;
        }
        internal CheckpointHeroInvulnerability BoundaryHeroInvulnerabilityCarry(CanonicalInputEpoch epoch)
        {
            if (!boundaryCapture || !BoundarySchedulerRestorePrepared || epoch.SourceNative != boundaryNativeFrame || nativeScheduler == null)
                throw new InvalidOperationException("Hero wait carry is outside its original scheduler boundary sample.");
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Hero wait carry owner is absent.");
            return CheckpointHeroInvulnerability.CaptureCanonical(hero, nativeScheduler);
        }

        internal CanonicalCoverageCatalog.Snapshot BoundaryCoverageCatalog() => lastCoverage?.Copy()
            ?? throw new InvalidOperationException("No canonical boundary coverage sample is available.");
        internal object BoundaryCoverageReadAudit() => coverageReadAudit
            ?? throw new InvalidOperationException("No canonical boundary coverage read audit is available.");

        private void RegisterCoverageFsm(string root, Fsm fsm)
        {
            if (!boundaryCapture || coverageHeroBindings == null) return;
            var sceneEnd = root.IndexOf('/');
            if (sceneEnd < 0) return;
            var path = root.Substring(sceneEnd + 1);
            if (!coverageHeroBindings.TryGetValue(path, out var expected) || !Equals(fields.Read(fsm, "name"), expected)) return;
            var visual = CanonicalHeroFsmBindingRegistry.IsCanonicalVisual(path, expected);
            s0Roots.Add(new CanonicalS0Observation.Root(root, fsm, visual
                ? CanonicalS0Observation.Ownership.S0 : CanonicalS0Observation.Ownership.D));
            coverage.BindRoot(root, visual ? CanonicalCoverageCatalog.Domain.S0 : CanonicalCoverageCatalog.Domain.D,
                visual ? "canonical-hero-visual-FSM-registry" : "canonical-hero-FSM-carry-registry");
        }

        private void RegisterCoverageStatic(string root)
        {
            if (!boundaryCapture) return;
            if (root == "static:PlayMaker:HutongGames.PlayMaker.Fsm.EventData"
                || root.StartsWith("static:PlayMaker:HutongGames.PlayMaker.FsmEvent.", StringComparison.Ordinal)
                    && !root.EndsWith(".syncObj", StringComparison.Ordinal))
                coverage.BindRoot(root, CanonicalCoverageCatalog.Domain.D, "canonical-event-carry-roots");
            else if (new[] { "FsmExecutionStack", "ActionData", "ReflectionUtils" }.Any(type =>
                root.StartsWith("static:PlayMaker:HutongGames.PlayMaker." + type + ".", StringComparison.Ordinal)))
                coverage.BindRoot(root, CanonicalCoverageCatalog.Domain.S0, "canonical-static-policy-reset");
        }

        private void RecordCoverageGap(string label, string root, string field, string? objectId)
        {
            omissions.Add(label);
            if (boundaryCapture) coverage.Record(label, root, field, objectId);
        }

        private void RecordUnityCoverage(UnityEngine.Object value, string id)
        {
            if (!boundaryCapture || !unityCoverageLabels.TryGetValue(value, out var label)) return;
            coverage.Record(label, coverageRoot.Length == 0 ? "<observer-object-binding>" : coverageRoot,
                coverageField.Length == 0 ? "$reference-binding" : coverageField, id);
        }

        private string? RegisterCoverageComponent(Component component)
        {
            if (!boundaryCapture) return null;
            var id = UnityId(component);
            coverage.RegisterObject(id, component.GetType().FullName!, "loaded-native-component", component.GetInstanceID());
            return id;
        }

        private void RegisterCoverageAsset(string id, string type, int instance, object provenance)
        {
            // Provenance is a closed Runtime-owned scalar DTO. Copy its existing
            // fields, not properties or any Unity object, and perform no new
            // native query. This preserves the original alias discovery order.
            var dataType = provenance.GetType();
            if (!coverageProvenanceFields.TryGetValue(dataType, out var bound))
            {
                if (dataType.Assembly != typeof(LoadingAssetProvenanceObservation).Assembly
                    || !dataType.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    throw new InvalidOperationException("Coverage provenance is not the original Runtime scalar record.");
                var raw = dataType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                var fieldNames = new[] { "<kind>i__Field", "<sourcePath>i__Field", "<localIdentifier>i__Field" };
                bound = fieldNames.Select(name => raw.SingleOrDefault(field => field.Name == name)
                    ?? throw new InvalidOperationException("Coverage provenance raw field is absent: " + name)).ToArray();
                if (raw.Length != 3 || bound[0].FieldType != typeof(string) || bound[1].FieldType != typeof(string) || bound[2].FieldType != typeof(long))
                    throw new InvalidOperationException("Coverage provenance raw layout differs.");
                coverageProvenanceFields.Add(dataType, bound);
            }
            coverage.RegisterObject(id, type, "asset-reference", instance,
                (string)bound[0].GetValue(provenance)!, (string)bound[1].GetValue(provenance)!, (long)bound[2].GetValue(provenance)!);
        }

        private static string AssetCoverageField(string label)
        {
            switch (label)
            {
                case "asset-font-atlas-cpu-data-unavailable": return "$native.FontImpl.atlas";
                case "asset-font-dynamic-face-and-packer-codec": return "$native.FontImpl.face-and-packer";
                case "asset-render-texture-gpu-contents": return "$native.RenderTexture.gpuContents";
                case "asset-sprite-native-uv-physics-and-skinning-data": return "$native.Sprite.rendering-physics-skinning-data";
                case "asset-texture-cpu-data-unreadable": return "$native.Texture2D.cpuData";
                case "asset-texture-native-copy-contract": return "$native.Texture2D.copyContract";
                case "asset-mesh-cpu-data-unreadable": return "$native.Mesh.cpuData";
                case "asset-mesh-native-skinning-data": return "$native.Mesh.skinningData";
                case "asset-material-unsupported-property-sheet": return "$native.Material.propertySheet";
                default: return "$native.resource-values";
            }
        }

        private static string NativeCoverageField(string label)
        {
            switch (label)
            {
                case "native-animator-hidden-controller-state": return ".running.$hidden-controller-state";
                case "native-animator-trigger-latch": return ".running.parameters.$trigger-latch";
                case "native-particle-pending-sync-not-observed": return ".playback.$pending-sync";
                default: return ".$native-state";
            }
        }

        internal void ObserveSuffixRandomBoundary(string phase, bool ready, string boundary)
        {
            try { ObserveSuffixRandomBoundaryCore(phase, ready, boundary); }
            catch (Exception error) {
                try {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "suffix-lifecycle-reader-fault.json"), JsonConvert.SerializeObject(new {
                        diagnosticOnly = true, phase, movieFrame = movieFrame(), nativeFrame = nativeFrame(),
                        error = error.GetType().FullName + ": " + error.Message, completeStateCoverage = false }));
                } catch { } // the original observer failure still faults the native gate
                throw;
            }
        }
        private void ObserveSuffixRandomBoundaryCore(string phase, bool ready, string boundary)
        {
            if (!suffixCapture || sealedTrace) return;
            suffixTweenCausality ??= new LoadingSuffixTweenCausality(() => new {
                movieFrame = movieFrame(), nativeFrame = nativeFrame(), gameplayRng = gameplayRng(),
                particleManagerRandom = CaptureParticleRandomState() }, UnityId);
            suffixTweenCausality.RequireHealthy();
            var beforeRng = gameplayRng(); var beforeParticle = CaptureParticleRandomState();
            suffixParticleCausality ??= new LoadingSuffixParticleCausality(UnityId);
            suffixParticleCausality.Observe(phase, movieFrame(), nativeFrame());
            var widgets = CaptureSuffixWidgetLifecycle();
            if (beforeRng != gameplayRng() || !beforeParticle.SequenceEqual(CaptureParticleRandomState()))
                throw new InvalidOperationException("Suffix lifecycle reader changed RNG state during its independent read.");
            if (suffixRandomRows >= 256 || suffixRandomTimeline.Length > 1024*1024)
                throw new InvalidOperationException("Actual D native suffix random observation exceeded its diagnostic bound.");
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
                diagnosticOnly = true, sequence = suffixRandomRows++, phase, ready, boundary,
                movieFrame = movieFrame(), nativeFrame = nativeFrame(), gameplayRng = gameplayRng(),
                particleManagerRandom = CaptureParticleRandomState(), widgets, completeStateCoverage = false }) + "\n");
            suffixRandomTimeline.Write(bytes, 0, bytes.Length);
        }
        private object CaptureSuffixWidgetLifecycle()
        {
            var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras
                ?? throw new InvalidOperationException("Suffix widget original camera owner is absent.");
            var rawField = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return new[] { "Low Health Vignette", "Inventory/Charms/Equipped Charms/Next Dot" }.Select(path => {
                var node = cameras.hudCamera.transform.Find(path);
                if (node == null && path == "Low Health Vignette") {
                    var children = cameras.hudCamera.GetComponentsInChildren<Transform>(true);
                    if (children.Length > 10000) throw new InvalidOperationException("Original HUD widget directory exceeded its bound.");
                    var matches = children.Where(value => value.name == "Low Health Vignette").ToArray();
                    if (matches.Length > 1) throw new InvalidOperationException("Exact Low Health Vignette owner is ambiguous inside original HUD.");
                    node = matches.SingleOrDefault();
                }
                var parents = new List<object>();
                for (var current = node; current != null; current = current.parent) {
                    if (parents.Count >= 16) throw new InvalidOperationException("Suffix widget parent chain exceeded its bound.");
                    parents.Add(new { owner = UnityId(current.gameObject), activeSelf = current.gameObject.activeSelf, active = current.gameObject.activeInHierarchy });
                }
                return new { path, found = node != null, parents, fsms = (node == null ? Array.Empty<PlayMakerFSM>() : node.GetComponents<PlayMakerFSM>()).Select(owner => {
                    var raw = rawField.GetValue(owner) as Fsm;
                    var active = raw == null ? null : typeof(Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(raw) as FsmState;
                    return new { owner = UnityId(owner), owner.enabled, lifecycle = BoundaryMonoLifecycle.ObserveUnstartedUi(owner),
                        uiLifecycleQualified = false, name = raw?.Name, started = raw?.Started, state = active?.Name };
                }).ToArray() };
            }).ToArray();
        }

        internal void Sample(string phase, string boundary)
        {
            if (sealedTrace || !samplingWindows.Contains(movieFrame()) || (suffixCapture && phase != "after")) return;
            if (physicsFollowOn) { SamplePhysicsFollowOn(boundary); return; }
            if (serialBoundaryOnly && phase != "candidate") return;
            if (CanonicalPhaseOneTrace.D0RouteEnabled && phase == "candidate" && boundary == "serial-source-unloaded") {
                CanonicalPhaseOneTrace.RequireContext(Environment.GetEnvironmentVariable);
                if (!serialBoundaryOnly || boundaryCapture || suffixCapture)
                    throw new InvalidOperationException("D0 route duplicate observation scope differs.");
                Write(new { kind = "observation-not-run", phase, boundary, movieFrame = movieFrame(), nativeFrame = nativeFrame(),
                    reason = "D0-route-has-complete-Canon-boundary-digest", p0CandidateObserved = false, completeStateCoverage = false });
                return;
            }
            try
            {
                var captureElapsed = Stopwatch.StartNew();
                captureProgress = captureElapsed;
                captureStages.Clear();
                captureStageStarted = 0;
                capturePhase = phase; captureBoundary = boundary;
                CaptureProgress("enumerate-roots");
                var serialCandidate = phase == "candidate" && boundary == "serial-source-unloaded";
                var candidate = serialCandidate || (nativePhysics != null && !candidateCaptured && currentTransition != null
                    && phase == "loading" && boundary == "SceneTransition" && transitionSourceUnloaded
                    && USceneManager.GetActiveScene().name == transitionTarget);
                var fullInventory = suffixCapture || (phase == "event" && (boundary == "will-activate" || boundary == "load-finish"))
                    || samplingWindows.IsEndpoint(movieFrame(), phase) || candidate;
                var state = CaptureState(fullInventory);
                lastSampleMovieFrame = movieFrame();
                if (nativePhysics != null)
                {
                    state["transitionOrdinal"] = transitionOrdinal.ToString(CultureInfo.InvariantCulture);
                    state["transitionSource"] = transitionSource;
                    state["transitionTarget"] = transitionTarget;
                    state["sourceUnloaded"] = transitionSourceUnloaded.ToString();
                    state["candidateBoundary"] = candidate ? "C" : "none";
                }
                if (candidate)
                {
                    var load = fields.Capture(currentTransition, "transition[" + transitionOrdinal + "]/SceneLoad", expandRoot: true);
                    foreach (var omitted in load.Unsupported) omissions.Add(omitted);
                    state["candidateSceneLoadFields"] = load.Sha256;
                    state["candidateSceneLoadNodes"] = load.Nodes.ToString(CultureInfo.InvariantCulture);
                    state["candidateNativeAsyncOperations"] = (nativeScheduler?.AsyncOperationCount ?? -1).ToString(CultureInfo.InvariantCulture);
                    state["unsupported"] = Hash(string.Join("\n", omissions));
                    candidateCaptured = true;
                    var encodedRoots = new LoadingInventoryRootEncoding(lastInventory);
                    var inventory = JsonConvert.SerializeObject(new { diagnosticOnly = true,
                        observerModuleId = typeof(LoadingGameplayOracle).Module.ModuleVersionId.ToString("D"),
                        nativeProfile = HollowKnightTAS.Core.Movie.MovieProtocolV2.SelectedNativeProfileId,
                        transitionOrdinal, movieFrame = movieFrame(),
                        rootIdentityEncoding = LoadingInventoryRootEncoding.Format,
                        rootIdentities = encodedRoots.Names, rootTypes = encodedRoots.Types, roots = encodedRoots.Roots,
                        rootSurvey, nativeComponentSurvey, nativeComponentDetails,
                        assetAliases = assetIdentities.Snapshot(),
                        textCacheDetails = textCacheDetails.ToArray(),
                        fontCacheDetails = assetValues.FontDetails,
                        unsupported = omissions, detachedFsmReferences, nativeScheduler = nativeScheduler?.Inventory,
                        nativePhysics = nativePhysics?.Inventory, nativePhysicsGroups = nativePhysics?.Groups,
                        nativeContactRawEvidence = nativePhysics?.ContactRawEvidence,
                        fieldDiagnostics = lastFieldDiagnostics, nativeCoroutines = nativeScheduler?.FieldDiagnostics });
                    if (Encoding.UTF8.GetByteCount(inventory) > 16 * 1024 * 1024)
                        throw new InvalidOperationException("P0 candidate inventory exceeds its 16 MiB budget.");
                    File.WriteAllText(Path.Combine(directory, "loading-candidate-" + transitionOrdinal + "-inventory.json"),
                        inventory, new UTF8Encoding(false));
                }
                Write(new { kind = "sample", sequence = rows++, phase, boundary,
                    movieFrame = movieFrame(), nativeFrame = nativeFrame(),
                    scene = USceneManager.GetActiveScene().name, state,
                    suffixOffset = suffixCapture ? (long?)(movieFrame() - first) : null,
                    inventory = suffixCapture ? EncodeSuffixInventory() : null,
                    nativePhysics = fullInventory ? nativePhysics?.Inventory : null,
                    nativePhysicsEntries = nativePhysics?.DiagnosticEntries,
                    nativeContactRawEvidence = nativePhysics?.ContactRawEvidence,
                    markerSharedGraphEvidence,
                    menuFlashCanvasDomain = fullInventory ? CanonicalMenuFlashColdPolicy.ObserveCanvasDomain(Reference) : null,
                    captureMilliseconds = captureElapsed.ElapsedMilliseconds,
                    captureStages = captureStages.ToArray() });
                if (phase == "before" || phase == "after") gameplaySamples++;
                priorCapture = new { movieFrame = movieFrame(), phase, boundary,
                    captureMilliseconds = captureElapsed.ElapsedMilliseconds, stages = captureStages.ToArray() };
                CaptureProgress("sample-complete");
            }
            catch (Exception exception) { Seal(false, exception.GetType().Name + ": " + exception.Message); if (suffixCapture) throw; }
        }

        internal void Completed()
        {
            if (sealedTrace || movieFrame() != end) return;
            if (physicsFollowOn) { SealPhysicsFollowOn(gameplaySamples == 3, "window-complete"); return; }
            var complete = serialBoundaryOnly || gameplaySamples == (suffixCapture ? 1 : 2) * samplingWindows.GameplayFrames;
            Seal(complete, complete ? "window-complete" : "missing-gameplay-samples");
        }

        // This is the actual suffix/non-boundary managed-root entry. Keep its
        // graph lifetime and evidence snapshot together, including failure paths.
        private void CaptureManagedSampleGraph(Action capture)
        {
            if (!boundaryCapture) fields.BeginSharedGraph(evidence: suffixCapture);
            try { capture(); }
            finally {
                readingMapKeyManagedRoots = false;
                if (!boundaryCapture) {
                    markerSharedGraphEvidence = fields.SharedGraphEvidence;
                    markerSharedGraphSummary = fields.SharedGraphEvidenceSummary;
                    fields.EndSharedGraph();
                }
            }
        }
        private object? markerSharedGraphEvidence, markerSharedGraphSummary;
        private SortedDictionary<string, string> CaptureState(bool fullInventory)
        {
            if (!boundaryCapture) return CaptureStateCore(fullInventory);
            // Retain aliases from gameplay fields into resource elements and
            // between array element roots, not only aliases inside one root.
            fields.BeginSharedGraph(evidence: suffixCapture);
            try { return CaptureStateCore(fullInventory); }
            finally { markerSharedGraphEvidence = fields.SharedGraphEvidence; fields.EndSharedGraph(); }
        }

        private SortedDictionary<string, string> CaptureStateCore(bool fullInventory)
        {
            var elapsed = Stopwatch.StartNew();
            if (boundaryCapture) canonicalIdentities = new BoundaryObjectIdentity();
            observationIdentity.Refresh();
            nativeValues.BeginSample();
            if (boundaryCapture) { coverage.BeginSample(); unityCoverageLabels.Clear(); coverageRoot = ""; coverageField = ""; }
            if (boundaryCapture) { s0Roots.Clear(); s0InvariantFailures.Clear(); s0Sample = null; s0EventLock = null; }
            detachedFsmReferences.Clear();
            detachedFsmMetadata.Clear();
            detachedFsmRoots.Clear();
            unityIds.Clear(); fsmIds.Clear(); fsmMemberIds.Clear();
            sceneObjectOwners.Clear();
            sceneRoots.Clear(); gameObjectPaths.Clear();
            assetIdentities.Clear();
            assetProvenance.Clear();
            assetRoots.Clear(); assetInstances.Clear();
            assetArrays.Clear();
            assetValues.BeginSample();
            textCacheDetails.Clear();
            geoIncoming.BeginSample(UnityId);
            mapKeyIncoming.BeginMapKeySample(UnityId, CanonicalMapKeyTextDirectory.Current());
            mapKeyIncoming.ObserveRetiringMapKeyMeshes();
            mapKeyParentIncoming.BeginMapKeyParentSample(UnityId, CanonicalMapKeyTextDirectory.Current());
            var frozenMapKey = CanonicalMapKeyTextColdTemplate.FrozenKeys;
            frozenMapKeySample = frozenMapKey != null;
            if (frozenMapKeySample) frozenMapKeyIncoming.BeginMapKeySample(UnityId, frozenMapKey!);
            markerIncoming.BeginMarkerSample(UnityId);
            var frozenMarker = CanonicalMarkerColdTemplate.FrozenOwner;
            frozenMarkerSample = frozenMarker != null;
            if (frozenMarkerSample) frozenMarkerIncoming.BeginMarkerSample(UnityId, frozenMarker);
            var frozenGeo = CanonicalGeoColdTemplate.FrozenCounter;
            frozenGeoSample = frozenGeo != null;
            if (frozenGeoSample) frozenGeoIncoming.BeginSample(UnityId, frozenGeo);
            var frozenMenuFlash = CanonicalMenuFlashColdPolicy.FrozenRoot;
            frozenMenuFlashSample = frozenMenuFlash != null;
            if (frozenMenuFlashSample) frozenMenuFlashIncoming.BeginMenuFlashSample(UnityId, frozenMenuFlash!);
            var allBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(component => component != null && Loaded(component.gameObject)
                    && component.GetType().Assembly != typeof(LoadingGameplayOracle).Assembly)
                .ToArray();
            if (allBehaviours.Length > 16000) throw new InvalidOperationException("P0 total inventory exceeds 16000 roots.");
            var behaviours = allBehaviours.Where(component => fullInventory || component.gameObject.activeInHierarchy)
                .Select(component => new { component, id = UnityId(component) })
                .OrderBy(item => item.id, StringComparer.Ordinal).ToArray();
            if (fullInventory) rootSurvey = behaviours.GroupBy(item => item.component.gameObject.scene.name + "|"
                + item.component.transform.root.name + "|" + item.component.GetType().FullName + "|"
                + item.component.gameObject.activeInHierarchy)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (object)new { group = group.Key, count = group.Count(),
                    examples = group.Take(2).Select(item => item.id).ToArray() }).ToArray();
            if (!fullInventory && behaviours.Length > 6000) throw new InvalidOperationException("P0 active inventory exceeds 6000 roots: total="
                + behaviours.Length + ";active=" + behaviours.Count(item => item.component.gameObject.activeInHierarchy));
            foreach (var item in behaviours)
                if (item.component is PlayMakerFSM && fields.Read(item.component, "fsm") is Fsm fsm)
                {
                    RegisterCoverageFsm(item.id, fsm);
                    fsmIds[fsm] = item.id;
                    var states = fields.Read(fsm, "states") as Array;
                    if (states == null) continue;
                    for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                    {
                        var state = states.GetValue(stateIndex);
                        if (state == null) continue;
                        var stateId = item.id + "/state[" + stateIndex + "]:" + fields.Read(state, "name");
                        fsmMemberIds[state] = stateId;
                        if (boundaryCapture) coverage.InheritRoot(stateId, item.id);
                        var actions = fields.Read(state, "actions") as Array;
                        if (actions == null) continue;
                        for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                        {
                            var action = actions.GetValue(actionIndex);
                            if (action != null)
                            {
                                var actionId = stateId + "/action[" + actionIndex + "]";
                                fsmMemberIds[action] = actionId;
                                if (boundaryCapture) coverage.InheritRoot(actionId, item.id);
                            }
                        }
                    }
                }
            CaptureProgress("native-scheduler");
            string schedulerDigest = "disabled";
            if (nativeScheduler != null)
            {
                var objects = new Dictionary<int, string>();
                var nativeBehaviours = new Dictionary<IntPtr, string>();
                var nativePointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("P0 native behaviour pointer field is unavailable.");
                // Native delayed calls can target GameObjects/components with
                // no MonoBehaviour (for example a temporary AudioSource). The
                // native owner map must cover the loaded hierarchy, even though
                // managed field roots are collected separately above.
                var schedulerComponents = Resources.FindObjectsOfTypeAll<Component>()
                    .Where(component => component != null && Loaded(component.gameObject)).ToArray();
                if (schedulerComponents.Length > 100000) throw new InvalidOperationException("Native scheduler owner inventory exceeds 100000 components.");
                foreach (var component in schedulerComponents)
                {
                    var id = UnityId(component);
                    objects[component.GetInstanceID()] = id;
                    objects[component.gameObject.GetInstanceID()] = UnityId(component.gameObject);
                    var pointer = (IntPtr)nativePointer.GetValue(component)!;
                    if (component is MonoBehaviour && pointer != IntPtr.Zero) nativeBehaviours[pointer] = id;
                }
                Dictionary<int, UnityEngine.Object>? otherNativeOwners = null;
                string? SchedulerOwner(int instance)
                {
                    if (objects.TryGetValue(instance, out var known)) return known;
                    if (!boundaryCapture) return null;
                    if (otherNativeOwners == null)
                    {
                        var owners = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
                        if (owners.Length > 200000) throw new InvalidOperationException("Native callback asset owner inventory exceeds its bound.");
                        otherNativeOwners = owners.Where(owner => owner != null).ToDictionary(owner => owner.GetInstanceID());
                    }
                    return otherNativeOwners.TryGetValue(instance, out var owner) ? UnityId(owner) : null;
                }
                schedulerDigest = nativeScheduler.Capture(fields,
                    SchedulerOwner,
                    pointer => nativeBehaviours.TryGetValue(pointer, out var value) ? value : null,
                    includeFieldDiagnostics: fullInventory && fieldDetails, retiredObjectId: boundaryRetiredOwner);
            }
            var inventory = new List<object>();
            CaptureProgress("managed-and-fsm-fields");
            // Only the actual Mono/static/Fsm roots expose writable managed
            // incoming slots. Native observation DTOs are read evidence, never
            // live field owners. Retired native consumers are checked directly.
            readingMapKeyManagedRoots = true;
            var fieldDiagnostics = new List<object>();
            var mono = new StringBuilder(); var fsms = new StringBuilder(); var staticRoots = new StringBuilder();
            CaptureManagedSampleGraph(() => {
            foreach (var item in nativePhysicsOnly ? behaviours.Take(0) : behaviours)
            {
                if (fullInventory && inventory.Count % 250 == 0)
                    File.WriteAllText(Path.Combine(directory, "loading-oracle-progress.json"),
                        JsonConvert.SerializeObject(new { diagnosticOnly = true, movieFrame = movieFrame(),
                            lastRoot = item.id, roots = inventory.Count, elapsedMs = elapsed.ElapsedMilliseconds,
                            completeStateCoverage = false }));
                var component = item.component;
                LoadingFieldDigestResult digest;
                try
                {
                if (component is PlayMakerFSM)
                {
                    var fsm = fields.Read(component, "fsm");
                    digest = fields.Capture(fsm, item.id, expandRoot: true);
                    fsms.Append(item.id).Append(':').Append(component.enabled).Append(':')
                        .Append(component.gameObject.activeSelf).Append(':').Append(digest.Sha256).Append('\n');
                    var states = fsm == null ? null : fullInventory ? fields.Read(fsm, "states") as Array
                        : new[] { fields.Read(fsm, "activeState") };
                    if (states != null)
                        for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                        {
                            var state = states.GetValue(stateIndex);
                            if (state == null) continue;
                            AppendFsmMember(state, fsms, inventory, fullInventory);
                            if (fields.Read(state, "actions") is Array actions)
                                for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                                {
                                    var action = actions.GetValue(actionIndex);
                                    if (action != null) AppendFsmMember(action, fsms, inventory, fullInventory);
                                }
                        }
                }
                else
                {
                    digest = fields.Capture(component, item.id, expandRoot: true,
                        includeFieldDiagnostics: fullInventory && fieldDetails
                            && (detailedTypes.Contains(component.GetType().FullName ?? "")
                                || (component is UnityEngine.UI.Text && item.id.Contains("/StartGameButton["))));
                    mono.Append(item.id).Append(':').Append(component.enabled).Append(':')
                        .Append(component.gameObject.activeSelf).Append(':').Append(digest.Sha256).Append('\n');
                }
                }
                catch (Exception exception)
                {
                    lastInventory = inventory.ToArray();
                    throw new InvalidOperationException("P0 field root " + item.id + ": " + exception.Message, exception);
                }
                foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
                if (digest.FieldDigests.Length != 0)
                    fieldDiagnostics.Add(new { root = item.id, fields = digest.FieldDigests });
                if (fullInventory && component is UnityEngine.UI.Text uiText && DetailedComponent(component, item.id))
                    textCacheDetails.Add(new { id = item.id, state = LoadingTextCacheObservation.Capture(uiText, fields, UnityId) });
                var markerDetails = fullInventory && FixedMarkerRoot(item.id);
                var detailOwner = markerDetails && component is PlayMakerFSM
                    ? fields.Read(component, "fsm") ?? throw new InvalidOperationException("Original Marker detailed FSM is absent.") : component;
                var fieldDigests = markerDetails ? CaptureFixedMarkerFields(fields, detailOwner, item.id, fullInventory)
                    : fullInventory && fieldDetails && DetailedComponent(component, item.id)
                        ? fields.Fields(detailOwner.GetType()).Select(field => new { field = field.Name, owner = field.DeclaringType!.FullName,
                            digest = fields.CaptureFields(detailOwner, item.id, field.Name).Sha256 }).ToArray() : null;
                inventory.Add(new { id = item.id, type = component.GetType().FullName,
                    digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported, fieldDigests });
            }
            if (!nativePhysicsOnly)
                foreach (var root in boundaryCapture ? LoadingRegisteredStaticRoots.ReadCanonical() : LoadingRegisteredStaticRoots.Read())
                {
                    RegisterCoverageStatic(root.Item1);
                    if (boundaryCapture && new[] { "FsmExecutionStack", "ActionData", "ReflectionUtils" }.Any(type =>
                        root.Item1.StartsWith("static:PlayMaker:HutongGames.PlayMaker." + type + ".", StringComparison.Ordinal)))
                        s0Roots.Add(new CanonicalS0Observation.Root(root.Item1, root.Item2, CanonicalS0FieldOwnership.StaticRoot(root.Item1)));
                    else if (boundaryCapture && (root.Item1 == "static:PlayMaker:HutongGames.PlayMaker.Fsm.EventData"
                        || root.Item1.StartsWith("static:PlayMaker:HutongGames.PlayMaker.FsmEvent.", StringComparison.Ordinal)))
                    {
                        const string eventPrefix = "static:PlayMaker:HutongGames.PlayMaker.FsmEvent.";
                        if (root.Item1 == eventPrefix + "syncObj") {
                            if (root.Item2 == null || root.Item2.GetType() != typeof(object))
                                throw new InvalidOperationException("Original FsmEvent sync lock layout changed.");
                            s0EventLock = root.Item2;
                            s0Roots.Add(new CanonicalS0Observation.Root(root.Item1, root.Item2, CanonicalS0Observation.Ownership.S0));
                        }
                        else {
                        var registeredEventRoot = root.Item1 == "static:PlayMaker:HutongGames.PlayMaker.Fsm.EventData"
                            || root.Item1 == eventPrefix + "_eventLookup"
                            || root.Item1.StartsWith(eventPrefix, StringComparison.Ordinal)
                                && CanonicalSystemEventBindings.Snapshot().ContainsKey(root.Item1.Substring(eventPrefix.Length));
                        s0Roots.Add(new CanonicalS0Observation.Root(root.Item1, root.Item2,
                            registeredEventRoot ? CanonicalS0Observation.Ownership.D : CanonicalS0Observation.Ownership.Unknown));
                        }
                        if (root.Item1.EndsWith("FsmEvent._eventLookup", StringComparison.Ordinal) && root.Item2 != null
                            && fields.Read(root.Item2, "entries") is Array entries)
                        {
                            if (entries.Length > 10000) throw new InvalidOperationException("S0 event binding registry budget exceeded.");
                            foreach (var entry in entries)
                                if (entry != null && fields.Read(entry, "hashCode") is int hashCode && hashCode >= 0
                                    && fields.Read(entry, "value") is FsmEvent cached && fields.Read(entry, "key") is string key)
                                    s0Roots.Add(new CanonicalS0Observation.Root("R6/FsmEvent-cache/" + key, cached, CanonicalS0Observation.Ownership.D));
                        }
                    }
                    var detailedStatic = root.Item1.EndsWith("InControl.InputManager.ActiveDevices", StringComparison.Ordinal)
                        || root.Item1.EndsWith("Modding.ModLoader.<ModInstances>k__BackingField", StringComparison.Ordinal);
                    var digest = fields.Capture(root.Item2, root.Item1, includeFieldDiagnostics: detailedStatic);
                    staticRoots.Append(root.Item1).Append(':').Append(digest.Sha256).Append('\n');
                    foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
                    inventory.Add(new { id = root.Item1, type = root.Item2?.GetType().FullName,
                        digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported,
                        fieldDigests = digest.FieldDigests });
                }
            for (var i = 0; i < detachedFsmRoots.Count; i++)
            {
                var entry = detachedFsmRoots[i];
                var digest = fields.Capture(entry.Item1, entry.Item2, expandRoot: true);
                fsms.Append(entry.Item2).Append(':').Append(digest.Sha256).Append('\n');
                foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
                inventory.Add(new { id = entry.Item2, type = entry.Item1.GetType().FullName,
                    digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported });
            }
            });
            readingMapKeyManagedRoots = false;
            if (suffixCapture) Modding.Logger.Log("[HKTAS] Marker selected shared-graph sample evidence: "
                + JsonConvert.SerializeObject(markerSharedGraphSummary));
            CaptureProgress("native-physics");
            var allBodies = Resources.FindObjectsOfTypeAll<Rigidbody2D>().Where(body => body != null && Loaded(body.gameObject)).ToArray();
            var physicsDigest = nativePhysics == null ? "disabled" : nativePhysics.Capture(allBodies,
                Resources.FindObjectsOfTypeAll<Collider2D>().Where(collider => collider != null && Loaded(collider.gameObject)), UnityId, fields);
            if (nativePhysics != null) foreach (var omitted in nativePhysics.Unsupported) omissions.Add("native-physics:" + omitted);
            var bodies = new StringBuilder();
            foreach (var body in allBodies
                         .Where(body => fullInventory || body.gameObject.activeInHierarchy)
                         .OrderBy(body => UnityId(body), StringComparer.Ordinal))
            {
                var id = UnityId(body);
                // position/rotation getters call PhysicsManager2D.AutoSyncTransforms
                // on this engine and therefore cannot be used by a state observer.
                var digest = fields.Capture(rigidbodies.CaptureValues(body), id, expandRoot: true);
                bodies.Append(id).Append(':').Append(digest.Sha256).Append('\n');
                inventory.Add(new { id, type = typeof(Rigidbody2D).FullName, digest = digest.Sha256,
                    nodes = digest.Nodes, unsupported = digest.Unsupported });
            }
            lastInventory = inventory.ToArray();
            if (fullInventory) lastFieldDiagnostics = fieldDiagnostics.ToArray();
            if (nativeScheduler != null)
                foreach (var omitted in nativeScheduler.Unsupported) omissions.Add("native-scheduler:" + omitted);
            CaptureProgress("global-and-hierarchy-values");
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["time"] = Bits(Time.time), ["timeDouble"] = Bits(Time.timeAsDouble),
                ["fixedTime"] = Bits(Time.fixedTime), ["fixedTimeDouble"] = Bits(Time.fixedTimeAsDouble),
                ["unscaledTime"] = Bits(Time.unscaledTime), ["realtime"] = Bits(Time.realtimeSinceStartup),
                ["frameCount"] = Time.frameCount.ToString(CultureInfo.InvariantCulture),
                ["controlledNativeFrame"] = (nativeFrame() - controlledNativeOrigin).ToString(CultureInfo.InvariantCulture),
                ["gameplayRng"] = gameplayRng(), ["monoFields"] = Hash(mono.ToString()),
                ["fsmFields"] = Hash(fsms.ToString()), ["physicsBodies"] = Hash(bodies.ToString()),
                ["rootCount"] = inventory.Count.ToString(CultureInfo.InvariantCulture),
                ["totalBehaviours"] = allBehaviours.Length.ToString(CultureInfo.InvariantCulture),
                ["inventoryScope"] = fullInventory ? "all-loaded" : "active-only",
                ["nativeScheduler"] = schedulerDigest,
                ["nativePhysics"] = physicsDigest,
                ["pooledAudioLifetime"] = Hash(JsonConvert.SerializeObject(LoadingAudioLifetimePolicy.CaptureState(UnityId))),
                ["sessionAchievements"] = Hash(JsonConvert.SerializeObject(CaptureSessionAchievements())),
                ["particleManagerRandom"] = Hash(JsonConvert.SerializeObject(CaptureParticleRandomState())),
                ["registeredStatics"] = nativePhysicsOnly ? "not-observed" : Hash(staticRoots.ToString()),
                ["unityHierarchy"] = nativePhysicsOnly ? "not-observed" : CaptureHierarchy(fullInventory, inventory)
            };
            CaptureProgress("asset-values");
            result["assetValues"] = nativePhysicsOnly || !fullInventory ? "not-observed" : CaptureAssetValues(inventory);
            if (frozenGeoSample) frozenGeoIncoming.RequireNoExternalConsumers();
            if (frozenMarkerSample) frozenMarkerIncoming.RequireNoExternalConsumers();
            if (frozenMapKeySample) frozenMapKeyIncoming.RequireNoExternalConsumers();
            if (frozenMenuFlashSample) frozenMenuFlashIncoming.RequireNoExternalConsumers();
            CaptureProgress("final-digests");
            result["assetBindings"] = Hash(JsonConvert.SerializeObject(assetIdentities.Snapshot()));
            result["unsupported"] = Hash(string.Join("\n", omissions));
            if (nativePhysics != null)
                foreach (var group in nativePhysics.Groups) result["nativePhysics/" + group.Key] = group.Value;
            lastInventory = inventory.ToArray();
            return result;
        }

        private void CaptureProgress(string stage)
        {
            var elapsed = captureProgress?.ElapsedMilliseconds ?? 0;
            captureStages.Add(new { stage, priorStageMilliseconds = elapsed - captureStageStarted });
            captureStageStarted = elapsed;
            File.WriteAllText(Path.Combine(directory, "loading-oracle-stage-progress.json"),
                JsonConvert.SerializeObject(new { diagnosticOnly = true, movieFrame = movieFrame(),
                    rows, phase = capturePhase, boundary = captureBoundary, stage, elapsedMilliseconds = elapsed,
                    stages = captureStages.ToArray(), priorCapture, completeStateCoverage = false }), new UTF8Encoding(false));
        }

        private static bool Loaded(GameObject value) => value.scene.IsValid() && value.scene.isLoaded;

        private string CaptureAssetValues(List<object> inventory)
        {
            var text = new StringBuilder();
            // Copying a Sprite/Material can discover additional resource references.
            // Process each native identity once and include the newly found leaves.
            var assetIndex = 0; var arrayIndex = 0;
            while (assetIndex < assetRoots.Count || arrayIndex < assetArrays.Entries.Count)
            {
                if (assetIndex == assetRoots.Count)
                {
                    var array = assetArrays.Entries[arrayIndex++];
                    AddAssetRecord(array.Id, array.Value.GetType().FullName!,
                        fields.Capture(new { length = array.Value.Length, elementType = array.Value.GetType().GetElementType()!.FullName },
                            array.Id, expandRoot: true), text, inventory);
                    for (var element = 0; element < array.Value.Length; element++)
                    {
                        var elementId = array.Id + "[" + element.ToString(CultureInfo.InvariantCulture) + "]";
                        var value = array.Value.GetValue(element);
                        AddAssetRecord(elementId, value?.GetType().FullName,
                            fields.Capture(value, elementId), text, inventory);
                    }
                    continue;
                }
                var entry = assetRoots[assetIndex++];
                // A Unity reference token does not cover the managed content
                // of animation libraries, sprite collections or cue assets.
                // Read their actual fields, with the same bounded reader and
                // without evaluating asset getters or running callbacks.
                if (boundaryCapture && (entry.Item1 is ScriptableObject || entry.Item1 is MonoBehaviour))
                {
                    var managedId = entry.Item2 + "/asset-managed-fields";
                    foreach (var field in fields.Fields(entry.Item1.GetType()))
                        if (fields.Read(entry.Item1, field) is Array array)
                            assetArrays.Register(array, managedId + "/array:" + field.DeclaringType!.FullName + "." + field.Name);
                    var managed = fields.Capture(entry.Item1, managedId, expandRoot: true);
                    AddAssetRecord(managedId, entry.Item1.GetType().FullName, managed, text, inventory);
                    // Native resource headers/configuration are still open.
                    // Observing the managed half must not clear that omission.
                }
                var snapshot = assetValues.Capture(entry.Item1, value =>
                    RecordCoverageGap(value, entry.Item2 + "/asset-values", AssetCoverageField(value), entry.Item2));
                if (snapshot == null) continue;
                var id = entry.Item2 + "/asset-values";
                var digest = fields.Capture(snapshot, id, expandRoot: true);
                AddAssetRecord(id, entry.Item1.GetType().FullName, digest, text, inventory);
            }
            return Hash(text.ToString());
        }

        private void AddAssetRecord(string id, string? type, LoadingFieldDigestResult digest, StringBuilder text, List<object> inventory)
        {
            foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
            text.Append(id).Append(':').Append(digest.Sha256).Append('\n');
            if (inventory.Count >= 200000) throw new InvalidOperationException("Boundary asset root budget exceeded.");
            inventory.Add(new { id, type, digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported });
        }

        private string CaptureHierarchy(bool fullInventory, List<object> inventory)
        {
            var objects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(value => value != null && Loaded(value) && (fullInventory || value.activeInHierarchy))
                .Select(value => new { value, id = UnityId(value) })
                .OrderBy(item => item.id, StringComparer.Ordinal).ToArray();
            if (objects.Length > 16000) throw new InvalidOperationException("P0 hierarchy root budget exceeded.");
            var text = new StringBuilder();
            nativeComponentSurvey.Clear();
            nativeComponentDetails.Clear();
            foreach (var item in objects)
            {
                var go = item.value;
                var transform = go.transform;
                var observedComponents = go.GetComponents<Component>();
                for (var componentIndex = 0; componentIndex < observedComponents.Length; componentIndex++)
                {
                    var component = observedComponents[componentIndex];
                    if (component != null && !(component is MonoBehaviour))
                    {
                        var type = component.GetType().FullName ?? component.GetType().Name;
                        nativeComponentSurvey.TryGetValue(type, out var count);
                        nativeComponentSurvey[type] = count + 1;
                        var runtimeState = component is Animator a ? (a.isInitialized ? "initialized" : "not-initialized")
                            : component is ParticleSystem ps ? (LoadingNativeComponentObservation.ParticleNeedsSync(ps)
                                ? "pending-sync" : ps.isPlaying ? "playing" : ps.isPaused ? "paused" : "stopped")
                            : null;
                        if (runtimeState != null)
                        {
                            var key = type + "/" + runtimeState + (component.gameObject.activeInHierarchy ? "/active" : "/inactive");
                            nativeComponentSurvey.TryGetValue(key, out var stateCount);
                            nativeComponentSurvey[key] = stateCount + 1;
                        }
                        if (!(component is Transform) && !(component is Rigidbody2D) && !(component is Collider2D))
                        {
                            var componentId = RegisterCoverageComponent(component);
                            RecordCoverageGap("native-component-codec:" + type, item.id + "/native-hierarchy",
                                ".components[" + componentIndex + "].nativePayload", componentId);
                        }
                    }
                }
                object? rect = transform is RectTransform rt ? new { rt.anchorMin, rt.anchorMax,
                    rt.anchoredPosition, rt.sizeDelta, rt.pivot, rt.offsetMin, rt.offsetMax, rt.rect } : null;
                var components = go.GetComponents<Component>().Select((component, index) => component == null
                    ? (object)new { type = "missing-script" }
                    : new { type = component.GetType().FullName,
                        enabled = component is Behaviour b ? (bool?)b.enabled : null,
                        hideFlags = (int)component.hideFlags,
                        runtime = CaptureNativeComponent(component, fullInventory, item.id + "/native-hierarchy", ".components[" + index + "].runtime"),
                        nativePayload = component is MonoBehaviour ? "managed-fields-separate"
                            : component is Transform ? "local-transform-and-layout"
                            : component is Rigidbody2D || component is Collider2D ? "native-physics-separate"
                            : "requires-native-codec" }).ToArray();
                var id = item.id + "/native-hierarchy";
                // Reuse the original hierarchy reader's one native scale read.
                // The independent field graph itself never evaluates getters.
                var localScale = transform.localScale;
                if (boundaryCapture)
                {
                    var lights = observedComponents.OfType<PlayMakerFSM>().Select(component => fields.Read(component, "fsm") as Fsm)
                        .Where(fsm => fsm != null && s0Roots.Any(root => root.Domain == CanonicalS0Observation.Ownership.S0
                            && ReferenceEquals(root.Value, fsm)) && Equals(fields.Read(fsm!, "name"), "HeroLight Control")).ToArray();
                    if (lights.Length > 1) throw new InvalidOperationException("S0 HeroLight native scale owner is ambiguous.");
                    if (lights.Length == 1)
                    {
                        s0Roots.Add(new CanonicalS0Observation.Root(id + "/R2.localScale", localScale, CanonicalS0Observation.Ownership.S0));
                        var variables = fields.Read(lights[0]!, "variables");
                        var vectors = variables == null ? null : fields.Read(variables, "vector3Variables") as Array;
                        if (vectors == null || vectors.Length > 4096) throw new InvalidOperationException("S0 fresh Idle Scale table is absent or exceeds its bound.");
                        var idle = vectors.Cast<object>().Where(variable => variable != null && Equals(fields.Read(variable, "name"), "Idle Scale")).ToArray();
                        if (idle.Length != 1 || !(fields.Read(idle[0], "value") is Vector3 idleScale))
                            throw new InvalidOperationException("S0 fresh Idle Scale field binding differs.");
                        s0Roots.Add(new CanonicalS0Observation.Root(id + "/R2.freshIdleScale", idleScale, CanonicalS0Observation.Ownership.S0));
                        if (Bits(localScale.x) != Bits(idleScale.x) || Bits(localScale.y) != Bits(idleScale.y) || Bits(localScale.z) != Bits(idleScale.z))
                            s0InvariantFailures.Add(id + "/R2.localScale:differs-from-actual-fresh-Idle-Scale");
                    }
                }
                var digest = fields.Capture(new { go.activeSelf, go.layer, go.tag, go.isStatic,
                    hideFlags = (int)go.hideFlags, parent = transform.parent == null ? null : UnityId(transform.parent),
                    sibling = transform.GetSiblingIndex(), transform.hasChanged, transform.localPosition, transform.localRotation,
                    localScale, rect, components }, id, expandRoot: true);
                text.Append(id).Append(':').Append(digest.Sha256).Append('\n');
                foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
                inventory.Add(new { id, type = "UnityEngine.GameObject/native-hierarchy", digest = digest.Sha256,
                    nodes = digest.Nodes, unsupported = digest.Unsupported });
            }
            return Hash(text.ToString());
        }

        private object? CaptureNativeComponent(Component component, bool diagnostics, string root, string field)
        {
            var state = LoadingNativeComponentObservation.Capture(component, message =>
                RecordCoverageGap(message, root, field + NativeCoverageField(message), RegisterCoverageComponent(component)));
            if (boundaryCapture && component is MonoBehaviour behaviour)
                state = BoundaryMonoLifecycle.Read(behaviour);
            if (diagnostics && component is Animator animator && animator.isInitialized)
            {
                var id = UnityId(component);
                var digest = fields.Capture(state, id, expandRoot: true, includeFieldDiagnostics: true);
                nativeComponentDetails.Add(new { id, digest = digest.Sha256, fields = digest.FieldDigests });
            }
            if (diagnostics && component is ParticleSystem particles && particles.gameObject.activeInHierarchy && state != null)
            {
                var id = UnityId(component);
                // Diagnose copied values without assigning new ownership in the shared graph.
                var groups = fields.Fields(state.GetType()).Select(field => new
                {
                    field = field.Name, digest = fields.CaptureFields(state, id, field.Name).Sha256
                }).ToArray();
                nativeComponentDetails.Add(new { id, particles.randomSeed, particles.useAutoRandomSeed,
                    particles.time, groups });
            }
            return state;
        }

        private static string[] CaptureSessionAchievements()
        {
            if (!HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesIsolatedOnline(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)) return Array.Empty<string>();
            var payload = AppDomain.CurrentDomain.GetAssemblies()
                .Single(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload");
            var method = payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!
                .GetMethod("CaptureSessionAchievements", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("V10 achievement observation is unavailable.");
            return (string[])method.Invoke(null, null)!;
        }

        internal static uint[] CaptureParticleRandomState()
        {
            if (!HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesCanonicalParticles(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)) return Array.Empty<uint>();
            var payload = AppDomain.CurrentDomain.GetAssemblies()
                .Single(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload");
            return (uint[])payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!
                .GetMethod("CaptureParticleRandomState", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        }

        private void AppendFsmMember(object member, StringBuilder digestText, List<object> inventory, bool fullInventory)
        {
            if (!fsmMemberIds.TryGetValue(member, out var id))
                throw new InvalidOperationException("P0 FSM member identity unavailable.");
            var digest = fields.Capture(member, id, expandRoot: true);
            digestText.Append(id).Append(':').Append(digest.Sha256).Append('\n');
            foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
            // Keep the shared graph digest and alias inventory above intact.
            // These additional independent fields distinguish actual Marker
            // differences from unrelated shared reference-number prefixes.
            var fieldDigests = CaptureFixedMarkerFields(fields, member, id, fullInventory);
            inventory.Add(new { id, type = member.GetType().FullName, digest = digest.Sha256,
                nodes = digest.Nodes, unsupported = digest.Unsupported, fieldDigests });
        }

        internal static object[]? CaptureFixedMarkerFields(LoadingFieldDigest reader, object owner, string id, bool fullInventory)
        {
            if (!fullInventory || !FixedMarkerRoot(id)) return null;
            var declarations = reader.Fields(owner.GetType()).ToArray();
            if (declarations.Length > 256) throw new InvalidOperationException("Fixed Marker independent diagnostic field bound exceeded.");
            return declarations.Select(field => (object)new { field = field.Name, owner = field.DeclaringType!.FullName,
                digest = reader.CaptureFields(owner, id, field.Name).Sha256 }).ToArray();
        }
        private static bool FixedMarkerRoot(string id)
        {
            if (id.IndexOf("/Map Marker Action[", StringComparison.Ordinal) < 0) return false;
            // Boundary identities use same-name ordinals; suffix identities
            // use original sibling indices. Match the fixed owner path in
            // both formats without making incidental sibling numbers part
            // of diagnostic field selection.
            var path = System.Text.RegularExpressions.Regex.Replace(id, @"\[[0-9]+\]", "[]")
                .Replace("DontDestroyOnLoad:", "DontDestroyOnLoad/").Replace('|', ':');
            const string live = "DontDestroyOnLoad/_GameCameras[]/HudCamera[]/Inventory[]/Map[]/World Map[]/Map Marker Action[]";
            const string frozen = "DontDestroyOnLoad/__HKTAS_OriginalMarkerColdTemplate[]/Map Marker Action[]";
            return Under(live) || Under(frozen);
            bool Under(string root) => path.StartsWith(root + "/", StringComparison.Ordinal)
                || path.StartsWith(root + ":", StringComparison.Ordinal);
        }

        private bool DetailedComponent(Component component, string id)
            => detailedTypes.Contains(component.GetType().FullName ?? "")
                || (component is UnityEngine.UI.Text && (id.Contains("/MainMenuButtons[") || id.Contains("/ControllerMessage[")));

        private string? Reference(object value)
        {
            if (!boundaryCapture) return ReferenceCore(value);
            var priorRoot = coverageRoot; var priorField = coverageField;
            coverageRoot = fields.DiagnosticRoot;
            var path = fields.DiagnosticPath;
            coverageField = path.StartsWith(coverageRoot, StringComparison.Ordinal) ? path.Substring(coverageRoot.Length) : path;
            if (coverageField.Length == 0) coverageField = "$reference";
            try { return ReferenceCore(value); }
            finally { coverageRoot = priorRoot; coverageField = priorField; }
        }

        private string? ReferenceCore(object value)
        {
            if (fields.Capturing && geoIncoming.Owns(value)) geoIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (fields.Capturing && readingMapKeyManagedRoots && mapKeyParentIncoming.Owns(value)) mapKeyParentIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (fields.Capturing && readingMapKeyManagedRoots && mapKeyIncoming.Owns(value)) mapKeyIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (fields.Capturing && readingMapKeyManagedRoots && frozenMapKeySample && frozenMapKeyIncoming.Owns(value)) frozenMapKeyIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (fields.Capturing && markerIncoming.Owns(value)) markerIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (fields.Capturing && frozenMarkerSample && frozenMarkerIncoming.Owns(value)) frozenMarkerIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (fields.Capturing && frozenGeoSample && frozenGeoIncoming.Owns(value)) frozenGeoIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (fields.Capturing && frozenMenuFlashSample && frozenMenuFlashIncoming.Owns(value)) frozenMenuFlashIncoming.Observe(value, fields.DiagnosticPath, fields.DiagnosticRoot);
            if (boundaryCapture && assetArrays.Reference(value) is string arrayId)
                return "asset-array:" + arrayId + ":shared:" + fields.BindSharedReference(value).ToString(CultureInfo.InvariantCulture);
            if (value is ParticleSystem.MinMaxGradient gradient) return GradientIdentity(gradient);
            if (value is Coroutine coroutine && nativeScheduler != null) return nativeScheduler.Reference(coroutine, fields);
            if (value is UnityEngine.Object unity) {
                return UnityId(unity);
            }
            if (fsmMemberIds.TryGetValue(value, out var memberId)) return "fsm-member:" + memberId;
            if (value is FsmState || value is FsmStateAction)
            {
                // A callback can keep an action alive after its owning state's
                // action array was replaced. It remains real managed state:
                // expand its fields and shared aliases instead of replacing the
                // target with only a type name. The graph walker also captures
                // any detached owning FSM and keeps cycles/aliases intact.
                if (fields.Read(value, "fsm") is Fsm ownerFsm) BindDetachedFsm(ownerFsm);
                if (!fsmMemberIds.TryGetValue(value, out memberId))
                {
                    memberId = "detached-fsm-member[" + detachedFsmRoots.Count + "]";
                    fsmMemberIds.Add(value, memberId);
                    QueueDetachedFsm(value, memberId);
                }
                return "fsm-member:" + memberId;
            }
            if (LoadingRuntimeReferencePolicy.IsOpaqueController(value.GetType(), typeof(LoadingGameplayOracle).Assembly))
                return "TAS-controller:" + value.GetType().FullName;
            if (value is Fsm fsm)
            {
                if (fsmIds.TryGetValue(fsm, out var id)) return "fsm:" + id;
                return "fsm:" + BindDetachedFsm(fsm);
            }
            return null;
        }

        private string BindDetachedFsm(Fsm fsm)
        {
            if (fsmIds.TryGetValue(fsm, out var existing)) return existing;
            var id = "detached-fsm[" + detachedFsmRoots.Count + "]";
            fsmIds.Add(fsm, id);
            var owner = fields.Read(fsm, "owner") as MonoBehaviour;
            var hasOwner = !ReferenceEquals(owner, null);
            var instanceField = typeof(UnityEngine.Object).GetField("m_InstanceID", BindingFlags.Instance | BindingFlags.NonPublic);
            var cachedInstance = hasOwner && instanceField != null ? (int)instanceField.GetValue(owner)! : 0;
            detachedFsmMetadata.Add(new { id, name = fields.Read(fsm, "name"),
                ownerReference = hasOwner ? UnityId(owner!) : null, ownerNativeLive = owner != null,
                retiredSourceOwner = cachedInstance != 0 ? LoadingSerialSceneProbe.RetiringSourceOwner(cachedInstance) : null,
                cachedInstanceAvailable = cachedInstance != 0 });
            QueueDetachedFsm(fsm, id);
            if (fields.Read(fsm, "states") is Array states)
                for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                {
                    var state = states.GetValue(stateIndex);
                    if (state == null) continue;
                    var stateId = id + "/state[" + stateIndex + "]:" + fields.Read(state, "name");
                    if (!fsmMemberIds.ContainsKey(state))
                    { fsmMemberIds.Add(state, stateId); QueueDetachedFsm(state, stateId); }
                    if (!(fields.Read(state, "actions") is Array actions)) continue;
                    for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                    {
                        var action = actions.GetValue(actionIndex);
                        if (action == null || fsmMemberIds.ContainsKey(action)) continue;
                        var actionId = stateId + "/action[" + actionIndex + "]";
                        fsmMemberIds.Add(action, actionId);
                        QueueDetachedFsm(action, actionId);
                    }
                }
            return id;
        }

        private void QueueDetachedFsm(object value, string id)
        {
            if (detachedFsmRoots.Count >= 4096)
                throw new InvalidOperationException("P0 detached FSM reference budget exceeded.");
            detachedFsmReferences.Add(id + ":" + value.GetType().FullName + ":" + fields.DiagnosticPath);
            detachedFsmRoots.Add(Tuple.Create(value, id));
        }

        private static bool IsObservationCallback(Delegate callback)
        {
            // Only these two read-only recorders (including their compiler
            // closures) are instrumentation. Retain every game/Mod/controller
            // callback and its order. Clock-write diagnostics are rejected.
            for (var type = callback.Method.DeclaringType; type != null; type = type.DeclaringType)
                if (type == typeof(LoadingBoundaryProbe) || type == typeof(LoadingGameplayOracle)) return true;
            return false;
        }

        private static string GradientIdentity(ParticleSystem.MinMaxGradient gradient)
        {
            string ColorIdentity(Color color) => Bits(color.r) + ":" + Bits(color.g) + ":" + Bits(color.b) + ":" + Bits(color.a);
            string Keys(Gradient? value) => value == null ? "null" : value.mode + ":"
                + string.Join(";", value.colorKeys.Select(key => Bits(key.time) + ":" + ColorIdentity(key.color))) + ":"
                + string.Join(";", value.alphaKeys.Select(key => Bits(key.time) + ":" + Bits(key.alpha)));
            switch (gradient.mode)
            {
                case ParticleSystemGradientMode.Color: return "gradient:Color:" + ColorIdentity(gradient.color);
                case ParticleSystemGradientMode.TwoColors: return "gradient:TwoColors:" + ColorIdentity(gradient.colorMin) + ":" + ColorIdentity(gradient.colorMax);
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor: return "gradient:" + gradient.mode + ":" + Keys(gradient.gradient);
                case ParticleSystemGradientMode.TwoGradients: return "gradient:TwoGradients:" + Keys(gradient.gradientMin) + ":" + Keys(gradient.gradientMax);
                default: throw new InvalidOperationException("P0 unsupported particle gradient mode.");
            }
        }

        private string UnityId(UnityEngine.Object value)
        {
            if (unityIds.TryGetValue(value, out var id))
            {
                RecordUnityCoverage(value, id);
                return id;
            }
            if (value == null)
            {
                // Unity's overloaded null means a live managed wrapper whose
                // native object was destroyed, not a null managed reference.
                var destroyed = value!;
                omissions.Add("destroyed-unity-wrapper-rebind-codec:" + destroyed.GetType().FullName);
                var instance = destroyed.GetInstanceID();
                id = instance == 0 ? assetIdentities.BindFakeNull(unityIds.Count, destroyed.GetType().FullName!)
                    : assetIdentities.Bind(instance, "destroyed", destroyed.GetType().FullName!, "");
                unityIds.Add(destroyed, id);
                if (boundaryCapture)
                {
                    coverage.RegisterObject(id, destroyed.GetType().FullName!, "destroyed-wrapper", instance);
                    unityCoverageLabels.Add(destroyed, "destroyed-unity-wrapper-rebind-codec:" + destroyed.GetType().FullName);
                    RecordUnityCoverage(destroyed, id);
                }
                return id;
            }
            GameObject? go = value as GameObject;
            if (value is Component component) go = component.gameObject;
            if (go != null && Loaded(go))
            {
                if (canonicalIdentities != null)
                {
                    id = canonicalIdentities.Get(value);
                    unityIds.Add(value, id);
                    return id;
                }
                var objectOwner = go.GetInstanceID();
                if (!gameObjectPaths.TryGetValue(objectOwner, out id))
                {
                    var scene = go.scene;
                    if (!sceneRoots.Contains(scene.handle))
                    {
                        if (scene.rootCount > 100000) throw new InvalidOperationException("P0 scene-root list exceeds its budget.");
                        sceneRoots.Register(scene.handle, scene.name,
                            scene.GetRootGameObjects().Select(root => root == null ? 0 : root.GetInstanceID()).ToArray());
                    }
                    var path = new Stack<string>();
                    for (var current = go.transform; current != null; current = current.parent)
                    {
                        var index = current.parent == null
                            ? sceneRoots.Ordinal(scene.handle, current.gameObject.GetInstanceID()) : current.GetSiblingIndex();
                        path.Push(current.name + "[" + index + "]");
                    }
                    id = scene.name + ":" + string.Join("/", path);
                    if (sceneObjectOwners.TryGetValue(id, out var previousOwner))
                    {
                        if (previousOwner != objectOwner) throw new InvalidOperationException("P0 scene-object identity collision: " + id);
                    }
                    else
                    {
                        if (sceneObjectOwners.Count >= 100000) throw new InvalidOperationException("P0 scene binding budget exceeded.");
                        sceneObjectOwners.Add(id, objectOwner);
                    }
                    gameObjectPaths.Add(objectOwner, id);
                }
                if (value is Component part)
                {
                    var sameType = go.GetComponents(part.GetType());
                    var index = 0;
                    while (index < sameType.Length && !ReferenceEquals(sameType[index], part)) index++;
                    if (index == sameType.Length) throw new InvalidOperationException("P0 component identity unavailable.");
                    id += "|" + part.GetType().FullName + "[" + index + "]";
                }
            }
            else
            {
                omissions.Add("asset-value-provenance-codec:" + value.GetType().FullName);
                var instance = value.GetInstanceID();
                var provenance = assetProvenance.Capture(instance);
                id = assetIdentities.Bind(instance, "asset", value.GetType().FullName!, value.name, provenance);
                if (boundaryCapture)
                {
                    RegisterCoverageAsset(id, value.GetType().FullName!, instance, provenance);
                    unityCoverageLabels.Add(value, "asset-value-provenance-codec:" + value.GetType().FullName);
                    RecordUnityCoverage(value, id);
                }
                if (assetInstances.Add(instance)) assetRoots.Add(Tuple.Create(value, id));
            }
            unityIds.Add(value, id);
            return id;
        }

        private object? RenderRngDiagnostic()
        {
            if (payloadType == null) return "payload-unavailable";
            var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var name in new[] { "fullRunRenderInitialized", "fullRunRenderEntered", "fullRunRenderState" })
            {
                // ClockController has already executed Bootstrap before Runtime
                // starts. Reading these existing static fields cannot initialize it.
                var field = payloadType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                result[name] = field == null ? "field-unavailable" : fields.Capture(field.GetValue(null), name).Sha256;
            }
            return result;
        }

        private object EncodeSuffixInventory()
        {
            var encoded = new LoadingInventoryRootEncoding(lastInventory);
            return encoded.EncodeSuffix(new {
                detachedFsmReferences, detachedFsmMetadata, originalGeoLifecycle = LoadingGeoLifecycleObservation.Capture(),
                originalMapLifecycle = LoadingMapLifecycleObservation.Capture(), geoIncomingReferences = geoIncoming.Snapshot(), markerIncomingReferences = markerIncoming.Snapshot(), mapKeyIncomingReferences = mapKeyIncoming.Snapshot(), mapKeyParentIncomingReferences = mapKeyParentIncoming.Snapshot() });
        }

        private void Write(object value)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value) + "\n");
            if (rows > 50000 || buffer.Length + bytes.Length > 32L * 1024 * 1024)
                throw new InvalidOperationException("P0 gameplay oracle trace budget exceeded.");
            buffer.Write(bytes, 0, bytes.Length);
        }

        private void Seal(bool success, string detail)
        {
            if (sealedTrace) return;
            sealedTrace = true;
            try
            {
                try
                {
                    var encodedRoots = new LoadingInventoryRootEncoding(lastInventory);
                    var inventory = JsonConvert.SerializeObject(new { schema = "hktas-p0-state-inventory-v1",
                        observerModuleId = typeof(LoadingGameplayOracle).Module.ModuleVersionId.ToString("D"),
                        nativeProfile = HollowKnightTAS.Core.Movie.MovieProtocolV2.SelectedNativeProfileId,
                        movieFrame = lastSampleMovieFrame, completionMovieFrame = movieFrame(), completeStateCoverage = false,
                        rootIdentityEncoding = LoadingInventoryRootEncoding.Format,
                        rootIdentities = encodedRoots.Names, rootTypes = encodedRoots.Types, roots = encodedRoots.Roots,
                        rootSurvey, nativeComponentSurvey, nativeComponentDetails,
                        assetAliases = assetIdentities.Snapshot(), fieldSchema = fields.Schema(), unsupported = omissions,
                        nativeScheduler = nativeScheduler?.Inventory,
                        nativePhysics = nativePhysics?.Inventory,
                        nativePhysicsGroups = nativePhysics?.Groups,
                        nativeContactRawEvidence = nativePhysics?.ContactRawEvidence,
                        renderRng = RenderRngDiagnostic(), renderRngObservationMovieFrame = movieFrame()
                    }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    var inventoryBytes = Encoding.UTF8.GetByteCount(inventory);
                    if (inventoryBytes > 16 * 1024 * 1024)
                        throw new InvalidOperationException("P0 state inventory exceeds its explicit 16 MiB budget: bytes="
                            + inventoryBytes + ";roots=" + lastInventory.Length + ".");
                    File.WriteAllText(Path.Combine(directory, "loading-state-inventory.json"), inventory, new UTF8Encoding(false));
                    var diagnostics = JsonConvert.SerializeObject(new { diagnosticOnly = true,
                        movieFrame = lastSampleMovieFrame, completionMovieFrame = movieFrame(), roots = lastFieldDiagnostics,
                        nativeCoroutines = nativeScheduler?.FieldDiagnostics });
                    if (Encoding.UTF8.GetByteCount(diagnostics) > 8 * 1024 * 1024)
                        throw new InvalidOperationException("P0 nested field diagnostics exceed the explicit 8 MiB budget.");
                    File.WriteAllText(Path.Combine(directory, "loading-field-digests.json"), diagnostics, new UTF8Encoding(false));
                }
                catch (Exception exception)
                {
                    success = false;
                    detail += "; inventory export failed: " + exception.GetType().Name + ": " + exception.Message;
                }
                if (suffixCapture) {
                    suffixTweenCausality?.Export(directory);
                    suffixParticleCausality?.Export(directory);
                    using (var randomOutput = new FileStream(Path.Combine(directory, "suffix-native-random.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        suffixRandomTimeline.WriteTo(randomOutput);
                }
                var content = buffer.ToArray();
                using (var digest = SHA256.Create())
                {
                    var footer = new { kind = "footer", success, detail, rows, first, end,
                        sha256 = BitConverter.ToString(digest.ComputeHash(content)).Replace("-", "").ToLowerInvariant() };
                    using (var output = new FileStream(Path.Combine(directory, "loading-gameplay-oracle.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        output.Write(content, 0, content.Length);
                        var tail = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(footer) + "\n");
                        output.Write(tail, 0, tail.Length);
                    }
                }
            }
            catch (Exception exception) { success = false; detail += "; export failed: " + exception.Message; Modding.Logger.LogError("P0 gameplay oracle export failed: " + exception.Message); }
            finally { suffixTweenCausality?.Dispose(); suffixParticleCausality?.Dispose(); nativeScheduler?.Release(); nativeValues.Dispose(); fields.Dispose(); buffer.Dispose(); suffixRandomTimeline.Dispose(); }
            if (suffixCapture && !success) throw new InvalidOperationException("Actual D suffix observer failed: " + detail);
            CompletedSuccessfully = success;
        }

        private static string Bits(float value) => "f32:" + BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("x8");
        private static string Bits(double value) => "f64:" + BitConverter.DoubleToInt64Bits(value).ToString("x16");
        private static string Hash(string value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }

        private void TransitionBegan(SceneLoad load)
        {
            currentTransition = load; transitionOrdinal++;
            transitionSource = USceneManager.GetActiveScene().name; transitionTarget = load.TargetSceneName;
            transitionSourceUnloaded = false; candidateCaptured = false;
            Sample("event", "transition-began:" + load.TargetSceneName);
            SceneLoad.FetchCompleteDelegate fetch = () => Sample("event", "fetch-complete");
            SceneLoad.WillActivateDelegate activate = () => Sample("event", "will-activate");
            SceneLoad.ActivationCompleteDelegate activated = () => Sample("event", "activation-complete");
            SceneLoad.CompleteDelegate complete = () => Sample("event", "load-complete");
            SceneLoad.StartCalledDelegate started = () => Sample("event", "start-called");
            SceneLoad.FinishDelegate finish = null!;
            Action unregister = () => { };
            Action remove = () => { load.FetchComplete -= fetch; load.WillActivate -= activate;
                load.ActivationComplete -= activated; load.Complete -= complete;
                load.StartCalled -= started; load.Finish -= finish; unregister(); };
            finish = () => { Sample("event", "load-finish"); remove(); detach.Remove(remove); };
            load.FetchComplete += fetch; load.WillActivate += activate;
            load.ActivationComplete += activated; load.Complete += complete;
            load.StartCalled += started; load.Finish += finish; detach.Add(remove);
            unregister = CanonicalSceneLoadDiagnosticRegistry.Register(load, this, new Dictionary<string, Delegate> {
                ["FetchComplete"] = fetch, ["WillActivate"] = activate, ["ActivationComplete"] = activated,
                ["Complete"] = complete, ["StartCalled"] = started, ["Finish"] = finish });
        }
        private void SceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
            => Sample("event", "scene-loaded:" + scene.name + ":" + mode);
        private void SceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            if (scene.name == transitionSource) transitionSourceUnloaded = true;
            Sample("event", "scene-unloaded:" + scene.name);
        }
        private void ActiveSceneChanged(UnityEngine.SceneManagement.Scene previous, UnityEngine.SceneManagement.Scene current)
            => Sample("event", "active-scene:" + previous.name + "->" + current.name);
        public void Dispose()
        {
            if (physicsFollowOn) { if (!sealedTrace) SealPhysicsFollowOn(false, "disposed-before-window-complete"); return; }
            if (boundaryCapture)
            {
                nativeScheduler?.Release();
                fields.Dispose();
                buffer.Dispose(); suffixRandomTimeline.Dispose();
                return;
            }
            currentTransition = null;
            GameManager.SceneTransitionBegan -= TransitionBegan;
            USceneManager.sceneLoaded -= SceneLoaded;
            USceneManager.sceneUnloaded -= SceneUnloaded;
            USceneManager.activeSceneChanged -= ActiveSceneChanged;
            foreach (var remove in detach) remove(); detach.Clear();
            if (!sealedTrace) Seal(false, "disposed-before-window-complete");
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
