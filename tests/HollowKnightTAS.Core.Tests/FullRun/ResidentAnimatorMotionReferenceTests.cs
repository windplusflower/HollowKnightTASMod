using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorMotionReferenceTests
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

    private static object New(Type type, string name) => Activator.CreateInstance(type.GetNestedType(name, Flags)!, true)!;
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags)!.GetValue(value)!;
    private static void Set(object value, string field, object? data) => value.GetType().GetField(field, Flags)!.SetValue(value, data);
    private static object Call(Type type, string method, params object[] args) => type.GetMethod(method, Flags)!.Invoke(null, args)!;
    private static object Member(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags)!.Invoke(value, args)!;
    private static void Invalid(Action action)
        => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);

    private static object Graph(Type type)
        => Activator.CreateInstance(type.GetNestedType("AvatarGraph", Flags)!, Flags, null, new object[] {
            New(type, "MemoryReader"), new Func<IntPtr, string>(_ => throw new InvalidOperationException()),
            new Func<int, string>(_ => throw new InvalidOperationException()) }, null)!;

    private static Array Nodes(Type type, object graph)
    {
        var list = (IList)Get(graph, "Nodes");
        var array = Array.CreateInstance(type.GetNestedType("AvatarNode", Flags)!, list.Count);
        list.CopyTo(array, 0);
        return array;
    }

    private static void ArrayField(object owner, string name, params object[] values)
    {
        var field = owner.GetType().GetField(name, Flags)!;
        var array = Array.CreateInstance(field.FieldType.GetElementType()!, values.Length);
        for (int index = 0; index < values.Length; index++) array.SetValue(values[index], index);
        field.SetValue(owner, array);
    }

    private static object OwnedData(Type type, object graph, bool valid)
    {
        var data = New(type, "Data");
        Set(data, "AvatarNodes", Nodes(type, graph));
        ArrayField(data, "Rows", New(type, "Row"));
        var topology = New(type, "Topology");
        Set(topology, "Owner", 0);
        var node = New(type, "Node");
        var clip = New(type, "ClipState");
        Set(clip, "Constant", "asset/clip");
        Set(clip, "Caches", new[] { -1, -1, 0, -1, -1, -1, -1, -1, -1 });
        ((byte[][])Get(clip, "Fields"))[0][4] = valid ? (byte)1 : (byte)0;
        Set(node, "Clip", clip);
        ArrayField(topology, "Nodes", node);
        ArrayField(data, "Graphs", topology);
        return data;
    }

    private static IList Prepare(Type type, object source, object target)
    {
        var writes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetNestedType("PendingWrite", Flags)!))!;
        Member(target, "Prepare", Nodes(type, source), writes,
            new Func<string, bool, IntPtr>((_, _) => throw new InvalidOperationException()), new HashSet<int>(),
            new Func<string, int>(_ => throw new InvalidOperationException()));
        return writes;
    }

    [TestMethod]
    public void UnprocessedReferenceKeepsAllocatedAliasWithoutReadingOrWritingItsBytes()
    {
        var type = Load();
        var source = Graph(type);
        var target = Graph(type);
        // Invalid addresses prove the retired payload is not followed.
        Assert.AreEqual(0, Member(source, "MotionReference", new IntPtr(1), false));
        Assert.AreEqual(0, Member(source, "MotionReference", new IntPtr(1), false));
        Assert.AreEqual(0, Member(target, "MotionReference", new IntPtr(2), false));
        Assert.AreEqual(1, ((IList)Get(source, "Nodes")).Count);
        var node = Nodes(type, source).GetValue(0)!;
        Assert.AreEqual(false, Get(node, "MotionReferenceValid"));
        Assert.AreEqual(0, ((byte[][])Get(node, "Fields")).Length);
        Call(type, "ValidateAvatar", OwnedData(type, source, false));
        Assert.AreEqual(0, Prepare(type, source, target).Count);
        Invalid(() => Member(source, "MotionReference", new IntPtr(1), true));
    }

    [TestMethod]
    public void ProcessedReferenceRetainsFullPayloadAndStrictMaskValidation()
    {
        var type = Load();
        var memory = Marshal.AllocHGlobal(256);
        try
        {
            var bytes = new byte[256];
            bytes[240] = 1;
            bytes[241] = 0;
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var graph = Graph(type);
            var kind = Enum.Parse(type.GetNestedType("AvatarKind", Flags)!, "MotionReference");
            Member(graph, "Retained", memory, kind); // Existing helper defaults to valid.
            var node = Nodes(type, graph).GetValue(0)!;
            Assert.AreEqual(true, Get(node, "MotionReferenceValid"));
            Assert.AreEqual(242, ((byte[][])Get(node, "Fields"))[0].Length);
            Call(type, "ValidateAvatar", OwnedData(type, graph, true));
            ((byte[][])Get(node, "Fields"))[0][240] = 47;
            var gaps = (IEnumerable<string>)Call(type, "AvatarNodeScalarGaps", node);
            CollectionAssert.Contains(new List<string>(gaps), "noncanonical-animator-bool:MotionXReference:+240=47");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [TestMethod]
    public void SourceTargetValidityAndLegacyOrForgedPayloadAreRejected()
    {
        var type = Load();
        var source = Graph(type);
        var target = Graph(type);
        Member(source, "MotionReference", new IntPtr(1), false);
        Member(target, "MotionReference", new IntPtr(2), false);
        var node = Nodes(type, source).GetValue(0)!;
        var data = OwnedData(type, source, false);
        Set(node, "MotionReferenceValid", true);
        Invalid(() => Prepare(type, source, target));
        Invalid(() => Call(type, "ValidateAvatar", data));
        Set(node, "MotionReferenceValid", null);
        Set(node, "Fields", new[] { new byte[242] });
        Invalid(() => Call(type, "ValidateAvatar", data));
        Set(node, "MotionReferenceValid", false);
        Invalid(() => Call(type, "ValidateAvatar", data));
    }
    [TestMethod]
    public void StateMessagesAcceptAllKnownNonzeroEventMasksAndSeparateInvalidSentinel()
    {
        var type = Load();
        var runtime = New(type, "ControllerRuntime");
        Set(runtime, "AnimationFlags", new byte[5]);
        var workspace = New(type, "ControllerWorkspace");
        var machine = New(type, "MachineWork");
        Set(machine, "OutputFlags", new byte[4]);
        ArrayField(workspace, "Machines", machine);
        Set(runtime, "Workspace", workspace);
        bool HasMessageGap(uint message)
        {
            var bytes = new byte[12];
            for (int offset = 0; offset < bytes.Length; offset += 4)
                BitConverter.GetBytes(message).CopyTo(bytes, offset);
            Set(machine, "Messages", bytes);
            var gaps = (IEnumerable<string>)Call(type, "RuntimeScalarGaps", runtime);
            foreach (var gap in gaps)
            {
                if (gap.Contains("StateMachineMessage")) return true;
            }
            return false;
        }
        for (uint message = 1; message <= 127; message++)
            Assert.IsFalse(HasMessageGap(message), "Known event mask " + message);
        Assert.IsFalse(HasMessageGap(128));
        foreach (uint message in new uint[] { 0, 129, 255, 256, uint.MaxValue })
            Assert.IsTrue(HasMessageGap(message), "Unknown or mixed sentinel " + message);
    }

}
