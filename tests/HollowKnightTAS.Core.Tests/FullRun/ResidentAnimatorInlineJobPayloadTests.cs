using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorInlineJobPayloadTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static Type Load()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")
            ?? Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState", true)!;
    }
    private static object New(Type type, string name) => Activator.CreateInstance(type.GetNestedType(name, Flags)!, true)!;
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags)!.GetValue(value)!;
    private static void Set(object value, string field, object? data) => value.GetType().GetField(field, Flags)!.SetValue(value, data);
    private static object? Call(Type type, string method, params object[] args) => type.GetMethod(method, Flags)!.Invoke(null, args);
    private static void Invalid(Action action)
        => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private static IList Writes(Type type)
        => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetNestedType("PendingWrite", Flags)!))!;

    private sealed class Image : IDisposable
    {
        internal readonly IntPtr Graph = Marshal.AllocHGlobal(1280);
        internal IntPtr Owner => IntPtr.Add(Graph, 256);
        internal IntPtr Data => IntPtr.Add(Owner, 512);
        internal Image()
        {
            Marshal.Copy(new byte[1280], 0, Graph, 1280);
            Marshal.WriteIntPtr(Graph, 56, IntPtr.Add(Owner, 8));
            Marshal.WriteIntPtr(Graph, 64, IntPtr.Add(Owner, 8));
            Marshal.WriteInt32(Graph, 44, 1);
            Marshal.WriteInt64(Graph, 160, -1);
            foreach (int offset in new[] { 72, 120 }) Marshal.WriteInt64(Graph, offset + 24, 1);
            Marshal.WriteIntPtr(Owner, 8, IntPtr.Add(Graph, 56));
            Marshal.WriteIntPtr(Owner, 16, IntPtr.Add(Graph, 56));
            Marshal.WriteIntPtr(Owner, 32, Graph);
            var handle = IntPtr.Add(Graph, 1040);
            Marshal.WriteIntPtr(Owner, 40, handle);
            Marshal.WriteIntPtr(handle, 40, Owner);
            Marshal.WriteInt32(handle, 32, 2);
            Marshal.WriteInt64(Owner, 144, -1);
            Marshal.WriteIntPtr(Owner, 184, Data);
        }
        internal object View(Type type, int vtable = 0x15d8b80)
            => Activator.CreateInstance(type.GetNestedType("GraphView", Flags)!, Flags, null, new object[] {
                0, Graph, New(type, "MemoryReader"), new Func<IntPtr, int>(_ => vtable), new Dictionary<IntPtr, int>() }, null)!;
        public void Dispose() => Marshal.FreeHGlobal(Graph);
    }

    [TestMethod]
    public void KnownFactoriesRequireExactNativeTypeExtentNoReflectionAndRelativeEndAlias()
    {
        var type = Load();
        foreach (var entry in new[] { (0x15d9380, 368), (0x15d97a8, 368), (0x15d8aa8, 384), (0x15d9608, 288), (0x15d8b80, 512), (0x15d8f08, 456) })
        {
            var owner = new IntPtr(0x10000);
            var end = IntPtr.Add(owner, entry.Item2);
            Assert.AreEqual(entry.Item2, Call(type, "CaptureEmptyInlineJobDataOffset", entry.Item1, owner, IntPtr.Zero, end));
            Assert.IsNull(Call(type, "CaptureEmptyInlineJobDataOffset", entry.Item1, owner, IntPtr.Zero, IntPtr.Add(end, 1)));
            Assert.IsNull(Call(type, "CaptureEmptyInlineJobDataOffset", entry.Item1, owner, new IntPtr(1), end));
            Assert.IsNull(Call(type, "CaptureEmptyInlineJobDataOffset", -1, owner, IntPtr.Zero, end));
            Assert.IsNull(Call(type, "CaptureEmptyInlineJobDataOffset", entry.Item1, owner, IntPtr.Zero, IntPtr.Zero));
        }
    }

    [TestMethod]
    public void GraphRestorePreservesTargetLocalInlinePointerAtDifferentNativeAddress()
    {
        var type = Load();
        using var source = new Image();
        using var target = new Image();
        var saved = Get(source.View(type), "Data");
        var view = target.View(type);
        var node = ((Array)Get(saved, "Nodes")).GetValue(0)!;
        Assert.AreEqual(true, Get(node, "JobData"));
        Assert.AreEqual(512, Get(node, "EmptyInlineJobDataOffset"));
        Assert.AreNotEqual(source.Data, target.Data);
        var gaps = (IEnumerable<string>)Call(type, "GraphGaps", saved)!;
        Assert.IsFalse(gaps.Contains("playable-native-job-script-or-property-producer"));
        var writes = Writes(type);
        view.GetType().GetMethod("Prepare", Flags)!.Invoke(view, new[] { saved, writes });
        foreach (var write in writes) write!.GetType().GetMethod("Apply", Flags)!.Invoke(write, null);
        Assert.AreEqual(target.Data, Marshal.ReadIntPtr(target.Owner, 184));
        Assert.AreEqual(source.Data, Marshal.ReadIntPtr(source.Owner, 184));
    }

    [TestMethod]
    public void LegacyBoolOnlyUnknownTypeReflectionAndWrongOffsetRemainRejected()
    {
        var type = Load();
        using var source = new Image();
        using var target = new Image();
        var saved = Get(source.View(type), "Data");
        var sourceNode = ((Array)Get(saved, "Nodes")).GetValue(0)!;
        Set(sourceNode, "EmptyInlineJobDataOffset", null);
        Assert.IsTrue(((IEnumerable<string>)Call(type, "GraphGaps", saved)!).Contains("playable-native-job-script-or-property-producer"));
        Invalid(() => target.View(type).GetType().GetMethod("Prepare", Flags)!.Invoke(target.View(type), new[] { saved, Writes(type) }));
        Set(sourceNode, "EmptyInlineJobDataOffset", 512);
        foreach (var mutation in new Action[] {
            () => Marshal.WriteIntPtr(target.Owner, 184, IntPtr.Add(target.Data, 1)),
            () => { Marshal.WriteIntPtr(target.Owner, 184, target.Data); Marshal.WriteIntPtr(target.Owner, 176, new IntPtr(1)); }
        })
        {
            mutation();
            var view = target.View(type);
            Invalid(() => view.GetType().GetMethod("Prepare", Flags)!.Invoke(view, new[] { saved, Writes(type) }));
        }
        Marshal.WriteIntPtr(target.Owner, 176, IntPtr.Zero);
        var unknown = Get(target.View(type, 0x16a23b8), "Data");
        Assert.IsTrue(((IEnumerable<string>)Call(type, "GraphGaps", unknown)!).Contains("playable-native-job-script-or-property-producer"));
        Set(sourceNode, "EmptyInlineJobDataOffset", 511);
        Invalid(() => Call(type, "ValidateTopology", saved));
    }
}
