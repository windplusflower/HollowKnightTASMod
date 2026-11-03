using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentTransformStateTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Flags)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type api;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            api = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentTransformState", true)!;
        }
        internal object Row(string type) => Activator.CreateInstance(api.GetNestedType(type, Flags)!, true)!;
        internal Array Array(string type, params object[] values)
        {
            var array = System.Array.CreateInstance(api.GetNestedType(type, Flags)!, values.Length);
            for (var index = 0; index < values.Length; index++) array.SetValue(values[index], index);
            return array;
        }
        internal object Fixture()
        {
            var root = Row("Node"); Set(root, "Owner", "root"); Set(root, "Hierarchy", "root"); Set(root, "Next", "text");
            Set(root, "Descendants", 2U); Set(root, "Local", new byte[40]); Set(root, "Cached", new byte[48]);
            var child = Row("Node"); Set(child, "Owner", "text"); Set(child, "Parent", "root"); Set(child, "Previous", "root");
            Set(child, "Hierarchy", "root"); Set(child, "Descendants", 1U); Set(child, "Rectangle", true);
            Set(child, "Local", new byte[40]); Set(child, "Cached", new byte[48]); Set(child, "Rect", new byte[58]);
            var hierarchy = Row("Hierarchy"); Set(hierarchy, "Key", "root"); Set(hierarchy, "Members", new[] { "root", "text" });
            var registry = Row("Registry"); Set(registry, "InterestTypes", new ulong[11]);
            Set(registry, "Names", Enumerable.Repeat("", 64).ToArray()); Set(registry, "ImmediateNames", Enumerable.Repeat("", 31).ToArray());
            Set(registry, "ImmediateInterests", new int[31]); Set(registry, "ImmediateCallbacks", new int[31]);
            var data = Row("Data"); Set(data, "Nodes", Array("Node", root, child)); Set(data, "Hierarchies", Array("Hierarchy", hierarchy));
            Set(data, "Queue", new[] { "root" }); Set(data, "Systems", registry); return data;
        }
        internal void Call(string name, params object[] values) => api.GetMethod(name, Flags)!.Invoke(null, values);
        internal void Reject(string name, params object[] values)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call(name, values));
            Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        }
    }
    [TestMethod]
    public void TransformReadbackIncludesStaleRectCacheSimdBytesAndDeferredOrder()
    {
        var api = new Api(); var expected = api.Fixture(); var actual = api.Fixture();
        api.Call("RequireEqual", expected, actual);
        var child = ((Array)Get(actual, "Nodes")).GetValue(1)!;
        ((byte[])Get(child, "Rect"))[56] = 1; api.Reject("RequireEqual", expected, actual);
        ((byte[])Get(child, "Rect"))[56] = 0;
        ((byte[])Get(child, "Cached"))[15] = 0x80; api.Reject("RequireEqual", expected, actual);
        ((byte[])Get(child, "Cached"))[15] = 0;
        Set(actual, "Queue", System.Array.Empty<string>()); api.Reject("RequireEqual", expected, actual);
    }
    [TestMethod]
    public void TransformManifestRejectsAliasCollapseForeignParentsAndCycles()
    {
        var api = new Api();
        void Reject(Action<object, Array> change) { var data = api.Fixture(); change(data, (Array)Get(data, "Nodes")); api.Reject("Validate", data); }
        Reject((_, nodes) => Set(nodes.GetValue(1)!, "Owner", "root"));
        Reject((_, nodes) => Set(nodes.GetValue(1)!, "Parent", "foreign"));
        Reject((_, nodes) => Set(nodes.GetValue(0)!, "Parent", "text"));
        Reject((data, _) => Set(data, "Queue", new[] { "root", "root" }));
    }
    [TestMethod]
    public void TransformRegistryAndCoverageAreIndependentlyChecked()
    {
        var api = new Api(); var expected = api.Fixture(); var actual = api.Fixture();
        Set(Get(actual, "Systems"), "Deprecated", 8UL); api.Reject("RequireEqual", expected, actual);
        var incomplete = api.Fixture(); Set(incomplete, "CoverageGaps", new[] { "missing native hierarchy" });
        api.Call("Validate", incomplete); api.Reject("RequireCompleteCoverage", incomplete);
    }
}
