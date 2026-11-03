using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentProcessSlotsTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Fixture
    {
        public object Left = new object(), Right = new object(), Control = new object();
        public double Number = -0.0;
        public string Dependency = "same";
    }
    [TestMethod]
    public void RuntimeSessionContractAccountsForEveryActualInstanceField()
    {
        var runtime = Runtime(); var session = runtime.GetType("HollowKnightTAS.Runtime.FullRun.RuntimeFullRunSession", true)!;
        var map = (IReadOnlyDictionary<string, string>)session.GetField("ResidentFieldOwnership", Flags)!.GetValue(null)!;
        var actual = session.GetFields(Flags).Where(field => !field.IsStatic).Select(field => field.Name).OrderBy(name => name).ToArray();
        CollectionAssert.AreEqual(actual, map.Keys.OrderBy(name => name).ToArray());
        Assert.AreEqual("D", map["boundBosses"]); Assert.AreEqual("D", map["movieFrame"]);
        Assert.AreEqual("Execution", map["clock"]); Assert.AreEqual("Dependency", map["replayMovie"]);
    }
    [TestMethod]
    public void LoaderContinuationPreservesSourceNameWithoutTransferringNativeSceneHandles()
    {
        var loader = Runtime().GetType("HollowKnightTAS.Runtime.FullRun.LoadingSerialSceneProbe", true)!;
        var map = (IReadOnlyDictionary<string, string>)loader.GetField("ResidentFieldOwnership", Flags)!.GetValue(null)!;
        var actual = loader.GetFields(Flags).Where(field => !field.IsStatic).Select(field => field.Name).OrderBy(name => name).ToArray();
        CollectionAssert.AreEqual(actual, map.Keys.OrderBy(name => name).ToArray());
        // Unload suppression after import must use the restored source name;
        // the native unloaded/staging scene handles still belong to the target.
        Assert.AreEqual("D", map["sourceName"]);
        Assert.AreEqual("Dependency", map["source"]); Assert.AreEqual("Dependency", map["staging"]);
        Assert.AreEqual("Dependency", map["transition"]); Assert.AreEqual("Dependency", map["checkpointTransitionRoutine"]);
        Assert.AreEqual("Execution", map["residentBeginRoutine"]);
    }
    [TestMethod]
    public void TypedRootsPreserveGameplayAliasesAndSeparateExecutionOwner()
    {
        var fixture = new Fixture(); fixture.Right = fixture.Left;
        var slots = Capture(fixture, Ownership()); Call(slots, "RequireComplete");
        var roots = (IDictionary)Get(slots, "ManagedRoots");
        Assert.AreEqual(3, roots.Count);
        var values = roots.Values.Cast<object>().Select(value => value.GetType().GetField("Value")!.GetValue(value)).ToArray();
        Assert.AreEqual(2, values.Count(value => ReferenceEquals(value, fixture.Left)));
        Assert.IsNull(Call(slots, "BindExternal", fixture.Left));
        Assert.AreSame(fixture, Call(slots, "ResolveExternal", Call(slots, "BindExternal", fixture)!));
        Assert.AreSame(fixture.Control, Call(slots, "ResolveExternal", Call(slots, "BindExternal", fixture.Control)!));
        fixture.Number = 0.0; Reject(() => Call(slots, "RequireUnchanged"));
    }
    [TestMethod]
    public void MissingFieldOrErasedGapCannotBecomeComplete()
    {
        var map = Ownership(); map.Remove("Number");
        var slots = Capture(new Fixture(), map); Reject(() => Call(slots, "RequireComplete"));
        var data = Get(slots, "Snapshot"); data.GetType().GetField("CoverageGaps")!.SetValue(data, Array.Empty<string>());
        Reject(() => Call(slots, "RequireComplete"));
    }
    [TestMethod]
    public void OwnershipDowngradeAndExecutionReplacementAreRejected()
    {
        var fixture = new Fixture(); var slots = Capture(fixture, Ownership()); var data = Get(slots, "Snapshot");
        var copy = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(data), data.GetType())!;
        var row = ((Array)Get(copy, "Fields")).Cast<object>().Single(field => (string)Get(field, "Name") == "Left");
        row.GetType().GetField("Storage")!.SetValue(row, "Execution"); row.GetType().GetField("Root")!.SetValue(row, "");
        Reject(() => Call(slots, "RequireLayout", copy));
        fixture.Control = new object(); Reject(() => Call(slots, "RequireControlUnchanged"));
    }
    private static Dictionary<string, string> Ownership() => new() {
        ["Left"] = "D", ["Right"] = "D", ["Number"] = "D", ["Control"] = "Execution", ["Dependency"] = "Dependency"
    };
    private static Assembly Runtime()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException("repository root is absent");
        var path = Path.Combine(root.FullName, "src", "HollowKnightTAS.Runtime", "bin", "Release", "HollowKnightTAS.dll");
        return Assembly.LoadFrom(path);
    }
    private static object Capture(object owner, IReadOnlyDictionary<string, string> map)
    {
        _ = typeof(UnityEngine.Object);
        var type = Runtime().GetType("HollowKnightTAS.Runtime.FullRun.ResidentProcessSlots", true)!;
        return Activator.CreateInstance(type, Flags, null, new object[] { "fixture", owner, map, (Action)(() => { }) }, null)!;
    }
    private static object Get(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value)
        ?? value.GetType().GetProperty(name, Flags)!.GetValue(value)!;
    private static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags)!.Invoke(value, args);
    private static void Reject(Action action)
    { var error = Assert.Throws<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.InnerException); }
}
