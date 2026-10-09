using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    // P0's independent observation path. Native schedulers/physics internals,
    // arbitrary Mod statics and asset aliasing remain explicit coverage gaps.
    internal sealed class LoadingGameplayOracle : IDisposable
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
        private readonly LoadingFieldDigest fields;
        private readonly LoadingObservationIdentity observationIdentity;
        private readonly LoadingNativeValueObservation nativeValues;
        private readonly Dictionary<Fsm, string> fsmIds = new Dictionary<Fsm, string>(ReferenceComparer<Fsm>.Instance);
        private readonly Dictionary<object, string> fsmMemberIds = new Dictionary<object, string>(ReferenceComparer<object>.Instance);
        private readonly Dictionary<object, string> unityIds = new Dictionary<object, string>(ReferenceComparer<object>.Instance);
        private readonly Dictionary<string, int> sceneObjectOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly LoadingSceneRootIdentity sceneRoots = new LoadingSceneRootIdentity();
        private readonly Dictionary<int, string> gameObjectPaths = new Dictionary<int, string>();
        private readonly LoadingUnityAssetIdentity assetIdentities = new LoadingUnityAssetIdentity();
        private readonly LoadingAssetProvenanceObservation assetProvenance = new LoadingAssetProvenanceObservation();
        private readonly List<Tuple<UnityEngine.Object, string>> assetRoots = new List<Tuple<UnityEngine.Object, string>>();
        private readonly HashSet<int> assetInstances = new HashSet<int>();
        private readonly LoadingAssetValueObservation assetValues = new LoadingAssetValueObservation();
        private readonly SortedSet<string> omissions = new SortedSet<string>(StringComparer.Ordinal);
        private readonly SortedSet<string> detachedFsmReferences = new SortedSet<string>(StringComparer.Ordinal);
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
            "DesktopPlatform", "GlobalPrefabDefaults", "ObjectPool", "InputHandler", "InControl.HollowKnightInputModule"
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
            Func<long> movieFrame, Func<long> nativeFrame, Func<string> gameplayRng)
        {
            this.directory = directory; this.first = first; this.end = end;
            samplingWindows = LoadingOracleSamplingWindows.Parse(
                Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_WINDOWS"), first, end);
            this.movieFrame = movieFrame; this.nativeFrame = nativeFrame; this.gameplayRng = gameplayRng;
            controlledNativeOrigin = nativeFrame();
            fieldDetails = Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_FIELD_DETAILS") == "1";
            nativePhysicsOnly = Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS_ONLY") == "1";
            serialBoundaryOnly = Environment.GetEnvironmentVariable("HKTAS_LOADING_SERIAL_BOUNDARY_ONLY") == "1";
            if (serialBoundaryOnly && samplingWindows.ExplicitWindows)
                throw new InvalidOperationException("Candidate-only diagnostics cannot declare gameplay sampling windows.");
            observationIdentity = new LoadingObservationIdentity(directory);
            nativeValues = new LoadingNativeValueObservation(Reference);
            rigidbodies = new LoadingRigidbodyObservation();
            fields = new LoadingFieldDigest(Reference, type => type == typeof(MonoBehaviour)
                || type == typeof(UnityEngine.Object), (owner, field, value) =>
                    nativeValues.Field(owner, field, value) ?? observationIdentity.Field(owner, field, value), IsObservationCallback,
                    packedArrayElement: LoadingPackedUnityArrays.Enabled ? LoadingPackedUnityArrays.Registered : (Func<Type, bool>?)null,
                    // The registered native pointers, roots, GUIDs and integer
                    // epochs do not bind any Single/Double instance field.
                    directFloatingField: _ => true);
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_SCHEDULER") == "1")
                nativeScheduler = new LoadingNativeSchedulerDigest();
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_NATIVE_PHYSICS") == "1")
                nativePhysics = new LoadingNativePhysicsDigest();
            if (nativePhysicsOnly && nativePhysics == null)
                throw new InvalidOperationException("Native-physics-only diagnostics require NativePhysics.");
            payloadType = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload")
                ?.GetType("HollowKnightTAS.ClockPayload.ClockController", false);
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
                samplingScope = serialBoundaryOnly ? "serial-candidate-boundaries-only-diagnostic"
                    : samplingWindows.ExplicitWindows ? "bounded-transition-windows-v1" : "bounded-window",
                gameplayWindows = samplingWindows.ExplicitWindows ? samplingWindows.Snapshot() : null,
                managedFieldGraph = "ordered-component-fsm-root-shared-references-v1; scheduler-and-SceneLoad-scopes-independent",
                completeStateCoverage = false,
                knownMissing = new[] { "native-coroutine-running-callstacks-and-unregistered-schedulers", "native-physics-contact-caches",
                    "arbitrary-mod-statics", "ambiguous-asset-identities", "ambiguous-scene-object-identities", "dormant-roots-between-window-endpoints-willactivate-loadfinish",
                    "TAS-controller-internals-require-separate-cursor-input-codecs",
                    "native-Unity-components-and-mutable-assets", "external-Steam-event-queue-and-account-state" } });
            GameManager.SceneTransitionBegan += TransitionBegan;
            USceneManager.sceneLoaded += SceneLoaded;
            USceneManager.sceneUnloaded += SceneUnloaded;
            USceneManager.activeSceneChanged += ActiveSceneChanged;
        }

        internal static LoadingGameplayOracle? Create(string directory, Func<long> movieFrame,
            Func<long> nativeFrame, Func<string> gameplayRng)
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_GAMEPLAY_ORACLE") != "1") return null;
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CLOCK_ROUNDTRIP") == "1")
                throw new InvalidOperationException("The read-only gameplay oracle cannot run with clock-write diagnostics.");
            if (!long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_FIRST"), out var first)
                || !long.TryParse(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_END"), out var end)
                || first < 0 || end <= first || end - first > 10000)
                throw new InvalidOperationException("P0 gameplay oracle requires a bounded Movie window.");
            return new LoadingGameplayOracle(directory, first, end, movieFrame, nativeFrame, gameplayRng);
        }

        internal void Sample(string phase, string boundary)
        {
            if (sealedTrace || !samplingWindows.Contains(movieFrame())) return;
            if (serialBoundaryOnly && phase != "candidate") return;
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
                var fullInventory = (phase == "event" && (boundary == "will-activate" || boundary == "load-finish"))
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
                        fieldDiagnostics = lastFieldDiagnostics, nativeCoroutines = nativeScheduler?.FieldDiagnostics });
                    if (Encoding.UTF8.GetByteCount(inventory) > 16 * 1024 * 1024)
                        throw new InvalidOperationException("P0 candidate inventory exceeds its 16 MiB budget.");
                    File.WriteAllText(Path.Combine(directory, "loading-candidate-" + transitionOrdinal + "-inventory.json"),
                        inventory, new UTF8Encoding(false));
                }
                Write(new { kind = "sample", sequence = rows++, phase, boundary,
                    movieFrame = movieFrame(), nativeFrame = nativeFrame(),
                    scene = USceneManager.GetActiveScene().name, state,
                    nativePhysics = fullInventory ? nativePhysics?.Inventory : null,
                    nativePhysicsEntries = nativePhysics?.DiagnosticEntries,
                    captureMilliseconds = captureElapsed.ElapsedMilliseconds,
                    captureStages = captureStages.ToArray() });
                if (phase == "before" || phase == "after") gameplaySamples++;
                priorCapture = new { movieFrame = movieFrame(), phase, boundary,
                    captureMilliseconds = captureElapsed.ElapsedMilliseconds, stages = captureStages.ToArray() };
                CaptureProgress("sample-complete");
            }
            catch (Exception exception) { Seal(false, exception.GetType().Name + ": " + exception.Message); }
        }

        internal void Completed()
        {
            if (sealedTrace || movieFrame() != end) return;
            var complete = serialBoundaryOnly || gameplaySamples == 2 * samplingWindows.GameplayFrames;
            Seal(complete, complete ? "window-complete" : "missing-gameplay-samples");
        }

        private SortedDictionary<string, string> CaptureState(bool fullInventory)
        {
            var elapsed = Stopwatch.StartNew();
            observationIdentity.Refresh();
            nativeValues.BeginSample();
            detachedFsmReferences.Clear();
            detachedFsmRoots.Clear();
            unityIds.Clear(); fsmIds.Clear(); fsmMemberIds.Clear();
            sceneObjectOwners.Clear();
            sceneRoots.Clear(); gameObjectPaths.Clear();
            assetIdentities.Clear();
            assetProvenance.Clear();
            assetRoots.Clear(); assetInstances.Clear();
            assetValues.BeginSample();
            textCacheDetails.Clear();
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
                    fsmIds[fsm] = item.id;
                    var states = fields.Read(fsm, "states") as Array;
                    if (states == null) continue;
                    for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                    {
                        var state = states.GetValue(stateIndex);
                        if (state == null) continue;
                        var stateId = item.id + "/state[" + stateIndex + "]:" + fields.Read(state, "name");
                        fsmMemberIds[state] = stateId;
                        var actions = fields.Read(state, "actions") as Array;
                        if (actions == null) continue;
                        for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                        {
                            var action = actions.GetValue(actionIndex);
                            if (action != null) fsmMemberIds[action] = stateId + "/action[" + actionIndex + "]";
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
                foreach (var component in allBehaviours)
                {
                    var id = UnityId(component);
                    objects[component.GetInstanceID()] = id;
                    objects[component.gameObject.GetInstanceID()] = UnityId(component.gameObject);
                    var pointer = (IntPtr)nativePointer.GetValue(component)!;
                    if (pointer != IntPtr.Zero) nativeBehaviours[pointer] = id;
                }
                schedulerDigest = nativeScheduler.Capture(fields,
                    id => objects.TryGetValue(id, out var value) ? value : null,
                    pointer => nativeBehaviours.TryGetValue(pointer, out var value) ? value : null,
                    includeFieldDiagnostics: fullInventory && fieldDetails);
            }
            var inventory = new List<object>();
            CaptureProgress("managed-and-fsm-fields");
            var fieldDiagnostics = new List<object>();
            var mono = new StringBuilder(); var fsms = new StringBuilder(); var staticRoots = new StringBuilder();
            fields.BeginSharedGraph();
            try
            {
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
                            AppendFsmMember(state, fsms, inventory);
                            if (fields.Read(state, "actions") is Array actions)
                                for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                                {
                                    var action = actions.GetValue(actionIndex);
                                    if (action != null) AppendFsmMember(action, fsms, inventory);
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
                var fieldDigests = fullInventory && fieldDetails && DetailedComponent(component, item.id)
                    ? fields.Fields(component.GetType()).Select(field => new
                        { field = field.Name, owner = field.DeclaringType!.FullName,
                            digest = fields.CaptureFields(component, item.id, field.Name).Sha256 }).ToArray() : null;
                inventory.Add(new { id = item.id, type = component.GetType().FullName,
                    digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported, fieldDigests });
            }
            if (!nativePhysicsOnly)
                foreach (var root in LoadingRegisteredStaticRoots.Read())
                {
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
            }
            finally { fields.EndSharedGraph(); }
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
            for (var i = 0; i < assetRoots.Count; i++)
            {
                var entry = assetRoots[i];
                var snapshot = assetValues.Capture(entry.Item1, value => omissions.Add(value));
                if (snapshot == null) continue;
                var id = entry.Item2 + "/asset-values";
                var digest = fields.Capture(snapshot, id, expandRoot: true);
                foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
                text.Append(id).Append(':').Append(digest.Sha256).Append('\n');
                inventory.Add(new { id, type = entry.Item1.GetType().FullName,
                    digest = digest.Sha256, nodes = digest.Nodes, unsupported = digest.Unsupported });
            }
            return Hash(text.ToString());
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
                foreach (var component in go.GetComponents<Component>())
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
                            omissions.Add("native-component-codec:" + type);
                    }
                object? rect = transform is RectTransform rt ? new { rt.anchorMin, rt.anchorMax,
                    rt.anchoredPosition, rt.sizeDelta, rt.pivot, rt.offsetMin, rt.offsetMax, rt.rect } : null;
                var components = go.GetComponents<Component>().Select(component => component == null
                    ? (object)new { type = "missing-script" }
                    : new { type = component.GetType().FullName,
                        enabled = component is Behaviour b ? (bool?)b.enabled : null,
                        hideFlags = (int)component.hideFlags,
                        runtime = CaptureNativeComponent(component, fullInventory),
                        nativePayload = component is MonoBehaviour ? "managed-fields-separate"
                            : component is Transform ? "local-transform-and-layout"
                            : component is Rigidbody2D || component is Collider2D ? "native-physics-separate"
                            : "requires-native-codec" }).ToArray();
                var id = item.id + "/native-hierarchy";
                var digest = fields.Capture(new { go.activeSelf, go.layer, go.tag, go.isStatic,
                    hideFlags = (int)go.hideFlags, parent = transform.parent == null ? null : UnityId(transform.parent),
                    sibling = transform.GetSiblingIndex(), transform.hasChanged, transform.localPosition, transform.localRotation,
                    transform.localScale, rect, components }, id, expandRoot: true);
                text.Append(id).Append(':').Append(digest.Sha256).Append('\n');
                foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
                inventory.Add(new { id, type = "UnityEngine.GameObject/native-hierarchy", digest = digest.Sha256,
                    nodes = digest.Nodes, unsupported = digest.Unsupported });
            }
            return Hash(text.ToString());
        }

        private object? CaptureNativeComponent(Component component, bool diagnostics)
        {
            var state = LoadingNativeComponentObservation.Capture(component, message => omissions.Add(message));
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

        private static uint[] CaptureParticleRandomState()
        {
            if (!HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesCanonicalParticles(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)) return Array.Empty<uint>();
            var payload = AppDomain.CurrentDomain.GetAssemblies()
                .Single(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload");
            return (uint[])payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!
                .GetMethod("CaptureParticleRandomState", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        }

        private void AppendFsmMember(object member, StringBuilder digestText, List<object> inventory)
        {
            if (!fsmMemberIds.TryGetValue(member, out var id))
                throw new InvalidOperationException("P0 FSM member identity unavailable.");
            var digest = fields.Capture(member, id, expandRoot: true);
            digestText.Append(id).Append(':').Append(digest.Sha256).Append('\n');
            foreach (var omitted in digest.Unsupported) omissions.Add(omitted);
            inventory.Add(new { id, type = member.GetType().FullName, digest = digest.Sha256,
                nodes = digest.Nodes, unsupported = digest.Unsupported });
        }

        private bool DetailedComponent(Component component, string id)
            => detailedTypes.Contains(component.GetType().FullName ?? "")
                || (component is UnityEngine.UI.Text && (id.Contains("/MainMenuButtons[") || id.Contains("/ControllerMessage[")));

        private string? Reference(object value)
        {
            if (value is ParticleSystem.MinMaxGradient gradient) return GradientIdentity(gradient);
            if (value is Coroutine coroutine && nativeScheduler != null) return nativeScheduler.Reference(coroutine, fields);
            if (value is UnityEngine.Object unity) return UnityId(unity);
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
            if (unityIds.TryGetValue(value, out var id)) return id;
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
                return id;
            }
            GameObject? go = value as GameObject;
            if (value is Component component) go = component.gameObject;
            if (go != null && Loaded(go))
            {
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
                id = assetIdentities.Bind(instance, "asset", value.GetType().FullName!, value.name, assetProvenance.Capture(instance));
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
            catch (Exception exception) { Modding.Logger.LogError("P0 gameplay oracle export failed: " + exception.Message); }
            finally { nativeScheduler?.Release(); nativeValues.Dispose(); fields.Dispose(); buffer.Dispose(); }
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
            Action remove = () => { load.FetchComplete -= fetch; load.WillActivate -= activate;
                load.ActivationComplete -= activated; load.Complete -= complete;
                load.StartCalled -= started; load.Finish -= finish; };
            finish = () => { Sample("event", "load-finish"); remove(); detach.Remove(remove); };
            load.FetchComplete += fetch; load.WillActivate += activate;
            load.ActivationComplete += activated; load.Complete += complete;
            load.StartCalled += started; load.Finish += finish; detach.Add(remove);
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
