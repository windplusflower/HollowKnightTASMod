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
            internal IReadOnlyDictionary<string, object> ManagedRoots = null!;
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
                    ResolveExternal = frame.ResolveExternal, ManagedRoots = frame.ManagedRoots, Snapshot = frame.Snapshot };
            } else {
                boundaryLoader = parked ?? LoadingSerialSceneProbe.CaptureResidentBoundary(); var before = boundaryLoader;
                loader = View(before);
            }
            clock = new ResidentClockBackend(session.ResidentClock, loader.Require, phase);
            try { RequireAdmission(); } catch (Exception error) { ResidentFailureEvidence.Record(error); clock.Dispose(); throw; }
        }
        private static LoaderView View(LoadingSerialSceneProbe.ResidentLoaderBoundary live)
            => new LoaderView { Require = live.Require, RequireUnchanged = live.RequireUnchanged, BindExternal = live.BindExternal,
                ResolveExternal = live.ResolveExternal, ManagedRoots = live.ManagedRoots, Snapshot = live.Snapshot };
        // Capture actual held producers in a protected first-fault receipt even
        // when a later component fails before the full process capture.
        public void TraceNativeAdmission()
        {
            ResidentFailureEvidence.Mark(clock.DescribeInputFence());
            var receipt = ResidentNativeAudioFenceState.CaptureBackend(clock.Token, clock.CaptureAudioStatus, clock.CaptureAudioWorkers, RequireAdmission);
            var audio = receipt.Snapshot;
            var director = clock.CaptureDirectorFence();
            ResidentFailureEvidence.Mark("audio workers=" + audio.WorkerCount + ", pending=" + audio.PendingStartups +
                ", domains=" + string.Join(",", audio.Domains.Select(domain => domain.Name + "=" + domain.Count)) +
                ", Director receipt v" + director.Version + " gaps=" + director.RequiredGaps);
            var module = LoadingEngineClockProbe.RequirePinnedEngine();
            string Code(IntPtr address) {
                if (address == IntPtr.Zero) return "null";
                var rva = address.ToInt64() - module.BaseAddress.ToInt64();
                return rva > 0 && rva < module.ModuleMemorySize ? rva.ToString("x") : "external";
            }
            var dispatch = receipt.DescribeWorkers(worker => {
                // The native registrar stores the startup callback. Read the
                // actual Thread callback fields now and check the slot again.
                var thread = new IntPtr(unchecked((long)worker.Thread));
                var callbackSlot = IntPtr.Add(thread,320);
                var callback = ResidentNativeMemory.Ptr(callbackSlot);
                var vtable = ResidentNativeMemory.Ptr(thread);
                var target = callback == IntPtr.Zero ? ResidentNativeMemory.Ptr(vtable) : callback;
                var name = worker.Kind + "/" + (callback == IntPtr.Zero ? "virtual@" : "direct@") + Code(target);
                if (unchecked((ulong)callback.ToInt64()) != worker.Callback) name += "/changed-since-start";
                if (ResidentNativeMemory.Ptr(callbackSlot) != callback || ResidentNativeMemory.Ptr(thread) != vtable ||
                    callback == IntPtr.Zero && ResidentNativeMemory.Ptr(vtable) != target)
                    throw Error("FMOD worker dispatch changed during held observation");
                return name;
            });
            ResidentFailureEvidence.Mark("FMOD held callback RVAs=" + dispatch);
        }
        public void BeginIndependentReadback()
        {
            RequireAdmission(); BoundaryLoader.Verify();
            var fresh = LoadingSerialSceneProbe.CaptureResidentBoundary();
            fresh.RequireComplete();
            loader = View(fresh); latest = null;
            RequireAdmission(); // the clock lease still checks the original parked owner and phase
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
        public string? BindNative(object owner) => latest?.Globals.BindExternal(owner) ?? latest?.Resources.BindExternal(owner);
        public string? BindExternal(object owner) => loader.BindExternal(owner) ?? latest?.Session.BindExternal(owner)
            ?? latest?.ControlPlane.BindExternal(owner) ?? latest?.Clock.BindExternal(owner);
        public object ResolveExternal(string key) => loader.ResolveExternal(key) ?? latest?.Resolve(key) ?? throw Error("unknown process external: " + key);

        internal sealed class Data
        {
            public int Version = 5;
            public ResidentProcessSlots.Data Session = null!;
            public ResidentClockInputState.Data Clock = null!;
            public ResidentSchedulerState.Data Scheduler = null!;
            public ResidentManagedNativeWrappers.Data Wrappers = null!;
            public ResidentNativeGlobalObjects.Data GlobalDirectory = null!;
            public ResidentNativeAssetDirectory.Data NativeAssets = null!;
            public ResidentSceneServiceSettings.Data SceneServices = null!;
            public ResidentResourceManagerState.Data ResourceManager = null!;
            public ResidentPhysicsSettingsState.Data PhysicsSettings = null!;
            public ResidentTagManagerState.Data Tags = null!;
            public ResidentBuildSettingsState.Data[] BuildSettings = null!;
            public ResidentNativeAudioFenceState.Data AudioFence = null!;
            public ResidentWasapiEndpoints.Row[] AudioOutputs = null!;
            public ResidentDirectorFence.Data DirectorFence = null!;
            public ResidentNativeInputState.Data NativeInput = null!;
            public ResidentControlPlaneState.Data ControlPlane = null!;
            public LoadingSerialSceneProbe.ResidentLoaderData Loader = null!;
            public string[] LoaderCoverageGaps = Array.Empty<string>();
        }
        public ResidentBoundaryCoordinator.ProcessObservation Capture(ResidentObjectDirectory directory, Func<object, string?> bind,
            IReadOnlyDictionary<string, object> graphRoots, IReadOnlyDictionary<string, NativeObject> nativeAssets)
        {
            RequireAdmission();
            var local = readOnlyFrame ? new ResidentProcessSlots("session", session, RuntimeFullRunSession.ResidentFieldOwnership, RequireAdmission) : session.CaptureResidentSlots(RequireAdmission);
            var input = readOnlyFrame ? ResidentClockInputState.CaptureFrame(clock, session.ResidentInput, session.ResidentMouse, RequireAdmission)
                : ResidentClockInputState.Capture(clock, session.ResidentInput, session.ResidentMouse, RequireAdmission);
            var audioFence = ResidentNativeAudioFenceState.CaptureBackend(clock.Token, clock.CaptureAudioStatus, clock.CaptureAudioWorkers, RequireAdmission);
            var controlPlane = session.CaptureResidentControlPlane(clock.Token, RequireAdmission, clock.RequirePayloadInitialized, readOnlyFrame);
            ResidentFailureEvidence.Mark("global native object census");
            var globals = ResidentNativeGlobalObjects.Capture(directory, nativeAssets, bind, RequireAdmission);
            var known = KnownPointers(globals);
            ResidentFailureEvidence.Mark("native resource images and bodies");
            var resources = ResidentNativeAssetDirectory.Capture(globals, known, RequireAdmission);
            var projected = resources.ProjectGlobalDirectory();
            ResidentFailureEvidence.Mark("global services");
            var services = ResidentSceneServiceSettings.Capture(globals, resources, projected, RequireAdmission);
            var manager = ResidentResourceManagerState.Capture(globals, SourceBinding(globals, resources), RequireAdmission);
            ResidentS0Projection.RequireClockBodyBinding(globals, input.Snapshot, RequireAdmission);
            var physicsSettings = ResidentPhysicsSettingsState.Capture(globals, bind, RequireAdmission);
            var tags = ResidentTagManagerState.Capture(globals, RequireAdmission);
            var build = BuildSettingsOwner(globals.Snapshot) == null ? null : ResidentBuildSettingsState.Capture(globals, RequireAdmission);
            ResidentFailureEvidence.Mark("platform input");
            var nativeInput = ResidentNativeInputState.Capture(globals, RequireAdmission, clock.RequireInputOwner);
            var roots = Merge(graphRoots, loader.ManagedRoots, local.ManagedRoots, controlPlane.ManagedRoots, input.ManagedRoots, globals.ManagedRoots, resources.ManagedRoots);
            string? Control(object value) => directory.Ownership.Bind(value) ?? loader.BindExternal(value) ?? local.BindExternal(value)
                ?? controlPlane.BindExternal(value) ?? input.BindExternal(value);
            ResidentFailureEvidence.Mark("Mono scheduler");
            var scheduler = ResidentSchedulerState.Capture(directory, roots, Control, nativeAssets);
            ResidentSchedulerState.RequireManagerBinding(globals, scheduler);
            foreach (var pair in scheduler.ManagedRoots) Add(roots, pair.Key, pair.Value);
            string? External(object value) => Control(value) ?? scheduler.BindExternal(value);
            var policy = new ResidentGraphPolicy(value => directory.Ownership.Owns(value) ? null : globals.BindExternal(value) ?? resources.BindExternal(value) ?? bind(value));
            ResidentFailureEvidence.Mark("managed native owners");
            var wrappers = ResidentManagedNativeWrappers.Capture(roots, policy, External, bind, RequireAdmission);
            foreach (var pair in wrappers.ManagedRoots) Add(roots, pair.Key, pair.Value);
            var result = new Observation(this, directory, bind, graphRoots, nativeAssets, local, controlPlane, input, scheduler, wrappers, globals, resources, projected, services, manager, physicsSettings, tags, build, audioFence, nativeInput,
                Merge(loader.ManagedRoots, local.ManagedRoots, controlPlane.ManagedRoots, input.ManagedRoots, scheduler.ManagedRoots, wrappers.ManagedRoots, globals.ManagedRoots, resources.ManagedRoots));
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
            internal readonly ResidentNativeGlobalObjects.CaptureResult Globals;
            internal readonly ResidentPhysicsSettingsState.Live PhysicsSettings;
            internal readonly ResidentNativeAssetDirectory.Live Resources;
            internal readonly ResidentSceneServiceSettings.Live Services;
            internal readonly ResidentResourceManagerState.Live ResourceManager;
            internal readonly ResidentTagManagerState.Live Tags;
            internal readonly ResidentBuildSettingsState.Live? Build;
            internal readonly ResidentNativeAudioFenceState.Live AudioFence;
            internal readonly ResidentNativeInputState.Live NativeInput;
            internal readonly Data Snapshot;
            internal override byte[] Data => Encode(Snapshot);
            internal override IReadOnlyDictionary<string, object> ManagedRoots { get; }
            internal override IReadOnlyCollection<ResidentObjectNativeState.GlobalOwner> GlobalOwners => Globals.GlobalOwners.Concat(Resources.NativeOwners).ToArray();
            internal override string[] CoverageGaps => Snapshot.Session.CoverageGaps.Concat(Snapshot.Clock.CoverageGaps)
                .Concat(Snapshot.ControlPlane.CoverageGaps)
                .Concat(Snapshot.Scheduler.CoverageGaps).Concat(Snapshot.Wrappers.CoverageGaps).Concat(Snapshot.LoaderCoverageGaps)
                .Concat(ResidentNativeAssetDirectory.RequiredGaps(Snapshot.NativeAssets)).Concat(Snapshot.ResourceManager.CoverageGaps).Concat(Snapshot.GlobalDirectory.CoverageGaps).Concat(Snapshot.PhysicsSettings.CoverageGaps).Concat(Snapshot.Tags.CoverageGaps).Concat(Snapshot.BuildSettings.SelectMany(x => x.CoverageGaps))
                .Concat(ResidentNativeAudioFenceState.RequiredCoverageGaps(Snapshot.AudioFence)).Concat(Snapshot.AudioFence.CoverageGaps)
                .Concat(ResidentDirectorFence.Gaps(Snapshot.DirectorFence))
                .Concat(ResidentNativeInputState.RequiredGaps(Snapshot.NativeInput)).Concat(Snapshot.NativeInput.CoverageGaps)
                .Concat(ResidentS0Projection.RequiredProcessCoverageGaps(Snapshot.GlobalDirectory))
                .Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            internal Observation(ResidentBoundaryProcessState owner, ResidentObjectDirectory directory, Func<object, string?> bind,
                IReadOnlyDictionary<string, object> baseRoots, IReadOnlyDictionary<string, NativeObject> assets,
                ResidentProcessSlots session, ResidentControlPlaneState.CaptureResult controlPlane, ResidentClockInputState.CaptureResult clock, ResidentSchedulerState.CaptureResult scheduler,
                ResidentManagedNativeWrappers.CaptureResult wrappers, ResidentNativeGlobalObjects.CaptureResult globals, ResidentNativeAssetDirectory.Live resources, ResidentNativeGlobalObjects.Data projected, ResidentSceneServiceSettings.Live services, ResidentResourceManagerState.Live manager,
                ResidentPhysicsSettingsState.Live physicsSettings, ResidentTagManagerState.Live tags, ResidentBuildSettingsState.Live? build, ResidentNativeAudioFenceState.Live audioFence,
                ResidentNativeInputState.Live nativeInput, IReadOnlyDictionary<string, object> roots)
            {
                Owner = owner; Directory = directory; Bind = bind; BaseRoots = baseRoots; Assets = assets;
                Session = session; ControlPlane = controlPlane; Clock = clock; Scheduler = scheduler; Wrappers = wrappers; Globals = globals; Resources = resources; Services = services; ResourceManager = manager; PhysicsSettings = physicsSettings; Tags = tags; Build = build; AudioFence = audioFence; NativeInput = nativeInput; ManagedRoots = roots;
                Snapshot = new Data { Session = session.Snapshot, ControlPlane = controlPlane.Snapshot, Clock = clock.Snapshot, Scheduler = scheduler.Snapshot,
                    Wrappers = wrappers.Snapshot, GlobalDirectory = projected, NativeAssets = resources.Snapshot, SceneServices = services.Snapshot, ResourceManager = manager.Snapshot, PhysicsSettings = physicsSettings.Snapshot, Tags = tags.Snapshot, BuildSettings = build == null ? Array.Empty<ResidentBuildSettingsState.Data>() : new[] { build.Snapshot }, AudioFence = audioFence.Snapshot, AudioOutputs = audioFence.Endpoints.Rows, NativeInput = nativeInput.Snapshot,
                    DirectorFence = owner.clock.CaptureDirectorFence(), Loader = owner.loader.Snapshot, LoaderCoverageGaps = (string[])owner.loader.Snapshot.CoverageGaps.Clone() };
            }
            internal override string? BindExternal(object value) => Owner.loader.BindExternal(value) ?? Session.BindExternal(value)
                ?? ControlPlane.BindExternal(value) ?? Clock.BindExternal(value) ?? Scheduler.BindExternal(value) ?? Wrappers.BindExternal(value);
            internal object? Resolve(string key)
            {
                var value = Owner.loader.ResolveExternal(key) ?? Session.ResolveExternal(key) ?? ControlPlane.ResolveExternal(key) ?? Globals.ResolveExternal(key) ?? Resources.ResolveExternal(key);
                if (value != null) return value;
                if (key.StartsWith("resident-clock-input/", StringComparison.Ordinal)) return Clock.ResolveExternal(key);
                if (key.StartsWith("resident-scheduler/", StringComparison.Ordinal)) return Scheduler.ResolveExternal(key);
                if (key.StartsWith("resident-native-wrapper/", StringComparison.Ordinal)) return Wrappers.ResolveExternal(key);
                return null;
            }
            internal override void RequireUnchanged()
            {
                Owner.RequireAdmission(); Owner.loader.RequireUnchanged(); Session.RequireUnchanged();
                ResidentDirectorFence.Validate(Snapshot.DirectorFence); _ = Owner.clock.CaptureDirectorFence();
                if (Owner.readOnlyFrame) { ControlPlane.RequireFrameUnchanged(); Clock.RequireFrameUnchanged(); }
                else { ControlPlane.RequireUnchanged(); Clock.RequireUnchanged(); }
                Scheduler.RequireUnchanged(); Wrappers.RequireUnchanged(); Globals.RequireUnchanged(); Resources.RequireUnchanged(); Services.RequireUnchanged(); ResourceManager.RequireUnchanged(); PhysicsSettings.RequireUnchanged(); Tags.RequireUnchanged(); Build?.RequireUnchanged(); AudioFence.RequireUnchanged(); AudioFence.Endpoints.RequireUnchanged(); NativeInput.RequireUnchanged();
            }
            internal override void RequireCanonicalizableCoverage()
            {
                Owner.BoundaryLoader.RequireComplete(); Session.RequireComplete(); ControlPlane.RequireComplete(); Clock.RequireComplete();
                ResidentSchedulerState.RequireCanonicalizableCoverage(Scheduler);
                ResidentManagedNativeWrappers.RequireCompleteCoverage(ResidentManagedNativeWrappers.NormalizeStorage(Wrappers.Snapshot));
                ResidentPhysicsSettingsState.RequireCanonicalizable(PhysicsSettings.Snapshot);
                ResidentTagManagerState.RequireCanonicalizable(Tags.Snapshot);
                if (Build != null) ResidentBuildSettingsState.RequireCanonicalizable(Build.Snapshot);
                AudioFence.RequireComplete();
                ResidentDirectorFence.RequireComplete(Snapshot.DirectorFence);
                ResidentNativeInputState.RequireCanonicalizable(NativeInput.Snapshot);
                ResidentNativeAssetDirectory.RequireCanonicalizableBodies(Resources.Snapshot);
                ResidentResourceManagerState.RequireCanonicalizable(ResourceManager.Snapshot);
                ResidentResourceManagerState.RequireReferenceCoverage(ResourceManager.Snapshot, Snapshot.GlobalDirectory);
                ResidentNativeGlobalObjects.RequireCompleteDirectory(Snapshot.GlobalDirectory); Globals.RequireUnchanged();
                ValidateNativeAssetDirectory(Snapshot.GlobalDirectory, Resources.Snapshot);
                ResidentS0Projection.RequireProcessCoverage(Snapshot.GlobalDirectory);
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
                ResidentPhysicsSettingsState.Canon(before.PhysicsSettings, before.Globals, scope, before.Bind, admitted);
                ResidentResourceManagerState.Canon(before.ResourceManager, scope); before.Services.Canon(scope);
                ResidentNativeAssetDirectory.Canon(before.Resources, scope);
                ResidentTagManagerState.Canon(before.Tags, before.Globals, scope, admitted);
                if (before.Build != null) ResidentBuildSettingsState.Canon(before.Build, before.Globals, scope, admitted);
                ResidentNativeInputState.Canon(before.NativeInput, before.Globals, scope, admitted);
                applied = true;
            }
            internal override void Verify()
            {
                if (!applied) throw Error("process Canon did not apply"); admitted(); scope.Require();
                var after = (Observation)owner.Capture(before.Directory, before.Bind, before.BaseRoots, before.Assets);
                after.Session.RequireComplete(); after.ControlPlane.RequireComplete(); after.Clock.RequireComplete(); after.Scheduler.RequireComplete(); after.Wrappers.RequireComplete();
                ResidentPhysicsSettingsState.RequireComplete(after.Snapshot.PhysicsSettings);
                ResidentResourceManagerState.RequireEqual(before.Snapshot.ResourceManager, after.Snapshot.ResourceManager);
                ResidentTagManagerState.RequireComplete(after.Snapshot.Tags);
                _ = ResidentSceneServiceSettings.EncodeS0(after.Snapshot.SceneServices);
                if (!ResidentSceneServiceSettings.Dependencies(before.Snapshot.SceneServices).SequenceEqual(ResidentSceneServiceSettings.Dependencies(after.Snapshot.SceneServices)))
                    throw Error("service owner dependencies changed during Canon");
                foreach (var row in after.Snapshot.BuildSettings) ResidentBuildSettingsState.RequireComplete(row);
                before.AudioFence.RequireUnchanged(); before.AudioFence.Endpoints.RequireUnchanged(); after.AudioFence.RequireComplete();
                ResidentNativeInputState.RequireComplete(after.Snapshot.NativeInput);
                var expectedScheduler = JsonConvert.DeserializeObject<ResidentSchedulerState.Data>(JsonConvert.SerializeObject(before.Snapshot.Scheduler))!;
                expectedScheduler.CoverageGaps = Array.Empty<string>(); expectedScheduler.ManagerPadding = 0;
                if (JsonConvert.SerializeObject(expectedScheduler) != JsonConvert.SerializeObject(after.Snapshot.Scheduler)
                    || JsonConvert.SerializeObject(ResidentManagedNativeWrappers.NormalizeStorage(before.Snapshot.Wrappers)) != JsonConvert.SerializeObject(after.Snapshot.Wrappers)
                    || JsonConvert.SerializeObject(before.Snapshot.Clock) != JsonConvert.SerializeObject(after.Snapshot.Clock)
                    || !ResidentPhysicsSettingsState.EncodeValues(before.Snapshot.PhysicsSettings).SequenceEqual(ResidentPhysicsSettingsState.EncodeValues(after.Snapshot.PhysicsSettings))
                    || !ResidentPhysicsSettingsState.EncodeDependency(before.Snapshot.PhysicsSettings).SequenceEqual(ResidentPhysicsSettingsState.EncodeDependency(after.Snapshot.PhysicsSettings))
                    || !before.Snapshot.BuildSettings.SelectMany(ResidentBuildSettingsState.EncodeValues).SequenceEqual(after.Snapshot.BuildSettings.SelectMany(ResidentBuildSettingsState.EncodeValues))
                    || !ResidentTagManagerState.EncodeValues(before.Snapshot.Tags).SequenceEqual(ResidentTagManagerState.EncodeValues(after.Snapshot.Tags))
                    || !ResidentNativeInputState.EncodeValues(before.Snapshot.NativeInput).SequenceEqual(ResidentNativeInputState.EncodeValues(after.Snapshot.NativeInput)))
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
            private readonly ResidentPhysicsSettingsState.Plan physicsSettings;
            private readonly ResidentTagManagerState.Plan tags;
            private readonly ResidentBuildSettingsState.Plan? build;
            private readonly ResidentNativeInputState.Plan nativeInput;
            private readonly ResidentNativeAssetDirectory.Plan resources;
            private readonly ResidentResourceManagerState.Plan resourceManager;
            private readonly ResidentControlPlaneState.Plan controlPlane;
            private readonly ResidentWasapiEndpoints.Plan outputPlan;
            private bool applied, imported;
            internal override IReadOnlyDictionary<string, object> ManagedRoots { get; }
            internal Restore(ResidentBoundaryProcessState owner, Data source, Observation target,
                ResidentMutationScope scope, Func<string, NativeObject> resolve, Action admitted)
            {
                this.target = target; this.scope = scope; this.admitted = admitted;
                target.Session.RequireLayout(source.Session); owner.BoundaryLoader.RequireLayout(source.Loader);
                ResidentNativeAudioFenceState.RequireCompleteCoverage(source.AudioFence); target.AudioFence.RequireComplete();
                ResidentWasapiEndpoints.RequireWorkerCensus(source.AudioOutputs,source.AudioFence);
                outputPlan = new ResidentWasapiEndpoints.Plan(source.AudioOutputs, target.AudioFence.Endpoints, scope, target.AudioFence.RequireComplete);
                ResidentDirectorFence.RequireComplete(source.DirectorFence); ResidentDirectorFence.RequireComplete(target.Snapshot.DirectorFence);
                if (source.LoaderCoverageGaps.Length != 0) throw Error("source loader coverage is incomplete");
                controlPlane = ResidentControlPlaneState.Prepare(source.ControlPlane, target.ControlPlane, scope, admitted);
                clock = ResidentClockInputState.Prepare(source.Clock, target.Clock, owner.session.ResidentMovie(), scope, admitted); clock.RequireComplete();
                scheduler = ResidentSchedulerState.Prepare(source.Scheduler, target.Scheduler, scope, admitted);
                physicsSettings = ResidentPhysicsSettingsState.Prepare(source.PhysicsSettings, target.Globals, scope, resolve, target.Bind, admitted);
                ResidentSceneServiceSettings.RequireMatching(source.SceneServices, target.Snapshot.SceneServices);
                resourceManager = ResidentResourceManagerState.Prepare(source.ResourceManager, target.Globals, SourceBinding(target.Globals, target.Resources), scope, admitted);
                var known = KnownPointers(target.Globals);
                resources = ResidentNativeAssetDirectory.Prepare(source.NativeAssets, target.Globals, known,
                    known.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal), scope, admitted);
                tags = ResidentTagManagerState.Prepare(source.Tags, target.Globals, scope, admitted);
                if ((source.BuildSettings.Length != 0) != (target.Build != null)) throw Error("build settings registration differs");
                if (source.BuildSettings.Length != 0) build = ResidentBuildSettingsState.Prepare(source.BuildSettings.Single(), target.Globals, scope, admitted);
                nativeInput = ResidentNativeInputState.Prepare(source.NativeInput, target.Globals, scope, admitted, target.NativeInput.ReadEventOwner); nativeInput.RequireComplete();
                wrappers = ResidentManagedNativeWrappers.Prepare(source.Wrappers, target.Wrappers, scope, resolve, target.Bind, admitted);
                try { ManagedRoots = Merge(owner.loader.ManagedRoots, target.Session.ManagedRoots, controlPlane.ManagedRoots, clock.ManagedRoots, scheduler.ManagedRoots, wrappers.ManagedRoots, target.Globals.ManagedRoots, target.Resources.ManagedRoots); }
                catch { wrappers.Dispose(); throw; }
            }
            internal override string? BindExternal(object value) => target.Owner.loader.BindExternal(value) ?? target.Session.BindExternal(value)
                ?? controlPlane.BindExternal(value) ?? clock.BindExternal(value) ?? scheduler.BindExternal(value) ?? wrappers.BindExternal(value);
            internal override object? ResolveExternal(string key)
            {
                var value = target.Owner.loader.ResolveExternal(key) ?? target.Session.ResolveExternal(key) ?? controlPlane.ResolveExternal(key) ?? target.Globals.ResolveExternal(key) ?? target.Resources.ResolveExternal(key);
                if (value != null) return value;
                if (key.StartsWith("resident-clock-input/", StringComparison.Ordinal)) return clock.ResolveExternal(key);
                if (key.StartsWith("resident-scheduler/", StringComparison.Ordinal)) return scheduler.ResolveExternal(key);
                if (key.StartsWith("resident-native-wrapper/", StringComparison.Ordinal)) return wrappers.ResolveExternal(key);
                return null;
            }
            internal override void ApplyAround(Action<Action> nativeOperations)
            {
                if (applied) throw Error("process restore was reused"); admitted(); scope.Require();
                wrappers.Apply();
                physicsSettings.Apply();
                target.Services.RequireUnchanged(); tags.Apply(); build?.Apply(); resourceManager.Apply(); resources.Apply();
                nativeInput.Apply();
                scheduler.ApplyAround(nativeOperations);
                target.Session.Apply(scope); target.Owner.BoundaryLoader.Apply(scope); controlPlane.Apply(); applied = true;
            }
            internal override void ApplyClockInputRandom()
            { if (!applied || imported) throw Error("clock import phase differs"); outputPlan.Apply(); clock.Apply(); imported = true; }
            internal override void Verify(IReadOnlyDictionary<string, object> roots)
            {
                if (!applied || !imported) throw Error("process restore is incomplete"); admitted(); scope.Require();
                target.Session.Verify(); target.Owner.BoundaryLoader.Verify(); controlPlane.Verify(); scheduler.Verify(roots); wrappers.Verify(roots); physicsSettings.Verify(); tags.Verify(); build?.Verify(); nativeInput.Verify(); clock.Verify(); target.AudioFence.RequireUnchanged(); outputPlan.Verify();
                var freshGlobals = ResidentNativeGlobalObjects.Capture(target.Directory, target.Assets, target.Bind, admitted);
                var freshAssets = resources.Verify(freshGlobals); resourceManager.Verify(freshGlobals, SourceBinding(freshGlobals, freshAssets)); target.Services.RequireUnchanged(); admitted();
            }
            public override void Dispose() => wrappers.Dispose();
        }
        // The pinned type may exist without a loaded BuildSettings instance.
        // Packet membership comes from the independently enumerated native registry.
        private static string? BuildSettingsOwner(ResidentNativeGlobalObjects.Data directory)
        {
            var types = directory.Classes.Where(ResidentBuildSettingsState.OwnsManager).Select(t => t.Rva).ToArray();
            var rows = directory.Rows.Where(row => types.Contains(row.TypeRva)).ToArray();
            if (types.Length > 1 || rows.Length > 1 || rows.Any(row => row.Kind != "global" || string.IsNullOrEmpty(row.Binding)))
                throw Error("registered build settings owner is ambiguous or has another binding");
            return rows.SingleOrDefault()?.Binding;
        }
        internal static void ValidateBuildSettings(ResidentNativeGlobalObjects.Data directory, ResidentBuildSettingsState.Data[] rows)
        {
            var key = BuildSettingsOwner(directory);
            if (rows == null || rows.Length != (key == null ? 0 : 1) || rows.Any(row => row == null || row.Manager != key))
                throw Error("build settings manifest does not cover the actual registered manager");
            foreach (var row in rows) ResidentBuildSettingsState.Validate(row);
        }
        private static Func<int,string?> SourceBinding(ResidentNativeGlobalObjects.CaptureResult globals, ResidentNativeAssetDirectory.Live assets)
        {
            var extra = assets.OriginalRows.ToDictionary(p => p.Value.Item1, p => p.Key);
            return id => globals.RegisteredRows.TryGetValue(id, out var row) && row.Binding.Length != 0 ? row.Binding :
                extra.TryGetValue(id, out var binding) ? binding : null;
        }
        private static IReadOnlyDictionary<IntPtr,string> KnownPointers(ResidentNativeGlobalObjects.CaptureResult globals)
            => globals.RegisteredRows.Where(pair => pair.Value.Kind != "unbound").ToDictionary(pair => globals.RegisteredPointers[pair.Key], pair => pair.Value.Binding);
        internal static void ValidateNativeAssetDirectory(ResidentNativeGlobalObjects.Data directory, ResidentNativeAssetDirectory.Data assets)
        {
            ResidentNativeAssetDirectory.Validate(assets);
            var global = directory.Rows.Where(row => row.Kind == "native-asset").ToDictionary(row => row.Binding, StringComparer.Ordinal);
            var classes = directory.Classes.ToDictionary(type => type.Rva);
            foreach (var row in assets.Rows.Where(row => row.Binding.Length != 0)) {
                if (!global.TryGetValue(row.Binding, out var actual) || actual.TypeRva != row.TypeRva || actual.Vtable != row.Vtable ||
                    actual.Persistent != row.Persistent || actual.ManagedType != row.ManagedType || actual.ManagedPresent != row.ManagedPresent ||
                    !classes.TryGetValue(row.TypeRva, out var type) || type.Key != row.TypeKey || type.Size != row.Size || type.GlobalManager)
                    throw Error("native asset body and registered object differ");
                global.Remove(row.Binding);
            }
            if (global.Count != 0) throw Error("registered native asset has no typed body inventory");
        }
        internal static void RequireSourceNativeAssetCoverage(byte[] process, ResidentObjectNativeState.Data objects, ResidentPhysicsState.Data physics)
        {
            var data = Decode(process);
            ResidentNativeGlobalObjects.RequireCompleteDirectory(data.GlobalDirectory);
            ResidentNativeAssetDirectory.RequireBaseCoverage(data.NativeAssets, objects);
            ResidentNativeAssetDirectory.RequireDerivedPhysicsCoverage(data.NativeAssets, physics, physics.Globals!);
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
        private static Data Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024) throw Error("process packet length differs");
            var data = JsonConvert.DeserializeObject<Data>(new UTF8Encoding(false, true).GetString(bytes)) ?? throw Error("process packet is absent");
            if (data.Version != 5 || data.Session == null || data.Loader == null || data.ControlPlane == null || data.Clock == null || data.Scheduler == null || data.Wrappers == null || data.GlobalDirectory == null || data.PhysicsSettings == null || data.Tags == null || data.AudioFence == null || data.DirectorFence == null || data.NativeInput == null || data.LoaderCoverageGaps == null)
                throw Error("process packet schema differs");
            ResidentClockInputState.Validate(data.Clock); ResidentSchedulerState.Validate(data.Scheduler); ResidentManagedNativeWrappers.Validate(data.Wrappers);
            ResidentNativeGlobalObjects.Validate(data.GlobalDirectory);
            ResidentPhysicsSettingsState.Validate(data.PhysicsSettings);
            ResidentTagManagerState.Validate(data.Tags);
            ValidateBuildSettings(data.GlobalDirectory, data.BuildSettings);
            ValidateNativeAssetDirectory(data.GlobalDirectory, data.NativeAssets);
            ResidentSceneServiceSettings.Validate(data.GlobalDirectory, data.SceneServices);
            ResidentResourceManagerState.Validate(data.ResourceManager);
            ResidentResourceManagerState.RequireReferenceCoverage(data.ResourceManager, data.GlobalDirectory);
            ResidentNativeAudioFenceState.Validate(data.AudioFence); ResidentWasapiEndpoints.RequireWorkerCensus(data.AudioOutputs,data.AudioFence);
            ResidentDirectorFence.Validate(data.DirectorFence);
            ResidentNativeInputState.Validate(data.NativeInput);
            ResidentControlPlaneState.Validate(data.ControlPlane);
            return data;
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident boundary process: " + message);
    }
}
