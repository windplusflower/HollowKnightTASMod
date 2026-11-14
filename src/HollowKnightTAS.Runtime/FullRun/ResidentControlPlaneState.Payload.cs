using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.Control;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentControlPlaneState
    {
        internal sealed class PayloadData
        {
            public int ProducerAbi;
            public SortedDictionary<string, string>? FrameValues;
            public ResidentManagedTypeCatalog.TypeReference ClockType = null!, ParticleType = null!;
            public PayloadField[] ClockFields = Array.Empty<PayloadField>(), ParticleFields = Array.Empty<PayloadField>();
            public byte[] Random = Array.Empty<byte>(), Observer = Array.Empty<byte>();
            public uint[] Particles = Array.Empty<uint>();
            public PayloadEvent[] Events = Array.Empty<PayloadEvent>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class PayloadEvent
        {
            public string Field = "", Role = "";
            public EventResetMode ResetMode;
            public bool Present, Observed, Signaled;
        }
        internal sealed class PayloadField
        {
            public string Name = "", Storage = "", Transport = "", Root = "";
            public int Token, Attributes;
            public ResidentManagedTypeCatalog.TypeReference DeclaringType = null!, ValueType = null!;
        }
        // Packet fields are declared D but have one writer: the existing clock
        // backend. Tokens are local execution state, checked against this epoch.
        internal static readonly IReadOnlyDictionary<string, string> ClockFields = Contract(
            "slot:RecordingAbsoluteTimeTarget,recordingRootConfigured,applied,calibrationAttempts,doublePhaseInitialResidualCaptured,doublePhaseCalibrationApplied,doublePhaseInitialResidualBits,doublePhaseFinalResidualBits,doublePhaseDownwardQuantizationCount,doublePhaseLastCorrectionBits,calibratedScene,randomSynchronizationApplied,randomSynchronizationRequested,recordingRandomSynchronizationRequested,recordingRandomSynchronizationApplied,recordingRootRequestObservedFrameCount,recordingRootFramePhase,recordingPhaseNormalizationCompleted,recordingPhaseNormalizationCaptureDeltaTimeBits,recordingPhaseNormalizationHeldTimeBits,recordingPhaseNormalizationHeldTimeDoubleBits,recordingPhaseNormalizationRemainingNormalFrameCount,recordingPhaseNormalizationRestoreFramePhase,recordingPhaseNormalizationBeginCount,recordingPhaseNormalizationHoldFrameCount,recordingPhaseNormalizationReleaseCount,recordingPhaseNormalizationLastFrameCount,recordingPhaseNormalizationLastFramePhase,<RecordingPhaseNormalizationDiagnostic>k__BackingField,timeUpdateResumeBoundaryCallbackCount,timeUpdateResumeBoundaryCommitCount,deterministicClockDuplicateTimeUpdateSkipCount,deterministicClockSceneLoadFrameSkipCount,realtimeEpochNormalizationApplied,realtimeEpochNormalizationCount,realtimeEpochNormalizationGameTimeBits,realtimeEpochNormalizationBeforeBits,realtimeEpochNormalizationTargetBits,realtimeEpochNormalizationAfterBits,realtimeEpochNormalizationCanonicalOffsetSeconds,realtimeEpochNormalizationDeltaTicks",
            "random-packet:fullRunRenderInitialized,fullRunRenderState,randomSynchronizationSnapshotAvailable,randomSynchronizationStateS0,randomSynchronizationStateS1,randomSynchronizationStateS2,randomSynchronizationStateS3,randomSynchronizationResetCount,randomSynchronizationSceneEpoch,randomSynchronizationLastAppliedSeed,randomSynchronizationLastBoundary,randomSynchronizationLastScene",
            "random-observer-packets:deterministicClockAdvanceSequence", "observer-packet:deterministicClockUnityFrameObserved,deterministicClockLastUnityFrameCount",
            "producer-gate:residentRandomProducerGate",
            "dependency:RandomState0Field,RandomState1Field,RandomState2Field,RandomState3Field,fullRunRenderIsolationEnabled,fullRunLegacyCalibrationDisabled,runtimeVirtualClockBoundaryType,playerLoopPostLateUpdateIndex,playerLoopTimeUpdateIndex,playerLoopResumeBoundaryIndex,playerLoopWaitForPresentationIndex,ProfileId,VirtualClockProviderId,RandomSynchronizationPolicyId,RandomSynchronizationSeed,RealtimeEpochNormalizationPolicyId,MaximumDoublePhaseCalibrationAttempts,MaximumRealtimeEpochNormalizationAttempts,RecordingPhaseNormalizationCaptureDeltaTime,SceneFramePhaseModulo,SceneFramePhaseTarget",
            "local:beforeCaptureDeltaTimeBits,beforeTargetFrameRate,beforeVSyncCount,randomSynchronizationRequest,randomSynchronizationAcknowledged,fullRunRandomRequest,fullRunRandomAcknowledged,recordingRandomSynchronizationRequest,recordingRandomSynchronizationAcknowledged,timeUpdateResumeBoundaryInstallCount,checkpointClockObserverImported,checkpointRandomImported",
            "active:registered,originalCaptured,runtimeVirtualClockRegistered,timeUpdateResumeBoundaryInstalled,deterministicClockEnabled",
            "epoch-token:residentRandomToken,residentObserverToken,residentParticleToken",
            "healthy:recordingClockCalibrationRequested,doublePhaseCalibrationFaultCode,fullRunRenderEntered,recordingPhaseNormalizationActive,recordingPhaseNormalizationLatchPending,recordingPhaseNormalizationFaultCode,randomSynchronizationFaultCode,sceneRandomSynchronizationPending,sceneClockExclusionActive,sceneClockExclusionFaultCode,sceneFrameAlignmentFaultCode,timeUpdateResumeRequested,timeUpdateResumeCommitFaultCode,deterministicClockEnableFaultCode,deterministicClockAdvanceFaultCode,deterministicClockUnityFrameFaultCode,realtimeEpochNormalizationFaultCode,playerLoopBoundaryError",
            "idle-zero:randomSynchronizationTransitionStartCount,randomSynchronizationGameplayReadyCount,randomSynchronizationGameplayReadyAlignmentHoldUpdateCount,sceneClockExclusionCaptureDeltaTimeBits,sceneClockExclusionBeginCount,sceneClockExclusionFinishCount,sceneClockExclusionFrozenTimeUpdateCount,sceneClockExclusionPreSynchronizationCount,sceneActivationAlignmentBeginCount,sceneActivationAlignmentHoldFrameCount,sceneActivationAlignmentReleaseCount,sceneFinishAlignmentBeginCount,sceneFinishAlignmentHoldFrameCount,sceneFinishAlignmentReleaseCount",
            "idle-minus-one:randomSynchronizationFirstGameplayReadyFrameCount,randomSynchronizationFirstGameplayReadyFramePhase,sceneActivationAlignmentFirstFrameCount,sceneActivationAlignmentFirstFramePhase,sceneActivationAlignmentLastFrameCount,sceneActivationAlignmentLastFramePhase,sceneFinishAlignmentLastFrameCount,sceneFinishAlignmentLastFramePhase");
        internal static readonly IReadOnlyDictionary<string, string> EarlyFields = ResidentProcessSlots.Ownership("Execution:installed,policy,online,particles");
        internal static readonly IReadOnlyDictionary<string, string> OnlineFields = ResidentProcessSlots.Ownership(
            "Execution:hooks,achievements,adapter,platform,thread,failure", "Dependency:online");
        internal static readonly IReadOnlyDictionary<string, string> AchievementFields = ResidentProcessSlots.Ownership("D:unlocked");
        internal static readonly IReadOnlyDictionary<string, string> OnlineAdapterFields = ResidentProcessSlots.Ownership("Execution:state");
        internal static readonly IReadOnlyDictionary<string, string> WallClockFields = ResidentProcessSlots.Ownership(
            "Execution:hooks,failure,installed,constructorReads,engineThread", "Dependency:target");
        internal static readonly IReadOnlyDictionary<string, string> ParticleFields = Contract(
            "native-manager:managerSlot,manager", "local:checkpointImported", "epoch-token:residentImportedToken");
        internal static readonly IReadOnlyDictionary<string, string> VirtualClockFields = ResidentProcessSlots.Ownership(
            "Execution:Sync,beginPause,endPause", "Dependency:providerId,RequiredProviderId");
        internal static readonly IReadOnlyDictionary<string, string> ProducerFields = ResidentProcessSlots.Ownership(
            "Execution:sync,requireThread,requireBoundary,activeToken,lastToken,depth,failed");
        internal static readonly IReadOnlyDictionary<string, string> EventFields = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["randomSynchronizationRequest"] = "request", ["fullRunRandomRequest"] = "request", ["recordingRandomSynchronizationRequest"] = "request",
            ["randomSynchronizationAcknowledged"] = "acknowledgement", ["fullRunRandomAcknowledged"] = "acknowledgement",
            ["recordingRandomSynchronizationAcknowledged"] = "acknowledgement"
        };


        internal static CaptureResult Capture(HollowKnightTASMod mod, ModStartupCompatibility startup, ulong token,
            Action admitted, Action requirePayloadInitialized)
            => CaptureControl(mod, startup, token, admitted, requirePayloadInitialized, false);

        internal static CaptureResult CaptureFrame(HollowKnightTASMod mod, ModStartupCompatibility startup, ulong token,
            Action admitted, Action requirePayloadInitialized)
            => CaptureControl(mod, startup, token, admitted, requirePayloadInitialized, true);

        private static CaptureResult CaptureControl(HollowKnightTASMod mod, ModStartupCompatibility startup, ulong token,
            Action admitted, Action requirePayloadInitialized, bool frameOnly)
        {
            if (token == 0 || requirePayloadInitialized == null) throw new ArgumentException("An actual admitted payload epoch is required.");
            var builder = CaptureRuntimeOwners(mod, startup, admitted, frameOnly);
            builder.Payload = CapturePayload(builder, mod, token, admitted, requirePayloadInitialized);
            return builder.Finish();
        }
        private static PayloadCapture CapturePayload(Builder builder, HollowKnightTASMod mod, ulong token, Action admitted, Action initialized)
        {
            admitted(); initialized();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload").ToArray();
            if (assemblies.Length != 1) throw Error("actual clock payload assembly is absent or ambiguous");
            var assembly = assemblies[0]; var clock = assembly.GetType("HollowKnightTAS.ClockPayload.ClockController", true)!;
            var early = assembly.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!;
            // A class without any .cctor cannot be lazily initialized by this
            // read. If one is added, the contract must gain a native proof.
            if (early.TypeInitializer != null) throw Error("early payload bootstrap gained an unproven static initializer");
            builder.AddStatic("payload/early", early, EarlyFields, initialized);
            builder.Require(() => { initialized(); if ((int)ReadStatic(early, "installed")! != 1) throw Error("early payload policies are not installed"); });
            var online = RequireActual(ReadStatic(early, "online"), assembly, "HollowKnightTAS.ClockPayload.IsolatedOnlinePolicy");
            var wall = RequireActual(ReadStatic(early, "policy"), assembly, "HollowKnightTAS.ClockPayload.CustomKnightModWallClockPolicy");
            var particle = RequireActual(ReadStatic(early, "particles"), assembly, "HollowKnightTAS.ClockPayload.CanonicalParticleRandomPolicy");
            builder.Add("payload/online", online, OnlineFields);
            builder.ObserveList("payload/online/hooks", (IList)Read(online, "hooks")!);
            var achievements = RequireActual(Read(online, "achievements"), assembly, "HollowKnightTAS.Core.Movie.SessionAchievements");
            var adapter = RequireActual(Read(online, "adapter"), assembly, "HollowKnightTAS.ClockPayload.IsolatedOnlinePolicy+MemoryOnlineSubsystem");
            var platform = Read(online, "platform") as DesktopPlatform ?? throw Error("actual online platform owner is absent");
            var onlineField = Read(online, "online") as FieldInfo ?? throw Error("actual online field metadata is absent");
            builder.Add("payload/achievements", achievements, AchievementFields);
            builder.Add("payload/online-adapter", adapter, OnlineAdapterFields);
            builder.Dependency("payload/online/platform-field", DescribeMetadata(onlineField));
            builder.Require(() => {
                if (Read(online, "failure") != null || (int)Read(online, "thread")! != Thread.CurrentThread.ManagedThreadId
                    || !ReferenceEquals(Read(online, "achievements"), achievements) || !ReferenceEquals(Read(adapter, "state"), achievements)
                    || !ReferenceEquals(Read(online, "adapter"), adapter) || !ReferenceEquals(Read(online, "platform"), platform)
                    || !ReferenceEquals(onlineField.GetValue(platform), adapter)) throw Error("live online callbacks lost their original state/platform aliases");
            });
            builder.Add("payload/wall-clock", wall, WallClockFields);
            builder.ObserveList("payload/wall-clock/hooks", (IList)Read(wall, "hooks")!);
            var wallAssembly = Read(wall, "target") as Assembly;
            builder.Dependency("payload/wall-clock/target", wallAssembly == null ? "absent" : DescribeMetadata(wallAssembly));
            builder.Require(() => {
                if (Read(wall, "failure") != null || (uint)Read(wall, "engineThread")! != ControlThreadId()
                    || wallAssembly != null && (!(bool)Read(wall, "installed")! || (int)Read(wall, "constructorReads")! < 1))
                    throw Error("actual Mod wall-clock hook installation differs or faulted");
            });
            void RequireVirtualInitialized() {
                initialized();
                if (!(bool)ReadStatic(clock, "runtimeVirtualClockRegistered")!
                    || ReadStatic(clock, "runtimeVirtualClockBoundaryType") as Type != typeof(RuntimeVirtualClockBoundary))
                    throw Error("clock did not prove the actual runtime virtual-clock registration");
            }
            builder.AddStatic("payload/virtual-boundary", typeof(RuntimeVirtualClockBoundary), VirtualClockFields, RequireVirtualInitialized);
            builder.Require(() => {
                RequireVirtualInitialized();
                if ((string)ReadStatic(typeof(RuntimeVirtualClockBoundary), "providerId")! != RuntimeVirtualClockBoundary.RequiredProviderId)
                    throw Error("virtual clock provider identity differs");
                RequireVirtualCallback(clock, "BeginMainThreadPause", "beginPause"); RequireVirtualCallback(clock, "EndMainThreadPause", "endPause");
            });
            builder.Dependency("payload/virtual-boundary/begin", DescribeMetadata(clock.GetMethod("BeginMainThreadPause", Fields)!));
            builder.Dependency("payload/virtual-boundary/end", DescribeMetadata(clock.GetMethod("EndMainThreadPause", Fields)!));
            CaptureProducerAuthority(builder, mod, clock, token, admitted, initialized);
            return new PayloadCapture(clock, particle, token, admitted, initialized, builder);
        }
        private static void CaptureProducerAuthority(Builder builder, HollowKnightTASMod mod, Type clock, ulong token, Action admitted, Action initialized)
        {
            initialized();
            var abi = clock.GetMethod("GetResidentRandomProducerAbi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var require = clock.GetMethod("RequireResidentRandomProducerFence", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ulong) }, null);
            if (abi == null || abi.ReturnType != typeof(int) || require == null || require.ReturnType != typeof(void)
                || (int)abi.Invoke(null, null)! != 1) throw Error("actual payload has no supported shared producer authority");
            foreach (var name in new[] { "OpenResidentRandomProducerFence", "RequireResidentRandomProducerFence", "CloseResidentRandomProducerFence", "AbortResidentRandomProducerFence", "RequestResidentRandom" }) {
                var parameters = name == "RequestResidentRandom" ? new[] { typeof(int) } : new[] { typeof(ulong) };
                var method = clock.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
                if (method == null || method.ReturnType != typeof(void)) throw Error("actual producer API signature differs: " + name);
                builder.Dependency("payload/random-producer/" + name, DescribeMetadata(method));
            }
            builder.Dependency("payload/random-producer/abi", "1/" + DescribeMetadata(abi));
            var gate = RequireActual(ReadStatic(clock, "residentRandomProducerGate"), clock.Assembly, "HollowKnightTAS.ClockPayload.ResidentRandomProducerGate");
            builder.Add("payload/random-producer", gate, ProducerFields);
            var thread = clock.GetMethod("RequireResidentRandomProducerThread", BindingFlags.NonPublic | BindingFlags.Static)!;
            var boundary = clock.GetMethod("RequireResidentRandomProducerBoundary", BindingFlags.NonPublic | BindingFlags.Static)!;
            builder.Require(() => {
                initialized(); require.Invoke(null, new object[] { token });
                if ((ulong)Read(gate, "activeToken")! != token || (ulong)Read(gate, "lastToken")! != token
                    || (int)Read(gate, "depth")! != 0 || (bool)Read(gate, "failed")!) throw Error("actual shared producer gate is not quiescent in this native epoch");
                RequireStaticProducerDelegate(Read(gate, "requireThread") as Delegate, thread);
                RequireStaticProducerDelegate(Read(gate, "requireBoundary") as Delegate, boundary);
            });
            builder.Dependency("producer/runtime/full-run", DescribeMetadata(typeof(RuntimeFullRunSession).GetMethod("OnNativeBeforeFrame", Fields)!));
            builder.Dependency("producer/runtime/root", DescribeMetadata(typeof(Companion.RuntimeRecordingRootCoordinator).GetMethod("SignalColdRootEvent", Fields)!));
            builder.Dependency("producer/runtime/client", DescribeMetadata(typeof(ResidentRngProducerClient).GetMethod("Request", Fields)!));
            var runtimeSession = Read(mod, "fullRunSession") as RuntimeFullRunSession ?? throw Error("actual random producer session is absent");
            builder.Require(() => { if (!ReferenceEquals(Read(mod, "fullRunSession"), runtimeSession)) throw Error("actual random producer session changed"); });
            CaptureEventPair(builder, runtimeSession, "randomSeedRequest", "randomSeedAcknowledged",
                clock, "fullRunRandomRequest", "fullRunRandomAcknowledged", admitted);
            var observers = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "HollowKnightTAS.ReferenceObserver").ToArray();
            if (observers.Length > 1) { builder.Gaps.Add("control-plane-reference-observer-producer-ambiguous"); return; }
            if (observers.Length == 0) {
                builder.Dependency("producer/reference-observer", "not-loaded");
                builder.Require(() => {
                    if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "HollowKnightTAS.ReferenceObserver"))
                        throw Error("a reference observer producer appeared inside the admitted epoch");
                });
                return;
            }
            var observer = observers[0]; var owner = observer.GetType("HollowKnightTAS.ReferenceObserver.HollowKnightTASReferenceObserverMod", false);
            var session = observer.GetType("HollowKnightTAS.ReferenceObserver.ReferenceObserverSession", false);
            var observerAbi = owner?.GetMethod("GetResidentRandomProducerAbi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var send = session?.GetMethod("SignalRandomRequest", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            // A legacy or unknown sender is an actual protocol gap. Do not call
            // a possibly dormant initializer simply to query an advertised ABI.
            if (owner == null || owner.TypeInitializer != null || observerAbi == null || observerAbi.ReturnType != typeof(int)
                || send == null || send.ReturnType != typeof(void)) {
                builder.Gaps.Add("control-plane-reference-observer-producer-abi"); return;
            }
            if ((int)observerAbi.Invoke(null, null)! != 1) { builder.Gaps.Add("control-plane-reference-observer-producer-abi"); return; }
            builder.Dependency("producer/reference-observer", DescribeMetadata(observer));
            builder.Dependency("producer/reference-observer/abi", DescribeMetadata(observerAbi));
            builder.Dependency("producer/reference-observer/send", DescribeMetadata(send));
            builder.Require(() => {
                var now = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "HollowKnightTAS.ReferenceObserver").ToArray();
                if (now.Length != 1 || !ReferenceEquals(now[0], observer)) throw Error("actual reference observer producer module changed");
            });
            object? FindObserver() {
                var mods = Modding.ModHooks.GetAllMods(onlyEnabled: false, allowLoadError: true).Where(modOwner => modOwner != null && modOwner.GetType() == owner).ToArray();
                if (mods.Length > 1) throw Error("actual reference observer mod is ambiguous");
                return mods.SingleOrDefault();
            }
            var observerMod = FindObserver();
            var observerSession = observerMod == null ? null : Read(observerMod, "session");
            builder.Require(() => {
                if (!ReferenceEquals(FindObserver(), observerMod) || observerMod != null && !ReferenceEquals(Read(observerMod, "session"), observerSession))
                    throw Error("actual reference observer or its producer session changed");
            });
            if (observerSession == null) return;
            if (observerSession.GetType() != session) throw Error("actual reference observer session type differs");
            CaptureEventPair(builder, observerSession, "randomSyncRequest", "randomSyncApplied",
                clock, "randomSynchronizationRequest", "randomSynchronizationAcknowledged", admitted);
            CaptureEventPair(builder, observerSession, "recordingRandomSyncRequest", "recordingRandomSyncApplied",
                clock, "recordingRandomSynchronizationRequest", "recordingRandomSynchronizationAcknowledged", admitted);
        }
        internal static void CaptureEventPair(Builder builder, object producer, string request, string ack,
            Type clock, string payloadRequest, string payloadAck, Action admitted)
        {
            var requestField = Field(producer.GetType(), request); var ackField = Field(producer.GetType(), ack);
            var payloadRequestField = Field(clock, payloadRequest); var payloadAckField = Field(clock, payloadAck);
            if (requestField.IsStatic || ackField.IsStatic || !payloadRequestField.IsStatic || !payloadAckField.IsStatic
                || new[] { requestField, ackField, payloadRequestField, payloadAckField }.Any(field => field.FieldType != typeof(EventWaitHandle)))
                throw Error("random event field storage or type differs");
            var r = (EventWaitHandle?)requestField.GetValue(producer); var a = (EventWaitHandle?)ackField.GetValue(producer);
            if ((r == null) != (a == null)) throw Error("actual producer holds an incomplete random request/acknowledgement pair");
            void RequireProducer() {
                admitted();
                if (!ReferenceEquals(requestField.GetValue(producer), r) || !ReferenceEquals(ackField.GetValue(producer), a))
                    throw Error("actual producer random event pair was replaced");
            }
            builder.Require(RequireProducer);
            // The runtime creates its V2 handles lazily. An absent producer pair
            // cannot signal; the payload still observes its own event state.
            if (r == null || a == null) return;
            var pr = (EventWaitHandle?)payloadRequestField.GetValue(null) ?? throw Error("actual payload request event is absent");
            var pa = (EventWaitHandle?)payloadAckField.GetValue(null) ?? throw Error("actual payload acknowledgement event is absent");
            var requestAlias = ResidentKernelEventState.RequireSameObject(r, pr, admitted);
            var ackAlias = ResidentKernelEventState.RequireSameObject(a, pa, admitted);
            builder.Require(() => {
                if (!ReferenceEquals(payloadRequestField.GetValue(null), pr) || !ReferenceEquals(payloadAckField.GetValue(null), pa))
                    throw Error("actual payload random event pair was replaced");
                requestAlias.RequireUnchanged(); ackAlias.RequireUnchanged();
            });
        }
        internal static void RequireStaticProducerDelegate(Delegate? callback, MethodInfo? method)
        {
            if (method == null || !method.IsStatic || callback == null || callback.Target != null || callback.GetInvocationList().Length != 1
                || !callback.Method.Equals(method)) throw Error("producer admission callback is not the original payload method");
        }
        private static object RequireActual(object? value, Assembly assembly, string type)
        {
            if (value == null || value.GetType() != assembly.GetType(type, true)) throw Error("actual payload owner is absent or changed type: " + type);
            return value;
        }
        private static void RequireVirtualCallback(Type clock, string methodName, string fieldName)
        {
            var callback = ReadStatic(typeof(RuntimeVirtualClockBoundary), fieldName) as Action;
            var method = clock.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            RequireVirtualDelegate(callback, method);
        }
        internal static void RequireVirtualDelegate(Action? callback, MethodInfo? method)
        {
            if (method == null || !method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0
                || callback == null || callback.Target != null || callback.GetInvocationList().Length != 1
                || !callback.Method.Equals(method)) throw Error("actual virtual clock callback differs");
        }
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static extern uint ControlThreadId();

        internal sealed class PayloadCapture
        {
            private sealed class Live
            {
                internal object? Owner, Original, Slot;
                internal FieldInfo Field = null!, Value = null!;
                internal PayloadField Descriptor = null!;
            }
            internal readonly PayloadData Snapshot;
            internal readonly Dictionary<string, object> ManagedRoots = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly Type clock;
            private readonly bool frameOnly;
            private readonly object particle;
            private readonly ulong token;
            private readonly Action admitted, initialized;
            private readonly Live[] fields;
            private readonly Dictionary<object, string> external = new Dictionary<object, string>(References.Instance);
            private readonly Dictionary<string, object> resolved = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly List<ResidentKernelEventState.Live> events = new List<ResidentKernelEventState.Live>();
            private readonly string frozen;
            private bool applied;
            internal PayloadCapture(Type clock, object particle, ulong token, Action admitted, Action initialized, Builder builder)
            {
                this.clock = clock; this.particle = particle; this.token = token; this.admitted = admitted; this.initialized = initialized;
                frameOnly = builder.ObservedFrame != null;
                var gaps = new SortedSet<string>(StringComparer.Ordinal);
                fields = CaptureFields(clock, null, true, ClockFields, "clock", gaps)
                    .Concat(CaptureFields(particle.GetType(), particle, false, ParticleFields, "particle", gaps)).ToArray();
                Snapshot = new PayloadData { ProducerAbi = 1, ClockType = ResidentManagedTypeCatalog.DescribeType(clock),
                    ParticleType = ResidentManagedTypeCatalog.DescribeType(particle.GetType()),
                    ClockFields = fields.Where(field => field.Owner == null).Select(field => field.Descriptor).ToArray(),
                    ParticleFields = fields.Where(field => field.Owner != null).Select(field => field.Descriptor).ToArray(),
                    Events = DescribePayloadEvents(clock),
                    FrameValues = frameOnly ? new SortedDictionary<string, string>(StringComparer.Ordinal) : null,
                    Random = Export<byte[]>("RandomState"), Observer = Export<byte[]>("ClockObserver"),
                    Particles = Export<uint[]>("ParticleRandomState") };
                foreach (var field in fields) {
                    if (field.Descriptor.Transport == "dependency") builder.Dependency("payload/clock/" + field.Field.Name, DependencyValue(field.Original));
                    if (frameOnly && IsFrameScalar(field.Descriptor.Transport, field.Field.FieldType))
                        Snapshot.FrameValues!.Add((field.Owner == null ? "clock/" : "particle/") + field.Field.Name, DependencyValue(field.Original));
                    if (typeof(EventWaitHandle).IsAssignableFrom(field.Field.FieldType)) {
                        var row = Snapshot.Events.Single(value => value.Field == field.Field.Name);
                        row.Present = field.Original != null;
                        if (row.Role == "unsupported") gaps.Add("control-plane-event-role-unclassified:" + field.Field.Name);
                        else if (field.Original is EventWaitHandle wait) {
                            events.Add(ObservePayloadEvent(row, wait, frameOnly, admitted));
                        }
                    }
                    if (field.Descriptor.Storage == "Execution" && field.Original != null && !field.Field.FieldType.IsValueType
                        && field.Original is not string && !ResidentGraphPolicy.IsMetadata(field.Original))
                        Bind(field.Original, "resident-control/payload/clock/" + field.Field.Name);
                }
                Bind(particle, "resident-control/payload/particle");
                Snapshot.CoverageGaps = gaps.ToArray(); ValidateObservedPayload(Snapshot, frameOnly); frozen = JsonConvert.SerializeObject(Snapshot); RequireUnchanged();
            }
            private IEnumerable<Live> CaptureFields(Type type, object? owner, bool statics,
                IReadOnlyDictionary<string, string> contract, string key, SortedSet<string> gaps)
            {
                initialized();
                var actual = statics ? type.GetFields(Fields | BindingFlags.DeclaredOnly).Where(field => field.IsStatic).OrderBy(field => field.MetadataToken).ToArray()
                    : CanonicalCarryGraphCodec.Fields(type);
                foreach (var missing in contract.Keys.Where(name => !actual.Any(field => field.Name == name))) gaps.Add("control-payload-field-absent:" + key + "/" + missing);
                foreach (var field in actual) {
                    var transport = contract.TryGetValue(field.Name, out var value) ? value : "unsupported";
                    var descriptor = DescribePayloadField(field, key, transport); var original = ResidentValueAccess.ReadOwner(owner,field);
                    var live = new Live { Owner = owner, Field = field, Descriptor = descriptor, Original = original };
                    if (transport == "unsupported") gaps.Add("control-payload-field-unclassified:" + key + "/" + field.Name);
                    if (transport == "slot") {
                        var slotType = typeof(ResidentStaticRoots.Slot<>).MakeGenericType(field.FieldType);
                        live.Slot = FormatterServices.GetUninitializedObject(slotType); live.Value = slotType.GetField("Value")!;
                        ResidentValueAccess.WriteField(live.Slot,live.Value,original); ManagedRoots.Add(descriptor.Root, live.Slot);
                    }
                    yield return live;
                }
                initialized();
            }
            internal IEnumerable<KeyValuePair<object, string>> ExternalBindings => external;
            internal string? BindExternal(object value) => external.TryGetValue(value, out var key) ? key : null;
            internal object? ResolveExternal(string key) => resolved.TryGetValue(key, out var value) ? value : null;
            private void Bind(object value, string key)
            { if (!external.ContainsKey(value)) { external.Add(value, key); resolved.Add(key, value); } }
            private T Export<T>(string packet)
            {
                admitted(); initialized();
                var name = (frameOnly ? "ExportResidentFrame" : "ExportResident") + packet;
                var method = clock.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ulong) }, null);
                if (method == null || method.ReturnType != typeof(T)) throw Error("payload resident export signature differs: " + name);
                var value = (T)(method.Invoke(null, new object[] { token }) ?? throw Error("payload resident export returned null"));
                admitted(); return value;
            }
            internal void RequireUnchanged()
            {
                RequireControlUnchanged();
                foreach (var field in fields)
                    if (!SameValue(field.Original, ResidentValueAccess.ReadOwner(field.Owner,field.Field), field.Field.FieldType)
                        || frameOnly && field.Slot != null && !SameValue(field.Original, ResidentValueAccess.ReadField(field.Slot!,field.Value), field.Field.FieldType))
                        throw Error("payload field changed before application: " + field.Field.Name);
                RequirePackets(Snapshot); RequireControlUnchanged();
            }
            internal void RequireControlUnchanged()
            {
                admitted(); initialized();
                if (frozen != JsonConvert.SerializeObject(Snapshot)) throw Error("payload field/transport manifest was mutated");
                foreach (var field in fields.Where(field => field.Descriptor.Storage != "D" && field.Descriptor.Transport != "epoch-token")) {
                    var value = ResidentValueAccess.ReadOwner(field.Owner,field.Field);
                    if (!SameValue(field.Original, value, field.Field.FieldType)) throw Error("payload execution/dependency field changed: " + field.Field.Name);
                    if (!frameOnly) RequireInvariant(field.Descriptor.Transport, value, field.Field.FieldType, field.Field.Name);
                }
                foreach (var witness in events) witness.RequireUnchanged();
                RequireEpoch(false); admitted();
            }
            internal void RequireComplete()
            {
                if (frameOnly) throw Error("frame payload is read-only");
                RequireControlUnchanged();
                if (Snapshot.CoverageGaps.Length != 0) throw Error(string.Join(";", Snapshot.CoverageGaps));
            }
            internal void Apply(ResidentMutationScope scope)
            {
                if (applied) throw Error("payload semantic fields were already applied"); RequireUnchanged(); RequireComplete(); scope.Require();
                scope.Mutate(() => {
                    foreach (var field in fields.Where(field => field.Slot != null)) {
                        admitted(); scope.Require(); ResidentValueAccess.WriteOwner(field.Owner,field.Field,ResidentValueAccess.ReadField(field.Slot!,field.Value));
                    }
                });
                applied = true; VerifySlots();
            }
            internal void VerifySlots()
            {
                if (!applied) throw Error("payload semantic slots were not applied"); RequireControlUnchanged();
                foreach (var field in fields.Where(field => field.Slot != null))
                    if (!SameValue(ResidentValueAccess.ReadField(field.Slot!,field.Value), ResidentValueAccess.ReadOwner(field.Owner,field.Field), field.Field.FieldType))
                        throw Error("payload semantic slot readback differs: " + field.Field.Name);
            }
            internal void VerifyImported(PayloadData source)
            { VerifySlots(); RequireEpoch(true); RequirePackets(source); VerifySlots(); }
            private void RequirePackets(PayloadData source)
            {
                if (!Export<byte[]>("RandomState").SequenceEqual(source.Random)
                    || !Export<byte[]>("ClockObserver").SequenceEqual(source.Observer)
                    || !Export<uint[]>("ParticleRandomState").SequenceEqual(source.Particles))
                    throw Error($"payload packet-owned fields did not independently read back [random={!Export<byte[]>("RandomState").SequenceEqual(source.Random)} observer={!Export<byte[]>("ClockObserver").SequenceEqual(source.Observer)} particles={!Export<uint[]>("ParticleRandomState").SequenceEqual(source.Particles)}]");
            }
            private void RequireEpoch(bool imported)
            {
                var tokens = fields.Where(field => field.Descriptor.Transport == "epoch-token").ToArray();
                if (tokens.Length != 4 || tokens.Any(field => (ulong)field.Original! >= token)
                    || tokens.Select(field => (ulong)field.Original!).Distinct().Count() != 1) throw Error("payload token baseline is not from an earlier complete epoch");
                var actual = tokens.Select(field => (ulong)ResidentValueAccess.ReadOwner(field.Owner,field.Field)!).ToArray();
                var completed = actual.All(value => value == token);
                var unchanged = tokens.Where((field, index) => (ulong)field.Original! != actual[index]).Any() == false;
                if (!completed && (imported || !unchanged)) throw Error("payload token state is partial, stale or foreign");
                if (completed && !applied) throw Error("payload clock import preceded semantic slot application");
            }
        }
        private static IReadOnlyDictionary<string, string> Contract(params string[] groups)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups) {
                var split = group.IndexOf(':'); if (split <= 0) throw Error("payload field contract is malformed");
                var transport = group.Substring(0, split);
                if (!new[] { "slot", "random-packet", "observer-packet", "random-observer-packets", "dependency", "native-manager", "producer-gate",
                    "local", "active", "epoch-token", "healthy", "idle-zero", "idle-minus-one" }.Contains(transport)) throw Error("payload field transport is unclassified");
                foreach (var name in group.Substring(split + 1).Split(',')) result.Add(name, transport);
            }
            return result;
        }
        private static PayloadField DescribePayloadField(FieldInfo field, string key, string transport)
        {
            var storage = transport == "slot" || transport == "random-packet" || transport == "observer-packet" || transport == "random-observer-packets" ? "D"
                : transport == "dependency" || transport == "native-manager" ? "Dependency" : transport == "unsupported" ? "Unsupported" : "Execution";
            return new PayloadField { Name = field.Name, Storage = storage, Transport = transport, Token = field.MetadataToken,
                Attributes = (int)field.Attributes, DeclaringType = ResidentManagedTypeCatalog.DescribeType(field.DeclaringType!),
                ValueType = ResidentManagedTypeCatalog.DescribeType(field.FieldType),
                Root = transport == "slot" ? "resident-control/payload/D/" + key + "/" + field.MetadataToken.ToString("x8") : "" };
        }
        private static void RequireInvariant(string transport, object? value, Type type, string name)
        {
            if (transport == "active") {
                if (!(type == typeof(bool) && Equals(value, true) || type == typeof(int) && Equals(value, 1))) throw Error("payload required installation is inactive: " + name);
            } else if (transport == "healthy" || transport == "idle-zero") {
                var wanted = type == typeof(string) ? "" : ResidentValueAccess.DefaultControlValue(type);
                if (!SameValue(wanted, value, type)) throw Error("payload idle/healthy invariant differs: " + name);
            } else if (transport == "idle-minus-one" && (type != typeof(int) || !Equals(value, -1)))
                throw Error("payload unused frame sentinel differs: " + name);
        }
        private static string DependencyValue(object? value)
        {
            if (value == null) return "null";
            if (ResidentGraphPolicy.IsMetadata(value)) return DescribeMetadata(value);
            if (value is float single) return "float:" + BitConverter.ToInt32(BitConverter.GetBytes(single), 0).ToString("x8");
            if (value is double number) return "double:" + BitConverter.DoubleToInt64Bits(number).ToString("x16");
            if (value is string text) return "string:" + text;
            if (value is ResidentValueAccess.EnumValue e) return e.Type.FullName + ":" + Convert.ToString(e.Bits,CultureInfo.InvariantCulture);
            if (value.GetType().IsPrimitive || value.GetType().IsEnum) return value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
            throw Error("payload dependency has no explicit value encoding");
        }
        private static bool SameValue(object? left, object? right, Type type)
            => ResidentValueAccess.SameControlValue(left,right,type,true);
        internal static void ValidatePayload(PayloadData data)
            => ValidateObservedPayload(data, false);
        private static bool IsFrameScalar(string transport, Type type)
            => (transport == "active" || transport == "healthy" || transport == "idle-zero" || transport == "idle-minus-one" || transport == "local")
                && (type.IsPrimitive || type.IsEnum || type == typeof(string));

        internal static ResidentKernelEventState.Live ObservePayloadEvent(PayloadEvent row, EventWaitHandle wait, bool frameOnly, Action admitted)
        {
            var observation = ResidentKernelEventState.Capture(wait, row.ResetMode, admitted);
            row.Observed = true; row.Signaled = observation.Snapshot.Signaled;
            if (!frameOnly && row.Role == "request" && row.Signaled) throw Error("payload RNG request is already pending: " + row.Field);
            return observation;
        }
        private static void ValidateObservedPayload(PayloadData data, bool frameOnly)
        {
            if (data == null || (data.FrameValues != null) != frameOnly || data.ProducerAbi < 0 || data.ProducerAbi > 1 || data.ClockType == null || data.ParticleType == null || data.ClockFields == null || data.ParticleFields == null
                || data.ClockFields.Length == 0 || data.ClockFields.Length > 1024 || data.ParticleFields.Length == 0 || data.ParticleFields.Length > 128
                || data.Random == null || data.Observer == null || data.Particles == null || data.Particles.Length != 4 || data.Particles.All(word => word == 0)
                || data.Events == null || data.Events.Length > 32 || data.CoverageGaps == null || data.CoverageGaps.Length > 4096)
                throw Error("payload packet is absent or exceeds its bound");
            var random = CheckpointRandomState.Decode(data.Random); var observer = CheckpointClockObserverState.Decode(data.Observer);
            if (random.AdvanceSequence != observer.AdvanceSequence || !frameOnly && observer.SceneClockExclusionActive)
                throw Error("payload packet-owned clock sequences or admission phase differ");
            foreach (var group in new[] { data.ClockFields, data.ParticleFields }) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in group)
                    if (field == null || string.IsNullOrEmpty(field.Name) || !names.Add(field.Name) || field.DeclaringType == null || field.ValueType == null
                        || field.Root == null || field.Transport == null || field.Storage == null || (field.Transport == "slot") != (field.Root.Length != 0))
                        throw Error("payload field descriptor is absent, duplicated or changed transport");
            }
            var clock = ResidentManagedTypeCatalog.ResolveType(data.ClockType); var particle = ResidentManagedTypeCatalog.ResolveType(data.ParticleType);
            var clockExpected = CanonicalPayloadFields(DescribePayloadFields(clock, true, ClockFields, "clock"));
            var clockActual = CanonicalPayloadFields(data.ClockFields);
            var particleExpected = CanonicalPayloadFields(DescribePayloadFields(particle, false, ParticleFields, "particle"));
            var particleActual = CanonicalPayloadFields(data.ParticleFields);
            if (JsonConvert.SerializeObject(clockActual) != JsonConvert.SerializeObject(clockExpected)
                || JsonConvert.SerializeObject(particleActual) != JsonConvert.SerializeObject(particleExpected))
                throw Error("payload field manifest changed actual layout or transport ownership");



            if (frameOnly) {
                var values = data.FrameValues!;
                var seen = 0;
                void RequireScalars(string domain, PayloadField[] descriptors) {
                    foreach (var descriptor in descriptors) {
                        var type = ResidentManagedTypeCatalog.ResolveType(descriptor.ValueType);
                        if (!IsFrameScalar(descriptor.Transport, type)) continue;
                        if (!values.TryGetValue(domain + "/" + descriptor.Name, out var text) || !IsCanonicalFrameScalar(type, text))
                            throw Error("frame clock control value is missing or malformed: " + descriptor.Name);
                        seen++;
                    }
                }
                RequireScalars("clock", data.ClockFields); RequireScalars("particle", data.ParticleFields);
                if (values.Count != seen || values["clock/sceneClockExclusionActive"] != DependencyValue(observer.SceneClockExclusionActive))
                    throw Error("read-only frame omits an actual clock or RNG control value");
            }
            var expectedEvents = DescribePayloadEvents(clock);
            if (expectedEvents.Length != data.Events.Length) throw Error("payload event manifest omits or invents an actual field");
            for (var index = 0; index < expectedEvents.Length; index++) {
                var expected = expectedEvents[index]; var actual = data.Events[index];
                if (actual == null || actual.Field != expected.Field || actual.Role != expected.Role || actual.ResetMode != expected.ResetMode
                    || actual.Role != "unsupported" && actual.Present != actual.Observed || !actual.Observed && actual.Signaled
                    || !frameOnly && actual.Role == "request" && actual.Signaled || actual.Role == "unsupported" && actual.Observed)
                    throw Error("payload event ownership, native observation or quiescent request differs");
                var required = actual.Role == "unsupported" ? "control-plane-event-role-unclassified:" + actual.Field
                    : actual.Role == "request" && actual.Present && data.ProducerAbi != 1 ? "control-plane-event-producer-admission:" + actual.Field : null;
                if (required != null && !data.CoverageGaps.Contains(required)) throw Error("payload erased a required event protocol coverage gap");
            }
            RequireFieldGaps(data.ClockFields, ClockFields, "clock", data.CoverageGaps);
            RequireFieldGaps(data.ParticleFields, ParticleFields, "particle", data.CoverageGaps);
        }
        private static bool IsCanonicalFrameScalar(Type type, string text)
        {
            if (text == null) return false;
            if (type == typeof(string)) return text == "null" || text.StartsWith("string:", StringComparison.Ordinal);
            if (type == typeof(float) || type == typeof(double)) {
                var prefix = type == typeof(float) ? "float:" : "double:";
                var digits = type == typeof(float) ? 8 : 16;
                return text.StartsWith(prefix, StringComparison.Ordinal) && text.Length == prefix.Length + digits
                    && text.Skip(prefix.Length).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
            }
            var tag = type.FullName + ":";
            if (!text.StartsWith(tag, StringComparison.Ordinal)) return false;
            try {
                var raw = text.Substring(tag.Length);
                var value = type.IsEnum ? ResidentValueAccess.Enum(type,Convert.ChangeType(raw,Enum.GetUnderlyingType(type),CultureInfo.InvariantCulture)) : Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
                return DependencyValue(value) == text;
            } catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException || error is InvalidCastException) {
                return false;
            }
        }
        private static void RequireFieldGaps(PayloadField[] actual, IReadOnlyDictionary<string, string> contract, string key, string[] gaps)
        {
            var required = actual.Where(field => field.Transport == "unsupported").Select(field => "control-payload-field-unclassified:" + key + "/" + field.Name)
                .Concat(contract.Keys.Where(name => !actual.Any(field => field.Name == name)).Select(name => "control-payload-field-absent:" + key + "/" + name));
            if (required.Any(gap => !gaps.Contains(gap))) throw Error("payload erased a required field coverage gap");
        }
        internal static PayloadField[] DescribePayloadFields(Type type, bool statics, IReadOnlyDictionary<string, string> contract, string key)
        {
            var fields = statics ? type.GetFields(Fields | BindingFlags.DeclaredOnly).Where(field => field.IsStatic).OrderBy(field => field.MetadataToken).ToArray()
                : CanonicalCarryGraphCodec.Fields(type);
            return fields.Select(field => DescribePayloadField(field, key, contract.TryGetValue(field.Name, out var transport) ? transport : "unsupported")).ToArray();
        }
        internal static PayloadEvent[] DescribePayloadEvents(Type clock)
            => clock.GetFields(Fields | BindingFlags.DeclaredOnly).Where(field => field.IsStatic && typeof(EventWaitHandle).IsAssignableFrom(field.FieldType))
                .OrderBy(field => field.MetadataToken).Select(field => {
                    var role = EventFields.TryGetValue(field.Name, out var value) ? value : "unsupported";
                    return new PayloadEvent { Field = field.Name, Role = role,
                        ResetMode = role == "request" ? EventResetMode.AutoReset : role == "acknowledgement" ? EventResetMode.ManualReset : (EventResetMode)(-1) };
                }).ToArray();
        private static object[] CanonicalPayloadFields(PayloadField[]? fields)
        {
            if (fields == null) return Array.Empty<object>();
            return fields.Select(f => new {
                f.Name,
                f.Storage,
                f.Transport,
                Root = ResidentManagedTypeCatalog.IsControlPlaneAssembly(f.DeclaringType?.Assembly) ? "" : f.Root,
                Token = ResidentManagedTypeCatalog.IsControlPlaneAssembly(f.DeclaringType?.Assembly) ? 0 : f.Token,
                f.Attributes,
                DeclaringType = ResidentManagedTypeCatalog.NormalizeType(f.DeclaringType),
                ValueType = ResidentManagedTypeCatalog.NormalizeType(f.ValueType)
            }).ToArray();
        }
        private static object? PayloadLayout(PayloadData? data) => data == null ? null : new {
            data.ProducerAbi, ClockType = ResidentProcessSlots.NormalizeType(data.ClockType), ParticleType = ResidentProcessSlots.NormalizeType(data.ParticleType),
            ClockFields = CanonicalPayloadFields(data.ClockFields), ParticleFields = CanonicalPayloadFields(data.ParticleFields), data.CoverageGaps,
            Events = data.Events.Select(value => new { value.Field, value.Role, value.ResetMode, value.Present, value.Observed }).ToArray()
        };



    }
}
