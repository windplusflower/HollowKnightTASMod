using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentInputEventQueuesTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const int State = 0x53544154, Delta = 0x444c5441, Ime = 0x494d4543;

    [TestMethod]
    public void BuiltInStateFieldsAndUnalignedDeltaSlicesRoundTripToTheLocalDeviceId()
    {
        var api = new Api();
        var formats = new[] { (1, 0x4b455953, 16), (2, 0x4d4f5553, 32), (3, 0x50454e20, 36),
            (4, 0x544f5543, 36), (5, 0x58494e50, 12) };
        foreach (var (role, format, width) in formats) {
            foreach (bool isDelta in new[] { false, true }) {
                var data = Enumerable.Range(1, isDelta ? width - 3 : width).Select(i => (byte)(0xf0 - i)).ToArray();
                var payload = BitConverter.GetBytes(format);
                if (isDelta) payload = payload.Concat(BitConverter.GetBytes(1)).ToArray();
                var record = Event(isDelta ? Delta : State, payload.Concat(data).ToArray(), 91);
                var rows = api.Parse(record, 1, false, role, 91);
                Assert.AreEqual(role, Get<int>(rows.GetValue(0)!, "Role"));
                Assert.IsTrue(Get<uint[]>(rows.GetValue(0)!, "Fields").Length > 1);
                var rebound = api.Pack(rows, false, role, 555);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)555), 0, record, 6, 2);
                CollectionAssert.AreEqual(record, rebound);
            }
        }
    }

    [TestMethod]
    public void EmbeddedImeNulsSurviveWhileCountsAlignmentAndOrderAreValidated()
    {
        var api = new Api();
        var ime = Event(Ime, new byte[] { 1, 0, 0, 0, 0, 0 }, 3);
        var text = Event(0x54455854, BitConverter.GetBytes(0x1f642u), 3);
        var main = ime.Concat(new byte[] { 0x35, 0xae }).Concat(text).ToArray();
        var rows = api.Parse(main, 2, false, 1, 3);
        CollectionAssert.AreEqual(new uint[] { 1, 0 }, Get<uint[]>(rows.GetValue(0)!, "Fields"));
        CollectionAssert.AreEqual(new byte[] { 0x35, 0xae }, Get<byte[]>(rows.GetValue(1)!, "Alignment"));
        CollectionAssert.AreEqual(main, api.Pack(rows, false, 1, 3));
        api.Reject(() => api.Parse(main, 1, false, 1, 3));
        api.Reject(() => api.Parse(main.Take(main.Length - 1).ToArray(), 2, false, 1, 3));
        ime[20] = 2;
        var bad = api.Parse(ime, 1, false, 1, 3);
        Assert.AreEqual(32, Get<byte[]>(bad.GetValue(0)!, "UnsupportedDigest").Length);
        api.Reject(() => api.Pack(bad, false, 1, 3));
    }

    [TestMethod]
    public void WrappedPublishedRecordsRequireExactOuterLengthsTailCountAndNoReservation()
    {
        var api = new Api();
        var ime = Event(Ime, new byte[] { 1, 0, 0, 0, 0x3d, 0xd8 }, 3);
        var occupied = BitConverter.GetBytes(ime.Length).Concat(ime).Concat(new byte[] { 0xab, 0xcd }).ToArray();
        var rows = api.Parse(occupied, 1, true, 1, 3);
        Assert.IsFalse(Get<bool>(rows.GetValue(0)!, "RingPaddingZero"));
        var canonical = (byte[])occupied.Clone();
        canonical[30] = 0; canonical[31] = 0;
        CollectionAssert.AreEqual(canonical, api.Pack(rows, true, 1, 3));
        var q = api.Queue();
        Set(q, "Background", rows); Set(q, "ReadHead", 0x7ffc); Set(q, "WriteHead", 28); Set(q, "ReadTail", 28);
        api.Call("ValidateEvents", q);
        var desired = (byte[])api.Call("EventImage", q);
        Assert.IsTrue(api.Gaps(q, true).Any(s => s.EndsWith("ring-storage-canon", StringComparison.Ordinal)));
        Set(q, "Background", api.Parse(canonical, 1, true, 1, 3));
        CollectionAssert.AreEqual(desired, (byte[])api.Call("EventImage", q));
        Assert.IsFalse(api.Gaps(q, true).Any(s => s.EndsWith("ring-storage-canon", StringComparison.Ordinal)));
        Set(q, "RingUnusedZero", false);
        Assert.IsTrue(api.Gaps(q, true).Any(s => s.EndsWith("ring-storage-canon", StringComparison.Ordinal)));
        Assert.IsFalse(api.Gaps(q).Any(s => s.EndsWith("ring-storage-canon", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(desired, (byte[])api.Call("EventImage", q));
        Set(q, "WriteHead", 32);
        api.Reject(() => api.Call("ValidateEvents", q));
        Set(q, "ReadTail", 32);
        api.Reject(() => api.Call("ValidateEvents", q));
        occupied[0] = 25;
        api.Reject(() => api.Parse(occupied, 1, true, 1, 3));
    }

    [TestMethod]
    public void UnresolvedFormatMissingDeviceAndExternalCallbackStorageRemainHardGaps()
    {
        var api = new Api();
        var q = api.Queue();
        Assert.IsTrue(api.Gaps(q).Contains("native-input/accepted-event-producer-and-consumer-admission"));
        var record = Event(State, BitConverter.GetBytes(0x48494420).Concat(BitConverter.GetBytes(0x12345678abcdUL)).ToArray(), 700);
        var rows = api.Parse(record, 1, false, 1, 3);
        var row = rows.GetValue(0)!;
        Assert.AreEqual(0, Get<int>(row, "Role"));
        Assert.AreEqual((ushort)700, Get<ushort>(row, "UnresolvedDevice"));
        Assert.AreEqual(0, Get<uint[]>(row, "Fields").Length);
        Assert.AreEqual(32, Get<byte[]>(row, "UnsupportedDigest").Length);
        Set(q, "Main", rows); Set(q, "Used", record.Length); Set(q, "MainExternal", true);
        Set(q, "NonzeroMainTailDigest", new byte[32]);
        var gaps = api.Gaps(q);
        foreach (var suffix in new[] { "device-binding", "unresolved-payload", "main-callback-storage", "main-untyped-capacity-tail" })
            Assert.IsTrue(gaps.Any(s => s.EndsWith(suffix, StringComparison.Ordinal)));
        api.Reject(() => api.Pack(rows, false, 1, 3));
        var first = (byte[])api.Call("EventImage", q);
        Get<byte[]>(row, "UnsupportedDigest")[0] ^= 1;
        Assert.IsFalse(first.SequenceEqual((byte[])api.Call("EventImage", q)));
        first = (byte[])api.Call("EventImage", q);
        Set(q, "RunningId", 20UL);
        Assert.IsFalse(first.SequenceEqual((byte[])api.Call("EventImage", q)));
    }

    private static byte[] Event(int type, byte[] payload, ushort device)
    {
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write(type); w.Write(checked((ushort)(20 + payload.Length))); w.Write(device);
        w.Write(0x7ff80000ffffffffUL); w.Write(unchecked((int)0xe1234567)); w.Write(payload);
        return output.ToArray();
    }
    private static T Get<T>(object obj, string field) => (T)obj.GetType().GetField(field, All)!.GetValue(obj)!;
    private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, All)!.SetValue(obj, value);
    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            type = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
                .GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeInputState", true)!;
        }
        internal object Call(string name, params object[] args) => type.GetMethod(name, All)!.Invoke(null, args)!;
        internal object Queue()
        {
            var q = Activator.CreateInstance(type.GetNestedType("EventQueues", All)!, true)!;
            Set(q, "Present", true); Set(q, "Capacity", 32768); Set(q, "MainSize", 8192); Set(q, "MainCapacity", 8192);
            Set(q, "MainAllocated", true); Set(q, "MainLabel", 43);
            return q;
        }
        internal string[] Gaps(object q, bool canonical = false) => ((IEnumerable<string>)Call("EventGaps", q, canonical)).ToArray();
        internal Array Parse(byte[] bytes, int count, bool ring, int role, int id)
        {
            var ids = new int[9]; ids[role] = id;
            return (Array)Call("ParseAccepted", bytes, count, ring, ids);
        }
        internal byte[] Pack(Array rows, bool ring, int role, int id)
        {
            var ids = new int[9]; ids[role] = id;
            return (byte[])Call("Repack", rows, ring, ids);
        }
        internal void Reject(Action action)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(action);
            Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
        }
    }
}
