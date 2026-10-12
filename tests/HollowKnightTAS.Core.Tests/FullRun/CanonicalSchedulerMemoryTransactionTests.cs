using System;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSchedulerMemoryTransactionTests
{
    private static CanonicalSchedulerCarry Queue(uint stamp, long frame) => new() { SourceNative = 10, Timestamp = stamp,
        Callbacks = new[] { new CanonicalSchedulerCarry.Callback { Order = 0, Owner = "GameManager", Call = "UnityPlayer:8a6c70",
            Cleanup = "UnityPlayer:8a6950", Coroutine = "begin", WakeTimeBits = BitConverter.DoubleToInt64Bits(0.5),
            Frame = frame, Mode = 10, TimestampAge = 2, UserDataPresent = true, Rate = 0, Repeat = 0 } },
        Coroutines = new[] { new CanonicalSchedulerCarry.Coroutine { Identity = "begin", Family = "serial-boundary",
            WaitingFor = "none", ContinueWhenFinished = "none", Done = false, EnumeratorBased = false, HasAsyncOperation = false } } };
    private static (CanonicalSchedulerMemoryTransaction transaction, byte[][] memory) Prepared()
    {
        var plan = CanonicalSchedulerRestorePlan.Prepare(Queue(1, 12), Queue(9, 90));
        // Nonzero bytes expose accidental clearing of links and opaque native
        // state. The transaction must patch exactly 24 registered scalar bytes.
        var manager = Enumerable.Range(0, 80).Select(index => (byte)(index + 1)).ToArray();
        var node = Enumerable.Range(0, 96).Select(index => (byte)(index + 101)).ToArray();
        var transaction = CanonicalSchedulerMemoryTransaction.Prepare(plan, manager, new[] { node });
        return (transaction, new[] { manager, node });
    }
    private static byte[] Read(byte[][] memory, int index, int length)
    { Assert.AreEqual(memory[index].Length, length); return (byte[])memory[index].Clone(); }
    private static void Write(byte[][] memory, int index, int offset, byte[] bytes)
        => Buffer.BlockCopy(bytes, 0, memory[index], offset, bytes.Length);

    [TestMethod]
    public void Complete_readback_preserves_all_links_and_unregistered_bytes_and_consumes_application()
    {
        var (transaction, memory) = Prepared(); var before = memory.Select(bytes => (byte[])bytes.Clone()).ToArray();
        transaction.Apply((index, length) => Read(memory, index, length),
            (index, offset, bytes) => Write(memory, index, offset, bytes), () => { });
        Assert.AreEqual(1u, BitConverter.ToUInt32(memory[0], 72));
        Assert.AreEqual(12L, BitConverter.ToInt64(memory[1], 40));
        Assert.AreEqual(uint.MaxValue, BitConverter.ToUInt32(memory[1], 88));
        for (var index = 0; index < 80; index++)
            if (index < 72 || index >= 76) Assert.AreEqual(before[0][index], memory[0][index]);
        for (var index = 0; index < 96; index++)
            if ((index < 32 || index >= 48) && (index < 88 || index >= 92)) Assert.AreEqual(before[1][index], memory[1][index]);
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Apply((i, n) => Read(memory, i, n),
            (_, _, _) => Assert.Fail("Repeated apply wrote native data."), () => { }));
    }
    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 56)]
    [DataRow(1, 0)]
    public void Any_stale_owner_or_tree_byte_rejects_before_the_first_write(int block, int offset)
    {
        var (transaction, memory) = Prepared(); memory[block][offset] ^= 1;
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Apply((i, n) => Read(memory, i, n),
            (_, _, _) => Assert.Fail("Stale preparation wrote native data."), () => { }));
    }
    [TestMethod]
    public void Pause_or_thread_guard_failure_rejects_without_writing()
    {
        var (transaction, memory) = Prepared();
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Apply((i, n) => Read(memory, i, n),
            (_, _, _) => Assert.Fail("Invalid guard wrote native data."), () => throw new InvalidOperationException()));
    }
    [TestMethod]
    public void Partial_write_failure_consumes_preparation_and_cannot_be_retried()
    {
        var (transaction, memory) = Prepared();
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Apply((i, n) => Read(memory, i, n),
            (i, o, bytes) => { Write(memory, i, o, bytes); throw new InvalidOperationException(); }, () => { }));
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Apply((i, n) => Read(memory, i, n),
            (_, _, _) => Assert.Fail("Failed preparation was reused."), () => { }));
    }
    [TestMethod]
    public void Readback_checks_opaque_bytes_in_addition_to_the_written_scalars()
    {
        var (transaction, memory) = Prepared();
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Apply((i, n) => Read(memory, i, n),
            (i, o, bytes) => { Write(memory, i, o, bytes); memory[1][56] ^= 1; }, () => { }));
    }
}
