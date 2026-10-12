using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalSchedulerCarryTests
{
    private static CanonicalSchedulerCarry Data() => new() { SourceNative = 10, Timestamp = uint.MaxValue,
        Callbacks = new[] { new CanonicalSchedulerCarry.Callback { Order = 0, Rate = 0, Repeat = 0, Owner = "canonical-pool/fsm", Call = "UnityPlayer:8ac680", Cleanup = "none", Frame = -1, Mode = 7,
            WakeTimeBits = unchecked((long)0x8000000000000000) }, new CanonicalSchedulerCarry.Callback {
                Order = 1, Owner = "GameManager", Call = "UnityPlayer:8a6c70", Cleanup = "UnityPlayer:8a6950", Coroutine = "GameManager/Begin[0]",
                Frame = 12, Mode = 10, WakeTimeBits = BitConverter.DoubleToInt64Bits(0.25), TimestampAge = 2, UserDataPresent = true } },
        Coroutines = new[] { new CanonicalSchedulerCarry.Coroutine { Identity = "GameManager/Begin[0]", Family = "serial-boundary",
            WaitingFor = "none", ContinueWhenFinished = "none", EnumeratorBased = false, Done = false } } };
    [TestMethod]
    public void Raw_deadline_bits_and_false_native_flag_are_preserved_with_no_zero_tick_sentinel()
    {
        var source = Data(); var epoch = CanonicalInputEpoch.Capture(0, 10, 0, 0, 0, 0, uint.MaxValue);
        source.RequireEpoch(epoch);
        var types = new[] { typeof(CanonicalSchedulerCarry), typeof(CanonicalSchedulerCarry.Callback), typeof(CanonicalSchedulerCarry.Callback[]),
            typeof(CanonicalSchedulerCarry.Coroutine), typeof(CanonicalSchedulerCarry.Coroutine[]) };
        var codec = new CanonicalCarryGraphCodec(types, "scheduler-v1");
        var graph = codec.Capture(new Dictionary<string, object> { [CanonicalSchedulerCarry.Root] = source }, _ => null);
        var target = new CanonicalSchedulerCarry();
        codec.PrepareApply(graph, new Dictionary<string, object> { [CanonicalSchedulerCarry.Root] = target }, _ => throw new InvalidDataException())();
        target.RequireEpoch(epoch); Assert.AreEqual(unchecked((long)0x8000000000000000), target.Callbacks[0].WakeTimeBits);
        Assert.IsFalse(target.Coroutines[0].EnumeratorBased); Assert.AreEqual((uint)2, target.Callbacks[1].TimestampAge);
        epoch.SchedulerTimestamp = 0; Assert.ThrowsExactly<InvalidDataException>(() => target.RequireEpoch(epoch));
    }
    [TestMethod]
    public void Restore_plan_maps_actual_queued_families_and_preserves_uint32_stamp_wrap()
    {
        var source = Data(); source.Timestamp = 1; source.Callbacks[1].TimestampAge = 2;
        var target = Data(); target.Coroutines[0].Identity = "GameManager/prepared-Begin[0]";
        target.Callbacks[1].Coroutine = target.Coroutines[0].Identity; target.Callbacks[1].Frame = 111;
        var plan = CanonicalSchedulerRestorePlan.Prepare(source, target);
        Assert.AreEqual(1u, plan.Timestamp); Assert.AreEqual(uint.MaxValue, plan.Changes[1].Timestamp);
        Assert.AreEqual(12L, plan.Changes[1].Frame); Assert.AreEqual(111L, target.Callbacks[1].Frame);
        source.Callbacks[1].Frame = 999; Assert.AreEqual(12L, plan.Changes[1].Frame);
    }
    [TestMethod]
    public void Restore_plan_rejects_changed_order_and_native_flags_instead_of_editing_a_tree_blindly()
    {
        var source = Data(); var target = Data(); target.Callbacks[0].Owner = "other-pool/fsm";
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSchedulerRestorePlan.Prepare(source, target));
        target = Data(); target.Coroutines[0].EnumeratorBased = true;
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalSchedulerRestorePlan.Prepare(source, target));
    }
    [TestMethod]
    [DataRow("order")]
    [DataRow("deadline")]
    [DataRow("relation")]
    [DataRow("family")]
    [DataRow("async")]
    [DataRow("start-stamp")]
    [DataRow("call")]
    public void Unregistered_or_inconsistent_native_queue_state_is_rejected(string fault)
    {
        var data = Data();
        if (fault == "order") data.Callbacks[1].Order = 0;
        if (fault == "deadline") data.Callbacks[1].WakeTimeBits = BitConverter.DoubleToInt64Bits(double.NaN);
        if (fault == "relation") data.Coroutines[0].WaitingFor = "foreign";
        if (fault == "family") data.Coroutines[0].Family = "other-mod";
        if (fault == "async") data.Coroutines[0].HasAsyncOperation = true;
        if (fault == "start-stamp") data.Callbacks[0].TimestampAge = 1;
        if (fault == "call") data.Callbacks[1].Call = "UnityPlayer:wrong";
        Assert.ThrowsExactly<InvalidDataException>(() => data.Validate());
    }
}
