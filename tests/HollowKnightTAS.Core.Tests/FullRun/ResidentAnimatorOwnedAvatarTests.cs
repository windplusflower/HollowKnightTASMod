using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorOwnedAvatarTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type Load()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")
            ?? Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState", true)!;
    }
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags)!.GetValue(value)!;
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags)!.SetValue(value, data);
    private static object? Call(Type type, string method, params object[] args) => type.GetMethod(method, Flags)!.Invoke(null, args);
    private static object Member(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags)!.Invoke(value, args)!;
    private static void Invalid(Action action)
    {
        Exception error = Assert.ThrowsExactly<TargetInvocationException>(action);
        while (error is TargetInvocationException wrapper && wrapper.InnerException != null) error = wrapper.InnerException;
        Assert.IsInstanceOfType<InvalidDataException>(error);
    }

    private sealed class Image : IDisposable
    {
        private readonly object image;
        internal readonly object Dataset;
        internal readonly object Graph;
        internal Image(Type type, int instance)
        {
            var harness = typeof(ResidentAnimatorStateTests).GetNestedType("AvatarImage", Flags)!;
            image = Activator.CreateInstance(harness, Flags, null, new object[] { type, instance, false }, null)!;
            Dataset = Get(image, "Set");
            Graph = Get(image, "Graph");
            var constant = (IntPtr)Member(image, "At", 1800);
            var names = (IntPtr)Member(image, "At", 2000);
            Marshal.WriteInt32(constant, 24, 1);
            Marshal.WriteInt64(constant, 32, names.ToInt64() - constant.ToInt64() - 32);
            Marshal.WriteInt32(names, unchecked((int)0x12345678));
            var kind = Enum.Parse(type.GetNestedType("AvatarKind", Flags)!, "Constant");
            int index = (int)Graph.GetType().GetMethod("Node", Flags)!.Invoke(Graph, new object?[] { constant, kind, true, -1, null })!;
            ((int[])Get(Dataset, "Edges"))[0] = index;
            Set(Dataset, "OwnsAvatar", (byte)1);
            Set(Dataset, "Initialized", (byte)1);
        }
        internal object Snapshot(Type type) => Member(image, "Snapshot", type);
        internal Array Nodes => (Array)image.GetType().GetProperty("Nodes", Flags)!.GetValue(image)!;
        internal void Transfer(Type type, Array nodes) => Member(image, "Transfer", type, nodes);
        public void Dispose() => ((IDisposable)image).Dispose();
    }

    [TestMethod]
    public void NonemptyOwnedConstantRetainsTypedTargetAllocationAndHasNoConstructionGate()
    {
        var type = Load();
        using var source = new Image(type, 143);
        using var target = new Image(type, 352);
        var data = source.Snapshot(type);
        Call(type, "Validate", data);
        var gaps = (string[])Call(type, "RequiredCoverageGaps", data)!;
        CollectionAssert.DoesNotContain(gaps, "owned-dynamic-avatar-constant-construction");
        target.Transfer(type, source.Nodes);
        Call(type, "RequireEqual", data, target.Snapshot(type));
    }

    [TestMethod]
    public void MissingOwnedRootAndNoncanonicalOwnershipAreRejected()
    {
        var type = Load();
        using var image = new Image(type, 143);
        var data = image.Snapshot(type);
        var edges = (int[])Get(image.Dataset, "Edges");
        int root = edges[0];
        edges[0] = -1;
        Invalid(() => Call(type, "ValidateAvatar", data));
        edges[0] = root;
        Set(image.Dataset, "OwnsAvatar", (byte)2);
        Call(type, "ValidateAvatar", data);
        var gaps = (string[])Call(type, "RequiredCoverageGaps", data)!;
        Assert.IsTrue(gaps.Any(gap => gap.Contains("AvatarDataSet") && gap.Contains("=2")));
        Invalid(() => Call(type, "RequireCompleteCoverage", data));
        Assert.AreEqual((byte)2, Get(image.Dataset, "OwnsAvatar"));
    }

    [TestMethod]
    public void TargetOwnedFlagAndReadonlyConstantContentMustStillMatch()
    {
        var type = Load();
        using var source = new Image(type, 143);
        using var target = new Image(type, 352);
        var saved = source.Snapshot(type);
        Set(target.Dataset, "OwnsAvatar", (byte)0);
        Invalid(() => Call(type, "RequireEqual", saved, target.Snapshot(type)));
        Set(target.Dataset, "OwnsAvatar", (byte)1);
        var constant = target.Nodes.Cast<object>().Single(node => Get(node, "Kind").ToString() == "Constant");
        ((byte[][])Get(constant, "Fields"))[0][0] ^= 1;
        Invalid(() => target.Transfer(type, source.Nodes));
    }
}
