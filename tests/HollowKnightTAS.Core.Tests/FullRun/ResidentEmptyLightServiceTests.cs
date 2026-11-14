using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class ResidentEmptyLightServiceTests
{
    private const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Load()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(root!.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentPresentationTypes", true)!;
    }
    private static object Get(object o, string n) => o.GetType().GetField(n, F)!.GetValue(o)!;
    private static object Call(string n, params object[] a) => Load().GetMethod(n, F)!.Invoke(null, a)!;
    private sealed class Image : IDisposable
    {
        internal readonly IntPtr Owner = Marshal.AllocHGlobal(1024), Dispatch = Marshal.AllocHGlobal(2744);
        internal string Name = "AnyGIService.m_TCSHandle";
        internal Image(int capacity)
        {
            Marshal.Copy(new byte[1024], 0, Owner, 1024);
            Marshal.Copy(new byte[2744], 0, Dispatch, 2744);
            Marshal.WriteInt32(Owner, 8, 7);
            Marshal.WriteIntPtr(Owner, 16, IntPtr.Add(Owner, 248));
            Marshal.WriteInt32(Owner, 248, -1);
            foreach (var a in new[]
            {
                (48,88),(112,9),(216,74)
            }
            )
            {
                Marshal.WriteIntPtr(Owner, a.Item1, IntPtr.Add(Owner, 512));
                Marshal.WriteInt32(Owner, a.Item1 + 8, a.Item2);
                Marshal.WriteInt64(Owner, a.Item1 + 24, capacity * 2L + 1);
            }
            Marshal.WriteInt64(Dispatch, 128, 1L << 7);
            foreach (int i in new[]
            {
                0,1,2,10
            }
            ) Marshal.WriteInt64(Dispatch, 40 + i * 8, 1L << 7);
        }
        internal byte[] Read(IntPtr p, int n)
        {
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return b;
        }
        internal object Capture() => Call("CaptureSceneLightService", Owner, 0x1621248, Dispatch, (Func<IntPtr, int, byte[]>)Read, (Func<IntPtr, string>)(_ => Name));
        public void Dispose()
        {
            Marshal.FreeHGlobal(Owner);
            Marshal.FreeHGlobal(Dispatch);
        }
    }
    [TestMethod]
    public void DifferentEmptyCapacitiesRetainPointersAndTransferAllConsumerBits()
    {
        using var source = new Image(0);
        using var target = new Image(10);
        Marshal.WriteByte(source.Owner, 80, 1);
        Marshal.WriteByte(source.Owner, 104, 1);
        Marshal.WriteByte(source.Owner, 144, 1);
        Marshal.WriteByte(source.Owner, 168, 1);
        Marshal.WriteByte(source.Owner, 208, 1);
        Marshal.WriteByte(source.Owner, 209, 1);
        foreach (int at in new[]
        {
            88,152,176,192
        }
        ) Marshal.WriteInt64(source.Owner, at, unchecked((long)0xfedcba9876543210));
        var s = source.Capture();
        var t = target.Capture();
        Assert.AreEqual("", Get(s, "Unsupported"));
        Assert.AreEqual("", Get(t, "Unsupported"));
        var pointer = Marshal.ReadIntPtr(target.Owner, 48);
        long capacity = Marshal.ReadInt64(target.Owner, 72);
        Call("ApplySceneLightFields", s, t, target.Owner, (Action)(() =>
        {
        }
        ));
        CollectionAssert.AreEqual((byte[])((byte[][])Get(s, "Fields"))[6], ((byte[][])Get(target.Capture(), "Fields"))[6]);
        Assert.AreEqual(pointer, Marshal.ReadIntPtr(target.Owner, 48));
        Assert.AreEqual(capacity, Marshal.ReadInt64(target.Owner, 72));
        Assert.AreEqual(7, Marshal.ReadInt32(target.Owner, 8));
        Assert.AreEqual(1, Marshal.ReadByte(target.Owner, 80));
        Assert.AreEqual(1, Marshal.ReadByte(target.Owner, 168));
        Assert.AreEqual(Marshal.ReadInt64(source.Owner, 88), Marshal.ReadInt64(target.Owner, 88));
    }
    [TestMethod]
    public void EveryNonemptyDirectoryBadRegistrationAndUnknownFlagStaysUnsupported()
    {
        foreach (int countAt in new[]
        {
            28,64,128,232
        }
        )
        {
            using var image = new Image(1);
            Marshal.WriteInt32(image.Owner, countAt, 1);
            Assert.AreNotEqual("", Get(image.Capture(), "Unsupported"));
        }
        using (var image = new Image(1))
        {
            Marshal.WriteInt32(image.Owner, 248, 4);
            Assert.AreNotEqual("", Get(image.Capture(), "Unsupported"));
        }
        using (var image = new Image(0))
        {
            image.Name = "unrelated";
            Assert.AreNotEqual("", Get(image.Capture(), "Unsupported"));
        }
        using (var image = new Image(0))
        {
            Marshal.WriteByte(image.Owner, 104, 2);
            Assert.AreNotEqual("", Get(image.Capture(), "Unsupported"));
        }
        using (var image = new Image(0))
        {
            Marshal.WriteInt64(image.Dispatch, 40 + 10 * 8, 0);
            Assert.AreNotEqual("", Get(image.Capture(), "Unsupported"));
        }
    }
    [TestMethod]
    public void LegacyListenerWithoutTypedPayloadRetainsGap()
    {
        var t = Load();
        var registry = Activator.CreateInstance(t.GetNestedType("LightRegistry", F)!, true)!;
        registry.GetType().GetField("ListenerVtables", F)!.SetValue(registry, new[]
        {
            0x1621248
        }
        );
        CollectionAssert.Contains(new List<string>((IEnumerable<string>)Call("LightRegistryGaps", registry)), "light/manager-listener-native-context");
    }
    [TestMethod]
    public void TargetLogicalRegistrationOrNonemptyCacheRefusesBeforeAnyServiceWrite()
    {
        using var source = new Image(0);
        using var target = new Image(2);
        var saved = source.Capture();
        target.Name = "DifferentGIService.m_TCSHandle";
        var before = target.Read(target.Owner, 248);
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => Call("ApplySceneLightFields", saved, target.Capture(), target.Owner, (Action)(() =>
        {
        }
        )));
        Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        CollectionAssert.AreEqual(before, target.Read(target.Owner, 248));
        target.Name = source.Name;
        Marshal.WriteInt64(target.Owner, 128, 1);
        before = target.Read(target.Owner, 248);
        error = Assert.ThrowsExactly<TargetInvocationException>(() => Call("ApplySceneLightFields", saved, target.Capture(), target.Owner, (Action)(() =>
        {
        }
        )));
        Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
        CollectionAssert.AreEqual(before, target.Read(target.Owner, 248));
    }
}
