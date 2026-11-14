using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.Movie;
using Newtonsoft.Json;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One admitted process epoch composes the actual loader/session, native
    // scheduler, managed native wrappers and all four clock/random domains.
    // Construction and completion belong inside the native frame callback.
    internal sealed class ResidentBoundaryProcessState : ResidentBoundaryCoordinator.IProcessState, IDisposable
    {
        internal static ResidentManagedTypeCatalog? s_CachedFrameCatalog;
        private readonly RuntimeFullRunSession session;
        private readonly LoadingSerialSceneProbe.ResidentLoaderBoundary? boundaryLoader;
        private LoadingSerialSceneProbe.ResidentLoaderBoundary BoundaryLoader => boundaryLoader ?? throw Error("frame observations do not admit boundary mutation");
        private LoaderView loader;
        private readonly Action requireSession;
        private readonly bool readOnlyFrame;
        internal RuntimeFullRunSession.ResidentIdentity? LocalIdentity, VerifiedSourceIdentity;
        private readonly ResidentClockBackend clock;
        private Observation? latest;
        private bool completed, faulted;

        private sealed class LoaderView
        {
            internal Action Require = null!, RequireUnchanged = null!;
            internal Func<object, string?> BindExternal = null!;
            internal Func<string, object?> ResolveExternal = null!;
            internal Func<IReadOnlyDictionary<string, object>> GetManagedRoots = null!;
            internal IReadOnlyDictionary<string, object> ManagedRoots => GetManagedRoots();
            internal LoadingSerialSceneProbe.ResidentLoaderData Snapshot = null!;
        }
        internal ResidentBoundaryProcessState(RuntimeFullRunSession session, LoadingSerialSceneProbe.ResidentLoaderBoundary? parked = null)
            : this(session, 0, session.RequireResidentSession, parked) { }
        internal static ResidentBoundaryProcessState OpenFrameObservation(RuntimeFullRunSession session, int phase, Action requireFrame)
            => phase == 1 || phase == 3 ? new ResidentBoundaryProcessState(session, phase, requireFrame) : throw new ArgumentOutOfRangeException(nameof(phase));
        private ResidentBoundaryProcessState(RuntimeFullRunSession session, int phase, Action requireSession, LoadingSerialSceneProbe.ResidentLoaderBoundary? parked = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.requireSession = requireSession ?? throw new ArgumentNullException(nameof(requireSession)); requireSession();
            readOnlyFrame = phase != 0;
            if (readOnlyFrame) {
                var frame = new LoadingSerialSceneProbe.FrameObservation(requireSession);
                loader = new LoaderView { Require = frame.Require, RequireUnchanged = frame.RequireUnchanged, BindExternal = frame.BindExternal,
                    ResolveExternal = frame.ResolveExternal, GetManagedRoots = () => frame.ManagedRoots, Snapshot = frame.Snapshot };
            } else {
                boundaryLoader = parked ?? LoadingSerialSceneProbe.CaptureResidentBoundary(); var before = boundaryLoader;
                loader = View(before);
            }
            clock = new ResidentClockBackend(session.ResidentClock, loader.Require, phase, restoring: parked != null);
            try { RequireAdmission(); } catch (Exception error) { ResidentFailureEvidence.Record(error); clock.Dispose(); throw; }
        }
        private static LoaderView View(LoadingSerialSceneProbe.ResidentLoaderBoundary live)
            => new LoaderView { Require = live.Require, RequireUnchanged = live.RequireUnchanged, BindExternal = live.BindExternal,
                ResolveExternal = live.ResolveExternal, GetManagedRoots = () => live.ManagedRoots, Snapshot = live.Snapshot };
        public void TraceNativeAdmission()
        {
            ResidentFailureEvidence.Mark(clock.DescribeInputFence());
        }
        public void BeginIndependentReadback()
        {
            RequireAdmission(); BoundaryLoader.Verify();
            var fresh = LoadingSerialSceneProbe.CaptureResidentBoundary();
            fresh.RequireComplete();
            loader = View(fresh); latest = null;
            RequireAdmission(); // the clock lease still checks the original parked owner and phase
        }
        public void BeginDirectorRestoreMutation()
        {
            RequireAdmission();
            if (readOnlyFrame) throw Error("frame observations do not admit director mutation");
            clock.BeginDirectorRestoreMutation();
        }
        public void EndDirectorRestoreMutation()
        {
            RequireAdmission();
            if (readOnlyFrame) throw Error("frame observations do not admit director mutation");
            clock.EndDirectorRestoreMutation();
        }
        public void RequireAdmission()
        {
            if (completed || faulted) throw Error("process epoch is closed or faulted");
            loader.Require(); requireSession(); clock.RequireBoundary();
        }
        public byte[] CaptureIdentity() {
            RequireAdmission(); if (readOnlyFrame) return Array.Empty<byte>();
            LocalIdentity = session.CaptureResidentIdentity(clock.Identity, BoundaryLoader);
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(LocalIdentity));
        }
        public void RequireSourceIdentity(byte[] identity) { RequireAdmission(); _ = BoundaryLoader; VerifiedSourceIdentity = session.RequireResidentIdentity(identity, clock.Identity); }
        public string? BindExternal(object owner) => loader.BindExternal(owner) ?? latest?.Session.BindExternal(owner)
            ?? latest?.ControlPlane.BindExternal(owner) ?? latest?.Clock.BindExternal(owner);
        public object ResolveExternal(string key) => loader.ResolveExternal(key) ?? latest?.Resolve(key) ?? throw Error("unknown process external: " + key);

        internal sealed class Data
        {
            public int Version = 7;
            public ResidentGuiValues.Row[] Gui = null!;
            public ResidentProcessSlots.Data Session = null!;
            public ResidentClockInputState.Data Clock = null!;
            public ResidentSchedulerState.Data Scheduler = null!;
            public ResidentManagedNativeWrappers.Data Wrappers = null!;
            public ResidentControlPlaneState.Data ControlPlane = null!;
            public LoadingSerialSceneProbe.ResidentLoaderData Loader = null!;
            public string[] LoaderCoverageGaps = Array.Empty<string>();
        }
        public ResidentBoundaryCoordinator.ProcessObservation Capture(ResidentObjectDirectory directory, Func<object, string?> bind,
            IReadOnlyDictionary<string, object> graphRoots, IReadOnlyDictionary<string, NativeObject> nativeAssets, ResidentBoundaryCoordinator.ProcessObservation? previous = null, bool afterCanon = false)
        {
            var prior = previous == null ? null : RequireObservation(previous, directory);
            RequireAdmission();
            if (afterCanon) {
                if (prior == null) throw Error("Canon readback lacks its discovered source closure");
                prior.Session.RequireUnchanged(); prior.ControlPlane.RequireUnchanged(); prior.Clock.RequireUnchanged(); prior.Gui.Verify();
            } else prior?.RequireUnchanged();
            var local = prior?.Session ?? (readOnlyFrame ? new ResidentProcessSlots("session", session, RuntimeFullRunSession.ResidentFieldOwnership, RequireAdmission) : session.CaptureResidentSlots(RequireAdmission));
            var input = prior?.Clock ?? (readOnlyFrame ? ResidentClockInputState.CaptureFrame(clock, session.ResidentInput, session.ResidentMouse, RequireAdmission)
                : ResidentClockInputState.Capture(clock, session.ResidentInput, session.ResidentMouse, RequireAdmission));
            ResidentS0Projection.RequireClockBodyBinding(input.Snapshot, RequireAdmission);
            var controlPlane = prior?.ControlPlane ?? session.CaptureResidentControlPlane(clock.Token, RequireAdmission, clock.RequirePayloadInitialized, readOnlyFrame);
            var roots = Merge(graphRoots, loader.ManagedRoots, local.ManagedRoots, controlPlane.ManagedRoots, input.ManagedRoots);
            string? Control(object value) {
                if (value is NativeObject native && ResidentObjectDirectory.IsLive(native)) {
                    var key = bind(native);
                    if (ResidentAssetBindings.IsSharedBinding(key)) return key;
                }
                return directory.Ownership.Bind(value) ?? loader.BindExternal(value) ?? local.BindExternal(value)
                    ?? controlPlane.BindExternal(value) ?? input.BindExternal(value);
            }
            ResidentFailureEvidence.Mark("Mono scheduler");
            var scheduler = afterCanon || prior == null ? ResidentSchedulerState.Capture(directory, roots, Control, nativeAssets) : prior.Scheduler;
            foreach (var pair in scheduler.ManagedRoots) Add(roots, pair.Key, pair.Value);
            var additions = new Queue<KeyValuePair<string, object>>();
            void AddDiscoveredRoot(string key, object value) {
                if (!roots.ContainsKey(key)) additions.Enqueue(new KeyValuePair<string, object>(key, value));
                Add(roots, key, value);
            }
            foreach (var asset in nativeAssets) scheduler.IncludeAsset(asset.Key, asset.Value, AddDiscoveredRoot);
            var collector = afterCanon || prior == null ? new ResidentManagedNativeWrappers.Collector() : prior.Collector;
            var gui = prior?.Gui ?? new ResidentGuiValues();
            var discovery = prior?.Discovery ?? new ResidentManagedTypeCatalog.DiscoveryCache();
            if (prior != null) {
                if (!afterCanon) foreach (var pair in prior.Wrappers.ManagedRoots) Add(roots, pair.Key, pair.Value);
                foreach (var pair in gui.Roots) Add(roots, pair.Key, pair.Value);
            }
            string? DiscoverExternal(object value) => Control(value) ?? scheduler.DiscoverStoredWrapper(value, AddDiscoveredRoot)
                ?? collector.Discover(value, AddDiscoveredRoot) ?? gui.Discover(value, AddDiscoveredRoot);
            var policy = new ResidentGraphPolicy(value => {
                if (directory.Ownership.Owns(value)) return null;
                var key = bind(value);
                if (key != null && !ResidentAssetBindings.IsSharedBinding(key) && !directory.NativeBindings.ContainsKey(key))
                {
                    scheduler.IncludeAsset(key, (NativeObject)value, AddDiscoveredRoot);
                    AddDiscoveredRoot(key, value);
                }
                return ResidentAssetBindings.IsSharedBinding(key) ? null : key;
            });
            ResidentFailureEvidence.Mark(afterCanon ? "native closure readback using discovered owners" : "managed type and wrapper discovery");
            if (afterCanon) {
                foreach (var wrapper in prior!.Scheduler.wrappers.Keys) scheduler.DiscoverStoredWrapper(wrapper, AddDiscoveredRoot);
                foreach (var wrapper in prior.Wrappers.Wrappers) collector.Discover(wrapper.Owner, AddDiscoveredRoot);
            }
            var catalog = afterCanon ? prior!.Catalog
                : (readOnlyFrame && s_CachedFrameCatalog != null ? s_CachedFrameCatalog
                : ResidentManagedTypeCatalog.Capture(roots, policy, DiscoverExternal, additions, ResidentFailureEvidence.Mark, discovery));
            if (readOnlyFrame && s_CachedFrameCatalog == null) s_CachedFrameCatalog = catalog;
            scheduler.SealStoredWrappers();
            string? External(object value) => Control(value) ?? scheduler.BindExternal(value);
            var wrappers = ResidentManagedNativeWrappers.Capture(roots, policy, External, bind, RequireAdmission, collector);
            var result = new Observation(this, directory, bind, graphRoots, nativeAssets, local, controlPlane, input, scheduler, wrappers, gui, collector, discovery,
                Merge(loader.ManagedRoots, local.ManagedRoots, controlPlane.ManagedRoots, input.ManagedRoots, scheduler.ManagedRoots, wrappers.ManagedRoots, gui.Roots,
                    roots.Where(pair => pair.Key.StartsWith("resident-asset/dynamic/", StringComparison.Ordinal))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
            result.Catalog = catalog;
            latest = result; return result;
        }
        private sealed class Observation : ResidentBoundaryCoordinator.ProcessObservation
        {
            internal readonly ResidentBoundaryProcessState Owner;
            internal readonly ResidentObjectDirectory Directory;
            internal readonly Func<object, string?> Bind;
            internal readonly IReadOnlyDictionary<string, object> BaseRoots;
            internal readonly IReadOnlyDictionary<string, NativeObject> Assets;
            internal readonly ResidentProcessSlots Session;
            internal readonly ResidentControlPlaneState.CaptureResult ControlPlane;
            internal readonly ResidentClockInputState.CaptureResult Clock;
            internal readonly ResidentSchedulerState.CaptureResult Scheduler;
            internal readonly ResidentManagedNativeWrappers.CaptureResult Wrappers;
            internal readonly ResidentGuiValues Gui;
            internal readonly ResidentManagedNativeWrappers.Collector Collector;
            internal readonly ResidentManagedTypeCatalog.DiscoveryCache Discovery;
            internal readonly Data Snapshot;
            internal override byte[] Data => Encode(Snapshot);
            internal override IReadOnlyDictionary<string, object> ManagedRoots { get; }
            internal override IReadOnlyCollection<ResidentObjectNativeState.GlobalOwner> GlobalOwners => Array.Empty<ResidentObjectNativeState.GlobalOwner>();
            internal override string[] CoverageGaps => Snapshot.Session.CoverageGaps.Concat(Snapshot.Clock.CoverageGaps)
                .Concat(Snapshot.ControlPlane.CoverageGaps)
                .Concat(Snapshot.Scheduler.CoverageGaps).Concat(Snapshot.Wrappers.CoverageGaps).Concat(Snapshot.LoaderCoverageGaps)
                .Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            internal Observation(ResidentBoundaryProcessState owner, ResidentObjectDirectory directory, Func<object, string?> bind,
                IReadOnlyDictionary<string, object> baseRoots, IReadOnlyDictionary<string, NativeObject> assets,
                ResidentProcessSlots session, ResidentControlPlaneState.CaptureResult controlPlane, ResidentClockInputState.CaptureResult clock, ResidentSchedulerState.CaptureResult scheduler,
                ResidentManagedNativeWrappers.CaptureResult wrappers, ResidentGuiValues gui, ResidentManagedNativeWrappers.Collector collector, ResidentManagedTypeCatalog.DiscoveryCache discovery,
                IReadOnlyDictionary<string, object> roots)
            {
                Owner = owner; Directory = directory; Bind = bind; BaseRoots = baseRoots; Assets = assets;
                Session = session; ControlPlane = controlPlane; Clock = clock; Scheduler = scheduler; Wrappers = wrappers; Gui = gui; Collector = collector; Discovery = discovery; ManagedRoots = roots;
                Snapshot = new Data { Session = session.Snapshot, ControlPlane = controlPlane.Snapshot, Clock = clock.Snapshot, Scheduler = scheduler.Snapshot,
                    Wrappers = wrappers.Snapshot, Gui = gui.Rows,
                    Loader = owner.loader.Snapshot, LoaderCoverageGaps = (string[])owner.loader.Snapshot.CoverageGaps.Clone() };
            }
            internal override string? BindExternal(object value) => Owner.loader.BindExternal(value) ?? Session.BindExternal(value)
                ?? ControlPlane.BindExternal(value) ?? Clock.BindExternal(value) ?? Scheduler.BindExternal(value) ?? Wrappers.BindExternal(value) ?? Gui.Bind(value);
            internal object? Resolve(string key)
            {
                var value = Owner.loader.ResolveExternal(key) ?? Session.ResolveExternal(key) ?? ControlPlane.ResolveExternal(key);
                if (value != null) return value;
                if (key.StartsWith("resident-clock-input/", StringComparison.Ordinal)) return Clock.ResolveExternal(key);
                if (key.StartsWith("resident-scheduler/", StringComparison.Ordinal)) return Scheduler.ResolveExternal(key);
                if (key.StartsWith("resident-native-wrapper/", StringComparison.Ordinal)) return Wrappers.ResolveExternal(key);
                return Gui.Resolve(key);
            }
            internal override void RequireUnchanged()
            {
                Owner.RequireAdmission(); Owner.loader.RequireUnchanged(); Session.RequireUnchanged();
                if (Owner.readOnlyFrame) { ControlPlane.RequireFrameUnchanged(); Clock.RequireFrameUnchanged(); }
                else { ControlPlane.RequireUnchanged(); Clock.RequireUnchanged(); }
                Scheduler.RequireUnchanged(); Wrappers.RequireUnchanged(); Gui.Verify();
            }
            internal override void RequireCanonicalizableCoverage()
            {
                Owner.BoundaryLoader.RequireComplete(); Session.RequireComplete(); ControlPlane.RequireComplete(); Clock.RequireComplete();
                ResidentSchedulerState.RequireCanonicalizableCoverage(Scheduler);
                ResidentManagedNativeWrappers.RequireCompleteCoverage(ResidentManagedNativeWrappers.NormalizeStorage(Wrappers.Snapshot));
            }
        }
        public ResidentBoundaryCoordinator.ProcessCanon PrepareCanon(ResidentBoundaryCoordinator.ProcessObservation before,
            ResidentObjectDirectory directory, ResidentMutationScope scope, Action admitted)
            => new Canon(this, RequireObservation(before, directory), scope, admitted);
        private sealed class Canon : ResidentBoundaryCoordinator.ProcessCanon
        {
            private readonly ResidentBoundaryProcessState owner;
            private readonly Observation before;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private bool applied;
            internal Canon(ResidentBoundaryProcessState owner, Observation before, ResidentMutationScope scope, Action admitted)
            { this.owner = owner; this.before = before; this.scope = scope; this.admitted = admitted; before.RequireCanonicalizableCoverage(); }
            private NativeObject Resolve(string key) => before.Directory.NativeBindings.TryGetValue(key, out var value) ? value
                : before.Assets.TryGetValue(key, out var asset) ? asset : throw Error("Canon asset is unbound");
            internal override void Apply()
            {
                if (applied) throw Error("process Canon was reused"); admitted(); scope.Require();
                ResidentManagedNativeWrappers.Canonicalize(before.Wrappers, scope, Resolve, before.Bind, admitted);
                ResidentSchedulerState.CanonicalizeIdleStorage(before.Scheduler, admitted);
                applied = true;
            }
            internal override void Verify(ResidentBoundaryCoordinator.ProcessObservation observed)
            {
                if (!applied) throw Error("process Canon did not apply"); admitted(); scope.Require();
                if (observed is not Observation after || ReferenceEquals(after, before) || !ReferenceEquals(after.Owner, owner))
                    throw Error("common process Canon readback belongs to another process");
                after.Session.RequireComplete(); after.ControlPlane.RequireComplete(); after.Clock.RequireComplete(); after.Scheduler.RequireComplete(); after.Wrappers.RequireComplete();
                var expectedScheduler = JsonConvert.DeserializeObject<ResidentSchedulerState.Data>(JsonConvert.SerializeObject(before.Snapshot.Scheduler))!;
                expectedScheduler.CoverageGaps = Array.Empty<string>(); expectedScheduler.ManagerPadding = 0;
                if (JsonConvert.SerializeObject(expectedScheduler) != JsonConvert.SerializeObject(after.Snapshot.Scheduler)
                    || JsonConvert.SerializeObject(ResidentManagedNativeWrappers.NormalizeStorage(before.Snapshot.Wrappers)) != JsonConvert.SerializeObject(after.Snapshot.Wrappers)
                    || JsonConvert.SerializeObject(before.Snapshot.Clock) != JsonConvert.SerializeObject(after.Snapshot.Clock))
                    throw Error("common process Canon changed a declared D value or failed to rebuild native storage");
                before.Session.RequireUnchanged(); before.ControlPlane.RequireUnchanged(); after.RequireUnchanged(); admitted();
            }
        }
        public ResidentBoundaryCoordinator.ProcessRestore PrepareRestore(byte[] source, ResidentBoundaryCoordinator.ProcessObservation target,
            ResidentObjectDirectory directory, ResidentMutationScope scope, Func<string, NativeObject> resolve, Action admitted)
            => new Restore(this, Decode(source), RequireObservation(target, directory), scope, resolve, admitted);
        private sealed class Restore : ResidentBoundaryCoordinator.ProcessRestore
        {
            private readonly Observation target;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly ResidentClockInputState.Plan clock;
            private readonly ResidentSchedulerState.Plan scheduler;
            private readonly ResidentManagedNativeWrappers.Plan wrappers;
            private readonly ResidentGuiValues gui;
            private readonly ResidentControlPlaneState.Plan controlPlane;
            private bool applied, imported;
            internal override IReadOnlyDictionary<string, object> ManagedRoots { get; }
            internal Restore(ResidentBoundaryProcessState owner, Data source, Observation target,
                ResidentMutationScope scope, Func<string, NativeObject> resolve, Action admitted)
            {
                this.target = target; this.scope = scope; this.admitted = admitted;
                target.Session.RequireLayout(source.Session); owner.BoundaryLoader.RequireLayout(source.Loader);
                if (source.LoaderCoverageGaps.Length != 0) throw Error("source loader coverage is incomplete");
                controlPlane = ResidentControlPlaneState.Prepare(source.ControlPlane, target.ControlPlane, scope, admitted);
                clock = ResidentClockInputState.Prepare(source.Clock, target.Clock, owner.session.ResidentMovie(), scope, admitted); clock.RequireComplete();
                scheduler = ResidentSchedulerState.Prepare(source.Scheduler, target.Scheduler, scope, admitted);
                gui = ResidentGuiValues.Prepare(source.Gui);
                wrappers = ResidentManagedNativeWrappers.Prepare(source.Wrappers, target.Wrappers, scope, resolve, target.Bind, admitted);
                try { ManagedRoots = Merge(owner.loader.ManagedRoots, target.Session.ManagedRoots, controlPlane.ManagedRoots, clock.ManagedRoots, scheduler.ManagedRoots, wrappers.ManagedRoots, gui.Roots); }
                catch { wrappers.Dispose(); throw; }
            }
            internal override string? BindExternal(object value) => target.Owner.loader.BindExternal(value) ?? target.Session.BindExternal(value)
                ?? controlPlane.BindExternal(value) ?? clock.BindExternal(value) ?? scheduler.BindExternal(value) ?? wrappers.BindExternal(value) ?? gui.Bind(value);
            internal override object? ResolveExternal(string key)
            {
                var value = target.Owner.loader.ResolveExternal(key) ?? target.Session.ResolveExternal(key) ?? controlPlane.ResolveExternal(key);
                if (value != null) return value;
                if (key.StartsWith("resident-clock-input/", StringComparison.Ordinal)) return clock.ResolveExternal(key);
                if (key.StartsWith("resident-scheduler/", StringComparison.Ordinal)) return scheduler.ResolveExternal(key);
                if (key.StartsWith("resident-native-wrapper/", StringComparison.Ordinal)) return wrappers.ResolveExternal(key);
                return gui.Resolve(key);
            }
            internal override void ApplyAround(Action<Action> nativeOperations)
            {
                if (applied) throw Error("process restore was reused"); admitted(); scope.Require();
                scope.Mutate(gui.Apply); wrappers.Apply();
                scheduler.ApplyAround(nativeOperations);
                target.Session.Apply(scope); target.Owner.BoundaryLoader.Apply(scope); controlPlane.Apply(); applied = true;
            }
            internal override void ApplyClockInputRandom()
            { if (!applied || imported) throw Error("clock import phase differs"); clock.Apply(); imported = true; }
            internal override void Verify(IReadOnlyDictionary<string, object> roots)
            {
                if (!applied || !imported) throw Error("process restore is incomplete"); admitted(); scope.Require();
                target.Session.Verify(); target.Owner.BoundaryLoader.Verify(); controlPlane.Verify(); scheduler.Verify(roots); wrappers.Verify(roots); gui.Verify(); clock.Verify();
            }
            public override void Dispose() => wrappers.Dispose();
        }
        private Observation RequireObservation(ResidentBoundaryCoordinator.ProcessObservation value, ResidentObjectDirectory directory)
        {
            RequireAdmission();
            if (value is not Observation result || !ReferenceEquals(result.Owner, this) || !ReferenceEquals(result.Directory, directory))
                throw Error("process observation belongs to another boundary");
            return result;
        }
        internal Data RequireFrameObservation(ResidentBoundaryCoordinator.ProcessObservation value, ResidentObjectDirectory directory)
        {
            if (!readOnlyFrame || !clock.IsFrameObservation) throw Error("frame evidence requires its own native read-only lease");
            var captured = RequireObservation(value, directory);
            if (!captured.Snapshot.Loader.FrameObservation || !captured.Snapshot.Clock.FrameOnly) throw Error("frame observation mixed boundary and frame packets");
            return captured.Snapshot;
        }
        public byte[] ReadCompleteS0(ResidentObjectDirectory directory, ResidentStaticRoots.Data statics, ResidentComponentAdapters.Data components,
            ResidentAssetPipeline.Data assets, ResidentObjectNativeState.Data nativeObjects, ResidentBoundaryCoordinator.ProcessObservation runtime)
        {
            var observation = RequireObservation(runtime, directory); observation.RequireUnchanged();
            return ResidentS0Projection.Read(directory, statics, components, assets, nativeObjects, observation.Snapshot);
        }
        internal void Complete() { RequireAdmission(); clock.Complete(); completed = true; }
        public void Fault(Exception error)
        { if (faulted || completed) return; faulted = true; ResidentFailureEvidence.Record(error); clock.Fault(); session.FaultResidentSession(error); }
        public void Dispose() { if (!completed && !faulted) Fault(Error("process epoch left without completion")); clock.Dispose(); }
        private static Dictionary<string, object> Merge(params IReadOnlyDictionary<string, object>[] groups)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var group in groups) foreach (var pair in group) Add(result, pair.Key, pair.Value);
            return result;
        }
        private static void Add(Dictionary<string, object> result, string key, object value)
        {
            if (result.TryGetValue(key, out var previous)) { if (!ReferenceEquals(previous, value)) throw Error("process graph root has two owners: " + key); }
            else result.Add(key, value);
        }
        private static byte[] Encode(Data data) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data));
        internal static Data Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024) throw Error("process packet length differs");
            var data = JsonConvert.DeserializeObject<Data>(new UTF8Encoding(false, true).GetString(bytes)) ?? throw Error("process packet is absent");
            if (data.Version != 7 || data.Gui == null || data.Session == null || data.Loader == null || data.ControlPlane == null || data.Clock == null || data.Scheduler == null || data.Wrappers == null || data.LoaderCoverageGaps == null)
                throw Error("process packet schema differs");
            ResidentClockInputState.Validate(data.Clock); ResidentSchedulerState.Validate(data.Scheduler); ResidentManagedNativeWrappers.Validate(data.Wrappers);
            ResidentControlPlaneState.Validate(data.ControlPlane);
            ResidentGuiValues.Validate(data.Gui);
            return data;
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident boundary process: " + message);
    }
}
