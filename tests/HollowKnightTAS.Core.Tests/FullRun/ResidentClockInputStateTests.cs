using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentClockInputStateTests
{
    // These fixtures instantiate only managed input containers and a fake
    // clock transport. They never install a hook or call a Unity native API.
    [TestMethod]
    public void ManifestAccountsForEveryActualFieldAndKeepsControlIdentitiesExternal()
    {
        using var a = new Api(); var fixture = a.Fixture(23, 4); var captured = a.Capture(fixture); var data = Get(captured, "Snapshot");
        a.Validate(data);
        var owners = (Array)Get(data, "Owners");
        CollectionAssert.AreEqual(new[] { "input", "keyboard", "keyboard-state", "mouse" }, owners.Cast<object>().Select(owner => (string)Get(owner, "Key")).ToArray());
        var roots = (IDictionary)Get(captured, "ManagedRoots");
        Assert.IsTrue(roots.Count > 10);
        foreach (var owner in owners.Cast<object>()) foreach (var field in ((Array)Get(owner, "Fields")).Cast<object>()) {
            var storage = (string)Get(field, "Storage"); var root = (string)Get(field, "Root");
            Assert.IsTrue(storage is "D" or "Dependency" or "Execution");
            Assert.AreEqual(storage == "D", root.Length != 0);
            if (root.Length != 0) Assert.IsTrue(roots.Contains(root));
        }
        var inputOwner = owners.Cast<object>().Single(owner => (string)Get(owner, "Key") == "input");
        var inputFields = ((Array)Get(inputOwner, "Fields")).Cast<object>().ToDictionary(field => (string)Get(field, "Name"));
        Assert.AreEqual("D", Get(inputFields["leases"], "Storage"));
        Assert.AreEqual("D", Get(inputFields["customKnightInputs"], "Storage"));
        Assert.AreEqual("Dependency", Get(inputFields["movie"], "Storage"));
        Assert.AreEqual("Execution", Get(inputFields["checkpointInputImported"], "Storage"));
        var key = a.Call(captured, "BindExternal", fixture.Input);
        Assert.AreSame(fixture.Input, a.Call(captured, "ResolveExternal", key));
        Assert.IsNull(a.Call(captured, "BindExternal", Get(fixture.Input, "leases")));
        Assert.AreSame(fixture.Keyboard, a.Call(captured, "ResolveExternal", a.Call(captured, "BindExternal", fixture.Keyboard!)));
        a.Call(captured, "RequireUnchanged");
    }

    [TestMethod]
    public void FrameWitnessPreservesAnActivePartiallyConsumedInputAndCannotRestoreIt()
    {
        using var a = new Api(); var fixture = a.Fixture(35, 10);
        var expected = new[] { new GameInputSample(GameInputChannel.Hero, new short[26], null), new GameInputSample(GameInputChannel.PreMenu, new short[6], null) };
        Set(fixture.Input, "expected", expected); Set(fixture.Input, "nativeFrameActive", true); Set(fixture.Input, "sampleIndex", 1);
        var transitioned = (HashSet<GameInputChannel>)Get(fixture.Input, "transitionedEdgeChannels"); transitioned.Add(GameInputChannel.Hero);
        a.Reject(() => a.Capture(fixture));
        a.Reject(() => a.Static("CaptureFrame", fixture.Backend, fixture.Input, fixture.Mouse, (Action)(() => { })));
        fixture.Backend.FrameOnly = true;
        var frame = a.Static("CaptureFrame", fixture.Backend, fixture.Input, fixture.Mouse, (Action)(() => { }));
        var data = Get(frame, "Snapshot"); var roots = (IDictionary)Get(frame, "ManagedRoots");
        var owner = ((Array)Get(data, "Owners")).Cast<object>().Single(row => (string)Get(row, "Key") == "input");
        object Slot(string name) {
            var field = ((Array)Get(owner, "Fields")).Cast<object>().Single(row => (string)Get(row, "Name") == name);
            return Get(roots[Get(field, "Root")]!, "Value");
        }
        Assert.AreEqual(true, Slot("nativeFrameActive")); Assert.AreEqual(1, Slot("sampleIndex")); Assert.AreSame(expected, Slot("expected"));
        Assert.AreSame(transitioned, Slot("transitionedEdgeChannels")); a.Call(frame, "RequireFrameUnchanged");
        Assert.AreEqual(0, fixture.Backend.Imports.Count); a.RejectValidate(data); a.Reject(() => a.Capture(fixture)); a.Reject(() => a.Call(frame, "RequireUnchanged"));
        Set(fixture.Input, "sampleIndex", 2); a.Reject(() => a.Call(frame, "RequireFrameUnchanged"));
        Set(fixture.Input, "sampleIndex", 1); a.Call(frame, "RequireFrameUnchanged"); Assert.IsTrue(transitioned.Contains(GameInputChannel.Hero));
    }

    [TestMethod]
    public void ManifestRejectsOmittedFieldsChangedOwnershipAndIncompleteNativePackets()
    {
        using var a = new Api(); var data = Get(a.Capture(a.Fixture(7, 1)), "Snapshot");
        object Copy() => JsonConvert.DeserializeObject(JsonConvert.SerializeObject(data), data.GetType())!;
        var missing = Copy(); var owner = ((Array)Get(missing, "Owners")).GetValue(0)!; var fields = (Array)Get(owner, "Fields");
        var shortened = Array.CreateInstance(fields.GetType().GetElementType()!, fields.Length - 1); Array.Copy(fields, shortened, shortened.Length); Set(owner, "Fields", shortened); a.RejectValidate(missing);
        var ownership = Copy(); var input = ((Array)Get(ownership, "Owners")).GetValue(0)!; var first = ((Array)Get(input, "Fields")).Cast<object>().First(field => (string)Get(field, "Storage") == "D");
        Set(first, "Storage", "Execution"); Set(first, "Root", ""); a.RejectValidate(ownership);
        var random = Copy(); Set(random, "Random", Array.Empty<byte>()); a.RejectValidate(random);
        var particle = Copy(); Set(particle, "Particles", Array.Empty<uint>()); a.RejectValidate(particle);
        var dependencies = Copy(); var row = ((Array)Get(dependencies, "Dependencies")).GetValue(0)!; Set(row, "Ownership", "D"); a.RejectValidate(dependencies);
        var keyboard = Copy(); Set(keyboard, "KeyboardPresent", false); a.RejectValidate(keyboard);
    }

    [TestMethod]
    public void PendingMovieUsesExactUnconsumedRunAndSeparatesCustomSamples()
    {
        using var a = new Api(); var source = Get(a.Capture(a.Fixture(9, 2)), "Snapshot");
        var hero = new GameInputSample(GameInputChannel.Hero, new short[26], null);
        var menu = new GameInputSample(GameInputChannel.PreMenu, new short[6], null);
        var movie = Movie(new NativeFrameRun(2, new[] { hero }, default), new NativeFrameRun(3, new[] { menu }, default, authored: true));
        var plan = a.Static("PrepareMovie", source, movie);
        CollectionAssert.AreEqual(new long[] { 0, 2 }, (long[])Get(plan, "Starts")); Assert.IsTrue((bool)Get(plan, "Authored"));
        CollectionAssert.AreEqual(new[] { menu }, (GameInputSample[])Get(plan, "Expected")); Assert.AreEqual(0, ((Array)Get(plan, "Custom")).Length);
        Set(source, "MovieFrame", 5L); a.Reject(() => a.Static("PrepareMovie", source, movie));
        Set(source, "MovieFrame", 0L); Set(source, "ConfiguredKeys", new short[] { 97 }); a.Reject(() => a.Static("PrepareMovie", source, movie));
        var keyed = MovieWithKeys(new short[] { 97 }, new NativeFrameRun(2, new[] { hero, CustomKeyInput.Sample(97, true) }, default));
        var keyedPlan = a.Static("PrepareMovie", source, keyed); Assert.AreEqual(1, ((Array)Get(keyedPlan, "Custom")).Length); Assert.AreEqual(1, ((Array)Get(keyedPlan, "Expected")).Length);
    }

    [TestMethod]
    public void TypedSlotsAndExactNativeImportSupportTwoIndependentBoundaryTransactions()
    {
        using var a = new Api(); var source = a.Fixture(17, 3); var sharedMouseState = new MouseFrameState(123, 456, 7, -8, 9, 10);
        Set(source.Mouse, "inControlState", sharedMouseState); Set(source.Mouse, "hkState", sharedMouseState);
        Set(source.Input, "authoredEdgeChannels", new HashSet<GameInputChannel> { GameInputChannel.Hero });
        var sourceCapture = a.Capture(source); var target = a.Fixture(91, 1);
        Set(target.Input, "checkpointInputImported", true); Set(target.Mouse, "checkpointMouseImported", true);
        var targetCapture = a.Capture(target); var targetKeyboard = target.Keyboard; var targetProvider = Get(target.Mouse, "filteredProvider");
        var scope = a.Scope();
        try {
            var plan = a.Static("Prepare", Get(sourceCapture, "Snapshot"), targetCapture, Movie(new NativeFrameRun(10, Array.Empty<GameInputSample>(), default)), scope, (Action)(() => { }));
            a.Call(plan, "RequireComplete"); CopySlots(sourceCapture, targetCapture); a.Call(plan, "Apply"); a.Call(plan, "Verify");
            CollectionAssert.AreEqual(new[] { "random:91", "native:91", "observer:17", "particles:17" }, target.Backend.Imports.ToArray());
            Assert.AreEqual(17L, target.Backend.Frame); Assert.AreEqual(3L, Get(target.Input, "currentFrame"));
            Assert.AreSame(Get(target.Mouse, "inControlState"), Get(target.Mouse, "hkState"));
            Assert.AreSame(targetKeyboard, Get(target.Input, "keyboard")); Assert.AreSame(targetProvider, Get(target.Mouse, "filteredProvider"));
            Assert.IsTrue((bool)Get(target.Input, "checkpointInputImported")); Assert.IsTrue((bool)Get(target.Mouse, "checkpointMouseImported"));
            a.Reject(() => a.Call(plan, "Apply"));

            // The next admitted boundary uses fresh capture/plan instances on
            // the same live controls. Legacy one-import flags stay untouched.
            var nextSource = a.Fixture(22, 7); var nextSourceCapture = a.Capture(nextSource); target.Register(); var nextTargetCapture = a.Capture(target);
            var nextPlan = a.Static("Prepare", Get(nextSourceCapture, "Snapshot"), nextTargetCapture, Movie(new NativeFrameRun(12, Array.Empty<GameInputSample>(), default)), scope, (Action)(() => { }));
            CopySlots(nextSourceCapture, nextTargetCapture); a.Call(nextPlan, "Apply"); a.Call(nextPlan, "Verify");
            CollectionAssert.AreEqual(new[] { "random:17", "native:17", "observer:22", "particles:22" }, target.Backend.Imports.Skip(4).ToArray());
            Assert.AreEqual(22L, target.Backend.Frame); Assert.AreEqual(7L, Get(target.Input, "currentFrame"));
        } finally { a.Call(scope, "Complete", (Action)(() => { })); ((IDisposable)scope).Dispose(); }
    }

    [TestMethod]
    public void StaleExecutionIdentityOrClockBytesRejectBeforeAnyNativeWrite()
    {
        using var a = new Api(); var fixture = a.Fixture(11, 2); var captured = a.Capture(fixture); var scope = a.Scope();
        try {
            var movie = Movie(new NativeFrameRun(10, Array.Empty<GameInputSample>(), default));
            Set(fixture.Input, "nativeFrameRunning", (Func<bool>)(() => false));
            a.Reject(() => a.Static("Prepare", Get(captured, "Snapshot"), captured, movie, scope, (Action)(() => { })));
            Set(fixture.Input, "nativeFrameRunning", null);
            var plan = a.Static("Prepare", Get(captured, "Snapshot"), captured, movie, scope, (Action)(() => { }));
            fixture.Backend.Random[0] ^= 1; a.Reject(() => a.Call(plan, "Apply"));
            Assert.AreEqual(0, fixture.Backend.Imports.Count); Assert.AreEqual(2L, Get(fixture.Input, "currentFrame"));
        } finally { a.Call(scope, "Complete", (Action)(() => { })); ((IDisposable)scope).Dispose(); }
    }

    [TestMethod]
    public void MissingRecordingKeyboardIsAnExplicitCoverageGap()
    {
        using var a = new Api(); var source = a.Fixture(2, 1, keyboard: false); var sourceCapture = a.Capture(source); var target = a.Fixture(4, 1); var targetCapture = a.Capture(target); var scope = a.Scope();
        try {
            var plan = a.Static("Prepare", Get(sourceCapture, "Snapshot"), targetCapture, Movie(new NativeFrameRun(4, Array.Empty<GameInputSample>(), default)), scope, (Action)(() => { }));
            CollectionAssert.Contains((string[])Get(plan, "Unsupported"), "input-keyboard-owner-presence-differs");
            a.Reject(() => a.Call(plan, "RequireComplete")); Assert.AreEqual(0, target.Backend.Imports.Count);
        } finally { a.Call(scope, "Complete", (Action)(() => { })); ((IDisposable)scope).Dispose(); }
    }

    private static MovieV2Document Movie(params NativeFrameRun[] runs) => MovieWithKeys(Array.Empty<short>(), runs);
    private static MovieV2Document MovieWithKeys(short[] keys, params NativeFrameRun[] runs) => new MovieV2Document("fixture",
        new MovieV2Header("game", "api", "mod", MovieProtocolV2.SelectedNativeProfileId, MovieProtocolV2.ActionSchemaId, false, new string('a', 64), 1280, 720, keys), runs);
    private static void CopySlots(object source, object target)
    {
        var from = (IDictionary)Get(source, "ManagedRoots"); var to = (IDictionary)Get(target, "ManagedRoots");
        Assert.AreEqual(from.Count, to.Count);
        foreach (DictionaryEntry item in from) Set(to[item.Key]!, "Value", Get(item.Value!, "Value"));
    }
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, All)?.GetValue(owner)
        ?? owner.GetType().GetProperty(name, All)!.GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, All)!.SetValue(owner, value);

    public class BackendProxy : DispatchProxy
    {
        public long Frame;
        public bool FrameOnly;
        public byte[] Native = Array.Empty<byte>(), Random = Array.Empty<byte>(), Observer = Array.Empty<byte>();
        public uint[] Particles = Array.Empty<uint>();
        public readonly List<string> Imports = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name) {
                case "get_Identity": return "offline-clock-input-fixture-v1";
                case "get_IsFrameObservation": return FrameOnly;
                case "get_CoverageGaps": return Array.Empty<string>();
                case "get_NativeFrame": return Frame;
                case "RequireBoundary": return null;
                case "CaptureNativeClock": return (byte[])Native.Clone();
                case "CaptureRandom": return (byte[])Random.Clone();
                case "CaptureObserver": return (byte[])Observer.Clone();
                case "CaptureParticles": return (uint[])Particles.Clone();
                case "ImportRandom": Assert.AreEqual(Frame, (long)args![1]!); Imports.Add("random:" + args[1]); Random = (byte[])args[0]!; return null;
                case "ImportNative": Assert.AreEqual(Frame, (long)args![1]!); Imports.Add("native:" + args[1]); Native = (byte[])args[0]!; Frame = BitConverter.ToInt64(Native, 0); return null;
                case "ImportObserver": Assert.AreEqual(Frame, (long)args![1]!); Imports.Add("observer:" + args[1]); Observer = (byte[])args[0]!; return null;
                case "ImportParticles": Assert.AreEqual(Frame, (long)args![1]!); Imports.Add("particles:" + args[1]); Particles = (uint[])args[0]!; return null;
                default: throw new InvalidOperationException("Unexpected fake backend call " + method.Name);
            }
        }
    }
    public class UncalledProviderProxy : DispatchProxy
    { protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Input provider must not run during a boundary: " + method?.Name); }

    private sealed class Fixture
    {
        internal readonly object Input, Mouse;
        internal readonly object? Keyboard;
        internal readonly BackendProxy Backend;
        private readonly FieldInfo providerSlot;
        internal Fixture(object input, object mouse, object? keyboard, BackendProxy backend, FieldInfo providerSlot)
        { Input = input; Mouse = mouse; Keyboard = keyboard; Backend = backend; this.providerSlot = providerSlot; }
        internal void Register() => providerSlot.SetValue(null, Get(Mouse, "filteredProvider"));
    }
    private sealed class Api : IDisposable
    {
        private readonly Assembly runtime;
        private readonly Type type, inputType, mouseType, scopeType;
        private readonly FieldInfo providerSlot;
        private readonly object? originalProvider;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentClockInputState", true)!;
            inputType = runtime.GetType("HollowKnightTAS.Runtime.Input.FullRunActionSetAdapter", true)!; mouseType = runtime.GetType("HollowKnightTAS.Runtime.Input.FullRunMouseBridge", true)!;
            scopeType = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentMutationScope", true)!;
            providerSlot = typeof(InControl.InputManager).GetField("<MouseProvider>k__BackingField", All)!; originalProvider = providerSlot.GetValue(null);
        }
        internal Fixture Fixture(long native, long movie, bool keyboard = true)
        {
            var input = Activator.CreateInstance(inputType, true)!; Set(input, "hooked", true); Set(input, "recording", !keyboard); Set(input, "replaying", keyboard); Set(input, "currentFrame", movie);
            var cutsceneType = runtime.GetType("HollowKnightTAS.Runtime.Input.FullRunCutsceneInput", true)!; var cutscene = RuntimeHelpers.GetUninitializedObject(cutsceneType);
            Set(cutscene, "hooks", Activator.CreateInstance(cutsceneType.GetField("hooks", All)!.FieldType)); Set(input, "cutsceneInput", cutscene);
            object? keyboardOwner = null;
            if (keyboard) { keyboardOwner = Activator.CreateInstance(runtime.GetType("HollowKnightTAS.Runtime.Input.FullRunKeyboardBridge", true)!, new object[] { (Func<bool>)(() => false) })!; Set(input, "keyboard", keyboardOwner); }
            var mouse = Activator.CreateInstance(mouseType, true)!; Set(mouse, "input", input); Set(mouse, "installed", true); Set(mouse, "replaying", keyboard); Set(mouse, "canonicalMouseCaches", true);
            Set(mouse, "viewportWidth", 1280); Set(mouse, "viewportHeight", 720);
            var original = DispatchProxy.Create(typeof(InControl.IMouseProvider), typeof(UncalledProviderProxy)); Set(mouse, "originalProvider", original);
            var filtered = Activator.CreateInstance(mouseType.GetNestedType("FilteredProvider", All)!, All, null, new[] { mouse, original }, null)!; Set(mouse, "filteredProvider", filtered);
            var backend = (BackendProxy)DispatchProxy.Create(type.GetNestedType("IBoundaryBackend", All)!, typeof(BackendProxy));
            backend.Frame = native; backend.Native = BitConverter.GetBytes(native); backend.Random = new byte[] { (byte)native, 2, 3, 4 }; backend.Observer = new byte[] { 5, (byte)native, 7, 8 }; backend.Particles = new uint[] { 9, (uint)native, 11, 12 };
            var result = new Fixture(input, mouse, keyboardOwner, backend, providerSlot); result.Register(); return result;
        }
        internal object Capture(Fixture fixture) => Static("Capture", fixture.Backend, fixture.Input, fixture.Mouse, (Action)(() => { }));
        internal object Static(string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values)!;
        internal object Call(object owner, string name, params object[] values) => owner.GetType().GetMethod(name, All)!.Invoke(owner, values)!;
        internal void Validate(object data) => Static("Validate", data);
        internal void RejectValidate(object data) => Reject(() => Validate(data));
        internal void Reject(Action operation) { var error = Assert.ThrowsExactly<TargetInvocationException>(operation); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
        internal object Scope() { var result = Activator.CreateInstance(scopeType, All, null, new object[] { Array.Empty<GameObject>(), (Action)(() => { }) }, null)!; Call(result, "Enter"); return result; }
        public void Dispose() => providerSlot.SetValue(null, originalProvider);
    }
}
