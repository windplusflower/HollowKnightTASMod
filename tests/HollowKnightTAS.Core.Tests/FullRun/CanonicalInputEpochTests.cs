using System;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalInputEpochTests
{
    private static CanonicalInputEpoch Source() => CanonicalInputEpoch.Capture(41, 1518, 991,
        0.125f, 29.5f, 29.48f, 1234);

    [TestMethod]
    public void Uses_the_actual_input_origin_and_preserves_all_raw_source_values()
    {
        var source = Source(); var copy = source.Copy();
        Assert.AreEqual(1477L, source.NativeFramesFromOrigin);
        Assert.AreEqual(41L, source.OriginNative); Assert.AreEqual(1518L, source.SourceNative);
        Assert.AreEqual(991UL, source.CurrentTick); Assert.AreEqual(1234U, source.SchedulerTimestamp);
        Assert.AreEqual(BitConverter.SingleToInt32Bits(0.125f), source.InitialTimeBits);
        Assert.AreEqual(BitConverter.SingleToInt32Bits(29.5f), source.CurrentTimeBits);
        Assert.AreEqual(BitConverter.SingleToInt32Bits(29.48f), source.LastUpdateTimeBits);
        source.RequireReadback(copy, 1518);
        copy.CurrentTimeBits ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => source.RequireReadback(copy, 1518));
    }

    [TestMethod]
    public void Portable_continuation_encoding_round_trips_every_raw_epoch_field()
    {
        var source = Source(); var codec = new CheckpointDataCodec(new[] { typeof(CanonicalInputEpoch) });
        var bytes = codec.Encode(source); var restored = codec.Decode<CanonicalInputEpoch>(bytes);
        source.RequireReadback(restored, 1518);
        CollectionAssert.AreEqual(bytes, codec.Encode(restored));
    }

    [TestMethod]
    public void Rejects_absent_negative_or_future_origins_and_wrong_native_import_cursor()
    {
        foreach (var origin in new long?[] { null, -1, 1519 })
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputEpoch.Capture(origin, 1518, 1, 0f, 0f, 0f, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputEpoch.Capture(0, -1, 1, 0f, 0f, 0f, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => Source().Validate(1517));
        var source = Source(); source.Version = 2;
        Assert.ThrowsExactly<InvalidDataException>(() => source.Validate(1518));
    }

    [TestMethod]
    public void Rejects_all_nonfinite_manager_times_and_keeps_signed_zero_bits()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputEpoch.Capture(0, 1, 1, value, 0f, 0f, 1));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputEpoch.Capture(0, 1, 1, 0f, value, 0f, 1));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalInputEpoch.Capture(0, 1, 1, 0f, 0f, value, 1));
        }
        var negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        var source = CanonicalInputEpoch.Capture(0, 1, 1, negativeZero, negativeZero, negativeZero, 1);
        Assert.AreEqual(unchecked((int)0x80000000), source.InitialTimeBits);
        var observed = source.Copy(); observed.InitialTimeBits = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => source.RequireReadback(observed, 1));
    }

    [TestMethod]
    public void Diagnostic_ages_use_modular_arithmetic_without_a_zero_tick_sentinel()
    {
        var source = Source(); source.CurrentTick = 0; source.SchedulerTimestamp = 0;
        source.Validate(1518);
        Assert.AreEqual(1UL, source.TickAge(ulong.MaxValue));
        Assert.AreEqual(1U, source.SchedulerAge(uint.MaxValue));
        Assert.AreEqual(0UL, source.TickAge(0)); Assert.AreEqual(0U, source.SchedulerAge(0));
        Assert.AreEqual(0UL, source.CurrentTick); Assert.AreEqual(0U, source.SchedulerTimestamp);
    }

    [TestMethod]
    public void Requires_exact_native_continuation_schema_and_saved_completed_frame()
    {
        var source = Source(); source.RequireNativeContinuation(NativeContinuation(1518));
        Assert.ThrowsExactly<InvalidDataException>(() => source.RequireNativeContinuation(NativeContinuation(1517)));
        foreach (var offset in new[] { 0, 4, 8, 12 })
        {
            var bytes = NativeContinuation(1518); bytes[offset] ^= 1;
            Assert.ThrowsExactly<InvalidDataException>(() => source.RequireNativeContinuation(bytes));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => source.RequireNativeContinuation(new byte[1127]));
        Assert.ThrowsExactly<InvalidDataException>(() => source.RequireNativeContinuation(NativeContinuation(ulong.MaxValue)));
    }

    [TestMethod]
    public void Restores_source_origin_once_after_native_import_and_checks_raw_readback()
    {
        var source = Source(); var observed = source.Copy(); observed.OriginNative = 20;
        var binding = new CanonicalInputEpoch.RestoreBinding(); var writes = 0;
        binding.ApplyOnce(source, 1518, origin => { writes++; observed.OriginNative = origin; }, () => observed.Copy());
        Assert.AreEqual(1, writes); Assert.AreEqual(41L, observed.OriginNative); Assert.IsTrue(binding.Consumed);
        Assert.AreEqual(1518L, observed.SourceNative); Assert.AreEqual(991UL, observed.CurrentTick);
        Assert.ThrowsExactly<InvalidOperationException>(() => binding.ApplyOnce(source, 1518, _ => writes++, () => observed.Copy()));
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public void Failed_preflight_does_not_write_and_failed_readback_cannot_retry()
    {
        var source = Source(); var binding = new CanonicalInputEpoch.RestoreBinding(); var writes = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => binding.ApplyOnce(source, 1517, _ => writes++, () => source.Copy()));
        Assert.AreEqual(0, writes); Assert.IsFalse(binding.Consumed);
        var observed = source.Copy(); observed.CurrentTick++;
        Assert.ThrowsExactly<InvalidDataException>(() => binding.ApplyOnce(source, 1518, _ => writes++, () => observed));
        Assert.AreEqual(1, writes); Assert.IsTrue(binding.Consumed);
        Assert.ThrowsExactly<InvalidOperationException>(() => binding.ApplyOnce(source, 1518, _ => writes++, () => source.Copy()));
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public void Restore_freezes_mutable_saved_data_before_origin_callback_runs()
    {
        var source = Source(); var observed = source.Copy(); var binding = new CanonicalInputEpoch.RestoreBinding();
        binding.ApplyOnce(source, 1518, origin =>
        {
            observed.OriginNative = origin; source.CurrentTick = 9999; source.InitialTimeBits = 0; source.OriginNative = 999;
        }, () => observed.Copy());
        Assert.AreEqual(41L, observed.OriginNative); Assert.AreEqual(991UL, observed.CurrentTick);
        Assert.AreEqual(BitConverter.SingleToInt32Bits(0.125f), observed.InitialTimeBits);
    }

    [TestMethod]
    public void Every_raw_readback_field_and_origin_must_match_bit_for_bit()
    {
        Action<CanonicalInputEpoch>[] mutations =
        {
            value => value.OriginNative++, value => value.SourceNative++, value => value.CurrentTick++,
            value => value.SchedulerTimestamp++, value => value.InitialTimeBits ^= 1,
            value => value.CurrentTimeBits ^= 1, value => value.LastUpdateTimeBits ^= 1
        };
        foreach (var mutate in mutations)
        {
            var source = Source(); var observed = source.Copy(); mutate(observed);
            Assert.ThrowsExactly<InvalidDataException>(() => source.RequireReadback(observed, 1518));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => Source().RequireReadback(null!, 1518));
    }

    [TestMethod]
    public void Control_origin_is_bound_to_actual_start_raw_tick_and_stamp()
    {
        var origin = CanonicalInputEpoch.Capture(41, 41, 41, 0, 0, 0, 164);
        var boundary = Source(); boundary.BindControlOrigin(origin); boundary.RequireControlOrigin();
        Assert.AreEqual(41UL, boundary.OriginTick); Assert.AreEqual(164U, boundary.OriginSchedulerTimestamp);
        Assert.AreEqual(950UL, boundary.TickAge(boundary.OriginTick));
        Assert.ThrowsExactly<InvalidDataException>(() => boundary.BindControlOrigin(origin));
    }

    [TestMethod]
    public void Restored_bound_control_origin_preserves_source_basis_at_later_boundary()
    {
        var origin = CanonicalInputEpoch.Capture(41, 41, 41, 0, 0, 0, 164);
        var saved = Source(); saved.BindControlOrigin(origin);
        var next = CanonicalInputEpoch.Capture(41, 1519, 992, .125f, 23.5f, 23.48f, 9001);
        next.BindControlOrigin(saved.Copy()); next.RequireControlOrigin();
        Assert.AreEqual(41UL, next.OriginTick); Assert.AreEqual(164U, next.OriginSchedulerTimestamp);
        Assert.AreEqual(1478L, next.NativeFramesFromOrigin);
        var changed = next.Copy(); changed.OriginSchedulerTimestamp++;
        Assert.ThrowsExactly<InvalidDataException>(() => next.RequireReadback(changed, 1519));
    }

    [TestMethod]
    public void Rejects_control_origin_from_other_start_or_unbound_later_frame()
    {
        var boundary = Source();
        Assert.ThrowsExactly<InvalidDataException>(boundary.RequireControlOrigin);
        Assert.ThrowsExactly<InvalidDataException>(() => boundary.BindControlOrigin(CanonicalInputEpoch.Capture(20, 20, 20, 0, 0, 0, 80)));
        Assert.ThrowsExactly<InvalidDataException>(() => boundary.BindControlOrigin(CanonicalInputEpoch.Capture(41, 42, 42, 0, 0, 0, 168)));
    }

    private static byte[] NativeContinuation(ulong completed)
    {
        var result = new byte[1128];
        BitConverter.GetBytes(0x31435448U).CopyTo(result, 0); BitConverter.GetBytes(1).CopyTo(result, 4);
        BitConverter.GetBytes(result.Length).CopyTo(result, 8); BitConverter.GetBytes(completed).CopyTo(result, 80);
        return result;
    }
}
