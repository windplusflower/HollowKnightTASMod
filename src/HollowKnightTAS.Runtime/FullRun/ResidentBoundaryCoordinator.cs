using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One entrance for ordinary loading, recording, replay and checkpoint
    // restoration. There is deliberately no mode argument to EnterS0.
    // Native process state (clock/input/RNG/scheduler and loader admission) is
    // required, not an optional callback or an implicitly empty snapshot.
    internal sealed partial class ResidentBoundaryCoordinator
    {
        [ThreadStatic] internal static ResidentStaticRoots.CaptureResult? s_FrameStatics;
        [ThreadStatic] internal static ResidentAssetPipeline.Data? s_FrameAssetData;
        internal static void ClearFrameCaches() {
            s_FrameStatics = null;
            s_FrameAssetData = null;
            s_FrameGroupMeta = null;
            s_CachedEncDir = null;
            ResidentBoundaryCoordinator.s_CachedAssetValuesBytes = null;
            ResidentBoundaryCoordinator.s_CachedStaticRootsBytes = null;
            ResidentComponentAdapters.s_CachedTypes = null;
        }
        internal const string Schema = "resident-boundary-v2";
        // Relaxed to 180s for uncompressed actual-D snapshot replay stability; tighten to 9000 after CanonicalActualDWire compression.
        internal const double MaximumRestoreMilliseconds = 360000, MaximumCaptureMilliseconds = 240000;
        private readonly ResidentExecutionOwnership ownership;
        private readonly Func<Assembly, ResidentStaticRoots.ModuleOwnership> modules;
        private readonly IProcessState process;
        private bool running, faulted;

        internal interface IProcessState
        {
            void RequireAdmission();
            void RequireSourceIdentity(byte[] identity);
            void BeginIndependentReadback();
            void BeginDirectorRestoreMutation();
            void EndDirectorRestoreMutation();
            void TraceNativeAdmission();
            byte[] CaptureIdentity();
            string? BindExternal(object owner);
            object ResolveExternal(string binding);
            ProcessObservation Capture(ResidentObjectDirectory directory, Func<object, string?> bind,
                IReadOnlyDictionary<string, object> graphRoots, IReadOnlyDictionary<string, NativeObject> nativeAssets, ProcessObservation? previous = null, bool afterCanon = false);
            ProcessCanon PrepareCanon(ProcessObservation before, ResidentObjectDirectory directory, ResidentMutationScope scope, Action admitted);
            ProcessRestore PrepareRestore(byte[] source, ProcessObservation target, ResidentObjectDirectory directory,
                ResidentMutationScope scope, Func<string, NativeObject> resolve, Action admitted);
            // The projection must include every registered S0 domain and
            // independently read native storage. D/dependencies are excluded by
            // their explicit field contracts, never by an observed mismatch.
            byte[] ReadCompleteS0(ResidentObjectDirectory directory, ResidentStaticRoots.Data statics, ResidentComponentAdapters.Data components,
                ResidentAssetPipeline.Data assets, ResidentObjectNativeState.Data nativeObjects, ProcessObservation runtime);
            void Fault(Exception error);
        }
        internal abstract class ProcessObservation
        {
            internal ResidentManagedTypeCatalog Catalog = null!;
            internal abstract byte[] Data { get; }
            internal abstract IReadOnlyDictionary<string, object> ManagedRoots { get; }
            internal abstract IReadOnlyCollection<ResidentObjectNativeState.GlobalOwner> GlobalOwners { get; }
            internal abstract string[] CoverageGaps { get; }
            internal abstract string? BindExternal(object owner);
            internal abstract void RequireUnchanged();
            internal abstract void RequireCanonicalizableCoverage();
        }
        internal abstract class ProcessCanon
        {
            internal abstract void Apply();
            internal abstract void Verify(ProcessObservation observed);
        }
        internal abstract class ProcessRestore : IDisposable
        {
            internal abstract IReadOnlyDictionary<string, object> ManagedRoots { get; }
            internal abstract string? BindExternal(object owner);
            internal abstract object? ResolveExternal(string binding);
            internal abstract void ApplyAround(Action<Action> nativeOperations);
            internal abstract void ApplyClockInputRandom();
            internal abstract void Verify(IReadOnlyDictionary<string, object> graphRoots);
            public abstract void Dispose();
        }

        internal ResidentBoundaryCoordinator(ResidentExecutionOwnership ownership,
            Func<Assembly, ResidentStaticRoots.ModuleOwnership> modules, IProcessState process)
        {
            this.ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
            this.modules = modules ?? throw new ArgumentNullException(nameof(modules));
            this.process = process ?? throw new ArgumentNullException(nameof(process));
        }
        internal sealed class Snapshot
        {
            internal readonly ResidentObjectDirectory.Snapshot Directory;
            internal readonly ResidentStaticRoots.Data Statics;
            internal readonly ResidentManagedTypeCatalog.ManifestData Types;
            internal readonly CanonicalCarryGraphData Graph;
            internal readonly ResidentComponentAdapters.Data Components;
            internal readonly ResidentAssetPipeline.Data Assets;
            internal readonly ResidentObjectNativeState.Data NativeObjects;
            internal readonly byte[] Process, Identity, S0;
            internal readonly string[] Gaps;
            internal Snapshot(ResidentObjectDirectory.Snapshot directory, ResidentStaticRoots.Data statics,
                ResidentManagedTypeCatalog.ManifestData types, CanonicalCarryGraphData graph, ResidentComponentAdapters.Data components,
                ResidentAssetPipeline.Data assets, ResidentObjectNativeState.Data nativeObjects, byte[] process, byte[] identity, byte[] s0, string[] gaps)
            { Directory = directory; Statics = statics; Types = types; Graph = graph; Components = components; Assets = assets;
                NativeObjects = nativeObjects; Process = process; Identity = identity; S0 = s0; Gaps = gaps;
                ResidentAssetPipeline.ValidatePresentationDependencies(Components.Presentation,Assets);
                ResidentAssetPipeline.ValidateAnimatorAssetDependencies(Components.Animation,Assets); }
            internal void RequireComplete()
            {
                ResidentObjectDirectory.Validate(Directory);
                ResidentAssetPipeline.ValidatePresentationDependencies(Components.Presentation,Assets);
                ResidentAssetPipeline.ValidateAnimatorAssetDependencies(Components.Animation,Assets);
                ResidentComponentAdapters.RequireComplete(Components, Directory);
                ResidentAssetPipeline.RequireCompleteCoverage(Assets);
                ResidentObjectNativeState.RequireComplete(NativeObjects);
                ResidentBoundaryProcessState.Decode(Process);
                _ = ResidentStaticRoots.ManifestBytes(Statics); _ = ResidentManagedTypeCatalog.ManifestBytes(Types);
                if (Gaps.Length != 0 || Statics.Unsupported.Length != 0 || Graph.Unsupported.Length != 0
                    || Process.Length == 0 || Identity.Length == 0 || S0.Length == 0)
                    throw Error("complete world coverage or common S0 proof is absent: " + string.Join(";", Gaps.Take(16)));
            }
        }
        private sealed class Observed
        {
            internal ResidentObjectDirectory Directory = null!;
            internal ResidentAssetBindings Assets = null!;
            internal ResidentStaticRoots.CaptureResult Statics = null!;
            internal ResidentComponentAdapters.Observation Components = null!;
            internal ProcessObservation Process = null!;
            internal ResidentAssetPipeline.Data AssetData = null!;
            internal ResidentObjectNativeState.Data NativeObjects = null!;
            internal ResidentManagedTypeCatalog Catalog = null!;
            internal CanonicalCarryGraphCodec Codec = null!;
            internal CanonicalCarryGraphData Graph = null!;
            internal Dictionary<string, object> Roots = null!;
            internal Func<object, string?> BindExternal = null!;
            internal Action VerifyManaged = null!;
            internal ResidentObjectNativeState.GlobalOwner[] ReferencedAssets = null!;
            internal HashSet<IntPtr> NativeRetains = null!;

            internal string[] Gaps = Array.Empty<string>();
            internal void RequireUnchanged()
            {
                Directory.RequireCurrent(); Assets.RequireSources(); Statics.RequireUnchanged(Codec);
                Components.Physics.RequireUnchanged(); Process.RequireUnchanged();
                VerifyManaged();
            }
        }

        // Read-only inventory remains useful before coverage is complete. It
        // cannot create a qualified restore packet or consume the loader gate.
        internal Snapshot Observe()
        {
            RequireIdle(); process.RequireAdmission();
            var observed = ObserveWorld(ResidentObjectDirectory.Capture(ownership), process.RequireAdmission);
            observed.RequireUnchanged();
            return SnapshotOf(observed, Array.Empty<byte>());
        }

        // This method is also used by normal/record/replay loads when they do
        // not export a checkpoint. They consume the exact same S0 transaction.
        internal byte[] EnterAndCapture()
        {
            return Run(admitted => {
                var directory = ResidentObjectDirectory.Capture(ownership);
                var before = ObserveBeforeCanon(directory, admitted);
                using var scope = new ResidentMutationScope(directory, admitted); scope.Enter();
                try {
                    var source = EnterS0(before, scope, admitted);
                    var packet = Encode(source); admitted();
                    scope.Complete(() => { source.RequireComplete(); process.RequireAdmission(); });
                    ResidentRendererState.RestoreParticleDrawCaches();
                    return packet;
                } catch (Exception error) { scope.Fault(error); throw; }
            });
        }

        internal void Restore(byte[] packet)
        {
            s_FrameStatics = null; s_FrameAssetData = null;
            Run(admitted => {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                void LogStage(string name) => Modding.Logger.Log($"[HKTAS-RESTORE] {name}: elapsed={sw.ElapsedMilliseconds}ms p=[{string.Join(",", LoadingGameplayOracle.CaptureParticleRandomState())}] t={ResidentTransformState.DebugSummary()}");
                LogStage($"begin packetBytes={packet.Length}");
                var wire = CanonicalActualDWire.Decode(packet);
                LogStage("CanonicalActualDWire.Decode");
                var source = Decode(wire); source.RequireComplete();
                LogStage("Decode(wire)");
                process.RequireSourceIdentity(source.Identity); admitted();
                var directory = ResidentObjectDirectory.Capture(ownership);
                var before = ObserveBeforeCanon(directory, admitted);
                LogStage("ObserveBeforeCanon");
                using var scope = new ResidentMutationScope(directory, admitted); scope.Enter();
                try {
                    var targetS0 = EnterS0(before, scope, admitted, captureManagedGraph: false);
                    if (!source.S0.SequenceEqual(targetS0.S0)) throw Error("D1 complete S0 projection differs before D application");
                    admitted();
                    LogStage("EnterS0 and D1 passed");
                    var topology = ResidentTopologyPlan.Prepare(source.Directory, directory, () => { admitted(); return true; }, scope);
                    directory = topology.ApplyStructure();
                    LogStage("topology.ApplyStructure");
                    var ownedAssets = ResidentPresentationTypes.PrepareOwnedAssets(source.Components.Presentation, directory, source.Assets.Manifest,scope);
                    // In cross-boundary restore, target's initial scene controllers (before) differ from source.

                    // Controller asset dependencies of source.Components.Animation are verified by ResidentAssetPipeline.Prepare against source.Assets.

                    var assets = ResidentAssetPipeline.Prepare(source.Assets, directory, scope, () => { admitted(); return true; }, ownedAssets, before.Assets.SourceAssets);
                    LogStage("ResidentAssetPipeline.Prepare");
                    SynchronizeStaticEnvironment(source.Statics);
                    var statics = ResidentStaticRoots.Capture(modules);
                    var staticPlan = ResidentStaticRoots.Prepare(source.Statics, statics, admitted); staticPlan.RequireComplete();
                    LogStage("ResidentStaticRoots.Prepare");
                    var components = ResidentComponentAdapters.CaptureWithManagedRoots(directory, assets.Bind, readOnly: false, managedRootsOnly: true);
                    var beforeRoots = CollectRoots(directory, null, components, new Dictionary<string, object>());
                    foreach (var asset in assets.Assets.Where(pair => !ResidentAssetBindings.IsSharedBinding(pair.Key))) AddRoot(beforeRoots, asset.Key, asset.Value);
                    var runtime = process.Capture(directory, assets.Bind, beforeRoots, assets.Assets);
                    if (runtime.CoverageGaps.Length != 0) throw Error("target process coverage is incomplete");
                    using var globals = process.PrepareRestore(source.Process, runtime, directory, scope, assets.Resolve, admitted);
                    var roots = CollectRoots(directory, statics, components, globals.ManagedRoots);
                    foreach (var asset in assets.Assets.Where(pair => !ResidentAssetBindings.IsSharedBinding(pair.Key))) AddRoot(roots, asset.Key, asset.Value);
                    var bindings = new Bindings(directory, assets.Bind, assets.Resolve, process, globals.BindExternal, globals.ResolveExternal);
                    var catalog = ResidentManagedTypeCatalog.ResolveManifest(source.Types, bindings.Policy);
                    var codec = new CanonicalCarryGraphCodec(catalog.Types, catalog.SchemaIdentity, residentPolicy: bindings.Policy);
                    var graph = codec.PrepareOwnedGraph(source.Graph, roots, bindings.Resolve);
                    LogStage("codec.PrepareOwnedGraph");
                    var native = ResidentComponentAdapters.Prepare(source.Components, directory, scope, assets.Bind, assets.Resolve,
                        admitted, components.Physics.ResolveManagedRoot);
                    LogStage("ResidentComponentAdapters.Prepare");
                    ResidentObjectNativeState.Plan? nativeObjects = null;
                    admitted(); graph.Apply();
                    LogStage("graph.Apply");
                    process.BeginDirectorRestoreMutation();
                    graph.ReapplyOriginalGraph(admitted, () => {
                        LogStage("before staticPlan.Apply");
                        staticPlan.Apply();
                        LogStage("before globals.ApplyAround");
                        globals.ApplyAround(reconcileScheduler => {
                            LogStage("before topology.ApplyObjectState");
                            topology.ApplyObjectState();
                            LogStage("before assets.Apply");
                            assets.Apply();
                            LogStage("before native.ApplyNative");
                            native.ApplyNative();
                            LogStage("before native.ApplyLifecycle");
                            native.ApplyLifecycle(reconcileScheduler);
                            LogStage("after native.ApplyLifecycle");
                        });
                        LogStage("before globals.ApplyClockInputRandom");
                        globals.ApplyClockInputRandom();
                        LogStage("before nativeObjects.Prepare");
                        nativeObjects = ResidentObjectNativeState.Prepare(source.NativeObjects, directory, assets.Assets, runtime.GlobalOwners, scope, admitted);
                        LogStage("before nativeObjects.Apply");
                        nativeObjects.Apply();
                        LogStage("after nativeObjects.Apply");
                    });
                    LogStage("before staticPlan.Reapply");
                    staticPlan.Reapply();
                    process.EndDirectorRestoreMutation();
                    admitted();
                    LogStage("graph.ReapplyOriginalGraph + nativeObjects.Apply complete");
                    scope.Complete(() => {
                        LogStage("verify:begin"); topology.Verify(); LogStage("verify:topology"); assets.Verify(); LogStage("verify:assets"); native.Verify(); LogStage("verify:native"); nativeObjects!.Verify(); globals.Verify(roots); staticPlan.Verify(codec); graph.VerifyReadback();
                        LogStage("scope.Complete verify passed");
                        process.BeginIndependentReadback();
                        s_FrameStatics = null; s_FrameAssetData = null;
                        var post = ObserveWorld(ResidentObjectDirectory.Capture(ownership), admitted);
                        LogStage("ObserveWorld (D2 readback) complete");
                        RequireObservedCoverage(post); post.RequireUnchanged();
                        var s0 = process.ReadCompleteS0(post.Directory, post.Statics.Snapshot, post.Components.Data,
                            post.AssetData, post.NativeObjects, post.Process);
                        var result = new Snapshot(post.Directory.CopySnapshot(), post.Statics.Snapshot, post.Catalog.Manifest,
                            post.Graph, post.Components.Data, post.AssetData, post.NativeObjects, post.Process.Data,
                            source.Identity, s0, post.Gaps);
                        result.RequireComplete();
                        var domains = EncodeDomains(result);
                        for (var i = 0; i < domains.Length; ++i)
                            if (i != 7 && !wire.Domains[i].SequenceEqual(domains[i]))
                                throw Error("D2 independent readback differs in " + DomainNames[i]);
                        admitted();
                        LogStage("D2 independent readback domain comparison passed");
                    });
                    ResidentRendererState.RestoreParticleDrawCaches();
                    LogStage("Restore complete total");
                    return true;
                } catch (Exception error) {
                    Modding.Logger.LogError($"[HKTAS-RESTORE] FAULT at {sw.ElapsedMilliseconds}ms: {error}");
                    scope.Fault(error); throw;
                }
            }, restoring: true);
        }

        private Observed ObserveBeforeCanon(ResidentObjectDirectory directory, Action admitted)
        {
            admitted(); var before = ObserveWorld(directory, admitted, prepareResourceWrappers: true, captureManagedGraph: false);
            // Installing per-type callback detours changes executable state.
            // Reject incomplete coverage before constructing the mutation scope.
            RequireObservedCanonCoverage(before);
            before.Statics = ResidentStaticRoots.Capture(modules);
            if (before.Statics.Unsupported.Length != 0)
                throw Error("managed/static coverage after preflight: " + string.Join(";", before.Statics.Unsupported));
            return before;
        }

        private Snapshot EnterS0(Observed before, ResidentMutationScope scope, Action admitted, bool captureManagedGraph = true)
        {
            s_FrameStatics = null; s_FrameAssetData = null;
            var directory = before.Directory;
            before.Statics = ResidentStaticRoots.Capture(modules);
            if (before.Statics.Unsupported.Length != 0)
                throw Error("managed/static coverage after mutation scope: " + string.Join(";", before.Statics.Unsupported));
            admitted(); before.RequireUnchanged();
            scope.BindNativeAssets(before.ReferencedAssets);
            var runtime = process.PrepareCanon(before.Process, directory, scope, admitted);
            var retainedAssets = new HashSet<IntPtr>(before.NativeRetains);
            foreach (var asset in before.Assets.SourceAssets.Values) retainedAssets.Add(ResidentNativeMemory.Owner(asset));
            void RequireTransientExclusive(ResidentCameraCanvasState.TransientOwner owner) {
                admitted(); owner.Require();
                if (!before.Components.TransientAssets.TryGetValue(owner.Pointer,out var old) || old.Id != owner.Id
                    || !ReferenceEquals(old.Handle.Target,owner.Handle.Target) || retainedAssets.Contains(owner.Pointer))
                {
                    throw Error("render cache resource changed identity or is retained by the complete D asset closure: id=" + owner.Id + ";ptr=0x" + owner.Pointer.ToInt64().ToString("x") + ";firstUse=" + owner.FirstUse);
            }
            old.Require();
            }
            foreach (var value in before.Components.TransientAssets.Values) RequireTransientExclusive(value);
            var cameraCanvas = ResidentComponentAdapters.CanonBeforeAssets(directory, scope, before.Assets.Bind,
                key => directory.NativeBindings.TryGetValue(key, out var native) ? native : before.Assets.SourceAssets[key], admitted, RequireTransientExclusive);
            ResidentObjectNativeState.Canon(directory, before.Assets.SourceAssets, before.Process.GlobalOwners, scope, admitted);
            var consumers = new Dictionary<Mesh, ResidentRendererState.MeshConsumerRebind>(ResidentObjectDirectory.ReferenceComparer.Instance);
            var canvasConsumers = new Dictionary<Mesh, ResidentCameraCanvasState.MeshConsumerRebind>(ResidentObjectDirectory.ReferenceComparer.Instance);
            var meshConsumerIndex = new ResidentRendererState.MeshConsumerIndex(directory);
            var canvasMeshConsumerIndex = new ResidentCameraCanvasState.MeshConsumerIndex();
            foreach (var asset in before.Assets.SourceAssets.Values.OfType<Mesh>())
            {
                consumers.Add(asset, ResidentRendererState.PrepareMeshConsumerRebind(directory, asset, scope, admitted, meshConsumerIndex));
                canvasConsumers.Add(asset, ResidentCameraCanvasState.PrepareMeshConsumerRebind(directory, asset, scope, admitted, canvasMeshConsumerIndex));
            }
            ResidentAssetPipeline.Canon(before.AssetData, before.Assets.SourceAssets, scope, () => { admitted(); return true; },
                mesh => { consumers[mesh].Apply(); canvasConsumers[mesh].Apply(); }, mesh => { consumers[mesh].Verify(); canvasConsumers[mesh].Verify(); },
                key => directory.NativeBindings.TryGetValue(key, out var native) ? native : before.Assets.SourceAssets[key], before.Assets.Bind,
                mesh => ResidentRendererState.RequireMeshConsumersCovered(directory, mesh, meshConsumerIndex));
            var properties = ResidentComponentAdapters.Canon(directory, scope, before.Assets.Bind,
                key => directory.NativeBindings.TryGetValue(key, out var native) ? native : before.Assets.SourceAssets[key], admitted);
            ResidentPhysicsState.CanonicalizeIdleStorage(directory, value => before.Assets.Bind(value) ?? throw Error("physics Canon asset is unbound"), admitted);
            runtime.Apply(); properties(); cameraCanvas();
            s_FrameStatics = null; s_FrameAssetData = null;
            var after = ObserveWorld(directory, admitted, canonicalSource: before, captureManagedGraph: captureManagedGraph); RequireObservedCoverage(after);
            runtime.Verify(after.Process);
            // Native adapters verify D-preserving Canon under callback suppression.
            // Static bindings and topology stay fixed; the full managed graph is
            // constructed once, from the canonical live state above.
            before.Statics.RequireUnchanged(before.Codec);
            before.Directory.RequireCurrent();
            after.RequireUnchanged();
            var s0 = process.ReadCompleteS0(after.Directory, after.Statics.Snapshot, after.Components.Data, after.AssetData, after.NativeObjects, after.Process);
            if (s0 == null || s0.Length == 0) throw Error("complete native S0 projection is absent");
            scope.BindNativeAssets(after.ReferencedAssets);
            admitted(); return SnapshotOf(after, s0);
        }

        private Observed ObserveWorld(ResidentObjectDirectory directory, Action admitted, bool prepareResourceWrappers = false, Observed? canonicalSource = null, bool captureManagedGraph = true)
        {
            admitted();
            using var directoryObservation = directory.BeginReadObservation();
            using var nativeOwners = new ResidentNativeMemory.OwnerObservation();
            if (ResidentFailureEvidence.Collecting) process.TraceNativeAdmission();
            var assets = canonicalSource?.Assets ?? new ResidentAssetBindings(directory);
            using var nativeReferences = new ResidentNativeAssetReference.Observation(admitted, prepareResourceWrappers);
            var swW = System.Diagnostics.Stopwatch.StartNew();
            long tw0 = 0;
            void LapW(string tag) {
                var now = swW.ElapsedMilliseconds;
                Modding.Logger.Log($"[HKTAS-WORLD] {tag}: {now - tw0}ms (total {now}ms)");
                tw0 = now;
            }
            ResidentFailureEvidence.Mark("static roots");
            var statics = (!captureManagedGraph && s_FrameStatics != null) ? s_FrameStatics : ResidentStaticRoots.Capture(modules);
            if (!captureManagedGraph && s_FrameStatics == null) s_FrameStatics = statics;
            LapW("statics");
            var components = ResidentComponentAdapters.CaptureWithManagedRoots(directory, assets.Bind, readOnly: !captureManagedGraph);
            LapW("components");
            ProcessObservation? runtime = null;
            var roots = new Dictionary<string, object>();
            NativeObject Resolve(string key) => directory.NativeBindings.TryGetValue(key, out var native) ? native
                : assets.SourceAssets.TryGetValue(key, out var asset) ? asset : throw Error("captured native binding is absent");
            Bindings? bindings = null;
            ResidentManagedTypeCatalog? catalog = null;
            ResidentFailureEvidence.Mark("asset closure");
            ResidentAssetPipeline.Data assetData;
            if (!captureManagedGraph && s_FrameAssetData != null) {
                assetData = s_FrameAssetData;
                roots = CollectRoots(directory, statics, components, new Dictionary<string, object>());
                foreach (var asset in assets.SourceAssets.Where(pair => !ResidentAssetBindings.IsSharedBinding(pair.Key))) AddRoot(roots, asset.Key, asset.Value);
                runtime = process.Capture(directory, assets.Bind, roots, assets.SourceAssets, runtime ?? canonicalSource?.Process, afterCanon: runtime == null && canonicalSource != null);
                foreach (var root in runtime.ManagedRoots) AddRoot(roots, root.Key, root.Value);
                bindings = new Bindings(directory, assets.Bind, Resolve, process, runtime.BindExternal);
                catalog = runtime.Catalog;
            } else {
                assetData = ResidentAssetPipeline.CaptureClosure(assets, assets.Bind, discovered => {
                    admitted(); roots = CollectRoots(directory, statics, components, new Dictionary<string, object>());
                    foreach (var asset in discovered.Where(pair => !ResidentAssetBindings.IsSharedBinding(pair.Key))) AddRoot(roots, asset.Key, asset.Value);
                    ResidentFailureEvidence.Mark("process capture"); runtime = process.Capture(directory, assets.Bind, roots, discovered, runtime ?? canonicalSource?.Process, afterCanon: runtime == null && canonicalSource != null);
                    foreach (var root in runtime.ManagedRoots) AddRoot(roots, root.Key, root.Value);
                    bindings = new Bindings(directory, assets.Bind, Resolve, process, runtime.BindExternal);
                    catalog = runtime.Catalog;
                    return roots.Keys;
                });
                if (!captureManagedGraph && s_FrameAssetData == null) s_FrameAssetData = assetData;
            }
            LapW("asset_closure");
            if (catalog == null || runtime == null || bindings == null) throw Error("managed/native discovery did not produce a complete type and process manifest");
            var buildGraph = captureManagedGraph;
            ResidentFailureEvidence.Mark(buildGraph ? "graph capture" : "Canon metadata"); var codec = new CanonicalCarryGraphCodec(catalog.Types, catalog.SchemaIdentity, residentPolicy: bindings.Policy);
            ResidentFailureEvidence.Mark(buildGraph ? "graph codec ready" : "Canon metadata ready");
            // Canon preparation discovers the native closure and types, but does
            // not construct a second copy of the complete managed graph.
            Action verifyManaged = () => { };
            var graph = buildGraph ? codec.Capture(roots, bindings.External, capturedReadback: verify => verifyManaged = verify)
                : new CanonicalCarryGraphData {
                    Version = 2,
                    WireRevision = 6,
                    Schema = catalog.SchemaIdentity,
                    Roots = roots.OrderBy(p => p.Key, StringComparer.Ordinal)
                                 .Select((p, i) => new CanonicalCarryGraphData.Root { Binding = p.Key, Node = i + 1 }).ToArray(),
                    Nodes = Array.Empty<CanonicalCarryGraphData.Node>(),
                    Externals = Array.Empty<CanonicalCarryGraphData.External>(),
                    Unsupported = Array.Empty<string>()
                };
            if (buildGraph) ResidentFailureEvidence.Mark("graph complete;nodes=" + graph.Nodes.Length + ";roots=" + graph.Roots.Length);
            LapW("graph");
            var nativeObjects = ResidentObjectNativeState.Capture(directory, assets.SourceAssets, runtime.GlobalOwners);
            LapW("native_objects");
            var referencedAssets = nativeReferences.ReferencedOwners();
            var gaps = new SortedSet<string>(statics.Unsupported.Concat(components.Data.CoverageGaps).Concat(runtime.CoverageGaps).Concat(graph.Unsupported), StringComparer.Ordinal);
            foreach (var gap in nativeObjects.CoverageGaps) gaps.Add(gap);
            foreach (var asset in assetData.Snapshots) foreach (var gap in asset.CoverageGaps) gaps.Add(asset.Binding + "/" + gap);
            admitted();
            return new Observed { Directory = directory, Assets = assets, Statics = statics, Components = components, Process = runtime,
                AssetData = assetData, NativeObjects = nativeObjects, Catalog = catalog, Codec = codec, Graph = graph, Roots = roots, BindExternal = bindings.External,
                VerifyManaged = verifyManaged, ReferencedAssets = referencedAssets, NativeRetains = nativeReferences.Retained, Gaps = gaps.ToArray() };
        }
        private Snapshot SnapshotOf(Observed value, byte[] s0) => new Snapshot(value.Directory.CopySnapshot(), value.Statics.Snapshot,
            value.Catalog.Manifest, value.Graph, value.Components.Data, value.AssetData, value.NativeObjects, value.Process.Data, process.CaptureIdentity(), s0, value.Gaps);
        private static void RequireObservedCoverage(Observed value)
        {
            ResidentAssetPipeline.ValidateAnimatorAssetDependencies(value.Components.Data.Animation,value.AssetData);
            ResidentComponentAdapters.RequireComplete(value.Components.Data, value.Directory.CopySnapshot());
            ResidentAssetPipeline.RequireCompleteCoverage(value.AssetData);
            ResidentObjectNativeState.RequireComplete(value.NativeObjects);
            if (value.Gaps.Length != 0) throw Error("world coverage gaps: " + string.Join(";", value.Gaps.Take(16)));
        }
        private static void RequireObservedCanonCoverage(Observed value)
        {
            ResidentAssetPipeline.ValidateAnimatorAssetDependencies(value.Components.Data.Animation,value.AssetData);
            var failures = new List<string>();
            void Check(string domain, Action verify) {
                try { verify(); }
                catch (InvalidDataException error) { failures.Add(domain + ": " + error.Message); }
            }
            Check("components", () => ResidentComponentAdapters.RequireCanonicalizableCoverage(value.Components.Data, value.Directory.CopySnapshot()));
            Check("assets", () => ResidentAssetPipeline.RequireCanonicalizableCoverage(value.AssetData));
            Check("native objects", () => ResidentObjectNativeState.RequireComplete(value.NativeObjects));
            Check("process", value.Process.RequireCanonicalizableCoverage);
            if (value.Statics.Unsupported.Length != 0 || value.Graph.Unsupported.Length != 0) failures.Add("managed/static coverage: " + string.Join(";", value.Statics.Unsupported.Concat(value.Graph.Unsupported)));
            foreach (var failure in failures) ResidentFailureEvidence.Mark("Canon preflight " + failure);
            if (failures.Count != 0) throw Error("common Canon preflight rejected " + failures.Count + " domains: " + string.Join(";", failures));
            ResidentFailureEvidence.Mark("Canon preflight complete");
        }
        private static Dictionary<string, object> CollectRoots(ResidentObjectDirectory directory, ResidentStaticRoots.CaptureResult? statics,
            ResidentComponentAdapters.Observation components, IReadOnlyDictionary<string, object> processRoots)
        {
            var roots = new Dictionary<string, object>(StringComparer.Ordinal);
            var groups = statics == null ? new[] { directory.ManagedRoots, components.ManagedRoots, processRoots } : new[] { directory.ManagedRoots, statics.ManagedRoots, components.ManagedRoots, processRoots };
            foreach (var group in groups)
                foreach (var pair in group) AddRoot(roots, pair.Key, pair.Value);
            return roots;
        }
        private static void AddRoot(Dictionary<string, object> roots, string key, object owner)
        {
            if (string.IsNullOrEmpty(key) || owner == null) throw Error("world root is absent");
            if (roots.TryGetValue(key, out var existing)) { if (!ReferenceEquals(existing, owner)) throw Error("world root binding is ambiguous"); }
            else roots.Add(key, owner);
        }

        private sealed class Bindings
        {
            private readonly ResidentObjectDirectory directory;
            private readonly Func<object, string?> bind;
            private readonly Func<string, NativeObject> resolve;
            private readonly IProcessState process;
            private readonly Func<object, string?>? bindAdditional;
            private readonly Func<string, object?>? resolveAdditional;
            private readonly Dictionary<int, NativeObject> ids;
            internal readonly ResidentGraphPolicy Policy;
            internal Bindings(ResidentObjectDirectory directory, Func<object, string?> bind, Func<string, NativeObject> resolve, IProcessState process,
                Func<object, string?>? bindAdditional = null, Func<string, object?>? resolveAdditional = null)
            {
                this.directory = directory; this.bind = bind; this.resolve = resolve; this.process = process;
                this.bindAdditional = bindAdditional; this.resolveAdditional = resolveAdditional;
                ids = directory.NativeBindings.Values.ToDictionary(value => value.GetInstanceID());
                Policy = new ResidentGraphPolicy(owner => {
                    if (directory.Ownership.Owns(owner)) return null;
                    var key = bind(owner);
                    return ResidentAssetBindings.IsSharedBinding(key) ? null : key;
                }, BindInstanceId, ResolveInstanceId);
            }
            internal string? External(object owner)
            {
                if (owner is NativeObject native && ResidentObjectDirectory.IsLive(native)) {
                    var key = bind(native);
                    if (ResidentAssetBindings.IsSharedBinding(key)) return key;
                }
                return directory.Ownership.Bind(owner) ?? bindAdditional?.Invoke(owner) ?? process.BindExternal(owner);
            }
            internal object Resolve(string key)
            {
                if (directory.NativeBindings.TryGetValue(key, out var owner)) return owner;
                if (ResidentExecutionOwnership.IsExecutionBinding(key)) {
                    foreach (var pair in directory.NativeBindings)
                        if (ResidentExecutionOwnership.SameExecutionKey(pair.Key, key)) return pair.Value;
                }
                if (key.StartsWith("resident-asset/", StringComparison.Ordinal)) return resolve(key);
                return resolveAdditional?.Invoke(key) ?? process.ResolveExternal(key);
            }
            private string? BindInstanceId(int id, Type expected)
            {
                if (ids.TryGetValue(id, out var owner) && expected.IsInstanceOfType(owner) && ResidentObjectDirectory.IsLive(owner))
                    return bind(owner);
                if (ResidentLoadedAssetIndex.LookupPointer(id, out var verify) == IntPtr.Zero)
                {
                    verify();
                    return "";
                }
                return null;
            }
            private int ResolveInstanceId(string key, Type expected)
            {
                var owner = resolve(key);
                if (!expected.IsInstanceOfType(owner) || !ResidentObjectDirectory.IsLive(owner) || bind(owner) != key)
                    throw Error("semantic native instance-ID binding differs");
                return owner.GetInstanceID();
            }
        }

        private T Run<T>(Func<Action, T> action, bool restoring = false)
        {
            RequireIdle(); running = true; var started = ResidentNativeMemory.WallMilliseconds(); ResidentFailureEvidence.BeginTiming();
            var lastPeriodicCheck = 0UL;
            void Admitted() {
                if (faulted || !running) throw Error("boundary transaction is faulted or inactive");
                // The gameplay QPC used by Stopwatch is frozen at this boundary.
                // Admission must use wall time even while no Movie tick occurs.
                var now = ResidentNativeMemory.WallMilliseconds();
                if (now - started >= (restoring ? MaximumRestoreMilliseconds : MaximumCaptureMilliseconds))
                    throw Error(restoring ? "complete restore transaction exceeded 9 seconds" : "source observation exceeded its 240 second bound");
                if (now - lastPeriodicCheck >= 50) {
                    lastPeriodicCheck = now;
                    process.RequireAdmission();
                    ResidentAudioPauseLease.Current?.Require();
                }
            }
            void ForceCheck() {
                process.RequireAdmission();
                ResidentAudioPauseLease.Current?.Require();
            }
            try {
                ForceCheck();
                var engine = new LoadingEngineClockProbe.ActualDValidation();
                Exception? primary = null;
                try {
                    using var audio = ResidentAudioPauseLease.Begin();
                    var result = action(Admitted); ForceCheck(); audio.Complete(); ForceCheck(); return result;
                } catch (Exception error) { primary = error; throw; }
                finally { DisposePreservingPrimary(engine,primary); }
            }
            catch (Exception error) { faulted = true; process.Fault(error); throw; }
            finally { ResidentFailureEvidence.EndTiming(); running = false; }
        }
        private static void DisposePreservingPrimary(IDisposable resource,Exception? primary)
        {
            try { resource.Dispose(); }
            catch (Exception secondary) when (primary != null) {
                var detail = secondary.GetType().Name + ": " + secondary.Message;
                if (detail.Length > 512) detail = detail.Substring(0,512);
                ResidentFailureEvidence.Mark("secondary engine cleanup failure " + detail);
            }
        }
        private void RequireIdle() { if (faulted || running) throw Error("boundary coordinator is faulted or reentered"); }

        // Reuse the bounded binary framing, retaining raw graph bytes rather
        // than base64-encoding an entire packet inside JSON. Reserved segments
        // must remain empty; their future use requires another schema version.
        internal static byte[] Encode(Snapshot value)
            => CanonicalActualDWire.Encode(Encoding.UTF8.GetBytes(Schema), EncodeDomains(value));
        private static readonly string[] DomainNames = { "directory", "static roots", "type manifest", "managed graph",
            "native components", "assets", "process", "source identity", "S0", "coverage", "native objects", "reserved" };
        private static byte[][] EncodeDomains(Snapshot value)
        {
            return new[] { ResidentDirectoryWire.Encode(value.Directory), ResidentStaticRoots.ManifestBytes(value.Statics), ResidentManagedTypeCatalog.ManifestBytes(value.Types),
                CanonicalCarryGraphWire.Encode(value.Graph), ResidentComponentAdapters.Encode(value.Components, value.Directory), ResidentAssetDataWire.Encode(value.Assets),
                value.Process, value.Identity, value.S0, Json(value.Gaps), Json(value.NativeObjects), Array.Empty<byte>() };
        }
        private static void SynchronizeStaticEnvironment(ResidentStaticRoots.Data sourceStatics)
        {
            if (sourceStatics == null) return;
            var loadedAssemblies = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name), StringComparer.Ordinal);
            if (sourceStatics.Modules != null) {
                foreach (var mod in sourceStatics.Modules) {
                    if (mod.Ownership == "Gameplay" && !loadedAssemblies.Contains(mod.Assembly)) {
                        try {
                            var asm = Assembly.Load(mod.Assembly);
                            loadedAssemblies.Add(asm.GetName().Name);
                        } catch {
                            try {
                                var asmPath = Path.Combine(UnityEngine.Application.dataPath, "Managed", mod.Assembly + ".dll");
                                if (File.Exists(asmPath)) {
                                    var asm = Assembly.LoadFrom(asmPath);
                                    loadedAssemblies.Add(asm.GetName().Name);
                                }
                            } catch { }
                        }
                    }
                }
            }
            if (sourceStatics.Types != null) {
                foreach (var entry in sourceStatics.Types) {
                    if (entry.Initialization == "Initialized" && entry.Type != null) {
                        try {
                            var type = ResidentManagedTypeCatalog.ResolveType(entry.Type);
                            if (type != null && !ResidentStaticRoots.IsInitialized(type)) {
                                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                            }
                        } catch { }
                    }
                }
            }
        }

        internal static Snapshot Decode(byte[] bytes)
            => Decode(CanonicalActualDWire.Decode(bytes));
        private static Snapshot Decode(CanonicalActualDWire.Decoded wire)
        {
            if (new UTF8Encoding(false, true).GetString(wire.Metadata) != Schema || wire.Domains[11].Length != 0)
                throw Error("packet schema or reserved segments differ");
            var data = wire.Domains; var directory = ResidentDirectoryWire.Decode(data[0]);
            var assets = ResidentAssetDataWire.Decode(data[5]); ResidentAssetPipeline.Validate(assets);
            var components = ResidentComponentAdapters.Decode(data[4], directory);
            ResidentAssetPipeline.ValidatePresentationDependencies(components.Presentation,assets);
            ResidentAssetPipeline.ValidateAnimatorAssetDependencies(components.Animation,assets);
            return new Snapshot(directory, ResidentStaticRoots.DecodeManifest(data[1]), ResidentManagedTypeCatalog.DecodeManifest(data[2]),
                CanonicalCarryGraphWire.Decode(data[3]), components, assets, FromJson<ResidentObjectNativeState.Data>(data[10]),
                data[6], data[7], data[8], FromJson<string[]>(data[9]));
        }
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 128 };
        private static byte[] Json<T>(T value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None, JsonSettings));
        private static T FromJson<T>(byte[] bytes) where T : class
        {
            try { return JsonConvert.DeserializeObject<T>(new UTF8Encoding(false, true).GetString(bytes), JsonSettings) ?? throw Error("packet domain is absent"); }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException) { throw new InvalidDataException("Resident packet domain is malformed.", error); }
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident boundary: " + reason);
    }
}
