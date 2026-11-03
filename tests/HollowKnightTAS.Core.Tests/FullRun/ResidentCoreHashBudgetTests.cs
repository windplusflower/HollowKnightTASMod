using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentCoreHashBudgetTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Put(byte[] bytes, int offset, uint value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Flags)!.GetValue(value)!;
    private static object Decode(byte[] header, byte[] slots, int stride, int maximum = 100000)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        var runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
        var type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentCoreHashTable", true)!;
        return type.GetMethod("Decode", Flags)!.Invoke(null, new object[] { header, slots, stride, maximum })!;
    }
    private static void Reject(byte[] header, byte[] slots, int stride, int maximum = 100000)
    {
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => Decode(header, slots, stride, maximum));
        Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
    }
    private static (byte[] Header, byte[] Slots) Table(int stride, int live = 0, int deleted = 0)
    {
        const int capacity = 64;
        var header = new byte[32]; Put(header, 8, (uint)((capacity - 1) * (stride == 72 ? 8 : 32)));
        Put(header, 12, (uint)live); Put(header, 20, (uint)(42 - live - deleted));
        var slots = new byte[capacity * stride];
        for (var index = 0; index < capacity; index++) Put(slots, index * stride, uint.MaxValue);
        for (var index = 0; index < live; index++) { Put(slots, index * stride, (uint)(4 * index)); slots[index * stride + stride - 1] = (byte)(index + 1); }
        for (var index = live; index < live + deleted; index++) Put(slots, index * stride, uint.MaxValue - 1);
        return (header, slots);
    }
    [TestMethod]
    [DataRow(72)] [DataRow(32)]
    public void SixtyFourEmptyPhysicalSlotsHaveFortyTwoUnusedInsertions(int stride)
    {
        var (header, slots) = Table(stride); var result = Decode(header, slots, stride);
        Assert.AreEqual(64, Field<int>(result, "Capacity")); Assert.AreEqual(0, Field<int>(result, "Deleted"));
        Assert.AreEqual(0, Field<byte[][]>(result, "Live").Length);
        Put(header, 20, 64); Reject(header, slots, stride); // Physical empties cannot replace the native load budget.
    }
    [TestMethod]
    [DataRow(72)] [DataRow(32)]
    public void TombstoneReusePreservesBudgetAndVirginInsertionConsumesIt(int stride)
    {
        var (header, slots) = Table(stride, 3, 2); var result = Decode(header, slots, stride);
        Assert.AreEqual(2, Field<int>(result, "Deleted"));
        var rows = Field<byte[][]>(result, "Live"); Assert.AreEqual(3, rows.Length);
        for (var index = 0; index < rows.Length; index++) CollectionAssert.AreEqual(slots.Skip(index * stride).Take(stride).ToArray(), rows[index]);
        Put(slots, 3 * stride, 12); Put(header, 12, 4); // Reuse a tombstone: U stays 37.
        result = Decode(header, slots, stride); Assert.AreEqual(1, Field<int>(result, "Deleted")); Assert.AreEqual(4, Field<byte[][]>(result, "Live").Length);
        Put(slots, 5 * stride, 20); Put(header, 12, 5); Reject(header, slots, stride);
        Put(header, 20, 36); result = Decode(header, slots, stride);
        Assert.AreEqual(1, Field<int>(result, "Deleted")); Assert.AreEqual(5, Field<byte[][]>(result, "Live").Length);
    }
    [TestMethod]
    [DataRow(72)] [DataRow(32)]
    public void ForgedBudgetLiveCountAndDeletedMarkersAreRejected(int stride)
    {
        foreach (var budget in new uint[] { 36, 38, uint.MaxValue }) { var table = Table(stride, 3, 2); Put(table.Header, 20, budget); Reject(table.Header, table.Slots, stride); }
        var (header, slots) = Table(stride, 3, 2); Put(header, 12, 2); Reject(header, slots, stride);
        (header, slots) = Table(stride, 3, 2); Put(slots, 3 * stride, uint.MaxValue); Reject(header, slots, stride);
    }
    [TestMethod]
    [DataRow(72)] [DataRow(32)]
    public void ZeroMaskUsesExactlyOneEmptySentinelAndNoOccupancy(int stride)
    {
        var header = new byte[32]; var slots = new byte[sizeof(uint)]; Put(slots, 0, uint.MaxValue);
        var result = Decode(header, slots, stride); Assert.AreEqual(0, Field<int>(result, "Capacity"));
        Assert.AreEqual(0, Field<int>(result, "Deleted")); Assert.AreEqual(0, Field<byte[][]>(result, "Live").Length);
        Reject(header, Array.Empty<byte>(), stride); Reject(header, slots.Concat(slots).ToArray(), stride);
        Put(slots, 0, uint.MaxValue - 1); Reject(header, slots, stride);
        Put(slots, 0, 0); Reject(header, slots, stride);
        Put(slots, 0, uint.MaxValue); Put(header, 12, 1); Reject(header, slots, stride);
        Put(header, 12, 0); Put(header, 20, 1); Reject(header, slots, stride);
    }
    [TestMethod]
    [DataRow(72)] [DataRow(32)]
    public void ActualNodeStrideScaledMaskAndExactStorageLengthAreRequired(int stride)
    {
        var (header, slots) = Table(stride); var scale = stride == 72 ? 8U : 32U;
        Put(header, 8, 63 * scale + 1); Reject(header, slots, stride);
        Put(header, 8, 62 * scale); Reject(header, slots.Take(63 * stride).ToArray(), stride); // Capacity must be a power of two.
        (header, slots) = Table(stride); Reject(header, slots[..^1], stride); Reject(header, slots.Concat(new byte[stride]).ToArray(), stride);
        Reject(header, slots, stride, 63); Reject(header[..^1], slots, stride);
        Reject(header, slots, stride == 72 ? 32 : 72);
    }
}
