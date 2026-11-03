using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAudioStateTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Flags)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type api, curve;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            api = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentAudioState", true)!;
            curve = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentManagedNativeWrappers", true)!.GetNestedType("CurveData", Flags)!;
        }
        internal object New(string type) => Activator.CreateInstance(api.GetNestedType(type, Flags)!, true)!;
        internal Array Array(string type, params object[] values)
        {
            var result = System.Array.CreateInstance(api.GetNestedType(type, Flags)!, values.Length);
            for (var index = 0; index < values.Length; index++) result.SetValue(values[index], index); return result;
        }
        internal object Fixture(bool listener = false)
        {
            var data = New("Data"); var row = New(listener ? "Listener" : "Source"); Set(row, "Owner", listener ? "listener" : "source");
            var type = Get(row, "Type"); Set(type, "Assembly", "UnityEngine.AudioModule"); Set(type, "Module", "aebd3224-17b8-4b41-ad28-0fa7ff609f00");
            Set(type, "Name", listener ? "UnityEngine.AudioListener" : "UnityEngine.AudioSource"); Set(type, "Token", 0x02000001);
            Set(row, "Scalars", (listener ? new[] { 2, 28, 64, 1 } : new[] { 2, 40, 233, 4, 1, 8, 6, 4, 14, 1 }).Select(count => new byte[count]).ToArray());
            if (!listener) {
                var curves = System.Array.CreateInstance(curve, 4);
                for (var index = 0; index < curves.Length; index++) {
                    var value = Activator.CreateInstance(curve, true)!; Set(value, "Caches", new byte[56]); curves.SetValue(value, index);
                }
                Set(row, "Curves", curves);
            }
            Set(data, listener ? "Listeners" : "Sources", Array(listener ? "Listener" : "Source", row)); return data;
        }
        internal object Row(object data, bool listener = false) => ((Array)Get(data, listener ? "Listeners" : "Sources")).GetValue(0)!;
        internal void Call(string name, params object[] values) => api.GetMethod(name, Flags)!.Invoke(null, values);
        internal void Reject(string name, params object[] values)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(name, values)); Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        }
    }
    [TestMethod]
    public void AudioReadbackRetainsSamplePositionAndListenerVelocityMatrix()
    {
        var api = new Api(); var expected = api.Fixture(); var actual = api.Fixture(); api.Call("RequireEqual", expected, actual);
        ((byte[][])Get(api.Row(actual), "Scalars"))[8][8] = 7; api.Reject("RequireEqual", expected, actual);
        expected = api.Fixture(true); actual = api.Fixture(true); api.Call("RequireEqual", expected, actual);
        ((byte[][])Get(api.Row(actual, true), "Scalars"))[2][48] = 1; api.Reject("RequireEqual", expected, actual);
    }
    [TestMethod]
    public void ExpiredNativeWeakChannelCannotBeFlattenedToAnEmptySource()
    {
        var api = new Api(); var data = api.Fixture(); var main = Get(api.Row(data), "Main"); Set(main, "WeakOwner", true); Set(main, "Alias", "audio-channel/main");
        Set(data, "CoverageGaps", System.Array.Empty<string>()); api.Call("Validate", data); api.Reject("RequireCompleteCoverage", data); api.Reject("RequireCanonicalizableCoverage", data);
        Set(main, "WeakOwner", false); api.Reject("Validate", data);
    }
    [TestMethod]
    public void OneShotOwnerAndFmodResourceGapsAreDerivedFromNativeShape()
    {
        var api = new Api(); var data = api.Fixture(); var row = api.Row(data); Set(row, "OneShots", api.Array("Channel", api.New("Channel")));
        api.Call("Validate", data); api.Reject("RequireCompleteCoverage", data);
        Set(row, "OneShots", api.Array("Channel")); ((bool[])Get(row, "NativeResources"))[1] = true; Set(data, "CoverageGaps", System.Array.Empty<string>());
        api.Reject("RequireCanonicalizableCoverage", data);
    }
    [TestMethod]
    public void CurveCanonOnlyNormalizesStorageAndRetainsAllCurveValues()
    {
        var api = new Api(); var data = api.Fixture(); var row = api.Row(data); var curve = ((Array)Get(row, "Curves")).GetValue(0)!;
        Set(curve, "Capacity", 8); Set(curve, "UnusedKeys", new byte[8 * 28]); api.Reject("RequireCanonicalizableCoverage", data);
        Set(curve, "KeyStorageFlags", 0); api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCompleteCoverage", data);
        Set(curve, "Capacity", 0); Set(curve, "KeyStorageFlags", 1); Set(curve, "UnusedKeys", System.Array.Empty<byte>()); api.Call("RequireCanonicalState", data);
        var other = api.Fixture(); ((byte[])Get(((Array)Get(api.Row(other), "Curves")).GetValue(0)!, "Caches"))[20] = 1; api.Reject("RequireEqual", data, other);
    }
    [TestMethod]
    public void EmbeddedCurveStorageGatePreservesTheActualOwnerAllocatorAndCacheD()
    {
        var api = new Api(); var data = api.Fixture(); var curve = ((Array)Get(api.Row(data), "Curves")).GetValue(0)!;
        Set(curve, "KeyMemoryLabel", 31); ((byte[])Get(curve, "Caches"))[20] = 0x7f; api.Call("RequireCanonicalState", data);
        ((byte[])Get(curve, "StoragePadding"))[0] = 1; api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCanonicalState", data);
        ((byte[])Get(curve, "StoragePadding"))[0] = 0; Set(curve, "KeyStorageFlags", 0); api.Call("RequireCanonicalizableCoverage", data); api.Reject("RequireCanonicalState", data);
        Set(curve, "KeyStorageFlags", 1); api.Call("RequireCanonicalState", data);
    }
    [TestMethod]
    public void NativeManagerRegistrationRequiresReciprocalComponentMembership()
    {
        var api = new Api(); var data = api.Fixture(); var row = api.Row(data); var manager = Get(data, "Manager"); Set(manager, "Present", true); Set(row, "Registered", true);
        api.Reject("Validate", data); Set(manager, "Sources", new[] { "source" }); api.Call("Validate", data);
        Set(row, "Scheduled", true); Set(row, "ScheduledSource", "source"); api.Reject("Validate", data);
        Set(manager, "Scheduled", new[] { "source" }); api.Call("Validate", data); Set(row, "ScheduledSource", ""); api.Reject("Validate", data);
    }
    [TestMethod]
    public void ComponentOnlySnapshotCannotGrantTheUnimplementedAudioBackendCoverage()
    {
        var api = new Api(); var data = api.Fixture(); Set(Get(data, "Manager"), "Present", true); Set(data, "CoverageGaps", System.Array.Empty<string>());
        api.Call("Validate", data); api.Reject("RequireCompleteCoverage", data); api.Reject("RequireCanonicalizableCoverage", data);
    }
    [TestMethod]
    public void ActualAudioTypeCannotSelectAnotherNativeFieldLayout()
    {
        var api = new Api(); var data = api.Fixture(); Set(Get(api.Row(data), "Type"), "Name", "UnityEngine.AudioListener"); api.Reject("Validate", data);
        data = api.Fixture(true); ((byte[][])Get(api.Row(data, true), "Scalars"))[2] = new byte[60]; api.Reject("Validate", data);
    }
}
