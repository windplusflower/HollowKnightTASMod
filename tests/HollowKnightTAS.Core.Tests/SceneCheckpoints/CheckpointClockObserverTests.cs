using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.SceneCheckpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.SceneCheckpoints;

[TestClass]
public sealed class CheckpointClockObserverTests
{
    private static CheckpointClockObserverState State() => new(true, false, 1476, 1477, 1477);

    [TestMethod]
    public void Hco1_has_exact_fixed_offsets_and_retains_source_cached_frame_separately()
    {
        var state = State(); var bytes = state.Encode();
        Assert.AreEqual(32, bytes.Length); Assert.AreEqual(CheckpointClockObserverState.EncodedBytes, bytes.Length);
        Assert.AreEqual(0x314f4348U, BitConverter.ToUInt32(bytes, 0)); Assert.AreEqual(1, BitConverter.ToInt32(bytes, 4));
        Assert.AreEqual(32, BitConverter.ToInt32(bytes, 8)); Assert.AreEqual(0, BitConverter.ToInt32(bytes, 12));
        Assert.AreEqual((byte)1, bytes[16]); Assert.AreEqual((byte)0, bytes[17]); Assert.AreEqual((ushort)0, BitConverter.ToUInt16(bytes, 18));
        Assert.AreEqual(1476, BitConverter.ToInt32(bytes, 20)); Assert.AreEqual(1477, BitConverter.ToInt32(bytes, 24));
        Assert.AreEqual(1477, BitConverter.ToInt32(bytes, 28));
        var decoded = CheckpointClockObserverState.Decode(bytes); state.RequireReadback(decoded);
        Assert.AreEqual(1476, decoded.LastUnityFrameCount); Assert.AreEqual(1477, decoded.CaptureUnityFrameCount);
        CollectionAssert.AreEqual(bytes, decoded.Encode());
    }

    [TestMethod]
    public void Handles_initial_and_maximum_observer_values_without_reconstructing_a_cache()
    {
        var initial = new CheckpointClockObserverState(false, false, 0, 0, 0);
        initial.RequireReadback(CheckpointClockObserverState.Decode(initial.Encode()));
        var maximum = new CheckpointClockObserverState(true, true, int.MaxValue, int.MaxValue - 1, int.MaxValue);
        maximum.RequireReadback(CheckpointClockObserverState.Decode(maximum.Encode()));
        Assert.IsTrue(maximum.SceneClockExclusionActive);
    }

    [TestMethod]
    public void Rejects_unknown_headers_reserved_bytes_noncanonical_flags_and_wrong_lengths()
    {
        foreach (var offset in new[] { 0, 4, 8, 12, 18, 19 })
        {
            var bytes = State().Encode(); bytes[offset] ^= 1;
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointClockObserverState.Decode(bytes));
        }
        foreach (var offset in new[] { 16, 17 })
        {
            var bytes = State().Encode(); bytes[offset] = 2;
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointClockObserverState.Decode(bytes));
        }
        var valid = State().Encode();
        Assert.ThrowsExactly<InvalidDataException>(() => CheckpointClockObserverState.Decode(valid[..^1]));
        Assert.ThrowsExactly<InvalidDataException>(() => CheckpointClockObserverState.Decode(valid.Concat(new byte[] { 0 }).ToArray()));
        Assert.ThrowsExactly<InvalidDataException>(() => CheckpointClockObserverState.Decode(null!));
    }

    [TestMethod]
    public void Rejects_negative_future_unobserved_cache_and_exhausted_sequence_values()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => new CheckpointClockObserverState(true, false, -1, 1, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => new CheckpointClockObserverState(true, false, 2, 1, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => new CheckpointClockObserverState(false, false, 1, 1, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => new CheckpointClockObserverState(true, false, 0, -1, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => new CheckpointClockObserverState(true, false, 0, int.MaxValue, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => new CheckpointClockObserverState(true, false, 0, 1, -1));
        foreach (var offset in new[] { 20, 24, 28 })
        {
            var bytes = State().Encode(); BitConverter.GetBytes(-1).CopyTo(bytes, offset);
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointClockObserverState.Decode(bytes));
        }
    }

    [TestMethod]
    public void Decode_does_not_retain_or_change_the_input_byte_array()
    {
        var bytes = State().Encode(); var original = (byte[])bytes.Clone(); var decoded = CheckpointClockObserverState.Decode(bytes);
        CollectionAssert.AreEqual(original, bytes); bytes[20] ^= 1;
        CollectionAssert.AreEqual(original, decoded.Encode());
    }

    [TestMethod]
    public void Native_readback_binds_exact_completed_sequence_and_engine_frame_anchor()
    {
        var state = State(); var native = Native(state, 1518); var original = (byte[])native.Clone();
        state.RequireNativeReadback(native, 1518, 1477); CollectionAssert.AreEqual(original, native);
        Assert.ThrowsExactly<InvalidDataException>(() => state.RequireNativeReadback(native, 1517, 1477));
        Assert.ThrowsExactly<InvalidDataException>(() => state.RequireNativeReadback(native, 1518, 1476));
        foreach (var offset in new[] { 0, 4, 8, 12, 80, 88, 256 })
        {
            var damaged = (byte[])native.Clone(); damaged[offset] ^= 1;
            Assert.ThrowsExactly<InvalidDataException>(() => state.RequireNativeReadback(damaged, 1518, 1477));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => state.RequireNativeReadback(native[..^1], 1518, 1477));
        Assert.ThrowsExactly<InvalidDataException>(() => state.RequireNativeReadback(native, -1, 1477));
    }

    [TestMethod]
    public void Raw_observer_readback_rejects_each_independent_field_change()
    {
        var source = State();
        CheckpointClockObserverState[] changes =
        {
            new(false, false, 0, 1477, 1477), new(true, true, 1476, 1477, 1477),
            new(true, false, 1477, 1477, 1477), new(true, false, 1476, 1478, 1477),
            new(true, false, 1476, 1477, 1478)
        };
        foreach (var changed in changes) Assert.ThrowsExactly<InvalidDataException>(() => source.RequireReadback(changed));
        Assert.ThrowsExactly<InvalidDataException>(() => source.RequireReadback(null!));
    }

    private static byte[] Native(CheckpointClockObserverState state, ulong completed)
    {
        var bytes = new byte[1128]; BitConverter.GetBytes(0x31435448U).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 4); BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 8);
        BitConverter.GetBytes(completed).CopyTo(bytes, 80); BitConverter.GetBytes(state.AdvanceSequence).CopyTo(bytes, 88);
        BitConverter.GetBytes((long)state.CaptureUnityFrameCount).CopyTo(bytes, 256); return bytes;
    }
}
