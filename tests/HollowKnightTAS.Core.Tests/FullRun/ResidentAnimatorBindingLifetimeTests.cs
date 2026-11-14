using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorBindingLifetimeTests
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
    private static object? Call(Type type, string method, params object[] args) => type.GetMethod(method, Flags)!.Invoke(null, args);
    private static object Member(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags)!.Invoke(value, args)!;
    private static void Invalid(Action action)
        => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private static IList Writes(Type type)
        => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetNestedType("PendingWrite", Flags)!))!;
    private static void ArrayField(object owner, string field, params object[] values)
    {
        var info = owner.GetType().GetField(field, Flags)!;
        var array = Array.CreateInstance(info.FieldType.GetElementType()!, values.Length);
        for (int index = 0; index < values.Length; index++) array.SetValue(values[index], index);
        info.SetValue(owner, array);
    }

    private sealed class BindingImage : IDisposable
    {
        internal readonly IntPtr Header = Marshal.AllocHGlobal(256);
        internal readonly object Graph;
        internal readonly object Data;
        internal readonly object Node;
        internal IntPtr Transform => IntPtr.Add(Header, 128);
        internal IntPtr GameObject => IntPtr.Add(Header, 160);
        internal BindingImage(Type type, int skeleton, int rootId, bool known = true)
        {
            Marshal.Copy(new byte[256], 0, Header, 256);
            Marshal.WriteInt64(Header, 0, skeleton);
            if (skeleton != 0)
            {
                Marshal.WriteIntPtr(Header, 8, IntPtr.Add(Header, 64));
                Marshal.WriteIntPtr(Header, 64, Transform);
            }
            Marshal.WriteInt32(Header, 16, rootId);
            Marshal.WriteInt64(Header, 24, 1);
            Marshal.WriteIntPtr(Header, 32, IntPtr.Add(Header, 80));
            Marshal.WriteIntPtr(Header, 80, Transform);
            Marshal.WriteInt32(GameObject, 8, rootId);
            string Root(int id)
            {
                if (skeleton == 0) throw new InvalidOperationException("inactive root word must not be interpreted");
                return id == 0 ? "" : known && id == rootId ? "root" : "#unresolved-native-object";
            }
            Graph = Activator.CreateInstance(type.GetNestedType("AvatarGraph", Flags)!, Flags, null, new object[] {
                New(type, "MemoryReader"), new Func<IntPtr, string>(pointer => pointer == Transform ? "transform" : ""), new Func<int, string>(Root) }, null)!;
            Member(Graph, "Retained", Header, Enum.Parse(type.GetNestedType("AvatarKind", Flags)!, "Binding"));
            Node = ((IList)Get(Graph, "Nodes"))[0]!;
            Data = New(type, "Data");
            ArrayField(Data, "AvatarNodes", Node);
            var row = New(type, "Row");
            var dataset = ((Array)Get(row, "Datasets")).GetValue(0)!;
            Set(dataset, "Edges", new[] { -1, -1, -1, -1, -1, 0 });
            ArrayField(Data, "Rows", row);
        }
        internal IList Prepare(Type type, BindingImage source)
        {
            var writes = Writes(type);
            IntPtr Resolve(string key, bool root)
            {
                if (root && ((int[])Get(Node, "Extents"))[0] == 0)
                    throw new InvalidOperationException("inactive root must not resolve");
                return root ? GameObject : Transform;
            }
            Member(Graph, "Prepare", Get(source.Data, "AvatarNodes"), writes,
                new Func<string, bool, IntPtr>(Resolve), new HashSet<int>(), new Func<string, int>(_ => throw new InvalidOperationException()));
            return writes;
        }
        public void Dispose() => Marshal.FreeHGlobal(Header);
    }

    [TestMethod]
    public void ZeroSkeletonKeepsDifferentJunkRootWordsAndStillRestoresExposedTransforms()
    {
        var type = Load();
        using var source = new BindingImage(type, 0, 123456);
        using var target = new BindingImage(type, 0, -98765);
        var sourceFields = (byte[][])Get(source.Node, "Fields");
        sourceFields[0][0] = 37;
        Call(type, "ValidateAvatar", source.Data);
        foreach (var write in target.Prepare(type, source)) Member(write!, "Apply");
        Assert.AreEqual(-98765, Marshal.ReadInt32(target.Header, 16));
        Assert.AreEqual(target.Transform, Marshal.ReadIntPtr(target.Header, 80));
        Assert.AreEqual((byte)37, Marshal.ReadByte(target.Header, 88));
        Assert.AreEqual("", ((string[])Get(source.Node, "References"))[0]);
        ((string[])Get(source.Node, "References"))[0] = "forged/root";
        Invalid(() => Call(type, "ValidateAvatar", source.Data));
        Invalid(() => target.Prepare(type, source));
    }

    [TestMethod]
    public void LiveSkeletonRootIdStillRebindsAndUnknownIdsRemainRejected()
    {
        var type = Load();
        using var source = new BindingImage(type, 1, 143);
        using var target = new BindingImage(type, 1, 352);
        foreach (var write in target.Prepare(type, source)) Member(write!, "Apply");
        Assert.AreEqual(352, Marshal.ReadInt32(target.Header, 16));
        using var unknown = new BindingImage(type, 1, 777, false);
        Invalid(() => target.Prepare(type, unknown));
    }

    private static object Policy(Type type, int main, int sampling)
    {
        var value = New(type, "Residual");
        Set(value, "SetLabels", new[] { 30, 30 });
        Set(value, "DestroyRvas", new[] { main, sampling });
        Set(value, "AllocatorRvas", Enumerable.Repeat(0x15d5b60, 6).ToArray());
        Set(value, "Labels", Enumerable.Repeat(30, 6).ToArray());
        Set(value, "PlaybackVtable", 0x15d5b70);
        ArrayField(value, "Arrays", Enumerable.Range(0, 6).Select(_ => New(type, "Storage")).ToArray());
        return value;
    }

    [TestMethod]
    public void KnownSetDestructorsMatchEachDomainAndNeverRewriteTargetCallbacks()
    {
        var type = Load();
        foreach (var pair in new[] { (0, 0), (0xcb660, 0), (0, 0xcb660), (0xcb660, 0xcb660) })
        {
            var source = Policy(type, pair.Item1, pair.Item2);
            var target = Policy(type, pair.Item1, pair.Item2);
            Call(type, "RequireSetPoliciesMatch", source, target);
            var row = New(type, "Row");
            Set(row, "Fields", new[] { 2, 1, 2, 12, 157, 4, 16, 14, 4 }.Select(size => new byte[size]).ToArray());
            var gaps = (IEnumerable<string>)Call(type, "ResidualGaps", source, row)!;
            Assert.IsFalse(gaps.Contains("unbound-native-allocator-function"));
        }
        Invalid(() => Call(type, "RequireSetPoliciesMatch", Policy(type, 0xcb660, 0), Policy(type, 0, 0)));
        Invalid(() => Call(type, "RequireSetPoliciesMatch", Policy(type, 0xcb661, 0), Policy(type, 0xcb661, 0)));
        var wrongLabel = Policy(type, 0xcb660, 0);
        Set(wrongLabel, "SetLabels", new[] { 31, 30 });
        Invalid(() => Call(type, "RequireSetPoliciesMatch", wrongLabel, wrongLabel));

        var memory = Marshal.AllocHGlobal(1696);
        try
        {
            Marshal.Copy(new byte[1696], 0, memory, 1696);
            var callback = new IntPtr(0x100000 + 0xcb660);
            Marshal.WriteIntPtr(memory, 536, callback);
            var viewType = type.GetNestedType("ResidualView", Flags)!;
            var view = RuntimeHelpers.GetUninitializedObject(viewType);
            Set(view, "Owner", memory);
            Set(view, "Data", Policy(type, 0xcb660, 0));
            var writes = Writes(type);
            Member(view, "Prepare", Policy(type, 0xcb660, 0), writes, new Action(() => { }));
            foreach (var write in writes) Member(write!, "Apply");
            Assert.AreEqual(callback, Marshal.ReadIntPtr(memory, 536));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [TestMethod]
    public void MatchingPolicyCannotHideDifferentLiveSetOwnerPresence()
    {
        var type = Load();
        var source = New(type, "Evaluation");
        Set(Get(source, "Backing"), "SetPresent", true);
        var view = RuntimeHelpers.GetUninitializedObject(type.GetNestedType("EvalView", Flags)!);
        Set(view, "Data", New(type, "Evaluation"));
        Invalid(() => Member(view, "Prepare", source, null!, null!, Writes(type)));
    }

}
