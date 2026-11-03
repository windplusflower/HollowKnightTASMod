using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentControlPlaneStateTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Fixture
    {
        public object Left = new object(), Right = new object(), Control = new object();
        public double Number = -0.0;
    }
    private sealed class Gameplay
    {
        public object? Owner;
        public int Number;
    }
    private sealed class ProducerEvents { public EventWaitHandle? Request, Ack; }
    private static class ConsumerEvents { public static EventWaitHandle? Request, Ack; }
    private static int initialized;
    private static class Dormant
    {
        public static object Value;
        static Dormant() { initialized++; Value = new object(); }
    }

    [TestMethod]
    public void RuntimeContractsCoverEveryActualFieldIncludingModBaseAndStaticOwners()
    {
        var runtime = Runtime(); var api = Api();
        void Check(string typeName, string mapName, bool statics = false) {
            var type = runtime.GetType(typeName, true)!;
            var expected = new List<string>();
            for (var current = type; current != null && current != typeof(object); current = statics ? null : current.BaseType)
                expected.AddRange(current.GetFields(Flags | BindingFlags.DeclaredOnly).Where(field => field.IsStatic == statics).Select(field => field.Name));
            var map = (IReadOnlyDictionary<string, string>)api.GetField(mapName, Flags)!.GetValue(null)!;
            CollectionAssert.AreEqual(expected.OrderBy(value => value).ToArray(), map.Keys.OrderBy(value => value).ToArray(), mapName);
        }
        Check("HollowKnightTAS.Runtime.HollowKnightTASMod", "ModFields");
        Check("HollowKnightTAS.Runtime.FullRun.ModStartupCompatibility", "StartupFields");
        Check("HollowKnightTAS.Runtime.FullRun.DeterministicCinematics", "CinematicFields");
        Check("HollowKnightTAS.Runtime.FullRun.DeterministicCinematics", "CinematicStatics", true);
        Check("HollowKnightTAS.Runtime.FullRun.LoadingAudioLifetimePolicy", "AudioFields");
        Check("HollowKnightTAS.Runtime.FullRun.LoadingAudioLifetimePolicy", "AudioStatics", true);
        Check("HollowKnightTAS.Runtime.FullRun.DeterministicSceneFetch", "FetchFields");
        Check("HollowKnightTAS.Runtime.FullRun.LoadingSynchronousFetchProbe", "FetchFields");
        Check("HollowKnightTAS.Runtime.FullRun.LoadingSynchronousFetchProbe", "ExperimentalFetchStatics", true);
        Check("HollowKnightTAS.Runtime.Input.FullRunCutsceneInput", "CutsceneFields");
        foreach (var (map, field) in new[] { ("StartupFields", "ordinals"), ("CinematicStatics", "players"),
            ("CinematicStatics", "<FrameDuration>k__BackingField"), ("AudioFields", "states"), ("FetchFields", "diagnosticRows") })
            Assert.AreEqual("D", ((IReadOnlyDictionary<string, string>)api.GetField(map, Flags)!.GetValue(null)!)[field]);
    }

    [TestMethod]
    public void SharedGraphCarriesAliasesAndReturnsToTheActualHookOwner()
    {
        var source = new Fixture(); var shared = new Gameplay { Owner = source, Number = 29 };
        source.Left = source.Right = shared; source.Number = 17.25;
        var sourceCapture = Capture(source); var target = new Fixture(); var targetControl = target.Control; var targetCapture = Capture(target);
        var scope = Scope();
        try {
            var restore = Static("Prepare", Get(sourceCapture, "Snapshot"), targetCapture, scope, (Action)(() => { }));
            var sourceRoots = (IReadOnlyDictionary<string, object>)Get(sourceCapture, "ManagedRoots");
            var targetRoots = (IReadOnlyDictionary<string, object>)Get(targetCapture, "ManagedRoots");
            string? Bind(object value) => (string?)Call(sourceCapture, "BindExternal", value);
            var policy = new ResidentGraphPolicy(_ => null);
            var catalog = ResidentManagedTypeCatalog.Capture(sourceRoots, policy, Bind);
            var codec = new CanonicalCarryGraphCodec(catalog.Types, catalog.SchemaIdentity, residentPolicy: policy);
            var data = codec.Capture(sourceRoots, Bind); Assert.AreEqual(0, data.Unsupported.Length);
            var graph = codec.PrepareOwnedGraph(data, targetRoots,
                key => Call(targetCapture, "ResolveExternal", key) ?? throw new InvalidDataException(key));
            graph.Apply(); Call(restore, "Apply"); Call(restore, "Verify"); graph.VerifyReadback();
            Assert.AreNotSame(shared, target.Left); Assert.AreSame(target.Left, target.Right);
            Assert.AreSame(target, ((Gameplay)target.Left).Owner); Assert.AreEqual(29, ((Gameplay)target.Left).Number);
            Assert.AreSame(targetControl, target.Control); Assert.AreEqual(17.25, target.Number);
            Reject(() => Call(restore, "Apply"));
        } finally { Call(scope, "Complete", (Action)(() => { })); ((IDisposable)scope).Dispose(); }
    }

    [TestMethod]
    public void MissingOwnershipAndErasedGapCannotBecomeComplete()
    {
        var ownership = Map(); ownership.Remove("Number"); var capture = Capture(new Fixture(), ownership);
        Reject(() => Call(capture, "RequireComplete"));
        Set(Get(capture, "Snapshot"), "CoverageGaps", Array.Empty<string>());
        Reject(() => Call(capture, "RequireComplete"));
    }

    [TestMethod]
    public void SourceCannotDowngradeSemanticStateOrChangeDependencies()
    {
        var capture = Capture(new Fixture()); var data = Get(capture, "Snapshot");
        object Copy() => JsonConvert.DeserializeObject(JsonConvert.SerializeObject(data), data.GetType())!;
        var changed = Copy(); var owner = ((Array)Get(changed, "Owners")).GetValue(0)!;
        var field = ((Array)Get(owner, "Fields")).Cast<object>().Single(value => (string)Get(value, "Name") == "Left");
        Set(field, "Storage", "Execution"); Set(field, "Root", ""); Reject(() => Call(capture, "RequireLayout", changed));
        var dependency = Copy(); Set(((Array)Get(dependency, "Dependencies")).GetValue(0)!, "Value", "different");
        Reject(() => Call(capture, "RequireLayout", dependency));
    }

    [TestMethod]
    public void ExecutionReplacementAndBitwiseNegativeZeroChangeAbortBeforeApply()
    {
        var fixture = new Fixture(); var capture = Capture(fixture); fixture.Control = new object();
        Reject(() => Call(capture, "RequireControlUnchanged"));
        fixture = new Fixture(); capture = Capture(fixture); fixture.Number = 0.0;
        Reject(() => Call(capture, "RequireUnchanged"));
    }

    [TestMethod]
    public void StaticProofRunsBeforeReflectionCanInitializeADormantOwner()
    {
        var builder = Builder();
        var method = builder.GetType().GetMethods(Flags).Single(value => value.Name == "AddStatic" && value.GetParameters().Length == 4);
        Assert.AreEqual(0, initialized);
        Reject(() => method.Invoke(builder, new object[] { "dormant", typeof(Dormant),
            new Dictionary<string, string> { ["Value"] = "D" }, (Action)(() => throw new InvalidDataException("not initialized")) }));
        Assert.AreEqual(0, initialized);
    }

    [TestMethod]
    public void PayloadContractsAccountForActualFieldsAndGivePacketStateOneWriter()
    {
        var payload = Payload(); var api = Api();
        void Check(Type type, string mapName, bool statics) {
            var map = (IReadOnlyDictionary<string, string>)api.GetField(mapName, Flags)!.GetValue(null)!;
            var actual = new List<string>();
            for (var current = type; current != null && current != typeof(object); current = statics ? null : current.BaseType)
                actual.AddRange(current.GetFields(Flags | BindingFlags.DeclaredOnly).Where(field => field.IsStatic == statics).Select(field => field.Name));
            CollectionAssert.AreEqual(actual.OrderBy(value => value).ToArray(), map.Keys.OrderBy(value => value).ToArray(), mapName);
        }
        Check(payload.GetType("HollowKnightTAS.ClockPayload.ClockController", true)!, "ClockFields", true);
        Check(payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!, "EarlyFields", true);
        Check(payload.GetType("HollowKnightTAS.ClockPayload.IsolatedOnlinePolicy", true)!, "OnlineFields", false);
        Check(payload.GetType("HollowKnightTAS.ClockPayload.IsolatedOnlinePolicy+MemoryOnlineSubsystem", true)!, "OnlineAdapterFields", false);
        Check(payload.GetType("HollowKnightTAS.Core.Movie.SessionAchievements", true)!, "AchievementFields", false);
        Check(payload.GetType("HollowKnightTAS.ClockPayload.CustomKnightModWallClockPolicy", true)!, "WallClockFields", false);
        Check(payload.GetType("HollowKnightTAS.ClockPayload.CanonicalParticleRandomPolicy", true)!, "ParticleFields", false);
        Check(payload.GetType("HollowKnightTAS.ClockPayload.ResidentRandomProducerGate", true)!, "ProducerFields", false);
        Check(Runtime().GetType("HollowKnightTAS.Runtime.Control.RuntimeVirtualClockBoundary", true)!, "VirtualClockFields", true);
        var data = PayloadData(); Static("ValidatePayload", data);
        var fields = ((Array)Get(data, "ClockFields")).Cast<object>().ToDictionary(field => (string)Get(field, "Name"));
        foreach (var name in new[] { "randomSynchronizationApplied", "randomSynchronizationRequested", "recordingRandomSynchronizationRequested",
            "recordingRandomSynchronizationApplied", "recordingPhaseNormalizationCompleted", "realtimeEpochNormalizationApplied", "RecordingAbsoluteTimeTarget", "recordingRootConfigured" }) {
            Assert.AreEqual("D", Get(fields[name], "Storage")); Assert.AreEqual("slot", Get(fields[name], "Transport"));
            Assert.IsTrue(((string)Get(fields[name], "Root")).Length != 0);
        }
        foreach (var name in new[] { "fullRunRenderState", "randomSynchronizationSceneEpoch", "deterministicClockAdvanceSequence", "deterministicClockLastUnityFrameCount" }) {
            Assert.AreEqual("D", Get(fields[name], "Storage")); Assert.AreNotEqual("slot", Get(fields[name], "Transport"));
            Assert.AreEqual("", Get(fields[name], "Root"));
        }
        Assert.AreEqual("Execution", Get(fields["residentRandomToken"], "Storage"));
        Assert.AreEqual("epoch-token", Get(fields["residentRandomToken"], "Transport"));
    }

    [TestMethod]
    public void PayloadManifestRejectsOmissionTransportDowngradeAndDifferentClockSequences()
    {
        var data = PayloadData();
        var fields = (Array)Get(data, "ClockFields"); var shorter = Array.CreateInstance(fields.GetType().GetElementType()!, fields.Length - 1);
        Array.Copy(fields, shorter, shorter.Length); Set(data, "ClockFields", shorter); Reject(() => Static("ValidatePayload", data));
        data = PayloadData(); var row = ((Array)Get(data, "ClockFields")).Cast<object>().First(field => (string)Get(field, "Transport") == "slot");
        Set(row, "Transport", "local"); Set(row, "Storage", "Execution"); Set(row, "Root", ""); Reject(() => Static("ValidatePayload", data));
        data = PayloadData(); Set(data, "Observer", new CheckpointClockObserverState(true, false, 3, 2, 3).Encode());
        Reject(() => Static("ValidatePayload", data));
    }

    [TestMethod]
    public void VirtualClockCallbacksRequireActualSingleStaticMethod()
    {
        Action callback = VirtualBoundary; var method = callback.Method;
        Static("RequireVirtualDelegate", callback, method);
        Reject(() => Static("RequireVirtualDelegate", (Action)OtherBoundary, method));
        Reject(() => Static("RequireVirtualDelegate", (Action)Delegate.Combine(callback, callback), method));
        Reject(() => Static("RequireVirtualDelegate", (Action)new CallbackOwner().Run, method));
    }

    [TestMethod]
    public void UnclassifiedPayloadFieldsStayUnsupportedAndIdleAssertionsRemainExact()
    {
        var fields = (Array)Static("DescribePayloadFields", typeof(Fixture), false,
            new Dictionary<string, string> { ["Left"] = "slot", ["Right"] = "slot", ["Control"] = "local" }, "fixture");
        var unknown = fields.Cast<object>().Single(field => (string)Get(field, "Name") == "Number");
        Assert.AreEqual("Unsupported", Get(unknown, "Storage")); Assert.AreEqual("unsupported", Get(unknown, "Transport"));
        Static("RequireInvariant", "idle-minus-one", -1, typeof(int), "frame");
        Reject(() => Static("RequireInvariant", "idle-minus-one", 0, typeof(int), "frame"));
        Reject(() => Static("RequireInvariant", "healthy", true, typeof(bool), "pending"));
    }

    [TestMethod]
    public void ActualProducerPairRejectsPartialSwappedAndReplacedHandleOwners()
    {
        using var request = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var ack = new EventWaitHandle(true, EventResetMode.ManualReset);
        var owner = new ProducerEvents();
        object Pair() {
            var builder = Builder(); Call(builder, "Add", "fixture", new Fixture(), Map());
            Static("CaptureEventPair", builder, owner, "Request", "Ack", typeof(ConsumerEvents), "Request", "Ack", (Action)(() => { }));
            return Call(builder, "Finish")!;
        }
        try {
            ConsumerEvents.Request = request; ConsumerEvents.Ack = ack; var absent = Pair();
            owner.Request = request; Reject(() => Call(absent, "RequireUnchanged")); Reject(() => Pair());
            owner.Ack = ack; var healthy = Pair(); Call(healthy, "RequireUnchanged");
            ConsumerEvents.Request = ack; Reject(() => Call(healthy, "RequireUnchanged")); Reject(() => Pair());
            ConsumerEvents.Request = request; ConsumerEvents.Ack = null; Reject(() => Pair());
            Assert.IsFalse(request.WaitOne(0)); Assert.IsTrue(ack.WaitOne(0));
        } finally { ConsumerEvents.Request = null; ConsumerEvents.Ack = null; }
    }

    [TestMethod]
    public void FrameCaptureReadsAPendingRequestWithoutConsumingItAndRestoreStillRejectsIt()
    {
        var data = PayloadData();
        var row = ((Array)Get(data, "Events")).Cast<object>().First(value => (string)Get(value, "Role") == "request");
        using var actual = new EventWaitHandle(true, EventResetMode.AutoReset);
        var witness = Static("ObservePayloadEvent", row, actual, true, (Action)(() => { }));
        Assert.AreEqual(true, Get(row, "Observed")); Assert.AreEqual(true, Get(row, "Signaled"));
        Call(witness, "RequireUnchanged");
        Set(data, "ProducerAbi", 1); Set(row, "Present", true);
        Set(data, "Observer", new CheckpointClockObserverState(true, true, 3, 1, 3).Encode());
        var frameValues = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (domain, group, typeName) in new[] { ("clock", "ClockFields", "ClockController"), ("particle", "ParticleFields", "CanonicalParticleRandomPolicy") }) {
            var type = Payload().GetType("HollowKnightTAS.ClockPayload." + typeName, true)!;
            foreach (var field in ((Array)Get(data, group)).Cast<object>()) {
                var name = (string)Get(field, "Name"); var declared = type.GetField(name, Flags)!.FieldType;
                var transport = (string)Get(field, "Transport");
                if (transport is "active" or "healthy" or "idle-zero" or "idle-minus-one" or "local")
                    if (declared.IsPrimitive || declared.IsEnum || declared == typeof(string)) {
                        object value = name == "sceneClockExclusionActive" ? true : declared == typeof(string) ? "" : Activator.CreateInstance(declared)!;
                        frameValues.Add(domain + "/" + name, (string)Static("DependencyValue", value));
                    }
            }
        }
        Set(data, "FrameValues", frameValues); Static("ValidateObservedPayload", data, true); Reject(() => Static("ValidatePayload", data));
        frameValues["clock/sceneRandomSynchronizationPending"] = "System.Boolean:invalid"; Reject(() => Static("ValidateObservedPayload", data, true));
        Assert.IsTrue(frameValues.Remove("clock/sceneRandomSynchronizationPending")); Reject(() => Static("ValidateObservedPayload", data, true));
        Reject(() => Static("ObservePayloadEvent", row, actual, false, (Action)(() => { })));
        Assert.IsTrue(actual.WaitOne(0)); Assert.IsFalse(actual.WaitOne(0));
        Reject(() => Call(witness, "RequireUnchanged"));
    }

    [TestMethod]
    public void PayloadEventContractRejectsPendingRequestsAndKeepsAcknowledgementsLocal()
    {
        var data = PayloadData(); var events = ((Array)Get(data, "Events")).Cast<object>().ToArray();
        foreach (var value in events) {
            var request = (string)Get(value, "Role") == "request";
            Assert.AreEqual(request ? EventResetMode.AutoReset : EventResetMode.ManualReset, Get(value, "ResetMode"));
        }
        var pending = events.First(value => (string)Get(value, "Role") == "request");
        Set(pending, "Present", true); Set(pending, "Observed", true); Set(pending, "Signaled", true);
        Reject(() => Static("ValidatePayload", data)); Set(pending, "Signaled", false);
        Reject(() => Static("ValidatePayload", data));
        Set(data, "CoverageGaps", new[] { "control-plane-event-producer-admission:" + (string)Get(pending, "Field") }); Static("ValidatePayload", data);
        Set(data, "ProducerAbi", 1); Set(data, "CoverageGaps", Array.Empty<string>()); Static("ValidatePayload", data);
        Set(data, "ProducerAbi", 2); Reject(() => Static("ValidatePayload", data)); Set(data, "ProducerAbi", 1);
        var acknowledged = events.First(value => (string)Get(value, "Role") == "acknowledgement");
        Set(acknowledged, "Present", true); Set(acknowledged, "Observed", true);
        var layout = JsonConvert.SerializeObject(Static("PayloadLayout", data));
        Set(acknowledged, "Signaled", true); Static("ValidatePayload", data);
        Assert.AreEqual(layout, JsonConvert.SerializeObject(Static("PayloadLayout", data)), "acknowledgement signal belongs to the actual local protocol, not source transfer");
        Set(acknowledged, "ResetMode", EventResetMode.AutoReset); Reject(() => Static("ValidatePayload", data));
    }

    private static void VirtualBoundary() { }
    private static void OtherBoundary() { }
    private sealed class CallbackOwner { public void Run() { } }
    private static object PayloadData()
    {
        var api = Api(); var assembly = Payload();
        var clock = assembly.GetType("HollowKnightTAS.ClockPayload.ClockController", true)!;
        var particle = assembly.GetType("HollowKnightTAS.ClockPayload.CanonicalParticleRandomPolicy", true)!;
        var clocks = (Array)Static("DescribePayloadFields", clock, true, api.GetField("ClockFields", Flags)!.GetValue(null)!, "clock");
        var particles = (Array)Static("DescribePayloadFields", particle, false, api.GetField("ParticleFields", Flags)!.GetValue(null)!, "particle");
        var data = Activator.CreateInstance(api.GetNestedType("PayloadData", Flags)!, true)!;
        Set(data, "ClockType", Get(clocks.GetValue(0)!, "DeclaringType")); Set(data, "ParticleType", Get(particles.GetValue(0)!, "DeclaringType"));
        Set(data, "ClockFields", clocks); Set(data, "ParticleFields", particles);
        Set(data, "Events", Static("DescribePayloadEvents", clock));
        Set(data, "Random", new CheckpointRandomState(new int[12], false, false, 0, 0, 0, 1, "", "").Encode());
        Set(data, "Observer", new CheckpointClockObserverState(true, false, 3, 1, 3).Encode()); Set(data, "Particles", new uint[] { 1, 2, 3, 4 });
        return data;
    }

    private static Dictionary<string, string> Map() => new() {
        ["Left"] = "D", ["Right"] = "D", ["Number"] = "D", ["Control"] = "Execution"
    };
    private static object Builder() => Activator.CreateInstance(Api().GetNestedType("Builder", Flags)!, Flags, null, new object[] { (Action)(() => { }) }, null)!;
    private static object Capture(Fixture owner, Dictionary<string, string>? map = null)
    {
        var builder = Builder(); Call(builder, "Add", "fixture", owner, map ?? Map()); Call(builder, "Dependency", "fixture/version", "one");
        return Call(builder, "Finish")!;
    }
    private static object Scope()
    {
        var type = Runtime().GetType("HollowKnightTAS.Runtime.FullRun.ResidentMutationScope", true)!;
        var scope = Activator.CreateInstance(type, Flags, null, new object[] { Array.Empty<UnityEngine.GameObject>(), (Action)(() => { }) }, null)!;
        Call(scope, "Enter"); return scope;
    }
    private static Assembly Runtime()
    {
        return Assembly.LoadFrom(Path.Combine(Repository(), "src", "HollowKnightTAS.Runtime", "bin", "Release", "HollowKnightTAS.dll"));
    }
    private static Assembly Payload() => Assembly.LoadFrom(Path.Combine(Repository(), "src", "HollowKnightTAS.ClockPayload", "bin", "Release", "HollowKnightTAS.ClockPayload.dll"));
    private static string Repository()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        return root?.FullName ?? throw new DirectoryNotFoundException("repository root is absent");
    }
    private static Type Api() => Runtime().GetType("HollowKnightTAS.Runtime.FullRun.ResidentControlPlaneState", true)!;
    private static object Static(string name, params object[] args) => Api().GetMethod(name, Flags)!.Invoke(null, args)!;
    private static object Get(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value)
        ?? value.GetType().GetProperty(name, Flags)!.GetValue(value)!;
    private static void Set(object value, string name, object replacement) => value.GetType().GetField(name, Flags)!.SetValue(value, replacement);
    private static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags)!.Invoke(value, args);
    private static void Reject(Action action)
    { var error = Assert.Throws<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.InnerException); }
}
